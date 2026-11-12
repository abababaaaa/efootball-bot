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
    /// <summary>true = 鼠标穿透（WS_EX_TRANSPARENT），HUD 不挡游戏操作。</summary>
    private bool _clickThrough;

    public ConnectionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyFusionStyles();
        Loaded += (_, _) =>
        {
            EnsureUsable();
            // 第一次出现摆在主屏正中偏上：多屏/超宽屏（3440×1440）下贴右上角容易正好落在
            // 别的内容上，用户找不到窗口就以为没弹出来
            if (!_placedOnce) { CenterOnPrimary(); _placedOnce = true; }
            else MoveToDefault();
            _followTimer.Tick += (_, _) => FollowTick();
            _followTimer.Start();
        };
        // 页面上的「显示悬浮窗」再次打开时：清除"用户主动关闭"标记并立即贴回游戏
        // （否则右键关掉再打开会停在旧位置、且不再跟随）
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _userHidden = false;
            EnsureUsable();
            ClampToScreen();
            ApplyClickThrough();
            if (_followGame && ResolveGameWindow() is var h && h != nint.Zero) SnapToGame(h);
        };
        Closed += (_, _) => _followTimer.Stop();
    }

    /// <summary>
    /// 兜底自检：SizeToContent="Height" 会把宽度压成 2px（WPF 实测 Loaded 时 Width 已经是 2），
    /// 窗口就成了一条看不见的竖线——这是「点了显示悬浮窗但根本没有」的真因。
    /// 这里在 Loaded / 重新显示 / 尺寸异常时统一修一次，并把结果记进日志，下次不用再猜。
    /// </summary>
    private bool _placedOnce;

    private void EnsureUsable()
    {
        try
        {
            if (double.IsNaN(Width) || Width < 120) Width = 300;
            if (MinWidth < 120) MinWidth = 300;
            if (!(Height > 60)) Height = 150;
            if (ActualWidth < 120 || ActualHeight < 60)
            {
                Width = 300;
                Height = 150;
                UpdateLayout();
            }
            if (Topmost) Topmost = false;
            Topmost = true;
            Log($"自检 {Width:0}x{Height:0}（实际 {ActualWidth:0}x{ActualHeight:0}）at {Left:0},{Top:0} topmost={Topmost}");
        }
        catch (Exception ex) { Log("自检失败：" + ex.Message); }
    }

    /// <summary>悬浮窗自己的诊断日志：data\overlay.log（「点了没有窗口」时先看这里）。</summary>
    private static void Log(string msg)
    {
        try
        {
            string dir = AppPaths.DataDir;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "overlay.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }

    /// <summary>主屏工作区正中偏上（保证一出现就在视野里）。</summary>
    private void CenterOnPrimary()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + Math.Max(40, (wa.Height - Height) / 3);
        }
        catch { }
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
        // 置顶保持：无边框全屏下游戏抢前台时，Topmost 偶尔被系统重置，每 250ms 补一次
        if (!Topmost) Topmost = true;
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

            // 游戏被「屏外停靠」（BackgroundParker 会把窗口挪到负坐标）时，跟着算出来的位置
            // 也在屏幕外——悬浮窗就"凭空消失"了。落在所有显示器之外就不跟，保持当前位置。
            if (left + ActualWidth < 0 || top + ActualHeight < 0
                || left > SystemParameters.VirtualScreenWidth - 40
                || top > SystemParameters.VirtualScreenHeight - 40)
                return;

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

    /// <summary>中键走这里（左键拖动、右键关闭各走各的处理器）。</summary>
    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) OnToggleClickThrough(sender, e);
    }

    /// <summary>鼠标穿透（中键切换）：穿透后悬浮窗只显示不接鼠标，点击直接落到游戏上。</summary>
    private void OnToggleClickThrough(object sender, MouseButtonEventArgs e)
    {
        _clickThrough = !_clickThrough;
        ApplyClickThrough();
        TxtHint.Text = _clickThrough
            ? "已穿透（点击直达游戏） · 中键恢复 · 右键关闭"
            : "拖动移位 · 双击贴回 · 中键穿透 · 右键关闭";
        e.Handled = true;
    }

    private void ApplyClickThrough()
    {
        try
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex = _clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(ex));
        }
        catch { }
    }

    /// <summary>把窗口拉回可见区域（换分辨率/副屏拔掉后不至于飘到屏幕外，看得见却没反应）。</summary>
    private void ClampToScreen()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            if (double.IsNaN(Left) || Left < wa.Left - ActualWidth + 60 || Left > wa.Right - 60) Left = wa.Right - Width - 24;
            if (double.IsNaN(Top) || Top < wa.Top - 10 || Top > wa.Bottom - 40) Top = wa.Top + 24;
        }
        catch { }
    }

    // ---------------- 数据刷新 ----------------

    public void Update(ConnSnapshot s)
    {
        TxtState.Text = s.State;
        Dot.Fill = s.DelayMs < 0 ? Idle : s.DelayMs < 60 ? Ok : s.DelayMs < 150 ? Warn : Bad;
        int shownMs = s.DelayMs;
        TxtDelay.Text = shownMs < 0 ? "—" : shownMs + " ms";
        TxtDelay.Foreground = shownMs < 0 ? Idle : shownMs < 60 ? Ok : shownMs < 110 ? Warn : Bad;
        bool lossKnown = s.LossPct >= 0;
        TxtLoss.Text = !lossKnown ? "—" : s.LossPct <= 0.05 ? "0%" : $"{s.LossPct:0.#}%";
        TxtLoss.Foreground = !lossKnown ? Idle : s.LossPct < 1 ? (Brush)Ok : s.LossPct < 5 ? Warn : Bad;
        TxtRegion.Text = s.Region;
        string tail = s.DelaySource.Length > 0 ? s.DelaySource : (s.DelayMs < 0 && s.PingStatus.Length > 0 ? s.PingStatus : "");
        tail += $"   最小/平均/最大 {Fmt(s.MinMs)}/{Fmt(s.AvgMs)}/{Fmt(s.MaxMs)}";
        if (s.JitterMs >= 0) tail += $"   抖动 {s.JitterMs}ms";
        if (s.UpPps + s.DownPps > 0) tail += $"   ↑{s.UpPps}/↓{s.DownPps}pps";
        TxtRemote.Text = $"远端 {s.Remote}   {tail}";
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
    private const long WS_EX_TRANSPARENT = 0x00000020;
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
