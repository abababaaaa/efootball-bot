using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// PreMatch 像素硬规则回归（真机 2026-10-06）：直播帧右下记分牌蓝色让 bBtn≈0.62，
/// 旧规则把比赛帧覆盖判成 PreMatch（4 分），冲破比赛迟滞导致每 ~10s 盲按 A。
/// 修复后 PreMatch 硬规则要求本帧未命中 InMatch。
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
    public void InMatchFrameWithBlueScoreboard_NotClassifiedAsPreMatch()
    {
        var bgra = new byte[W * H * 4];
        Fill(bgra, 52, 140, 72); // 草皮绿 RGB(72,140,52)，hue≈106
        Paint(bgra, 0.84, 0.89, 0.15, 0.07, 255, 0, 0); // 右下蓝色记分牌 → bBtn≈1.0

        var ocr = new OcrResult(new[]
        {
            // 比赛时钟（冒号被 OCR 吞）：两词 x 间隙 0 ≤ 0.012
            new OcrWord("21", 0.46, 0.06, 0.03, 0.03),
            new OcrWord("34", 0.49, 0.06, 0.03, 0.03),
        }, W, H);
        var frame = new FrameData(bgra, W, H, 1);

        var (screen, _, _) = new ScreenClassifier().Classify(ocr, frame);

        Assert.Equal(GameScreen.InMatch, screen);
    }
}
