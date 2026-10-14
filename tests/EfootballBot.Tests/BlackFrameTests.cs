using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

// DXGI 启动黑帧校验的采样判定
public class BlackFrameTests
{
    [Fact]
    public void AllZeroFrame_IsMostlyBlack()
    {
        var f = new FrameData(new byte[1280 * 720 * 4], 1280, 720, 0);
        Assert.True(BotEngine.IsMostlyBlack(f));
    }

    [Fact]
    public void ContentFrame_NotMostlyBlack()
    {
        var px = new byte[1280 * 720 * 4];
        // 每 16 像素一个亮蓝像素（>1% 采样点），其余黑
        for (int i = 0; i < px.Length; i += 16 * 4)
        {
            px[i] = 255;     // B
            px[i + 1] = 128; // G
            px[i + 2] = 64;  // R
        }
        Assert.False(BotEngine.IsMostlyBlack(new FrameData(px, 1280, 720, 0)));
    }

    [Fact]
    public void NearBlackFrame_IsMostlyBlack()
    {
        // 全部像素 RGB=4（低于阈值 8）仍算黑
        var px = new byte[100 * 100 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 4; px[i + 1] = 4; px[i + 2] = 4;
        }
        Assert.True(BotEngine.IsMostlyBlack(new FrameData(px, 100, 100, 0)));
    }
}
