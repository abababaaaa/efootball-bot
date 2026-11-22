using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// OcrWord 规范化文本缓存契约：结果必须与 OcrText.Norm 完全一致（缓存只省重复计算，
/// 不允许改变任何判定结果），且同一词多次读取返回同一实例。
/// </summary>
public class OcrWordNormTests
{
    [Theory]
    [InlineData("前往比賽")]
    [InlineData(" 比 赛 回 放 ")]
    [InlineData("Trendyol")]
    [InlineData("组别9")]
    public void NormText_EqualsNorm(string raw)
    {
        var w = new OcrWord(raw, 0, 0, 1, 1);
        Assert.Equal(OcrText.Norm(raw), w.NormText);
        Assert.Same(w.NormText, w.NormText); // 缓存命中：同一实例
    }

    [Fact]
    public void NormText_FeedsProbes()
    {
        // 探针链与缓存结果一致：「前往比賽」经单字映射后可被整词探针命中
        var ocr = new OcrResult([new OcrWord("前往比賽", 0.4, 0.5, 0.2, 0.05)], 1280, 720);
        Assert.True(ocr.Contains("前往比赛"));
        Assert.True(ocr.ContainsJoined("前往比赛"));
    }

    [Fact]
    public void FindButton_PrefersHigherConfidence_WhenMultipleHits()
    {
        // 同一关键词命中多处时，取置信度更高的（如弹窗按钮 vs 背景文字）
        var ocr = new OcrResult([
            new OcrWord("确定", 0.1, 0.2, 0.08, 0.04, Confidence: 0.32),
            new OcrWord("确定", 0.7, 0.8, 0.08, 0.04, Confidence: 0.96),
        ], 1280, 720);
        var hit = ocr.FindButton("确定");
        Assert.NotNull(hit);
        Assert.Equal(0.7, hit.X, 5); // 命中高置信度那个（靠右下）
    }

    [Fact]
    public void FindButton_KeepsFirstHit_WhenConfidenceTied()
    {
        // 兼容契约：置信度相同（含默认 1.0）时稳定返回首个命中，既有行为不变
        var ocr = new OcrResult([
            new OcrWord("确定", 0.1, 0.2, 0.08, 0.04),
            new OcrWord("确定", 0.7, 0.8, 0.08, 0.04),
        ], 1280, 720);
        var hit = ocr.FindButton("确定");
        Assert.NotNull(hit);
        Assert.Equal(0.1, hit.X, 5); // 仍是第一个
    }
}
