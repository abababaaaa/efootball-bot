using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

using EfootballBot.Core.Bot.Match;
namespace EfootballBot.Tests;

// 分析/回归：用户实机比赛中截图 → OCR + YOLO → InMatch 判定
public class InMatchRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public InMatchRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_InMatch_Frame()
    {
        string img = Path.Combine(TestDir, "TestData", "inmatch_user_1.png");
        if (!File.Exists(img)) { _out.WriteLine("no fixture, skip"); return; }

        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {img}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        _out.WriteLine($"==== inmatch_user_1.png ({mat.Width}x{mat.Height}) ====");
        _out.WriteLine("-- YOLO --");
        foreach (var d in dets.Where(d => d.Score >= 0.2).OrderByDescending(d => d.Score))
            _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.X:F2},{d.Y:F2},{d.W:F2},{d.H:F2})");
        _out.WriteLine("-- OCR words --");
        foreach (var w in ocrRes.Words)
            _out.WriteLine($"  \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine("-- Classify --");
        _out.WriteLine($"  {MatchBrain.Classify(dets, ocrRes)}");

        // 回归：真实比赛帧（左上比分框 AME/GS/0:31）必须判为比赛中
        Assert.Equal(MatchScreen.InMatch, MatchBrain.Classify(dets, ocrRes));

        // 右上角角区像素签名：比赛帧 vs 菜单帧（x 0.80~1.0，y 0.04~0.18）
        foreach (var name in new[] { "inmatch_user_1.png", "matchhub_user_1.png", "matchhub_user_2.png" })
        {
            string p = Path.Combine(TestDir, "TestData", name);
            if (!File.Exists(p)) continue;
            DumpCorner(_out, p);
        }

        // 跨会话稳定性：val 集里的真实比赛帧（此前被判 Unknown 的那些）
        string valDir = Path.Combine(TestDir, "..", "..", "..", "..", "training", "dataset", "images", "val");
        if (Directory.Exists(valDir))
            foreach (var f in new[] { "231710_426.jpg", "231720_790.jpg", "232522_513.jpg", "233002_793.jpg", "233136_036.jpg" })
            {
                string p = Path.Combine(valDir, f);
                if (File.Exists(p)) DumpCorner(_out, p);
            }
    }

    /// <summary>
    /// 2026-11-25 实机帧 A：国际服教练模式比赛画面（左上黄色比分板 FRA 2-0 ALB 38:28，
    /// 底部迷你球场，左下球员名 Deco），右下没有任何按钮。锁两件事：
    /// ① 确实是球场帧（InMatch 由草地探针兜底，所以 OCR 漏读时钟也能进 InMatch 分支）；
    /// ② 没有覆盖层按钮 → 决策必须是挂机节奏（按设置 1~4s 随机点），不许快按/慢按。
    /// </summary>
    [Fact]
    public void Probe_YellowClockFrame_NoOverlay_FallsBackToIdleTap()
    {
        var frame = LoadFrame("weekly", "wk_inmatch_yellow_clock_20261125.png");
        if (frame is null) { _out.WriteLine("no fixture, skip"); return; }

        Assert.True(ScreenProbe.LooksLikePitch(frame), "这帧必须被草地探针认成球场（否则进不了 InMatch 分支）");
        Assert.False(ScreenProbe.HasBlueAdvanceButton(frame), "这帧右下没有推进键按钮");
        Assert.Equal(MatchActionKind.MatchIdleTap,
            MatchBrain.Decide(MatchScreen.InMatch, OcrResult.Empty, 0, inMatchRecently: true, frame: frame).Kind);
    }

    /// <summary>
    /// 2026-11-25 实机帧 B：进球庆祝画面（球员特写占满屏幕，草地只有 22%），右下挂着
    /// 「跳过 A ▶」蓝底白字按钮（OCR 常整块漏读）。这张帧过不了草地探针（0.224 &lt; 0.30），
    /// 所以它靠左上时钟判 InMatch；像素兜底只负责「看见这枚按钮就快按 A」。
    /// </summary>
    [Fact]
    public void Probe_GoalSkipFrame_BlueAdvanceButtonPressedFast()
    {
        var frame = LoadFrame("weekly", "wk_inmatch_goal_skip_20261125.png");
        if (frame is null) { _out.WriteLine("no fixture, skip"); return; }

        // 右下「跳过」按钮必须被像素探针命中（OCR 漏读这枚小字时的唯一兜底）
        Assert.True(ScreenProbe.HasBlueAdvanceButton(frame), "右下「跳过」按钮必须被像素探针命中");
        var action = MatchBrain.Decide(MatchScreen.InMatch, OcrResult.Empty, 0, inMatchRecently: true, frame: frame);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 500, $"过场推进键要快按（实际 {action.WaitMs}ms）");

        // 这张帧草地不足（球员特写），分类靠左上时钟：有时钟词就必须判 InMatch
        var ocr = new OcrResult(new List<OcrWord>
        {
            new("86:58", 0.31, 0.04, 0.07, 0.035),
            new("FRA", 0.07, 0.04, 0.04, 0.035),
        }, 1280, 720);
        Assert.Equal(MatchScreen.InMatch,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    /// <summary>读 TestData 下的实机帧（失败返回 null，测试按「无夹具跳过」处理）。</summary>
    private static FrameData? LoadFrame(string subDir, string name)
    {
        string img = Path.Combine(TestDir, "TestData", subDir, name);
        if (!File.Exists(img)) return null;
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        if (mat.Empty()) return null;
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    private static void DumpCorner(ITestOutputHelper o, string path)
    {
        using var m = Cv2.ImRead(path, ImreadModes.Color);
        if (m.Empty()) return;
        // 6 列 × 3 行网格：x 0.80~1.0，y 0.03~0.18
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Path.GetFileName(path),22}: ");
        var idx = m.GetGenericIndexer<Vec3b>();
        for (int gy = 0; gy < 3; gy++)
        {
            int y0 = (int)(m.Height * (0.03 + 0.05 * gy)), y1 = (int)(m.Height * (0.03 + 0.05 * (gy + 1)));
            for (int gx = 0; gx < 6; gx++)
            {
                int x0 = (int)(m.Width * (0.80 + 0.0333 * gx)), x1 = (int)(m.Width * (0.80 + 0.0333 * (gx + 1)));
                long b = 0, g = 0, r = 0; int n = 0;
                for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2)
                { var c = idx[y, x]; b += c.Item0; g += c.Item1; r += c.Item2; n++; }
                sb.Append($"({b / n:000},{g / n:000},{r / n:000})");
            }
            sb.Append(" | ");
        }
        o.WriteLine(sb.ToString());
    }
}


