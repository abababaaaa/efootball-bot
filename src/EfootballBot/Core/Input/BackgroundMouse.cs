using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 面向窗口句柄的后台鼠标模拟（不移动全局光标、不抢前台）。
/// 通过 PostMessage 投递 WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP，坐标为客户区像素。
/// 适用于会处理窗口鼠标消息的游戏（如国服 eFootballOnline 窗口化运行时）。
/// </summary>
public sealed class BackgroundMouse
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;

    private const int MK_LBUTTON = 0x0001;
    private const int MK_RBUTTON = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>向指定窗口客户区坐标投递一次左键单击（移动→按下→抬起）。</summary>
    public async Task ClickAsync(IntPtr hWnd, int x, int y, int downHoldMs = 60, CancellationToken ct = default)
    {
        PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));
        await Task.Delay(30, ct);
        PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LParam(x, y));
        await Task.Delay(downHoldMs, ct);
        PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>归一化坐标（0~1）单击，需传入客户区尺寸。</summary>
    public Task ClickNormalizedAsync(IntPtr hWnd, double nx, double ny, int clientW, int clientH,
        int downHoldMs = 60, CancellationToken ct = default)
    {
        int x = Math.Clamp((int)Math.Round(nx * clientW), 0, Math.Max(0, clientW - 1));
        int y = Math.Clamp((int)Math.Round(ny * clientH), 0, Math.Max(0, clientH - 1));
        return ClickAsync(hWnd, x, y, downHoldMs, ct);
    }

    /// <summary>仅移动（悬停），用于触发高亮/焦点。</summary>
    public void Move(IntPtr hWnd, int x, int y)
        => PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));

    /// <summary>右键单击。</summary>
    public async Task RightClickAsync(IntPtr hWnd, int x, int y, int downHoldMs = 60, CancellationToken ct = default)
    {
        PostMessage(hWnd, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, LParam(x, y));
        await Task.Delay(downHoldMs, ct);
        PostMessage(hWnd, WM_RBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>同步 SendMessage 版本（等待窗口处理完返回），个别窗口更可靠。</summary>
    public void ClickSend(IntPtr hWnd, int x, int y)
    {
        SendMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));
        SendMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LParam(x, y));
        SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    private static IntPtr LParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
