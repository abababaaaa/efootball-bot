using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 国际服活动列表（EventHub）识别回归（ProbeEngine 版）：真机故障为收拢后活动卡文字
/// OCR 全失败，只命中单个 Tab 弱词 → Unknown → B 退回 → 重导死循环。
/// 两个一级 Tab（对阵AI + 对阵玩家）同时可见即可识别。
/// </summary>
public sealed class EventHubClassifierTests
{
    private static Observation Obs(params (string text, double x, double y)[] words)
        => new(new FrameData(Array.Empty<byte>(), 1280, 720, 0),
               new OcrResult(words.Select(w => new OcrWord(w.text, w.x, w.y, 0.12, 0.04)).ToList(), 1280, 720),
               GameScreen.Unknown, 0, Array.Empty<string>());

    [Fact]
    public void BothTabsVisible_RecognizesEventHub_EvenWithoutCardWords()
    {
        var o = Obs(("对阵AI", 0.10, 0.10), ("对阵玩家", 0.30, 0.10));
        Assert.Equal(GameScreen.EventHub, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void OnlyPlayerTab_FallsBackToUnknown()
    {
        var o = Obs(("对阵玩家", 0.30, 0.10));
        Assert.Equal(GameScreen.Unknown, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void PlayerEmptyPage_ArbitratesToNoEvent()
    {
        // 对阵玩家空页 Tab 行也外露，但「没有正在进行」强特征存在时必须判 NoEvent（P100）
        var o = Obs(
            ("对阵AI", 0.10, 0.10),
            ("对阵玩家", 0.30, 0.10),
            ("没有正在进行的对阵玩家活动", 0.20, 0.40));
        Assert.Equal(GameScreen.NoEvent, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }
}
