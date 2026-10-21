using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 共享区域探针（<see cref="RegionProbe"/>）契约测试。
/// 这些方法此前在四个 Brain 里各有一份逐字相同的副本，统一到共享实现后
/// 必须锁死采样步长与颜色判据——它们直接决定各界面「灰锁/绿勾/白卡」的判定结果，
/// 一旦步长或通道判据被改动，实机判定会整体漂移而没有任何编译错误。
/// </summary>
public class RegionProbeTests
{
    /// <summary>构造纯色帧（BGRA）。</summary>
    private static FrameData Solid(int w, int h, byte b, byte g, byte r)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
        }
        return new FrameData(buf, w, h, 0);
    }

    [Fact]
    public void CountGreen_CountsGreenPixelsOnly()
    {
        // 纯绿 (0,200,0) 全帧 → 每 2px 采样落入区域内的点都命中
        var green = Solid(200, 200, 0, 200, 0);
        int counted = RegionProbe.CountGreen(green, 0, 1, 0, 1);
        // 200/2 × 200/2 = 10000 个采样点
        Assert.Equal(10000, counted);

        // 纯白、纯灰、纯红都不该命中
        Assert.Equal(0, RegionProbe.CountGreen(Solid(200, 200, 255, 255, 255), 0, 1, 0, 1));
        Assert.Equal(0, RegionProbe.CountGreen(Solid(200, 200, 128, 128, 128), 0, 1, 0, 1));
        Assert.Equal(0, RegionProbe.CountGreen(Solid(200, 200, 0, 0, 200), 0, 1, 0, 1));
    }

    [Fact]
    public void CountGreen_RespectsRegionBounds()
    {
        var green = Solid(200, 200, 0, 200, 0);
        // 右半区、上半区 → 100×100 采样点
        Assert.Equal(2500, RegionProbe.CountGreen(green, 0.5, 1.0, 0.0, 0.5));
    }

    [Fact]
    public void CountGray_MatchesNeutralMidTonesOnly()
    {
        // 中灰 (128,128,128)：R≈G≈B 且亮度落 90~220 → 全命中
        Assert.Equal(10000, RegionProbe.CountGray(Solid(200, 200, 128, 128, 128), 0, 1, 0, 1));
        // 近黑（亮度 30）与近白（240）都出范围
        Assert.Equal(0, RegionProbe.CountGray(Solid(200, 200, 30, 30, 30), 0, 1, 0, 1));
        Assert.Equal(0, RegionProbe.CountGray(Solid(200, 200, 240, 240, 240), 0, 1, 0, 1));
        // 高饱和色（通道差 ≥30）不算灰
        Assert.Equal(0, RegionProbe.CountGray(Solid(200, 200, 120, 120, 200), 0, 1, 0, 1));
    }

    [Fact]
    public void MedianLuma_ReturnsMiddleValue()
    {
        // 纯色帧的中位数即该色亮度
        Assert.Equal(150, RegionProbe.MedianLuma(Solid(120, 120, 150, 150, 150), 0, 1, 0, 1));
    }

    [Fact]
    public void Probes_ReturnZeroOnTooSmallFrame()
    {
        var tiny = new FrameData(new byte[4 * 10 * 10], 10, 10, 0);
        Assert.Equal(0, RegionProbe.CountGreen(tiny, 0, 1, 0, 1));
        Assert.Equal(0, RegionProbe.CountGray(tiny, 0, 1, 0, 1));
        Assert.Equal(0, RegionProbe.MedianLuma(tiny, 0, 1, 0, 1));
    }

    [Fact]
    public void RefProbes_AreExactlyIdenticalAt1280x720()
    {
        // 720p 是标定基准：换算系数恰为 1.0，判定值必须逐像素一致（现有挂机路径零变化）
        var green = Solid(1280, 720, 0, 200, 0);
        Assert.Equal(RegionProbe.CountGreen(green, 0, 1, 0, 1), RegionProbe.CountGreenRef(green, 0, 1, 0, 1));

        var gray = Solid(1280, 720, 128, 128, 128);
        Assert.Equal(RegionProbe.CountGray(gray, 0, 1, 0, 1), RegionProbe.CountGrayRef(gray, 0, 1, 0, 1));
        Assert.Equal(1.0, RegionProbe.AreaFactor(green));
    }

    [Theory]
    [InlineData(1720, 720)] // 21:9 超宽
    [InlineData(1152, 720)] // 16:10
    [InlineData(960, 720)]  // 4:3
    public void RefProbes_ScaleByArea_OnNon16By9Frames(int w, int h)
    {
        var green = Solid(w, h, 0, 200, 0);
        int raw = RegionProbe.CountGreen(green, 0, 1, 0, 1);
        int scaled = RegionProbe.CountGreenRef(green, 0, 1, 0, 1);
        int expected = (int)Math.Round(raw * (1280.0 * 720.0) / ((double)w * h));
        Assert.Equal(expected, scaled);
        // 基准化之后，「同面积占比的同一块颜色」在不同宽高比下数出来是同一个值
        // （原始计数会随面积漂移，这正是归一化要消除的差异）
        double areaRatio = ((double)w * h) / (1280.0 * 720.0);
        Assert.InRange((double)scaled / expected, 0.999, 1.001);
        Assert.True(raw > 0);
        Assert.NotEqual(raw, (int)Math.Round(scaled * areaRatio) + 1);
    }

    [Fact]
    public void HasTopTabs_RequiresAllThreeTabs()    {
        OcrResult Frame(params string[] texts)
        {
            var words = texts.Select((t, i) =>
                new OcrWord(t, 0.1 * i, 0.05, 0.08, 0.04)).ToArray();
            return new OcrResult(words, 1280, 720);
        }

        Assert.True(RegionProbe.HasTopTabs(Frame("推荐", "签约", "商店")));
        Assert.False(RegionProbe.HasTopTabs(Frame("推荐", "签约")));

        // 页签之外的词不参与
        var low = new OcrResult(
        [
            new OcrWord("推荐", 0.1, 0.05, 0.08, 0.04),
            new OcrWord("签约", 0.2, 0.05, 0.08, 0.04),
            new OcrWord("商店", 0.3, 0.60, 0.08, 0.04),
        ], 1280, 720);
        Assert.False(RegionProbe.HasTopTabs(low));
    }
}
