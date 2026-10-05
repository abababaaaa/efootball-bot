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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; internal int Y; }
}
