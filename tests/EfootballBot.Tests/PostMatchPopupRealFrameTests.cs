using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 分析/回归：赛后弹窗（今天的进球 / 比赛回放）截图 → OCR + YOLO → 当前分类行为
public class PostMatchPopupRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public PostMatchPopupRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_PostMatch_Popups()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        foreach (var name in new[] { "postmatch_popup_1.png", "postmatch_popup_2.png" })
        {
            string img = Path.Combine(TestDir, "TestData", name);
            if (!File.Exists(img)) { _out.WriteLine($"no fixture {name}"); continue; }
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

            _out.WriteLine($"==== {name} ({mat.Width}x{mat.Height}) ====");
            _out.WriteLine("-- YOLO --");
            foreach (var d in dets.Where(d => d.Score >= 0.2).OrderByDescending(d => d.Score))
                _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.X:F2},{d.Y:F2},{d.W:F2},{d.H:F2})");
            _out.WriteLine("-- OCR words --");
            foreach (var w in ocrRes.Words)
                _out.WriteLine($"  \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
            _out.WriteLine("-- OCR lines --");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");
            _out.WriteLine("-- Classify(无角区) / Classify(角区有内容) --");
            _out.WriteLine($"  {MyLeagueBrain.Classify(dets, ocrRes)} / {MyLeagueBrain.Classify(dets, ocrRes, true)}");

            // 回归：两种弹窗即使背景是球场（角区有内容）也必须立刻判 HasConfirm → 按 A 下一步
            Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(dets, ocrRes, topRightHasContent: true));
        }
    }
}
