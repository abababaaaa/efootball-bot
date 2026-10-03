using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.Input;

/// <summary>
/// 全局热键注册/注销器。使用 Win32 RegisterHotKey + HwndSource Hook 捕获 WM_HOTKEY。
/// 即使游戏全屏、本窗口失焦也能响应。
/// 
/// 修饰键位映射（与 HotkeyConfig.Modifiers 一致）：
///   Alt=1, Ctrl=2, Shift=4, Win=8
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint WM_HOTKEY = 0x0312;
    private const int HotkeyId = 1;

    // Win32 MOD_* 常量
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private HwndSource? _source;
    private IntPtr _hWnd;
    private HotkeyConfig? _config;

    /// <summary>热键触发事件。</summary>
    public event Action? Triggered;

    /// <summary>当前是否已注册。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>
    /// 从窗口句柄获取 HwndSource 并添加 Hook。必须在窗口 Loaded 之后调用。
    /// </summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hWnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hWnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>注册热键。返回 true 表示成功。</summary>
    public bool Register(HotkeyConfig cfg)
    {
        if (!cfg.Enabled) return false;
        if (_hWnd == IntPtr.Zero)
            throw new InvalidOperationException("请先调用 Attach(window) 绑定窗口。");

        // 先注销旧的（如果有）
        Unregister();

        uint fsMods = 0;
        if ((cfg.Modifiers & 1) != 0) fsMods |= MOD_ALT;
        if ((cfg.Modifiers & 2) != 0) fsMods |= MOD_CONTROL;
        if ((cfg.Modifiers & 4) != 0) fsMods |= MOD_SHIFT;
        if ((cfg.Modifiers & 8) != 0) fsMods |= MOD_WIN;

        bool ok = RegisterHotKey(_hWnd, HotkeyId, fsMods, (uint)cfg.VirtualKey);
        if (ok)
        {
            IsRegistered = true;
            _config = cfg;
        }
        return ok;
    }

    /// <summary>注销热键。</summary>
    public void Unregister()
    {
        if (IsRegistered && _hWnd != IntPtr.Zero)
        {
            try { UnregisterHotKey(_hWnd, HotkeyId); } catch { }
            IsRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Triggered?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        if (_source is not null)
        {
            try { _source.RemoveHook(WndProc); } catch { }
        }
    }
}
