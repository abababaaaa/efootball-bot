using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 国际服活动列表（EventHub）识别回归：真机故障为收拢后活动卡文字 OCR 全失败，
/// 旧目录只命中「对阵玩家」1.5 分 → Unknown → B 退回 → 重导死循环。
/// 修复后两个一级 Tab（对阵AI + 对阵玩家）同时可见即可识别。
/// </summary>
public sealed class EventHubClassifierTests
{
    private static OcrResult Ocr(params (string text, double x, double y)[] words)
        => new(words.Select(w => new OcrWord(w.text, w.x, w.y, 0.12, 0.04)).ToList(), 1280, 720);

    [Fact]
    public void BothTabsVisible_RecognizesEventHub_EvenWithoutCardWords()
    {
        var ocr = Ocr(("对阵AI", 0.10, 0.10), ("对阵玩家", 0.30, 0.10));
        var (screen, score, _) = new ScreenClassifier().Classify(ocr, null);
        Assert.Equal(GameScreen.EventHub, screen);
        // 原始词与同行合并词各计一次：2.5+1.5 双倍 = 8
        Assert.Equal(8.0, score);
    }

    [Fact]
    public void OnlyPlayerTab_FallsBackToUnknown()
    {
        var ocr = Ocr(("对阵玩家", 0.30, 0.10));
        var (screen, _, _) = new ScreenClassifier().Classify(ocr, null);
        Assert.Equal(GameScreen.Unknown, screen);
    }

    [Fact]
    public void PlayerEmptyPage_ArbitratesToNoEvent()
    {
        // 对阵玩家空页 Tab 行也外露，但「没有正在进行」强特征存在时必须判 NoEvent
        var ocr = Ocr(
            ("对阵AI", 0.10, 0.10),
            ("对阵玩家", 0.30, 0.10),
            ("没有正在进行的对阵玩家活动", 0.20, 0.40));
        var (screen, _, _) = new ScreenClassifier().Classify(ocr, null);
        Assert.Equal(GameScreen.NoEvent, screen);
    }
}
