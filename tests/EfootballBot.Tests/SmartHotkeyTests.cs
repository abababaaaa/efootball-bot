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

    [Fact]
    public void ShouldAcceptKeyDown_SuppressesAutoRepeat_RecoversAfterMissedKeyUp()
    {
        int held = 0;
        bool qPhysicallyDown = true;

        // 首次按下 Q 放行，持续按住产生系统连发时抑制
        Assert.True(SmartHotkey.ShouldAcceptKeyDown(0x51, ref held, _ => qPhysicallyDown));
        Assert.False(SmartHotkey.ShouldAcceptKeyDown(0x51, ref held, _ => qPhysicallyDown));

        // 模拟在提权窗口丢失 WM_KEYUP，但物理按键实际已松开 → 下次再按 Q 自动恢复
        qPhysicallyDown = false;
        Assert.True(SmartHotkey.ShouldAcceptKeyDown(0x51, ref held, _ => qPhysicallyDown));
    }
}
