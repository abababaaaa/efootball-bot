using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 比赛起始页回归（实机截图）：分类 + 决策 + 非起始页不误判
public class MatchStartPageTests
{
    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    [Fact]
    public void Classify_And_Decide_MatchStartPage()
    {
        string img = Path.Combine(TestDir, "TestData", "match_start_page.png");
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);

        Assert.Equal(MlScreen.MatchStartPage, MyLeagueBrain.Classify(
            Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocrRes, true));

        var action = MyLeagueBrain.Decide(MlScreen.MatchStartPage, ocrRes, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.Contains("开赛", action.Target);
    }

    [Fact]
    public void Classify_TeamSelectPage_NotMatchStartPage()
    {
        // 球衣选择页（val 帧 231320_759）：底部也有「下一步」，但无「球场」——不是起始页
        var ocr = Ocr(("返回主场球衣客场球衣下一步", 0.50, 0.93));
        Assert.NotEqual(MlScreen.MatchStartPage,
            MyLeagueBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }
}
