using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>游戏进程窗口信息。国服/国际服通过进程名区分。</summary>
public sealed record GameWindowInfo(nint Handle, int ProcessId, string ProcessName, string Title)
{
    public bool IsGlobal => ProcessName.Equals("eFootball", StringComparison.OrdinalIgnoreCase)
                        || ProcessName.Equals("eFootball2024", StringComparison.OrdinalIgnoreCase)
                        || ProcessName.Equals("efootballsteam", StringComparison.OrdinalIgnoreCase);
}

public static class GameWindow
{
    public const string GlobalProcessName = "eFootball";

    public static IReadOnlyList<GameWindowInfo> FindAll(string processName = GlobalProcessName)
    {
        var list = new List<GameWindowInfo>();
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                var h = proc.MainWindowHandle;
                if (h != nint.Zero && IsWindowVisible(h))
                    list.Add(new GameWindowInfo(h, proc.Id, proc.ProcessName, proc.MainWindowTitle ?? ""));
            }
            catch { /* 进程可能已退出 */ }
        }
        return list;
    }

    public static GameWindowInfo? FindFirst(string processName = GlobalProcessName)
        => FindAll(processName).FirstOrDefault();

    /// <summary>窗口客户区物理像素尺寸（用于捕获诊断）。</summary>
    public static (int Width, int Height) GetClientSize(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rc)) return (0, 0);
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref pt);
        return (rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    /// <summary>
    /// 整窗外框尺寸 + 客户区在整窗内的偏移（物理像素）。
    /// WGC 输出的是含标题栏的整窗帧，用它把客户区归一化坐标映射到帧坐标。
    /// </summary>
    public static (int WinW, int WinH, int OffX, int OffY, int CliW, int CliH) GetWindowFrame(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var wr) || !GetClientRect(hwnd, out var cr))
            return (0, 0, 0, 0, 0, 0);
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref pt);
        return (wr.Right - wr.Left, wr.Bottom - wr.Top,
                pt.X - wr.Left, pt.Y - wr.Top,
                cr.Right - cr.Left, cr.Bottom - cr.Top);
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out RECT rc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rc);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref POINT pt);

    // ---------------- 挂机自动窗口化（全屏超宽 DWM 合成开销是卡顿根因，与捕获无关） ----------------

    public sealed record SavedWindowState(long Style, int X, int Y, int W, int H, bool Maximized);

    /// <summary>
    /// 把游戏窗口强制窗口化到指定客户区尺寸（默认 1280×720，位于屏幕左上角附近）。
    /// 已是小窗口（客户区高 ≤ 760）时不动作并返回 null。返回还原所需的原始状态。
    /// </summary>
    public static SavedWindowState? WindowizeTo(nint hwnd, int clientW = 1280, int clientH = 720)
    {
        var (_, _, _, _, cw, ch) = GetWindowFrame(hwnd);
        if (ch > 0 && ch <= 760) return null; // 已是小窗口，不动

        if (!GetWindowRect(hwnd, out var rc)) return null;
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        bool max = IsZoomed(hwnd);
        var saved = new SavedWindowState(style, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, max);

        if (max) ShowWindow(hwnd, SW_RESTORE);

        // 去掉 WS_POPUP（无边框全屏标志），换成标准重叠窗口（标题栏+边框）
        long newStyle = (style & ~WS_POPUP) | WS_OVERLAPPEDWINDOW;
        if (newStyle != style) SetWindowLongPtr(hwnd, GWL_STYLE, new nint(newStyle));

        // SetWindowPos 设置的是整窗尺寸，用 AdjustWindowRectEx 反算含边框的外框
        var adj = new RECT { Left = 0, Top = 0, Right = clientW, Bottom = clientH };
        AdjustWindowRectEx(ref adj, (uint)(newStyle & 0xFFFFFFFF), false, 0);
        SetWindowPos(hwnd, nint.Zero, 50, 30, adj.Right - adj.Left, adj.Bottom - adj.Top,
                     SWP_NOZORDER | SWP_FRAMECHANGED);
        return saved;
    }

    /// <summary>还原 WindowizeTo 保存的窗口状态。</summary>
    public static void RestoreWindow(nint hwnd, SavedWindowState s)
    {
        SetWindowLongPtr(hwnd, GWL_STYLE, new nint(s.Style));
        SetWindowPos(hwnd, nint.Zero, s.X, s.Y, s.W, s.H, SWP_NOZORDER | SWP_FRAMECHANGED);
        if (s.Maximized) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    /// <summary>
    /// 把游戏窗口移到屏幕外（-32000,-32000），尺寸不变。
    /// DWM 对屏幕外窗口合成开销为零，WGC 捕获不受影响。
    /// 独占全屏游戏通常抵抗样式修改，但对坐标修改的抵抗较弱。
    /// 返回原始坐标供还原；失败返回 null。
    /// </summary>
    public static (int X, int Y)? MoveOffscreen(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rc)) return null;
        var orig = (rc.Left, rc.Top);
        // 尺寸保持原样，只挪坐标
        if (!SetWindowPos(hwnd, nint.Zero, -32000, -32000,
                          rc.Right - rc.Left, rc.Bottom - rc.Top, SWP_NOZORDER))
            return null;
        return orig;
    }

    /// <summary>把 MoveOffscreen 挪走的窗口移回原坐标。</summary>
    public static void MoveBack(nint hwnd, int x, int y)
        => SetWindowPos(hwnd, nint.Zero, x, y, 0, 0, SWP_NOZORDER | 0x0001 /*SWP_NOSIZE*/);

    /// <summary>最小化游戏窗口（DWM 完全不合成最小化窗口；WGC 通常仍能捕获）。返回是否真的已最小化。</summary>
    public static bool MinimizeWindow(nint hwnd)
    {
        ShowWindow(hwnd, 6 /*SW_MINIMIZE*/);
        Thread.Sleep(200);
        return IsIconic(hwnd);
    }

    /// <summary>从最小化还原。</summary>
    public static void RestoreFromMinimize(nint hwnd) => ShowWindow(hwnd, SW_RESTORE);

    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);

    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint h, int idx);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint h, int idx, nint val);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint h);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT rc, uint style, bool menu, uint exStyle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; internal int Y; }
}
