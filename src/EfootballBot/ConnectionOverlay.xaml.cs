using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EfootballBot.Core.System;

namespace EfootballBot;

/// <summary>
/// 游戏内连接监控悬浮窗：无边框置顶、半透明，拖到游戏画面上边打边看延迟/丢包。
/// 左键拖动、双击贴回游戏窗口、右键关闭（关闭只隐藏，监控线程继续，页面可再打开）。
///
/// P1-15「融进游戏」的四件事（旧实现只是一个孤立的置顶窗口）：
/// ① 不抢焦点：WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW（不进 Alt-Tab，不打断游戏前台状态）；
/// ② 不被自己抓到：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)——DXGI 桌面复制模式下
///    悬浮窗原本会被抓进画面，污染 OCR 与预览（还会让"当前画面"里出现延迟数字）；
/// ③ 跟随游戏：游戏窗口移动/切分辨率时贴住其客户区右上角，最小化时自动隐藏、还原时回来；
/// ④ 位置可接管：拖动即退出跟随（想放哪放哪），双击重新贴回游戏。
/// </summary>
public partial class ConnectionOverlay : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x3E));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x7B, 0x86, 0x96));

    // —— 跟随游戏窗口 ——
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private nint _gameHwnd;
    private DateTime _lastScanAt = DateTime.MinValue;
    /// <summary>true = 贴住游戏窗口（默认）；用户拖动后自动置 false，双击恢复。</summary>
    private bool _followGame = true;
    /// <summary>用户右键主动隐藏（此时即使游戏还原也不要自动弹出来）。</summary>
    private bool _userHidden;
    /// <summary>因游戏最小化而自动隐藏（游戏还原时要自动恢复）。</summary>
    private bool _autoHidden;

    public ConnectionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyFusionStyles();
        Loaded += (_, _) =>
        {
            MoveToDefault();
            _followTimer.Tick += (_, _) => FollowTick();
            _followTimer.Start();
        };
        // 页面上的「显示悬浮窗」再次打开时：清除"用户主动关闭"标记并立即贴回游戏
        // （否则右键关掉再打开会停在旧位置、且不再跟随）
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _userHidden = false;
            if (_followGame && ResolveGameWindow() is var h && h != nint.Zero) SnapToGame(h);
        };
        Closed += (_, _) => _followTimer.Stop();
    }

    // ---------------- 与游戏窗口的融合 ----------------

    private void ApplyFusionStyles()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        try
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(ex));

            // 排除在屏幕捕获之外：否则 DXGI 桌面复制模式会把悬浮窗抓进"游戏画面"，
            // 既污染 OCR 判定，也让预览里出现一块本程序自己的 UI。
            // Win10 2004+ 支持 WDA_EXCLUDEFROMCAPTURE（0x11）；老系统退回 WDA_MONITOR（0x1，黑块）。
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
        }
        catch { /* 融合样式失败不影响监控本身 */ }
    }

    /// <summary>解析当前游戏窗口（国际服/国服都认）。缓存句柄，失效时才重新扫进程。</summary>
    private nint ResolveGameWindow()
    {
        if (_gameHwnd != nint.Zero && IsWindow(_gameHwnd) && IsWindowVisible(_gameHwnd))
            return _gameHwnd;
        if ((DateTime.Now - _lastScanAt).TotalSeconds < 2) return nint.Zero;
        _lastScanAt = DateTime.Now;
        _gameHwnd = nint.Zero;
        foreach (var name in new[] { GameWindow.GlobalProcessName, "eFootballOnline", "eFootball2024", "efootballsteam" })
        {
            try
            {
                if (GameWindow.FindFirst(name) is { } win) { _gameHwnd = win.Handle; break; }
            }
            catch { /* 进程在扫描瞬时退出 */ }
        }
        return _gameHwnd;
    }

    private void FollowTick()
    {
        if (!_followGame || _userHidden) return;
        nint hwnd = ResolveGameWindow();
        if (hwnd == nint.Zero) return;

        // 游戏最小化：藏起来一起走；还原后自动回来
        if (IsIconic(hwnd))
        {
            if (!_autoHidden) { _autoHidden = true; Hide(); }
            return;
        }
        if (_autoHidden)
        {
            _autoHidden = false;
            Show();
        }
        SnapToGame(hwnd);
    }

    /// <summary>贴到游戏客户区右上角（物理像素 → WPF 设备无关单位，兼容缩放/副屏）。</summary>
    private void SnapToGame(nint hwnd)
    {
        try
        {
            if (!GetClientRect(hwnd, out var rc)) return;
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return;

            var src = PresentationSource.FromVisual(this);
            double toDipX = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double toDipY = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            double clientLeft = origin.X * toDipX;
            double clientTop = origin.Y * toDipY;
            double clientWidth = (rc.Right - rc.Left) * toDipX;

            double left = clientLeft + clientWidth - ActualWidth - 12;
            double top = clientTop + 12;
            if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5)
            {
                Left = left;
                Top = top;
            }
        }
        catch { /* 窗口尺寸异常（正在切换显示模式）时忽略这一拍 */ }
    }

    // ---------------- 交互 ----------------

    private void MoveToDefault()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 24;
        Top = wa.Top + 24;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击：把「贴住游戏」切换回来并立即归位（用户手动拖走后想复原）
            _followGame = true;
            _autoHidden = false;
            if (ResolveGameWindow() is var h && h != nint.Zero) SnapToGame(h);
            else MoveToDefault();
            if (!IsVisible && !_userHidden) Show();
            return;
        }
        // 手动拖动 = 接管位置：退出跟随，用户放哪就停哪
        _followGame = false;
        try { DragMove(); } catch { }
    }

    private void OnClose(object sender, MouseButtonEventArgs e)
    {
        _userHidden = true;
        Hide();
    }

    // ---------------- 数据刷新 ----------------

    public void Update(ConnSnapshot s)
    {
        TxtState.Text = s.State;
        Dot.Fill = s.DelayMs < 0 ? Idle : s.DelayMs < 60 ? Ok : s.DelayMs < 150 ? Warn : Bad;
        TxtDelay.Text = s.DelayMs < 0 ? "—" : s.DelayMs + " ms";
        TxtDelay.Foreground = Dot.Fill;
        TxtLoss.Text = s.LossPct <= 0.05 ? "0%" : $"{s.LossPct:0.#}%";
        TxtLoss.Foreground = s.LossPct < 1 ? (Brush)Ok : s.LossPct < 5 ? Warn : Bad;
        TxtRegion.Text = s.Region;
        TxtRemote.Text = $"远端 {s.Remote}   最小/平均/最大 {Fmt(s.MinMs)}/{Fmt(s.AvgMs)}/{Fmt(s.MaxMs)}";
        DrawSpark(s.Curve);
    }

    private static string Fmt(int v) => v < 0 ? "—" : v.ToString();

    private void DrawSpark(IReadOnlyList<int> samples)
    {
        CvSpark.Children.Clear();
        if (samples.Count < 2) return;
        double w = CvSpark.ActualWidth;
        double h = CvSpark.ActualHeight;
        if (w < 10 || h < 5) return;
        int peak = Math.Max(200, samples.Where(v => v > 0).DefaultIfEmpty(200).Max());
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int i = 0; i < samples.Count; i++)
            {
                int v = samples[i];
                if (v < 0) { started = false; continue; }
                double x = w * i / Math.Max(1, samples.Count - 1);
                double y = h - 1 - (h - 4) * Math.Min(1.0, v / (double)peak);
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();
        CvSpark.Children.Add(new Path { Data = geo, Stroke = Ok, StrokeThickness = 1.4 });
    }

    // ---------------- Win32 ----------------

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);
}
