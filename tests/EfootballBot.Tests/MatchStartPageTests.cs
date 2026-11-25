using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;

using EfootballBot.Core.Bot.Match;
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

        Assert.Equal(MatchScreen.MatchStartPage, MatchBrain.Classify(
            Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocrRes, true));

        var action = MatchBrain.Decide(MatchScreen.MatchStartPage, ocrRes, 0);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.Contains("开赛", action.Target);
    }

    [Fact]
    public void Classify_TeamSelectPage_NotMatchStartPage()
    {
        // 球衣选择页（val 帧 231320_759）：底部也有「下一步」，但无「球场」——不是起始页
        var ocr = Ocr(("返回主场球衣客场球衣下一步", 0.50, 0.93));
        Assert.NotEqual(MatchScreen.MatchStartPage,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_PreMatchWarmupSubstitutesPage_IsMatchStartPage()
    {
        // 2026-11-24 实机回归：第一屏起始页点下一步后进入赛前热身/替补球员页
        // （左列「替补球员」名单 + 底栏「返回 | 比赛计划 | 前往比赛」），即使 YOLO 检出 btn_confirm
        // 也必须判为 MatchStartPage 直接按 A 开赛，不可落 HasConfirm 卡在「未识别到按钮词」。
        var ocr = Ocr(
            ("Villarreal A", 0.16, 0.17),
            ("替补球员", 0.14, 0.21),
            ("返回", 0.11, 0.93),
            ("比赛计划", 0.45, 0.93),
            ("前往比赛", 0.88, 0.93));
        var dets = new[]
        {
            new Core.Vision.Yolo.YoloDetection("btn_confirm", 0, 0.91f, 0.80, 0.90, 0.18, 0.06),
        };
        Assert.Equal(MatchScreen.MatchStartPage, MatchBrain.Classify(dets, ocr));
        var action = MatchBrain.Decide(MatchScreen.MatchStartPage, ocr, 0);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Classify_PreMatchStartingLineupPage_IsMatchStartPage()
    {
        // 2026-11-24 实机回归：左列「首发阵容」名单 + 底栏「返回 | 比赛计划 | 前往比赛」
        var ocr = Ocr(
            ("Vigo AB", 0.16, 0.17),
            ("首发阵容", 0.14, 0.21),
            ("返回", 0.11, 0.93),
            ("比赛计划", 0.45, 0.93),
            ("前往比赛", 0.88, 0.93));
        var dets = new[]
        {
            new Core.Vision.Yolo.YoloDetection("btn_confirm", 0, 0.91f, 0.80, 0.90, 0.18, 0.06),
        };
        Assert.Equal(MatchScreen.MatchStartPage, MatchBrain.Classify(dets, ocr));
    }
}


