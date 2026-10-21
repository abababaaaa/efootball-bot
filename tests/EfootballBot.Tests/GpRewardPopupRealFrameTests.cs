using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 回归（实机 2026-10-16）：我的联赛主页「物品已送到收件箱（30,000 GP 我的联赛奖励）」OK 弹窗。
// 终场后奖励弹窗链 ≥9 页且每页同判 HasConfirm，旧逻辑第 8 帧退避停手 → 尾页永远等不到 A，
// 挂机空转 8 小时。本帧固化：必须判 HasConfirm 且退避轮数后仍产出按 A 动作。
public class GpRewardPopupRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public GpRewardPopupRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void GpRewardPopup_ClassifiesHasConfirm_AndKeepsPressingA()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        string img = Path.Combine(TestDir, "TestData", "myleague_gp_reward_popup.png");
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {img}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        var dets = det.Detect(frame);
        var ocrRes = TestOcr.Run(ocr, frame);

        bool corner = MyLeagueScenario.CornerHasContent(frame);
        bool light = MyLeagueScenario.IsMostlyLightPage(frame);
        _out.WriteLine($"corner={corner} mostlyLight={light}");
        _out.WriteLine("-- YOLO --");
        foreach (var d in dets.Where(d => d.Score >= 0.2).OrderByDescending(d => d.Score))
            _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.X:F2},{d.Y:F2},{d.W:F2},{d.H:F2})");
        _out.WriteLine("-- OCR lines --");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");

        // 场景分类链（AI 天梯 → 周活动 → 我的联赛）必须落到 HasConfirm（OK 按钮 btn_confirm 检出）
        var al = AiLadderBrain.Classify(ocrRes);
        var wk = al == AlScreen.None ? WeeklyBrain.Classify(dets, ocrRes, corner, false, light) : WkScreen.None;
        var ml = (al == AlScreen.None && wk == WkScreen.None)
            ? MyLeagueBrain.Classify(dets, ocrRes, corner, false, light)
            : MlScreen.Unknown;
        _out.WriteLine($"Classify: Al={al} Wk={wk} Ml={ml}");
        Assert.Equal(MlScreen.HasConfirm, ml);

        // 卡死关键断言：退避轮数之后动作仍是按 A（放慢不停手），绝不是 Wait
        var stuck = MyLeagueBrain.Decide(ml, ocrRes, MyLeagueBrain.StuckStreak);
        _out.WriteLine($"Decide(streak={MyLeagueBrain.StuckStreak}) → {stuck.Kind} \"{stuck.Target}\" wait={stuck.WaitMs}");
        Assert.Equal(MlActionKind.PadConfirm, stuck.Kind);
        Assert.True(stuck.WaitMs >= 5000, "退避后应放慢节奏");
    }
}
