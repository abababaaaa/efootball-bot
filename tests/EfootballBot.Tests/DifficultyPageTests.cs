using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 难度选择页回归：分类（置顶判定）+ 蓝勾检测 + 行号映射（实机 difficulty_2~4）
public class DifficultyPageTests
{
    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    private static (FrameData Frame, OcrResult Ocr, IReadOnlyList<YoloDetection> Dets) Load(string name)
    {
        string img = Path.Combine(TestDir, "TestData", name);
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new Core.Inference.OnnxSessionFactory("CPU"));
        return (frame, ocrRes, det.Detect(frame));
    }

    [Theory]
    [InlineData("difficulty_2.png")]
    [InlineData("difficulty_3.png")]
    [InlineData("difficulty_4.png")]
    public void Classify_DifficultyPage_BeatsHomeWords(string file)
    {
        // 该页覆盖在联赛主页上（画面含前往比赛/赛程表等词），必须置顶判为 DifficultyPage
        var (frame, ocr, dets) = Load(file);
        Assert.Equal(MlScreen.DifficultyPage, MyLeagueBrain.Classify(dets, ocr, true));
    }

    [Theory]
    [InlineData("difficulty_2.png", "传奇")]   // 蓝勾在传奇（蓝像素 24）
    [InlineData("difficulty_3.png", "新手")]   // 新手 151
    [InlineData("difficulty_4.png", "新手")]   // 新手 179
    public void DetectSelectedDifficulty_BlueCheck(string file, string expected)
    {
        var (frame, ocr, _) = Load(file);
        Assert.Equal(expected, MyLeagueBrain.DetectSelectedDifficulty(frame, ocr));
    }

    [Fact]
    public void DifficultyRowNumber_MapsListOrder()
    {
        string[] names = { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(i + 1, MyLeagueBrain.DifficultyRowNumber(names[i]));
        Assert.Equal(1, MyLeagueBrain.DifficultyRowNumber("未知难度")); // 未知名兜底第 1 行
    }
}
