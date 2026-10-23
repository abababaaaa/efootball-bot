using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 赛后活动任务页（活动地图）实机截图回归：识别为地图/「机会交易」弹窗，
// 并读出左侧小图标旁的推进次数 x。截图来自用户 2026-10-22 实机（Halloween Campaign）。
public class CampaignMapRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public CampaignMapRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string WeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "weekly");

    private OcrResult OcrOf(string name, out FrameData frame)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(WeeklyDir, name), out int w, out int h);
        frame = new FrameData(data, w, h, 0);
        using var ocr = new OcrRecognizer();
        return TestOcr.Run(ocr, frame);
    }

    [Theory]
    [InlineData("campaign_map.png", PmScreen.Map)]
    [InlineData("campaign_map_after.png", PmScreen.Map)]
    [InlineData("campaign_popup.png", PmScreen.Popup)]
    public void Classify_Campaign_Frames(string name, PmScreen expected)
    {
        var ocr = OcrOf(name, out _);
        Assert.Equal(expected, PostMatchActivityBrain.Classify(ocr));
    }

    // 实机 2026-10-23 反馈包：活动列表卡的奖励栏就印着「机会交易」，被误判成弹窗后
    // ↑+A 把光标顶上页签行、切到「对阵玩家」。列表/主页帧必须判 None。
    // vsplayers 帧即反馈包「手动停止」原截图（GDI 降质捕获，切到对阵玩家页签后的现场）。
    [Theory]
    [InlineData("weekly_events_list.png")]
    [InlineData("weekly_events_list_vsplayers.png")]
    [InlineData("weekly_event_home.png")]
    public void Classify_EventPages_AreNotPopup(string name)
    {
        var ocr = OcrOf(name, out _);
        Assert.Equal(PmScreen.None, PostMatchActivityBrain.Classify(ocr));
    }

    [Theory]
    [InlineData("campaign_map.png")]
    [InlineData("campaign_map_after.png")]
    public void CountCrop_ReadsOne(string name)
    {
        // 全帧 OCR 会把图标旁的数字并进图标读成「C'」，裁切放大后应稳定读出 1
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(WeeklyDir, name), out int w, out int h);
        var crop = ImageScaling.CropUpscale(new FrameData(data, w, h, 0), 0.03, 0.20, 0.22, 0.33, 8);
        using var ocr = new OcrRecognizer();
        var res = TestOcr.Run(ocr, crop);
        foreach (var wd in res.Words) _out.WriteLine($"  {wd.Text}");
        Assert.Contains(res.Words, wd => wd.NormText.Contains('1'));
    }
}
