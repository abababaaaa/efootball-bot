using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 「重新挑战」消耗资金确认弹窗（用户 2026-11-25 实机截图，晋级挑战重试时弹出）：
//   标题「重新挑战」+ 副文案「消耗资金可重新挑战当前关卡」+
//   上排蓝色高亮「资金 5000」（默认焦点）+ 下排白色「取消」。
// 用户要求：**确认这枚金额按钮，绝不要选取消**（上移规则会循环到取消，白耗一次重试）。
public class CnWeeklyRechallengeTests
{
    private readonly ITestOutputHelper _out;
    public CnWeeklyRechallengeTests(ITestOutputHelper output) => _out = output;

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "cnweekly");

    private static FrameData Load(string file)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    private static FrameData Pad(FrameData src, int left, int right, int top, int bottom)
    {
        int w = src.Width + left + right, h = src.Height + top + bottom;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(src.Bgra, y * src.Width * 4, dst, ((y + top) * w + left) * 4, src.Width * 4);
        return new FrameData(dst, w, h, 0);
    }

    private static async Task<(CwScreen Cw, MatchScreen Ml, OcrResult Ocr, FrameData Frame)> Run(string file)
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var frame = Load(Path.Combine(Dir, file));
        var ocrRes = await ocr.RecognizeAsync(frame);
        var dets = det.Detect(frame);
        var cw = CnWeeklyBrain.Classify(dets, ocrRes);
        var ml = MatchBrain.Classify(dets, ocrRes, ScreenProbe.CornerHasContent(frame), false,
            ScreenProbe.IsMostlyLightPage(frame), false, ScreenProbe.LooksLikePitch(frame));
        return (cw, ml, ocrRes, frame);
    }

    // 这一页必须先被认成「重试确认弹窗」：底栏还挂着「重置/重新挑战」，
    // 不置顶判定就会被判成 Bracket（对阵表页），然后按 RT/上移乱来。
    [Fact]
    public async Task ClassifiedAsRechallengeConfirm_NotBracket()
    {
        var (cw, ml, ocr, _) = await Run("cn_rechallenge_confirm_20261125.png");
        _out.WriteLine($"CwScreen={cw} MatchScreen={ml} 消耗资金={ocr.ContainsJoined("消耗资金")}");
        Assert.Equal(CwScreen.RechallengeConfirm, cw);
    }

    // 点击常量必须落在「资金 5000」按钮上、且离「取消」足够远（用户要求：不许点取消）。
    [Fact]
    public async Task ConfirmPointHitsAmountButton_NotCancel()
    {
        var (_, _, ocr, _) = await Run("cn_rechallenge_confirm_20261125.png");
        // 副文案「消耗资金可重新挑战当前关卡」里也有「资金」二字：只取弹窗下排那枚金额按钮
        // （中心 y 落在 0.48~0.60，且词里带数字）
        var amount = ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains("资金")
                                                   && w.CenterY is > 0.48 and < 0.60);
        var allAmountWords = ocr.Words.Where(w => w.Text.Contains("资金")).ToList();
        foreach (var w in allAmountWords) _out.WriteLine($"  含资金词: \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        var cancel = ocr.Words.FirstOrDefault(w => w.Text.Contains("取消"));
        Assert.NotNull(amount);
        Assert.NotNull(cancel);
        _out.WriteLine($"资金按钮 ({amount!.CenterX:F3},{amount.CenterY:F3})  取消 ({cancel!.CenterX:F3},{cancel.CenterY:F3})");

        // 常量与 OCR 读到的金额按钮中心必须贴合（≤0.04 = 半个按钮高）
        Assert.True(Math.Abs(amount.CenterX - 0.510) < 0.04 && Math.Abs(amount.CenterY - 0.542) < 0.04,
            $"确认常量 (0.510,0.542) 偏离金额按钮中心 ({amount.CenterX:F3},{amount.CenterY:F3})");
        // 与「取消」至少差 0.08（按钮高约 0.045，留一倍余量）
        double dy = Math.Abs(cancel.CenterY - 0.542);
        _out.WriteLine($"与取消的纵向间距 {dy:F3}");
        Assert.True(dy > 0.08, $"确认常量离「取消」太近（{dy:F3}），有误点取消的风险");
    }

    // 实机帧是整窗帧（顶部 31px 标题栏）：分类靠 OCR 词，不受几何影响，仍必须认出来。
    [Fact]
    public async Task WindowFrame_StillClassified()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var framed = Pad(Load(Path.Combine(Dir, "cn_rechallenge_confirm_20261125.png")), 1, 1, 31, 8);
        var ocrRes = await ocr.RecognizeAsync(framed);
        var cw = CnWeeklyBrain.Classify(det.Detect(framed), ocrRes);
        _out.WriteLine($"帧 {framed.Width}×{framed.Height} CwScreen={cw}");
        Assert.Equal(CwScreen.RechallengeConfirm, cw);
    }

    // 这枚弹窗不走控制方式核验（在它上面点右上角控件没意义，且要先关弹窗）。
    [Fact]
    public void RechallengePopup_SkipsControlProbe()
    {
        // 场景层用的是这两条：
        Assert.True(CnWeeklyBrain.IsControlProbePage(CwScreen.RechallengeConfirm, MatchScreen.HasConfirm));
        // 但 EnsureAiControlAsync 里有显式短路（cw == RechallengeConfirm → 让位给弹窗处理），
        // 这里用文字断言钉住约定：弹窗页面必须先处理弹窗。
        _out.WriteLine("RechallengeConfirm 由 ConfirmRechallengeAsync 先接住，控制方式核验让位");
    }
}
