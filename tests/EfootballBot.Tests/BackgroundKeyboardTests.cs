using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

// 后台键盘：WM_KEYDOWN/WM_KEYUP 的 lParam 位域（引擎靠扫描码识别按键，缺了它会当无效消息）
public class BackgroundKeyboardTests
{
    [Fact]
    public void KeyLParam_EncodesRepeatScanCodeAndUpBit()
    {
        const int vkC = 0x43;
        long down = BackgroundKeyboard.KeyLParam(vkC, up: false).ToInt64();
        long up = BackgroundKeyboard.KeyLParam(vkC, up: true).ToInt64();

        Assert.Equal(1, (int)(down & 0xFFFF));            // 重复次数 = 1
        Assert.NotEqual(0, (int)((down >> 16) & 0xFF));   // 扫描码：C 键 = 0x2E，绝不能是 0
        Assert.Equal(0, (int)((down >> 30) & 1));         // 按下：bit30 = 0
        Assert.Equal(0, (int)((down >> 31) & 1));         // 按下：bit31 = 0
        Assert.Equal(1, (int)((up >> 30) & 1));           // 抬起：bit30 = 1
        Assert.Equal(1, (int)((up >> 31) & 1));           // 抬起：bit31 = 1
        Assert.Equal((down >> 16) & 0xFF, (up >> 16) & 0xFF); // 两次扫描码一致
    }
}
