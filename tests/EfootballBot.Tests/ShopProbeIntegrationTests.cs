using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// ProbeEngine 端到端回归（真机 2026-10-06）：商店「我的联赛物品」页右上角
/// 余额白字不再让 InMatch 成候选；本帧应仲裁为 Shop。
/// </summary>
public sealed class ShopProbeIntegrationTests
{
    private const int W = 1280, H = 720;

    [Fact]
    public void ShopItemFrame_ClassifiedAsShop_NotInMatch()
    {
        var bgra = new byte[W * H * 4]; // 全黑（商店底色；草皮绿占比≈0）
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        // 右上角余额白字（落在机器人 ROI，x 0.86..0.995, y 0.005..0.075）
        for (int y = 4; y < 44; y++)
            for (int x = 1120; x < 1260; x++)
            {
                int i = (y * W + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
            }

        // 真机该帧 20+ OCR 词；列足 8 个即触发词数护栏
        var words = new List<OcrWord>
        {
            new("我的联赛物品", 0.24, 0.11, 0.20, 0.06),
            new("经验值:4x", 0.25, 0.58, 0.10, 0.04),
            new("有效场数:5场", 0.25, 0.63, 0.12, 0.03),
        };
        for (int i = 0; i < 8; i++)
            words.Add(new OcrWord($"w{i}", 0.05 + 0.08 * i, 0.30, 0.05, 0.04));

        var obs = new Observation(new FrameData(bgra, W, H, 1),
            new OcrResult(words, W, H), GameScreen.Home, 0, Array.Empty<string>());

        var result = new ProbeEngine(PageCatalog.Pages).Classify(obs);

        Assert.Equal(GameScreen.Shop, result.Screen);
    }
}
