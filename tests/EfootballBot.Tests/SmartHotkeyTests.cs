using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

public class SmartHotkeyTests
{
    [Fact]
    public void MatchHotkey_MatchesPauseAndResumeWithModifiers()
    {
        var cfg = new HotkeyConfig
        {
            Enabled = true,
            VirtualKey = 0x51,      // Q
            Modifiers = 2 | 4,      // Ctrl + Shift
            IsMouse = false,
            ResumeVirtualKey = 0x52, // R
            ResumeModifiers = 2 | 4,
            ResumeIsMouse = false,
        };

        Assert.Equal(SmartHotkey.HotkeyAction.PauseStop,
            SmartHotkey.MatchHotkey(cfg, 0x51, 2 | 4, isMouse: false));
        Assert.Equal(SmartHotkey.HotkeyAction.Resume,
            SmartHotkey.MatchHotkey(cfg, 0x52, 2 | 4, isMouse: false));

        // 缺修饰键时不误触发（旧版 GetKeyState 失焦时就是返回 0）
        Assert.Equal(SmartHotkey.HotkeyAction.None,
            SmartHotkey.MatchHotkey(cfg, 0x51, 0, isMouse: false));
        // 鼠标键不混触键盘配置
        Assert.Equal(SmartHotkey.HotkeyAction.None,
            SmartHotkey.MatchHotkey(cfg, 0x51, 2 | 4, isMouse: true));
    }

    [Fact]
    public void MatchHotkey_DisabledOrNull_ReturnsNone()
    {
        var cfg = new HotkeyConfig { Enabled = false, VirtualKey = 0x51, Modifiers = 6 };
        Assert.Equal(SmartHotkey.HotkeyAction.None, SmartHotkey.MatchHotkey(cfg, 0x51, 6, isMouse: false));
        Assert.Equal(SmartHotkey.HotkeyAction.None, SmartHotkey.MatchHotkey(null, 0x51, 6, isMouse: false));
    }
}
