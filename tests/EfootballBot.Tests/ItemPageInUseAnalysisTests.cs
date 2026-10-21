using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 物品页「使用中」（已购买变灰）截图：回归已拥有判定
public class ItemPageInUseAnalysisTests
{
    private readonly ITestOutputHelper _out;
    public ItemPageInUseAnalysisTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_InUse_Rows()
    {
        using var ocr = new OcrRecognizer();
        foreach (var name in new[] { "itempage_inuse_1.png", "itempage_inuse_2.png" })
        {
            string img = Path.Combine(TestDir, "TestData", name);
            if (!File.Exists(img)) { _out.WriteLine($"no fixture {name}"); continue; }
            using var mat = Cv2.ImRead(img, ImreadModes.Color);
            if (mat.Empty()) continue;
            using var bgra = new Mat();
            Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
            int byteCount = (int)(bgra.Total() * bgra.ElemSize());
            var data = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
            var ocrRes = TestOcr.Run(ocr, new FrameData(data, mat.Width, mat.Height, 0));

            _out.WriteLine($"==== {name} ({mat.Width}x{mat.Height}) ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");

            // 回归：「Exp经验值4x使用中」= 已拥有——任何档位需求（≤4x）都应判 InUse 跳过
            var row = MyLeagueBrain.FindItemRow(ocrRes,
                new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "2x"));
            Assert.NotNull(row);
            Assert.True(row.InUse);
        }
    }
}
