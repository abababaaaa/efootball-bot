using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.System;

/// <summary>游戏的显示模式（决定置顶悬浮窗能不能看得见）。</summary>
public enum GameDisplayMode
{
    Unknown,
    /// <summary>带标题栏的窗口模式。</summary>
    Windowed,
    /// <summary>无标题栏、窗口矩形正好铺满显示器 = 无边框全屏（置顶窗口能盖在上面）。</summary>
    BorderlessFullscreen,
    /// <summary>独占全屏（DXGI 全屏交换链，DWM 不参与合成）：任何置顶窗口都会被游戏画面盖掉。</summary>
    ExclusiveFullscreen,
}

/// <summary>
/// 游戏窗口显示模式的判定与「切成无边框全屏」。
///
/// 为什么必须有这段：WPF 悬浮窗再 Topmost 也盖不住**独占全屏**——独占全屏时 DWM 退出合成，
/// 屏幕上只有游戏那张交换链，其它窗口（包括 WS_EX_TOPMOST）根本不上屏。唯一的正路是把游戏
/// 切到无边框全屏（窗口化 + 铺满 + 无标题栏），这时桌面合成恢复，置顶分层窗口才能浮在游戏上。
/// 改动只碰游戏窗口的样式与位置，不动渲染 API；游戏自己切回来（Alt+Enter / 设置里选全屏）即可还原。
/// </summary>
public static class GameWindowMode
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const int VK_RETURN = 0x0D;
    private const uint KEYEVENTF_EXTENDED = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static GameDisplayMode Detect(nint hwnd, out string detail)
    {
        detail = "";
        if (hwnd == nint.Zero) { detail = "没有游戏窗口句柄"; return GameDisplayMode.Unknown; }
        try
        {
            int style = (int)GetWindowLongPtr(hwnd, GWL_STYLE);
            if (!GetWindowRect(hwnd, out var wr)) { detail = "GetWindowRect 失败"; return GameDisplayMode.Unknown; }
            nint mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) { detail = "GetMonitorInfo 失败"; return GameDisplayMode.Unknown; }

            int monW = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            int winW = wr.Right - wr.Left;
            int winH = wr.Bottom - wr.Top;
            bool fills = winW >= monW - 2 && winH >= monH - 2
                      && Math.Abs(wr.Left - mi.rcMonitor.Left) <= 2 && Math.Abs(wr.Top - mi.rcMonitor.Top) <= 2;
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            bool popup = (style & WS_POPUP) != 0;

            detail = $"窗口 {winW}x{winH} @{wr.Left},{wr.Top} · 显示器 {monW}x{monH} @{mi.rcMonitor.Left},{mi.rcMonitor.Top}"
                   + $" · style=0x{style:X8} · 标题栏={(hasCaption ? "有" : "无")} · popup={(popup ? "是" : "否")}";

            if (hasCaption) return GameDisplayMode.Windowed;
            if (fills) return GameDisplayMode.BorderlessFullscreen;
            return GameDisplayMode.ExclusiveFullscreen;
        }
        catch (Exception ex) { detail = ex.Message; return GameDisplayMode.Unknown; }
    }

    /// <summary>
    /// 把游戏从独占全屏切成无边框全屏。先只改窗口样式（不注入按键），不成再发一次 Alt+Enter。
    /// 已经是窗口/无边框时不做任何改动。
    /// </summary>
    public static (bool ok, string message) ForceBorderless(nint hwnd)
    {
        var mode = Detect(hwnd, out var detail);
        switch (mode)
        {
            case GameDisplayMode.Windowed:
                return (true, "游戏是窗口模式，悬浮窗可以直接盖在上面。" + detail);
            case GameDisplayMode.BorderlessFullscreen:
                return (true, "游戏已是无边框全屏，置顶悬浮窗能正常显示。" + detail);
            case GameDisplayMode.Unknown:
                return (false, "认不出游戏窗口状态：" + detail);
        }

        // 独占全屏：去边框 + 铺满显示器
        string first = StripAndFill(hwnd);
        Sleep(700);
        var after = Detect(hwnd, out var detail2);
        if (after != GameDisplayMode.ExclusiveFullscreen)
            return (true, "已切成无边框全屏（" + detail2 + "）。");

        // 样式改不动（UE4 的 flip-model 全屏很常见）：发 Alt+Enter 让游戏自己退出独占全屏，再补一次铺满
        SendAltEnter(hwnd);
        Sleep(1300);
        string second = StripAndFill(hwnd);
        Sleep(500);
        var final = Detect(hwnd, out var detail3);
        bool ok = final != GameDisplayMode.ExclusiveFullscreen;
        string msg = ok
            ? "已切成无边框全屏（" + detail3 + "）。"
            : "没切成无边框全屏：" + detail3 + "；游戏设置 → 画面 → 显示模式改成「窗口模式」，分辨率选显示器原生分辨率即可。";
        return (ok, msg + (first.Length > 0 ? " ｜ " + first : "") + (second.Length > 0 ? " ｜ " + second : ""));
    }

    /// <summary>去掉标题栏/可调边/系统菜单，并把窗口摆到显示器矩形上（无边框全屏）。</summary>
    private static string StripAndFill(nint hwnd)
    {
        try
        {
            int style = (int)GetWindowLongPtr(hwnd, GWL_STYLE);
            int neu = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            neu |= WS_VISIBLE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new nint(neu));

            nint mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return "GetMonitorInfo 失败";
            var r = mi.rcMonitor;
            bool moved = SetWindowPos(hwnd, nint.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (!moved)
                return $"SetWindowPos 失败（错误 {Marshal.GetLastWin32Error()}）：游戏以更高权限运行时本程序需要同样以管理员启动";
            return "";
        }
        catch (Exception ex) { return "改样式异常：" + ex.Message; }
    }

    /// <summary>发一次 Alt+Enter（UE 系游戏切全屏/窗口的通用键）。</summary>
    private static void SendAltEnter(nint hwnd)
    {
        try
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);                     // Alt 按下
            PostMessage(hwnd, WM_SYSKEYDOWN, (nint)VK_RETURN, (nint)0x201C0001);   // Alt 上下文字位置 1
            Sleep(120);
            PostMessage(hwnd, WM_SYSKEYUP, (nint)VK_RETURN, unchecked((nint)0xC01C0001) | 0x2);
            Sleep(80);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);       // Alt 抬起
        }
        catch { }
    }

    private static void Sleep(int ms) => global::System.Threading.Thread.Sleep(ms);

    public static string Describe(nint hwnd)
    {
        var m = Detect(hwnd, out var d);
        string mode = m switch
        {
            GameDisplayMode.Windowed => "窗口模式",
            GameDisplayMode.BorderlessFullscreen => "无边框全屏（悬浮窗可显示）",
            GameDisplayMode.ExclusiveFullscreen => "独占全屏（悬浮窗会被游戏画面盖掉）",
            _ => "未知",
        };
        return mode + " — " + d;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
