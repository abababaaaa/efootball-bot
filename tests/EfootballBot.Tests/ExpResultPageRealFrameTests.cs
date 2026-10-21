using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 实机 2026-10-17：赛后「经验值」页（4 名球员获得经验，2×2 网格，底部「Y 查看经验值」+「下一步」）。
// 固化：页面判定为真 + 球员数=4（>3 不触发「大部分球员已满级」日志）。OCR 词与坐标全部打印，
// 判定偏差时可直接据此调参。
public class ExpResultPageRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public ExpResultPageRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void ExpResultPage_Detected_AndCountsFourPlayers()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        string img = Path.Combine(TestDir, "TestData", "myleague_exp_result_page.png");
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {img}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        var ml = MyLeagueBrain.Classify(dets, ocrRes,
            MyLeagueScenario.CornerHasContent(frame), false, MyLeagueScenario.IsMostlyLightPage(frame));
        _out.WriteLine($"MlScreen={ml}");
        _out.WriteLine("-- OCR lines --");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");
        _out.WriteLine("-- 等级/加成 words --");
        foreach (var w in ocrRes.Words.Where(w =>
                     OcrText.Norm(w.Text).Contains("等级") || OcrText.Norm(w.Text).Contains("加成")))
            _out.WriteLine($"  \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");

        Assert.True(MyLeagueBrain.IsExpResultPage(ocrRes), "应识别为赛后经验值页");
        int n = MyLeagueBrain.CountExpResultPlayers(ocrRes);
        _out.WriteLine($"球员数={n}");
        Assert.Equal(4, n);
    }
}
