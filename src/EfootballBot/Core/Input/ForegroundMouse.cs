using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 全局真实鼠标输入（SendInput + SetCursorPos）。
/// 仅对当前前台/激活窗口可靠，属于“点击瞬间需置前”的方案，作为后台消息失效时的兜底。
/// </summary>
public sealed class ForegroundMouse
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public MOUSEINPUT Mi; public int Pad1; public int Pad2; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>将窗口还原并置前，返回此前前台窗口（便于恢复）。</summary>
    public IntPtr BringToFront(IntPtr hWnd)
    {
        IntPtr old = GetForegroundWindow();
        if (hWnd == old) return old;
        ShowWindow(hWnd, SW_RESTORE);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        return old;
    }

    /// <summary>恢复指定窗口到前台。</summary>
    public void RestoreFront(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
    }

    /// <summary>移动真实光标到屏幕坐标并左键点击。</summary>
    public async Task ClickScreenAsync(int screenX, int screenY, int downHoldMs = 60, CancellationToken ct = default)
    {
        SetCursorPos(screenX, screenY);
        await Task.Delay(50, ct);
        Send(0, MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(downHoldMs, ct);
        Send(0, MOUSEEVENTF_LEFTUP);

        static void Send(int _, uint flags)
        {
            var input = new INPUT
            {
                Type = 0, // INPUT_MOUSE
                Mi = new MOUSEINPUT { Flags = flags, ExtraInfo = IntPtr.Zero }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>把客户区坐标换算为屏幕坐标并点击（需窗口已在前台）。</summary>
    public Task ClickClientAsync(IntPtr hWnd, int clientX, int clientY, int downHoldMs = 60, CancellationToken ct = default)
    {
        var p = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hWnd, ref p);
        return ClickScreenAsync(p.X, p.Y, downHoldMs, ct);
    }
}
