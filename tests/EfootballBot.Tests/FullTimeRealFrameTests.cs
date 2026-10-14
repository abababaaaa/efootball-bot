using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 分析/回归：全场结束（FullTime）界面截图 → OCR + YOLO → 终场判定
public class FullTimeRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public FullTimeRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_FullTime_Frame()
    {
        string img = Path.Combine(TestDir, "TestData", "fulltime_user_1.png");
        if (!File.Exists(img)) { _out.WriteLine("no fixture, skip"); return; }
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {img}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var dets = det.Detect(frame);
        var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();

        _out.WriteLine($"==== fulltime_user_1.png ({mat.Width}x{mat.Height}) ====");
        _out.WriteLine("-- YOLO --");
        foreach (var d in dets.Where(d => d.Score >= 0.2).OrderByDescending(d => d.Score))
            _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.X:F2},{d.Y:F2},{d.W:F2},{d.H:F2})");
        _out.WriteLine("-- OCR words --");
        foreach (var w in ocrRes.Words)
            _out.WriteLine($"  \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine("-- OCR lines --");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");
        _out.WriteLine("-- Classify(角区有内容) --");
        _out.WriteLine($"  {MyLeagueBrain.Classify(dets, ocrRes, true)}");

        // 回归：全场结束页必须判 FullTime（脱离比赛中），即使右上角有内容/残留比分词
        Assert.Equal(MlScreen.FullTime, MyLeagueBrain.Classify(dets, ocrRes, topRightHasContent: true));
        // 决策：立即按 A（下一步）
        var action = MyLeagueBrain.Decide(MlScreen.FullTime, ocrRes, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }
}
