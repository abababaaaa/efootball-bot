using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// VisualProbe 护栏回归（真机 2026-10-06）：商店右上角「M 112,980」余额白字落在
/// InMatch 机器人图标 ROI 内，旧探针无护栏 → 5.2 分误判 InMatch。护栏：词数≤6、
/// 白占比≤0.6（旧分类器 Screens.cs 校准值）。
/// </summary>
public sealed class ProbeGuardTests
{
    private const int W = 1280, H = 720;
    private static readonly NormRect RobotRoi = new(0.86, 0.005, 0.135, 0.07);

    private static byte[] Blank()
    {
        var bgra = new byte[W * H * 4]; // B/G/R=0 黑底
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255; // Alpha
        return bgra;
    }

    private static void PaintWhite(byte[] bgra, double x0, double y0, double w, double h)
    {
        int px0 = (int)(x0 * W), py0 = (int)(y0 * H);
        int px1 = (int)((x0 + w) * W), py1 = (int)((y0 + h) * H);
        for (int y = py0; y < py1; y++)
            for (int x = px0; x < px1; x++)
            {
                int i = (y * W + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
            }
    }

    private static Observation Obs(byte[] bgra, int wordCount)
    {
        var words = Enumerable.Range(0, wordCount)
            .Select(i => new OcrWord("x", 0.01 * i, 0.95, 0.005, 0.02))
            .ToArray();
        return new Observation(new FrameData(bgra, W, H, 1), new OcrResult(words, W, H),
            GameScreen.Home, 0, Array.Empty<string>());
    }

    private static VisualProbe Probe() => new()
    {
        Filter = HsvFilter.White,
        Roi = RobotRoi,
        MinRatio = 0.015,
        Weight = 10,
        MaxOcrWords = 6,
        MaxRatio = 0.6,
    };

    [Fact]
    public void TooManyOcrWords_ScoresZero()
    {
        var bgra = Blank();
        PaintWhite(bgra, 0.86, 0.005, 0.04, 0.035); // ROI 内小块白，占比≈0.149

        Assert.Equal(0, Probe().Score(Obs(bgra, 7)));
    }

    [Fact]
    public void WhiteRatioAboveMax_ScoresZero()
    {
        var bgra = Blank();
        PaintWhite(bgra, 0.86, 0.005, 0.135, 0.07); // 整块 ROI 白 → 占比≈1

        Assert.Equal(0, Probe().Score(Obs(bgra, 2)));
    }

    [Fact]
    public void NormalMatchFrame_ScoresProportional()
    {
        var bgra = Blank();
        PaintWhite(bgra, 0.86, 0.005, 0.04, 0.035); // 占比≈0.149，在 [0.015, 0.6] 内

        double s = Probe().Score(Obs(bgra, 2));

        Assert.True(s is > 1.3 and < 1.7, $"实际得分 {s}");
    }
}
