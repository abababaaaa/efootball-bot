using Windows.Graphics;
using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

public class WindowCaptureCapSizeTests
{
    [Fact]
    public void CapSize_HeightAtOrBelow720_KeepsOriginal()
    {
        SizeInt32 s = WindowCapture.CapSize(1280, 720);
        Assert.Equal(1280, s.Width);
        Assert.Equal(720, s.Height);
    }

    [Fact]
    public void CapSize_FullHd_ScalesTo1280x720()
    {
        SizeInt32 s = WindowCapture.CapSize(1920, 1080);
        Assert.Equal(1280, s.Width);
        Assert.Equal(720, s.Height);
    }

    [Fact]
    public void CapSize_Qhd2560x1440_ScalesTo1280x720()
    {
        // 本次故障尺寸：全屏切换后封顶尺寸必须与窗口化时相同，避免无谓重建
        SizeInt32 s = WindowCapture.CapSize(2560, 1440);
        Assert.Equal(1280, s.Width);
        Assert.Equal(720, s.Height);
    }

    [Fact]
    public void CapSize_Ultrawide3440x1440_KeepsAspectRatio()
    {
        SizeInt32 s = WindowCapture.CapSize(3440, 1440);
        Assert.Equal(1720, s.Width);
        Assert.Equal(720, s.Height);
    }

    [Fact]
    public void CapSize_DegenerateInput_ClampsToMinimum2()
    {
        SizeInt32 s = WindowCapture.CapSize(0, 0);
        Assert.Equal(2, s.Width);
        Assert.Equal(2, s.Height);
    }

    [Fact]
    public void Downscale_PassthroughWhenSmall()
    {
        byte[] src = new byte[2 * 2 * 4];
        var dst = WindowCapture.DownscaleIfNeeded(src, 2, 2, out int dw, out int dh);
        Assert.Same(src, dst);
        Assert.Equal(2, dw); Assert.Equal(2, dh);
    }

    [Fact]
    public void NearestSample_2xHorizontal_RightHalfEntersOutput()
    {
        // 回归：WGC 小帧池输出的每一列都来自左上区域（右半屏被裁）。
        // 重采样后右半个输出必须采样自源右半屏，而不是重复左半屏。
        byte[] src = new byte[4 * 1 * 4];
        src[0] = 11; src[3] = 255;                    // 源列0 蓝 11
        src[4] = 11; src[7] = 255;                    // 源列1 蓝 11
        src[8] = 22; src[11] = 255;                   // 源列2 绿 22
        src[12] = 22; src[15] = 255;                  // 源列3 绿 22

        var dst = WindowCapture.NearestSample(src, 4, 1, 2, 1);

        Assert.Equal(2 * 1 * 4, dst.Length);
        Assert.Equal(11, dst[0]);                 // 输出左 ← 源左半
        Assert.Equal(22, dst[4]);                 // 输出右 ← 源右半（裁切时这里会是 11）
    }

    [Theory]
    [InlineData(1296, 759, 1282, 752, true)]   // Win10 item.Size 含不可见边框 vs 实际内容（2026-10-15 用户实机）
    [InlineData(1296, 759, 1264, 727, true)]   // 差值恰在 32px 容差内
    [InlineData(1296, 759, 1296, 759, true)]   // 相同尺寸（理论上游已短路，仍应判定为无需重建）
    [InlineData(1296, 759, 1920, 1080, false)] // 内容超出帧池（窗口化→全屏）必须重建
    [InlineData(1296, 759, 1200, 700, false)]  // 内容远小于帧池（真窗口缩放）必须重建
    [InlineData(1296, 759, 1282, 700, false)]  // 单方向超差也必须重建
    public void IsBorderOnlyContent_ClassifiesWin10BorderPhantom(
        int poolW, int poolH, int cw, int ch, bool expected)
    {
        Assert.Equal(expected, WindowCapture.IsBorderOnlyContent(poolW, poolH, cw, ch));
    }
}
