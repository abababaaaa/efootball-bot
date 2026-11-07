using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 回归：2026-11-07 反馈包「右下角的下一步点不到」的实机卡死帧
// （白银联赛 / 天梯积分 1500 / 1600（▲升级）/ 右下「C 下一步」）。
// 这一页虚拟手柄 A/B 都不生效（A×5 + B×6 画面一动不动），只有鼠标点右下按钮能推进，
// 所以必须先判成 RankProgress 才能走到鼠标分支——判成 HasConfirm 就还是继续空按 A。
public class RankProgressRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public RankProgressRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_RankProgress_StuckFrame()
    {
        string img = Path.Combine(TestDir, "TestData", "rank_progress_20261107.png");
        var data = TestFrameLoader.LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);

        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        _out.WriteLine($"==== rank_progress_20261107.png ({w}x{h}) ====");
        _out.WriteLine("-- YOLO --");
        foreach (var d in dets.Where(d => d.Score >= 0.2).OrderByDescending(d => d.Score))
            _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.CenterX:F3},{d.CenterY:F3})");
        _out.WriteLine("-- OCR words --");
        foreach (var word in ocrRes.Words)
            _out.WriteLine($"  \"{word.Text}\" @({word.CenterX:F3},{word.CenterY:F3})");
        _out.WriteLine("-- Classify --");
        _out.WriteLine($"  pl={PeakLadderBrain.Classify(ocrRes, dets)}  ml={MyLeagueBrain.Classify(dets, ocrRes)}");

        // 必须判成段位积分页（鼠标点击分支），不能再落回 HasConfirm 的盲按 A
        Assert.Equal(PlScreen.RankProgress, PeakLadderBrain.Classify(ocrRes, dets));

        // 点击锚点必须落在右下角按钮上：优先 OCR 的「下一步」词，其次右下 btn_confirm 框
        var next = PeakLadderBrain.FindAdvanceWord(ocrRes, "下一步");
        if (next is not null)
        {
            Assert.True(next.CenterX > 0.7 && next.CenterY > 0.8,
                $"「下一步」词位置异常：({next.CenterX:F3},{next.CenterY:F3})");
        }
        else
        {
            var box = PeakLadderBrain.FindBottomConfirm(dets);
            Assert.NotNull(box);
            Assert.True(box!.CenterX > 0.7 && box.CenterY > 0.75,
                $"右下确认框位置异常：({box.CenterX:F3},{box.CenterY:F3})");
        }
    }
}
