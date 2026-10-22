using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 赛后活动任务判定（纯 OCR 合成帧）：地图/弹窗分类 + 推进次数 x 读取
public class PostMatchActivityTests
{
    private static OcrWord W(string text, double cx, double cy, double h = 0.03)
        => new(text, cx - 0.05, cy - h / 2, 0.1, h);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    [Fact]
    public void Classify_Map_ByCurrentPositionAndNext()
    {
        var ocr = Ocr(W("奖励", 0.78, 0.08), W("下一步", 0.87, 0.83), W("当前位置", 0.58, 0.93));
        Assert.Equal(PmScreen.Map, PostMatchActivityBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_NeedsCurrentPosition()
    {
        // 只有「下一步」/「奖励」的普通推进页不是活动地图
        Assert.Equal(PmScreen.None, PostMatchActivityBrain.Classify(Ocr(W("下一步", 0.5, 0.6), W("奖励", 0.78, 0.08))));
    }

    [Fact]
    public void Classify_Popup_BeatsMapWords()
    {
        // 弹窗背景暗化后地图词可能仍被读到，弹窗必须优先
        var ocr = Ocr(W("当前位置", 0.58, 0.93), W("下一步", 0.87, 0.83),
            W("获取机会交易×1?", 0.5, 0.25), W("消耗1点地图前进步数以获得奖励。", 0.5, 0.56));
        Assert.Equal(PmScreen.Popup, PostMatchActivityBrain.Classify(ocr));
    }

    [Fact]
    public void ReadAdvanceCount_TakesIconRow_NotTotalRow()
    {
        // 图标计数行 1（y=0.284）在「总积分 1/9」行（y=0.354）上方；不得把总积分的 19 当次数
        var ocr = Ocr(
            W("活动期间:2026/10/2210:00", 0.21, 0.171, 0.025),
            W("总积分:1/9", 0.115, 0.354, 0.028),
            W("7", 0.088, 0.284, 0.049));
        Assert.Equal(7, PostMatchActivityBrain.ReadAdvanceCount(ocr));
    }

    [Fact]
    public void ReadAdvanceCount_ReturnsNull_WhenUnreadable()
    {
        var ocr = Ocr(W("总积分:1/9", 0.115, 0.354), W("C'", 0.089, 0.284, 0.049));
        Assert.Null(PostMatchActivityBrain.ReadAdvanceCount(ocr));
    }
}
