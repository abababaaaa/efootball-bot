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
        return (MyLeagueBrain.Classify(dets), dets);
    }

    [Fact]
    public void ItemListFrame_ClassifiesItemList_ClicksTopItemRow()
    {
        if (!ValDataExists) return;
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        var (screen, dets) = ClassifyFrame(det, "225603_680.jpg");
        Assert.Equal(MlScreen.ItemList, screen);

        var action = MyLeagueBrain.Decide(screen, dets, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.ClickItem, action.Kind);
        // 点的是最高分 item_row 的中心：坐标必须落在画面内
        Assert.InRange(action.X, 0, 1);
        Assert.InRange(action.Y, 0, 1);
        var best = dets.Where(d => d.ClassName == "item_row").OrderByDescending(d => d.Score).First();
        Assert.Equal(best.X + best.W / 2, action.X, 3);
        Assert.Equal(best.Y + best.H / 2, action.Y, 3);
    }

    [Fact]
    public void MainMenuFrame_ClassifiesMainMenu_WaitsWithoutOcrEntry()
    {
        if (!ValDataExists) return;
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        var (screen, dets) = ClassifyFrame(det, "224710_125.jpg");
        Assert.Equal(MlScreen.MainMenu, screen);

        // OCR 未命中"我的联赛"→ 等待而非乱点
        var action = MyLeagueBrain.Decide(screen, dets, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void ConfirmDialogFrame_ClassifiesHasConfirm_ClicksConfirmButton()
    {
        if (!ValDataExists) return;
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        var (screen, dets) = ClassifyFrame(det, "233412_804.jpg");
        Assert.Equal(MlScreen.HasConfirm, screen);

        var action = MyLeagueBrain.Decide(screen, dets, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.ClickPoint, action.Kind);
        // 标注真值：btn_confirm 中心 (0.874, 0.936)
        Assert.Equal(0.874, action.X, 2);
        Assert.Equal(0.936, action.Y, 2);
    }
}
