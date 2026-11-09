using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 回归：2026-11-08 国际服周活动新子内容（「Road to CAF AFCON 2027」球队选择前置流程）。
// 介绍页按进入 →「选择地区」→「选择球队」→「要使用<队名>吗？」确认弹窗 → 正常挑战清单。
// 三张用户实机帧锁定分类，防这三页重新掉回 Unknown 原地干等。
public class WkTeamPickRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public WkTeamPickRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
    private static string WeeklyDir => Path.Combine(TestDir, "TestData", "weekly");

    [Theory]
    [InlineData("wk_teampick_region.png", WkScreen.RegionSelect)]
    [InlineData("wk_teampick_team.png", WkScreen.TeamSelect)]
    [InlineData("wk_teampick_confirm.png", WkScreen.TeamConfirm)]
    public void Classify_TeamPickPages(string file, WkScreen expected)
    {
        var path = Path.Combine(WeeklyDir, file);
        Assert.True(File.Exists(path), $"缺少实机帧: {path}");

        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);

        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);
        var wk = WeeklyBrain.Classify(dets, ocrRes,
            MyLeagueScenario.CornerHasContent(frame), matchContext: false,
            MyLeagueScenario.IsMostlyLightPage(frame));

        _out.WriteLine($"==== {file} ({w}x{h}) -> wk={wk}（期望 {expected}） ====");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(expected, wk);
    }

    [Fact]
    public void ConfirmPopup_IsNotClassifiedAsEntryPopup()
    {
        // 确认弹窗的蓝色「取消」会被 YOLO 判 btn_confirm：必须仍归 TeamConfirm（上→A），
        // 不能落到 EntryPopup 的「盲按 A」——A 落在取消上会把选择流程整个取消掉
        var path = Path.Combine(WeeklyDir, "wk_teampick_confirm.png");
        if (!File.Exists(path)) { _out.WriteLine("缺少实机帧，跳过"); return; }

        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);
        var dets = new[]
        {
            new YoloDetection("btn_confirm", 0, 0.95f, 0.35, 0.48, 0.30, 0.08),
        };
        var wk = WeeklyBrain.Classify(dets, ocrRes, false, false, false);
        Assert.Equal(WkScreen.TeamConfirm, wk);
    }
}
