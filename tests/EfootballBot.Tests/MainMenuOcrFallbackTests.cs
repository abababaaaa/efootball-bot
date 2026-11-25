using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// v8.0.13 重训后的 YOLO 更保守：弹窗盖在真主菜单上时，导航条 main_menu 会从 0.8x
/// 掉到 0.0x（login_bonus_popup / inbox_popup 实测），但顶部导航条的四个固定词
/// 「游戏主菜单/商店/学院/额外选项」OCR 一直读得很稳（y≈0.08）。
/// 这些测试锁住两件事：① OCR 证据只认导航条，不把周活动/合约页认成主菜单；
/// ② 兜底放在所有按钮判定之后 —— 有推进键的弹窗仍判 HasConfirm，不能被抢成 MainMenu。
/// </summary>
public class MainMenuOcrFallbackTests
{
    private static string Frame(string rel) => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", rel.Replace('/', Path.DirectorySeparatorChar));

    private static (OcrResult Ocr, IReadOnlyList<YoloDetection> Dets) Load(string rel)
    {
        string png = Frame(rel);
        Assert.True(File.Exists(png), $"缺实机帧 {png}");
        using var mat = Cv2.ImRead(png, ImreadModes.Color);
        Assert.False(mat.Empty());
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        return (ocrRes, det.Detect(frame));
    }

    [Theory]
    [InlineData("stud/stud_01_main_menu.png")]
    [InlineData("intlboot/login_bonus_popup.png")]
    [InlineData("intlboot/inbox_popup.png")]
    public void 导航条固定词_能被OCR认出(string rel)
    {
        var (ocr, _) = Load(rel);
        Assert.True(MatchBrain.HasMainMenuNavBar(ocr), $"{rel} 应认出顶部导航条");
    }

    [Theory]
    [InlineData("weekly/weekly_events_list.png")]   // 只有「对阵AI/对阵玩家」
    [InlineData("stud/stud_03_contract_home.png")]  // 合约页，没有导航条
    public void 非主菜单页_不会被OCR证据误认(string rel)
    {
        var (ocr, _) = Load(rel);
        Assert.False(MatchBrain.HasMainMenuNavBar(ocr), $"{rel} 不该认出导航条");
    }

    [Fact]
    public void 弹窗叠主菜单_有推进键时仍判HasConfirm_不被兜底抢成MainMenu()
    {
        var (ocr, dets) = Load("intlboot/login_bonus_popup.png");
        Assert.True(MatchBrain.HasMainMenuNavBar(ocr));   // 导航条确实在
        // 显式模拟「YOLO 漏检导航条」：把 main_menu 检测剔除，只留按钮等其它类别。
        // 这样断言不绑定具体模型权重（旧模型会检出 main_menu 0.8x，新模型漏检），
        // 在 PR 分支的上游模型与 fork 的重训模型下都能验证同一条产品规则。
        var withoutNav = dets.Where(d => d.ClassName != "main_menu").ToList();
        Assert.Equal(MatchScreen.HasConfirm, MatchBrain.Classify(withoutNav, ocr));
    }

    [Fact]
    public void YOLO全漏检时_OCR兜底让真主菜单不落Unknown()
    {
        var (ocr, _) = Load("stud/stud_01_main_menu.png");
        // 模拟重训模型漏检导航条的极端帧：没有任何 YOLO 检测结果
        Assert.Equal(MatchScreen.MainMenu,
            MatchBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }
}
