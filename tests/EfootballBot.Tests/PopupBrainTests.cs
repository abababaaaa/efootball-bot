using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 弹窗按键决策（用户 2026-11-10 规则）：单键弹窗直接 A；双键弹窗先看白底高光那枚是不是
/// 确认/进行/OK/下一步 这类推进键，是就 A，不是就按上下关系挪一格再 A。
/// 另锁定 2026-11-09 夜间事故帧（「物品已送到收件箱 / OK」）的处置：必须是 PressConfirm，
/// 且该帧在周活动场景里走弹窗分支（不再是「其他板块页 → B」）。
/// </summary>
public class PopupBrainTests
{
    private readonly ITestOutputHelper _out;
    public PopupBrainTests(ITestOutputHelper output) => _out = output;

    private static string WeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "weekly");

    // ---------------- 合成帧工具 ----------------

    /// <summary>造一帧：整体灰 240（浅色弹窗底），在给定矩形内涂指定灰度。</summary>
    private static FrameData MakeFrame(int w, int h, params (double X0, double Y0, double X1, double Y1, byte Lum)[] rects)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 240; bgra[i + 1] = 240; bgra[i + 2] = 240; bgra[i + 3] = 255;
        }
        foreach (var r in rects)
        {
            for (int y = (int)(r.Y0 * h); y < (int)(r.Y1 * h); y++)
                for (int x = (int)(r.X0 * w); x < (int)(r.X1 * w); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = r.Lum; bgra[i + 1] = r.Lum; bgra[i + 2] = r.Lum;
                }
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static OcrResult Ocr(params (string Text, double Cx, double Cy, double W, double H)[] words)
        => new(words.Select(x => new OcrWord(x.Text, x.Cx - x.W / 2, x.Cy - x.H / 2, x.W, x.H)).ToList(), 1280, 720);

    // ---------------- 纯规则 ----------------

    [Fact]
    public void SingleButton_PressesConfirm()
    {
        // 2026-11-09 夜间事故帧的样子：浅色弹窗 + 一枚 OK
        var frame = MakeFrame(1280, 720, (0.30, 0.72, 0.70, 0.86, 250));
        var ocr = Ocr(("物品已送到收件箱", 0.50, 0.55, 0.30, 0.05), ("OK", 0.50, 0.79, 0.03, 0.04));
        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Single(buttons);
        Assert.True(buttons[0].IsConfirm);
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnCancelBelow_MovesDownToConfirm()
    {
        // 取消在上且白底高光，确认在下且暗 → 下×1 再 A
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 255),   // 取消：亮
            (0.40, 0.66, 0.60, 0.78, 40));   // 确认：暗
        var ocr = Ocr(("取消", 0.50, 0.54, 0.05, 0.04), ("确认", 0.50, 0.72, 0.05, 0.04));
        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Equal(2, buttons.Count);
        Assert.Equal(PopupAction.DownThenConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnCancelAbove_MovesUpToConfirm()
    {
        // 取消在下且白底高光，确认在上且暗 → 上×1 再 A（选难度/选球队那类弹窗）
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 40),    // 进入：暗
            (0.40, 0.66, 0.60, 0.78, 255));  // 取消：亮
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.UpThenConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnConfirm_PressesConfirm()
    {
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 255),   // 进入：亮（高光在推进键）
            (0.40, 0.66, 0.60, 0.78, 40));   // 取消：暗
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusUnreadable_FallsBackToConfirm()
    {
        // 两枚都亮：探针读不出高光 → 直接 A（与旧行为等价，不冒险乱挪）
        var frame = MakeFrame(1280, 720, (0.40, 0.40, 0.60, 0.80, 255));
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void NoButton_ReturnsNone()
    {
        var frame = MakeFrame(1280, 720);
        var ocr = Ocr(("活动期间：2026/11/05 10:00", 0.30, 0.20, 0.35, 0.04));
        Assert.Empty(PopupBrain.FindButtons(frame, ocr));
        Assert.Equal(PopupAction.None, PopupBrain.Decide(frame, ocr, out _));
    }

    [Fact]
    public void BodyTextContainingCancel_IsNotAButton()
    {
        // 正文「取消比赛」不是按钮（长度超限）；底栏 y>0.92 的热键提示也不算
        var frame = MakeFrame(1280, 720);
        var ocr = Ocr(("取消比赛将丢失进度", 0.35, 0.40, 0.30, 0.03), ("下一步", 0.86, 0.95, 0.05, 0.03));
        Assert.Empty(PopupBrain.FindButtons(frame, ocr));
    }

    // ---------------- 实机帧 ----------------

    [Fact]
    public void InboxOkPopup_RealFrame_PressesConfirm()
    {
        var path = Path.Combine(WeeklyDir, "popup_inbox_ok_1109.png");
        if (!File.Exists(path)) { _out.WriteLine("popup_inbox_ok_1109.png missing, skip"); return; }
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocrEngine = new OcrRecognizer();
        var ocr = TestOcr.Run(ocrEngine, frame);

        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Single(buttons);
        Assert.True(buttons[0].IsConfirm, $"唯一按钮应被认成推进键，实读「{buttons[0].Label}」");
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine("decide: " + note);

        // 该帧在周活动场景里必须有弹窗分支接住（旧逻辑被「其他板块页 → B」规则吃掉）
        var dets = new List<YoloDetection>
        {
            new("btn_confirm", 0, 0.84f, 0.42, 0.75, 0.10, 0.08), // 实机 YOLO 检出 OK 按钮 0.82~0.85
        };
        var wk = WeeklyBrain.Classify(dets, ocr);
        var ml = MyLeagueBrain.Classify(dets, ocr);
        _out.WriteLine($"classify: wk={wk} ml={ml}");
        Assert.Equal(WkScreen.None, wk);
        Assert.Equal(MlScreen.HasConfirm, ml);
    }
}
