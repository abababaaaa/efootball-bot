using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 统计训练集 val 帧：各界面分类下 OCR 是否出现"我的联赛"——
// 用于确认"OCR 含我的联赛 → MatchHub"判定的误伤面
public class ValFrameOcrSurvey
{
    private readonly ITestOutputHelper _out;
    public ValFrameOcrSurvey(ITestOutputHelper output) => _out = output;

    private static string RepoDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");
    private static string ValDir => Path.Combine(RepoDir, "training", "dataset", "images", "val");

    [Fact]
    public void Survey_ContainsMyLeague_ByScreen()
    {
        if (!Directory.Exists(ValDir)) { _out.WriteLine("no val dir, skip"); return; }
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var counts = new Dictionary<MlScreen, (int Total, int ContainsMl, int ContainsMatch)>();
        foreach (var img in Directory.GetFiles(ValDir, "*.jpg").Take(60))
        {
            using var mat = Cv2.ImRead(img, ImreadModes.Color);
            if (mat.Empty()) continue;
            using var bgra = new Mat();
            Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
            int byteCount = (int)(bgra.Total() * bgra.ElemSize());
            var data = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
            var frame = new FrameData(data, mat.Width, mat.Height, 0);
            var dets = det.Detect(frame);
            var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
            var screen = MyLeagueBrain.Classify(dets, ocrRes);
            bool ml = ocrRes.ContainsJoined("我的联赛");
            bool match = ocrRes.ContainsJoined("比赛");
            var (t, c, m) = counts.TryGetValue(screen, out var v) ? v : (0, 0, 0);
            counts[screen] = (t + 1, c + (ml ? 1 : 0), m + (match ? 1 : 0));
            _out.WriteLine($"==== {Path.GetFileName(img)} -> {screen} ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"    \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        }
        _out.WriteLine("-- summary: screen (total, contains我的联赛, contains比赛) --");
        foreach (var (k, v) in counts)
            _out.WriteLine($"  {k}: {v.Total}, {v.ContainsMl}, {v.ContainsMatch}");
    }
}
