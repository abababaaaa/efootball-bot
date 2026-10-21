using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 分析：购买流程 8 张截图 → OCR + YOLO + 当前分类
public class PurchaseFlowAnalysisTests
{
    private readonly ITestOutputHelper _out;
    public PurchaseFlowAnalysisTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_Purchase_Flow()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var results = new MlScreen[9];

        for (int i = 1; i <= 8; i++)
        {
            string img = Path.Combine(TestDir, "TestData", $"purchase_flow_{i}.png");
            if (!File.Exists(img)) { _out.WriteLine($"no fixture {i}"); continue; }
            using var mat = Cv2.ImRead(img, ImreadModes.Color);
            if (mat.Empty()) continue;
            using var bgra = new Mat();
            Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
            int byteCount = (int)(bgra.Total() * bgra.ElemSize());
            var data = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
            var frame = new FrameData(data, mat.Width, mat.Height, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);

            _out.WriteLine($"==== purchase_flow_{i} ({mat.Width}x{mat.Height}) ====");
            _out.WriteLine($"YOLO: {string.Join(" ", dets.Where(d => d.Score >= 0.4).Select(d => $"{d.ClassName} {d.Score:F2}"))}");
            _out.WriteLine($"Classify: {MyLeagueBrain.Classify(dets, ocrRes, true)}");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");
            results[i] = MyLeagueBrain.Classify(dets, ocrRes, true);
        }

        // 回归（实机 8 截图）：关键页面分类
        Assert.Equal(MlScreen.LeaguePage, results[1]);      // 主页（挑战面板）
        Assert.Equal(MlScreen.LeaguePage, results[3]);      // 主页（使用的物品面板，右拨后）
        Assert.Equal(MlScreen.PointsPage, results[5]);      // 我的联赛积分页
        Assert.Equal(MlScreen.ItemList, results[6]);        // 我的联赛物品页
        Assert.Equal(MlScreen.PurchaseDialog, results[8]);  // 购买确认弹窗

        // 购买弹窗决策：上移确认（焦点默认在取消）
        var dlgAction = MyLeagueBrain.Decide(MlScreen.PurchaseDialog, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadUpConfirm, dlgAction.Kind);
    }
}
