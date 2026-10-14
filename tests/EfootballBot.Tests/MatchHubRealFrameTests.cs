using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 分析/回归：用户实机比赛选择页截图 → OCR + YOLO → Classify/Decide
// 用于验证 MatchHub 判定与"右移选择我的联赛"导航
public class MatchHubRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public MatchHubRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..");

    private static byte[] LoadPngBgra(string file, out int w, out int h)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        w = mat.Width; h = mat.Height;
        return data;
    }

    [Fact]
    public void Analyze_MatchHub_Frames()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer(); // RapidV6

        // 帧1：进入比赛页默认焦点（不在我的联赛）→ 判 MatchHub，决策右移
        var (screen1, action1, dets1, ocr1) = Run(det, ocr, "matchhub_user_1.png");
        Assert.Equal(MlScreen.MatchHub, screen1);
        Assert.Equal(MlActionKind.PadMoveRight, action1.Kind);

        // 帧2：右移后焦点在我的联赛（预览区文字）→ 判 MatchHub，决策 A 进入
        var (screen2, action2, dets2, ocr2) = Run(det, ocr, "matchhub_user_2.png");
        Assert.Equal(MlScreen.MatchHub, screen2);
        Assert.Equal(MlActionKind.PadConfirm, action2.Kind);

        _out.WriteLine($"frame1: {screen1} -> {action1.Kind}");
        foreach (var d in dets1.Where(d => d.Score >= MyLeagueBrain.MinScore))
            _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2}");
        _out.WriteLine($"frame2: {screen2} -> {action2.Kind}");
        foreach (var d in dets2.Where(d => d.Score >= MyLeagueBrain.MinScore))
            _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2}");
    }

    private (MlScreen Screen, MlAction Action, IReadOnlyList<YoloDetection> Dets, OcrResult Ocr) Run(
        YoloDetector det, OcrRecognizer ocr, string name)
    {
        var data = LoadPngBgra(Path.Combine(TestDir, "TestData", name), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var dets = det.Detect(frame);
        var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
        var screen = MyLeagueBrain.Classify(dets, ocrRes);
        var action = MyLeagueBrain.Decide(screen, ocrRes, 0);
        return (screen, action, dets, ocrRes);
    }
}
