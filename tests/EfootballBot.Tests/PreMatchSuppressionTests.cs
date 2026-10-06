using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 直播帧识别回归（ProbeEngine 版，真机 2026-10-06）：直播帧右下记分牌蓝色让旧 PreMatch
/// 像素规则误判，冲破比赛迟滞导致每 ~10s 盲按 A。新引擎：顶部比赛时钟探针 → InMatch；
/// 草皮绿只是弱佐证，单独出现不得越过 InMatch 阈值。
/// </summary>
public sealed class PreMatchSuppressionTests
{
    private const int W = 1280, H = 720;

    private static void Fill(byte[] bgra, byte b, byte g, byte r)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
        }
    }

    private static void Paint(byte[] bgra,
        double x0, double y0, double w, double h, byte b, byte g, byte r)
    {
        int px0 = (int)(x0 * W), py0 = (int)(y0 * H);
        int px1 = (int)((x0 + w) * W), py1 = (int)((y0 + h) * H);
        for (int y = py0; y < py1; y++)
            for (int x = px0; x < px1; x++)
            {
                int i = (y * W + x) * 4;
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
    }

    [Fact]
    public void InMatchFrameWithClockAndBlueScoreboard_ClassifiedAsInMatch()
    {
        var bgra = new byte[W * H * 4];
        Fill(bgra, 52, 140, 72); // 草皮绿 RGB(72,140,52)
        Paint(bgra, 0.84, 0.89, 0.15, 0.07, 255, 0, 0); // 右下蓝色记分牌

        var ocr = new OcrResult(new[]
        {
            // 比赛时钟（冒号被 OCR 吞）：两词 x 间隙 0 ≤ 0.012
            new OcrWord("21", 0.46, 0.06, 0.03, 0.03),
            new OcrWord("34", 0.49, 0.06, 0.03, 0.03),
        }, W, H);
        var frame = new FrameData(bgra, W, H, 1);
        var obs = new Observation(frame, ocr, GameScreen.Unknown, 0, Array.Empty<string>());

        Assert.Equal(GameScreen.InMatch, new ProbeEngine(PageCatalog.Pages).Classify(obs).Screen);
    }

    [Fact]
    public void GreenFrameWithoutSignal_DoesNotFireInMatch()
    {
        // 入场过场满屏草皮绿但无时钟/机器人/回放信号时，不得单凭绿色判 InMatch
        var bgra = new byte[W * H * 4];
        Fill(bgra, 52, 140, 72);
        var frame = new FrameData(bgra, W, H, 1);
        var obs = new Observation(frame, new OcrResult(new List<OcrWord>(), W, H),
            GameScreen.Unknown, 0, Array.Empty<string>());

        Assert.NotEqual(GameScreen.InMatch, new ProbeEngine(PageCatalog.Pages).Classify(obs).Screen);
    }
}
