using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 端到端模拟：真实 val 截图 → YOLO 检测 → MyLeagueBrain 分类/决策（训练数据缺失时自动跳过）
public class MyLeagueRealFrameTests
{
    private static string RepoDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    private static bool ValDataExists =>
        File.Exists(Path.Combine(RepoDir, "training", "dataset", "images", "val", "225603_680.jpg"));

    private static (MlScreen Screen, IReadOnlyList<YoloDetection> Dets) ClassifyFrame(
        YoloDetector det, string fileName)
    {
        string imgPath = Path.Combine(RepoDir, "training", "dataset", "images", "val", fileName);
        using var mat = Cv2.ImRead(imgPath, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {imgPath}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var dets = det.Detect(new FrameData(data, mat.Width, mat.Height, 0));
        return (MyLeagueBrain.Classify(dets, OcrResult.Empty), dets);
    }

    private static YoloDetector CreateDetector() => new(
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
        new OnnxSessionFactory("CPU"));

    [Fact]
    public void ItemListFrame_ClassifiesItemList_ConfirmsFocusedRow()
    {
        if (!ValDataExists) return;
        using var det = CreateDetector();
        var (screen, _) = ClassifyFrame(det, "225603_680.jpg");
        Assert.Equal(MlScreen.ItemList, screen);

        var action = MyLeagueBrain.Decide(screen, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void MainMenuFrame_ClassifiesMainMenu_EntersMatchCard()
    {
        if (!ValDataExists) return;
        using var det = CreateDetector();
        var (screen, _) = ClassifyFrame(det, "224710_125.jpg");
        Assert.Equal(MlScreen.MainMenu, screen);

        // 全手柄版：主菜单直接执行 复合导航（下×2→左×6→A）
        var action = MyLeagueBrain.Decide(screen, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadEnterMatch, action.Kind);
    }

    [Fact]
    public void ConfirmDialogFrame_ClassifiesHasConfirm_ConfirmsByPad()
    {
        if (!ValDataExists) return;
        using var det = CreateDetector();
        var (screen, _) = ClassifyFrame(det, "233412_804.jpg");
        Assert.Equal(MlScreen.HasConfirm, screen);

        var action = MyLeagueBrain.Decide(screen, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void MatchHistoryFrame_ClassifiesMatchHistory_NotHasConfirm()
    {
        // 2026-10-26 反馈包实机帧（国际服万圣节活动，误点开比赛历史后卡死）：底栏「B 返回」
        // 蓝框被 YOLO 判 btn_confirm 0.63 —— 共享分类必须判 MatchHistory（B 退回主页），
        // 不得落 HasConfirm 盲按 A
        string png = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestData", "weekly", "weekly_match_history.png");
        if (!File.Exists(png)) return;
        using var mat = Cv2.ImRead(png, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {png}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        using var det = CreateDetector();
        using var ocr = new OcrRecognizer();
        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        Assert.Contains(dets, d => d.ClassName == "btn_confirm" && d.Score >= MyLeagueBrain.MinScore);
        Assert.Equal(MlScreen.MatchHistory, MyLeagueBrain.Classify(dets, ocrRes));
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.MatchHistory, OcrResult.Empty, 1).Kind);
    }
}
