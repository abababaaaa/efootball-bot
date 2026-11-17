using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 判定走查（手动停止帧）：把一张实机帧喂进真实管线，打印完整判定链
/// （探针 → OCR 行 → 界面 → 动作）。诊断用，不做断言。
/// 用法：设置环境变量 RECOG_WALK_FRAME=图片路径 后运行本用例。
/// </summary>
public class RecogWalkProbe
{
    private readonly ITestOutputHelper _out;
    public RecogWalkProbe(ITestOutputHelper output) => _out = output;

    private static string RepoDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    [Fact]
    public void Walk_ManualStopFrame()
    {
        var env = Environment.GetEnvironmentVariable("RECOG_WALK_FRAME");
        var img = !string.IsNullOrWhiteSpace(env) && File.Exists(env)
            ? env
            : Path.Combine(RepoDir, "publish", "data", "ManualStopShots", "game_20261111_163142.png");
        _out.WriteLine($"帧：{img}（存在={File.Exists(img)}）");
        if (!File.Exists(img)) return;

        var data = TestFrameLoader.LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        _out.WriteLine($"尺寸：{w}x{h}");

        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle);

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        _out.WriteLine($"YOLO：{dets.Count} 框");
        foreach (var d in dets) _out.WriteLine($"    {d.ClassName} {d.Score:F2} @({d.CenterX:F3},{d.CenterY:F3})");
        _out.WriteLine($"OCR：{ocrRes.Words.Count} 词 / {ocrRes.Lines.Count} 行");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"    行 [{l.Text}] @({l.CenterX:F3},{l.CenterY:F3}) h={l.H:F3}");

        _out.WriteLine($"探针：corner={MyLeagueScenario.CornerHasContent(frame)} light={MyLeagueScenario.IsMostlyLightPage(frame)}");

        var screen = MyLeagueBrain.Classify(dets, ocrRes);
        var screenCtx = MyLeagueBrain.Classify(dets, ocrRes, MyLeagueScenario.CornerHasContent(frame), true,
            MyLeagueScenario.IsMostlyLightPage(frame), false);
        _out.WriteLine($"判定：默认探针={screen} / 完整探针={screenCtx}");

        foreach (var s in new[] { screen, screenCtx })
        {
            var a = MyLeagueBrain.Decide(s, ocrRes, 0, inMatchRecently: false, frame: frame);
            _out.WriteLine($"动作[{s}]：{a.Kind} wait={a.WaitMs} target={a.Target}");
        }
    }
}
