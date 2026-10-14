using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 全局热键（低级钩子实现）：WH_KEYBOARD_LL + WH_MOUSE_LL，支持键盘任意键与鼠标键
/// （左/右/中/侧键），可不带修饰键——RegisterHotKey 做不到的能力。
/// 主热键语义（调用方实现）：第一次按=暂停，暂停中再按=彻底停止；恢复热键=从暂停恢复。
/// 钩子须在带消息循环的线程（WPF UI 线程）上 Register。
/// </summary>
public sealed class SmartHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private const int VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr _kbHook, _msHook;
    // 委托必须强引用，否则被 GC 回收后钩子失效
    private readonly LowLevelProc _kbProc, _msProc;
    private Config.HotkeyConfig? _cfg;

    /// <summary>主热键触发（暂停/停止语义由调用方实现）。钩子线程回调，UI 操作需封送。</summary>
    public event Action? PauseStopTriggered;
    /// <summary>恢复热键触发。</summary>
    public event Action? ResumeTriggered;

    public bool IsRegistered { get; private set; }

    public SmartHotkey()
    {
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    /// <summary>兼容旧 API：低级钩子不需要窗口句柄，空实现。</summary>
    public void Attach(object window) { }

    /// <summary>注册热键（安装低级钩子）。失败返回 false。</summary>
    public bool Register(Config.HotkeyConfig cfg)
    {
        if (!cfg.Enabled) return false;
        Unregister();
        _cfg = cfg;
        if (_kbHook == IntPtr.Zero)
        {
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, GetModuleHandle(null), 0);
            if (_kbHook == IntPtr.Zero) return false;
        }
        if (_msHook == IntPtr.Zero)
        {
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, GetModuleHandle(null), 0);
            if (_msHook == IntPtr.Zero) return false;
        }
        IsRegistered = true;
        return true;
    }

    public void Unregister()
    {
        if (_kbHook != IntPtr.Zero) { try { UnhookWindowsHookEx(_kbHook); } catch { } _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { try { UnhookWindowsHookEx(_msHook); } catch { } _msHook = IntPtr.Zero; }
        IsRegistered = false;
    }

    private static int CurrentMods()
    {
        int m = 0;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) m |= 2;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) m |= 4;
        if ((GetKeyState(VK_MENU) & 0x8000) != 0) m |= 1;
        if ((GetKeyState(VK_LWIN) & 0x8000) != 0) m |= 8;
        return m;
    }

    private static bool IsModifierVk(int vk) =>
        vk is VK_CONTROL or VK_SHIFT or VK_MENU or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private void Fire(int code, int mods, bool isMouse)
    {
        var cfg = _cfg;
        if (cfg is null) return;
        if (!cfg.IsMouse && !isMouse && code == cfg.VirtualKey && mods == cfg.Modifiers)
            PauseStopTriggered?.Invoke();
        else if (!cfg.ResumeIsMouse && !isMouse && code == cfg.ResumeVirtualKey && mods == cfg.ResumeModifiers)
            ResumeTriggered?.Invoke();
        else if (cfg.IsMouse && isMouse && code == cfg.VirtualKey && mods == cfg.Modifiers)
            PauseStopTriggered?.Invoke();
        else if (cfg.ResumeIsMouse && isMouse && code == cfg.ResumeVirtualKey && mods == cfg.ResumeModifiers)
            ResumeTriggered?.Invoke();
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt64() == WM_KEYDOWN || wParam.ToInt64() == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam, 0); // KBDLLHOOKSTRUCT.vkCode
            if (!IsModifierVk(vk))
                Fire(vk, CurrentMods(), isMouse: false);
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int btn = wParam.ToInt64() switch
            {
                WM_LBUTTONDOWN => 1,
                WM_RBUTTONDOWN => 2,
                WM_MBUTTONDOWN => 4,
                WM_XBUTTONDOWN => (Marshal.ReadInt32(lParam, 8) >> 16) == 1 ? 5 : 6, // mouseData 高字
                _ => 0,
            };
            if (btn != 0)
                Fire(btn, CurrentMods(), isMouse: true);
        }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
    }
}
