using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 模型回归：云端自训练 6 类模型对真实 val 截图的端到端推理（训练数据缺失时自动跳过）
public class YoloRealImageValTests
{
    private static string RepoDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    private static string ValImg(string name) =>
        Path.Combine(RepoDir, "training", "dataset", "images", "val", name);

    private static bool ValDataExists => File.Exists(ValImg("225603_680.jpg"));

    private static YoloDetector CreateDetector()
    {
        string modelPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx");
        string classesPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt");
        var factory = new OnnxSessionFactory("CPU");
        return new YoloDetector(modelPath, classesPath, factory);
    }

    private static FrameData LoadFrame(string relativePath)
    {
        string imgPath = Path.Combine(RepoDir, relativePath);
        using var mat = Cv2.ImRead(imgPath, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {imgPath}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    private static string Describe(IReadOnlyList<YoloDetection> dets) =>
        string.Join(", ", dets.Select(d => $"{d.ClassName}:{d.Score:F2}({d.X:F2},{d.Y:F2},{d.W:F2},{d.H:F2})"));

    [Fact]
    public void ItemListPage_DetectsRowsBackAndCoinBar()
    {
        if (!ValDataExists) return; // 训练数据不在本地时跳过
        using var det = CreateDetector();
        var dets = det.Detect(LoadFrame(@"training\dataset\images\val\225603_680.jpg"));
        Assert.Contains(dets, d => d.ClassName == "item_row");
        Assert.Contains(dets, d => d.ClassName == "btn_back");
        Assert.Contains(dets, d => d.ClassName == "coin_bar");
        Assert.True(dets.Count(d => d.ClassName == "item_row") >= 2, Describe(dets));
    }

    [Fact]
    public void MainMenuPage_DetectsMenuAndPlayerCards()
    {
        if (!ValDataExists) return; // 训练数据不在本地时跳过
        using var det = CreateDetector();
        var dets = det.Detect(LoadFrame(@"training\dataset\images\val\224710_125.jpg"));
        Assert.Contains(dets, d => d.ClassName == "main_menu");
        Assert.Contains(dets, d => d.ClassName == "player_card");
    }

    [Fact]
    public void MatchScene_NoFalsePositives()
    {
        if (!ValDataExists) return; // 训练数据不在本地时跳过
        using var det = CreateDetector();
        var dets = det.Detect(LoadFrame(@"training\dataset\images\val\231402_084.jpg"));
        Assert.True(dets.Count <= 2, Describe(dets));
    }
}

