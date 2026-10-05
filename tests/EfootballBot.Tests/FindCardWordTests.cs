using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public class FindCardWordTests
{
    private static OcrResult Result(params OcrWord[] words) => new(words, 1000, 1000);

    [Fact]
    public void ExactRawWord_Wins()
    {
        var ocr = Result(
            new OcrWord("赛程表", 0.05, 0.50, 0.20, 0.05),
            new OcrWord("兑换积分", 0.62, 0.40, 0.22, 0.05));

        var w = BotEngine.FindCardWord(ocr, "兑换积分");

        Assert.NotNull(w);
        Assert.Equal("兑换积分", w!.Text);
        Assert.InRange(w.CenterX, 0.70, 0.76); // 右卡真实中心，不被左卡拖走
    }

    [Fact]
    public void SplitCharsAcrossTwoCards_ReturnsRightCardBox_NotMergedLine()
    {
        // 真机故障：同 y 高度「赛程表」与右侧「兑换积分」被行合并成横跨两卡的假词，
        // 中心落在左半屏（~0.31），导航因此只上下移动、永不右移。
        var ocr = Result(
            new OcrWord("赛程表", 0.05, 0.48, 0.20, 0.05),
            new OcrWord("兑", 0.62, 0.48, 0.05, 0.05),
            new OcrWord("换", 0.67, 0.48, 0.05, 0.05),
            new OcrWord("积", 0.72, 0.48, 0.05, 0.05),
            new OcrWord("分", 0.77, 0.48, 0.05, 0.05));

        // 确认行合并确实产生了横跨假词（测试前置条件）
        Assert.Contains(ocr.MergedWords, w => w.Text == "赛程表兑换积分");

        var w = BotEngine.FindCardWord(ocr, "兑换积分");

        Assert.NotNull(w);
        Assert.True(w!.X >= 0.60, $"词框左缘应在右卡（实际 {w.X}），不能取整行合并词");
        Assert.InRange(w.CenterX, 0.70, 0.76);
    }

    [Fact]
    public void NotFound_ReturnsNull()
    {
        var ocr = Result(new OcrWord("前往比赛", 0.05, 0.10, 0.20, 0.05));
        Assert.Null(BotEngine.FindCardWord(ocr, "兑换积分"));
    }

    [Fact]
    public void ShortestContainingWord_PreferredOverLongGluedLine()
    {
        // 原始词里同时有干净词与粘连长句时取最短
        var ocr = Result(
            new OcrWord("兑换积分兑换说明", 0.10, 0.30, 0.50, 0.05),
            new OcrWord("兑换积分", 0.60, 0.30, 0.20, 0.05));

        var w = BotEngine.FindCardWord(ocr, "兑换积分");

        Assert.NotNull(w);
        Assert.Equal("兑换积分", w!.Text);
        Assert.Equal(0.20, w.W, 3);
    }
}
