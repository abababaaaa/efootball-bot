using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 面向窗口句柄的后台键盘模拟（PostMessage，不动全局键盘状态、不抢前台）。
///
/// 为什么需要：国服客户端个别页面是「网页层」UI，只认它自己印在按钮上的键盘提示。
/// 实机 2026-11-07 段位积分页右下按钮写的是「C 下一步」（不是手柄的 A、也不是 RT）——
/// 同一帧 A 连按 5 次、B 连按 6 次画面一动不动。UE/Slate 的 UI 走标准 WM_KEYDOWN 消息，
/// 后台投递即可触发绑定动作，与背景鼠标（<see cref="BackgroundMouse"/>）同一路数。
/// </summary>
public sealed class BackgroundKeyboard
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>向指定窗口投递一次按键（按下 → 保持 holdMs → 抬起）。</summary>
    public async Task TapAsync(IntPtr hWnd, int virtualKey, int holdMs = 60, CancellationToken ct = default)
    {
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)virtualKey, KeyLParam(virtualKey, up: false));
        await Task.Delay(holdMs, ct);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)virtualKey, KeyLParam(virtualKey, up: true));
    }

    /// <summary>
    /// WM_KEYDOWN / WM_KEYUP 的 lParam 组装：重复次数=1、第 16~23 位放扫描码
    /// （MapVirtualKey 换算，缺了它部分引擎识别不出按键）、抬起事件再置第 30/31 位。
    /// 纯函数，便于单测锁定位域。
    /// </summary>
    internal static IntPtr KeyLParam(int virtualKey, bool up)
    {
        uint scan = MapVirtualKey((uint)virtualKey, 0);
        int lp = 1 | ((int)scan << 16);
        if (up) lp |= (1 << 30) | (1 << 31);
        return (IntPtr)lp;
    }
}
