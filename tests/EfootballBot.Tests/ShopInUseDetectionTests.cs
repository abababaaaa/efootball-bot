using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 商店「使用中」判定回归（真机 2026-10-06）：「有效场数:5场」是道具时效说明，
/// 商店每一行都常驻（2x/3x/4x 均 5 场，弧线球 1 场），旧代码把它当在用信号，
/// 导致未生效的 4x 被误跳。在用只认「使用中 / 已使用」。
/// </summary>
public sealed class ShopInUseDetectionTests
{
    private static OcrResult Result(params OcrWord[] words) => new(words, 1000, 1000);

    [Fact]
    public void OnlyEffectiveMatches_NotInUse()
    {
        var ocr = Result(
            new OcrWord("经验值:4x", 0.25, 0.58, 0.10, 0.04),
            new OcrWord("有效场数:5场", 0.25, 0.63, 0.12, 0.03));

        Assert.False(MyLeagueScenario.IsInUse(ocr));
    }

    [Fact]
    public void InUseKeyword_IsInUse()
    {
        var ocr = Result(new OcrWord("使用中", 0.25, 0.63, 0.08, 0.03));

        Assert.True(MyLeagueScenario.IsInUse(ocr));
    }

    [Fact]
    public void AlreadyUsedKeyword_IsInUse()
    {
        var ocr = Result(new OcrWord("已使用", 0.25, 0.63, 0.08, 0.03));

        Assert.True(MyLeagueScenario.IsInUse(ocr));
    }
}
