using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 共享层（Core/Bot/Match）契约：界面模型 + 分类 + 决策 + 流程记忆 + 赛中匹配。
// 这一层被国服/国际服所有模块共用，它是「改一次全模块生效」的唯一入口——
// 所以这里断言的是「形状」而不是某一模块的业务细节：模块换了、加了都不该影响这些。
public class SharedMatchLayerTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 20, 0, 0);
    private static readonly YoloDetection[] NoDets = Array.Empty<YoloDetection>();
    private static OcrResult EmptyOcr() => OcrResult.Empty;

    [Fact]
    public void 无特征画面判_Unknown_不盲按()
    {
        Assert.Equal(MatchScreen.Unknown, MatchBrain.Classify(NoDets, EmptyOcr()));
    }

    [Fact]
    public void 主菜单特征帧判_MainMenu()
    {
        var dets = new[] { new YoloDetection("main_menu", 0, 0.92f, 0.10, 0.05, 0.80, 0.90) };
        Assert.Equal(MatchScreen.MainMenu, MatchBrain.Classify(dets, EmptyOcr()));
    }

    [Fact]
    public void 比赛中默认动作是随机点击而不是按_B()
    {
        // 比赛中 B 是铲球键：共享决策只能给出 MatchIdleTap（A），绝不能给 B 逃生。
        // 必须带比分板时钟——真实 Live Match（左上角 12:34）一定有时钟，Classify 也靠它判 InMatch；
        // 空 OCR 的 InMatch 帧在真实流程里不存在（那是过场/黑帧，走的是快按 A 跳过）。
        var live = new OcrResult(new List<OcrWord> { new("12:34", 0.15, 0.08, 0.08, 0.035) }, 1, 1);
        var action = MatchBrain.Decide(MatchScreen.InMatch, live, screenStreak: 0);
        Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
    }

    [Fact]
    public void 认不出的界面在没有上下文时先等待()
    {
        var action = MatchBrain.Decide(MatchScreen.Unknown, EmptyOcr(), screenStreak: 0, inMatchRecently: false);
        Assert.Equal(MatchActionKind.Wait, action.Kind);
    }

    [Fact]
    public void 认不出的界面连续多轮才_B_逃生()
    {
        // 帧里确实有字（OCR 非空）才允许逃生：完全无感知（黑帧/驱动异常）时只能等待
        var ocr = new OcrResult(new List<OcrWord> { new("未知页面的字", 0.5, 0.5, 0.2, 0.03) }, 1, 1);
        var action = MatchBrain.Decide(MatchScreen.Unknown, ocr,
            screenStreak: MatchBrain.UnknownEscapeStreak);
        Assert.Equal(MatchActionKind.GamepadBack, action.Kind);
    }

    [Fact]
    public void 完全无感知时绝不盲按_B()
    {
        var action = MatchBrain.Decide(MatchScreen.Unknown, EmptyOcr(),
            screenStreak: MatchBrain.UnknownEscapeStreak * 5);
        Assert.Equal(MatchActionKind.Wait, action.Kind);
    }

    [Fact]
    public void 终场页立刻按_A_推进()
    {
        var action = MatchBrain.Decide(MatchScreen.FullTime, EmptyOcr(), screenStreak: 0);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void MatchAction_Wait_是纯等待()
    {
        var a = MatchAction.Wait(1234);
        Assert.Equal(MatchActionKind.Wait, a.Kind);
        Assert.Equal(1234, a.WaitMs);
    }

    [Fact]
    public void 像素探针在无效帧上保守返回_false()
    {
        var tiny = new FrameData(new byte[16], 2, 2, 0);
        Assert.False(ScreenProbe.CornerHasContent(tiny));
        Assert.False(ScreenProbe.IsMostlyLightPage(tiny));
    }

    [Fact]
    public void 流程记忆_空记忆没有比赛上下文()
    {
        var mem = new MatchFlowMemory();
        Assert.False(MatchShared.MatchContext(mem, T0));
        Assert.False(MatchShared.LoginRecovery(mem, T0));

        mem.Record(MatchScreen.InMatch, T0);
        Assert.True(MatchShared.MatchContext(mem, T0.AddSeconds(10)));
    }

    [Fact]
    public void 标题画面宽限_无比赛上下文时不拦()
    {
        var mem = new MatchFlowMemory();
        DateTime? since = null;
        bool skipped = MatchShared.TitleGrace(mem, T0, ref since, (_, _) => { }, "单测");
        Assert.False(skipped);
    }

    [Fact]
    public void 标题画面宽限_刚见过比赛时先等不按_X()
    {
        var mem = new MatchFlowMemory();
        mem.Record(MatchScreen.InMatch, T0);
        DateTime? since = null;
        bool skipped = MatchShared.TitleGrace(mem, T0.AddSeconds(5), ref since, (_, _) => { }, "单测");
        Assert.True(skipped);
    }

    // ---------------- 赛中匹配：场次判定的时段语义（MatchPlay 背后的共享跟踪器） ----------------

    [Fact]
    public void 场次判定_开赛_终场_停靠菜单()
    {
        var t = new MatchSessionTracker();
        Assert.False(t.Observe(MatchScreen.InMatch, T0));
        Assert.True(t.Active);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(22)));
        Assert.False(t.Active);
        Assert.True(t.LastEndTrusted);              // 见到终场页 → 可信，计场次
        Assert.Equal("22:00", t.LastDurationText);
    }

    [Fact]
    public void 场次时长_超过60分钟不回绕()
    {
        var t = new MatchSessionTracker();
        Assert.False(t.Observe(MatchScreen.InMatch, T0));
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(65).AddSeconds(12)));
        Assert.Equal("65:12", t.LastDurationText);
    }

    [Fact]
    public void 场次判定_超时收尾不可信()
    {
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.Unknown, T0.AddMinutes(10)));
        Assert.False(t.LastEndTrusted);             // 5 分钟无比赛特征 → 不计场次
    }

    [Fact]
    public void 场次判定_关场后必须先见菜单()
    {
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(20)));
        // 结算链里的"比赛帧"不能被当成新的一场（用户 2026-11-13：完成场次 76 / 战绩 29）
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(22)));
        Assert.False(t.Active);
        t.MarkAtMenu();
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(30)));
        Assert.True(t.Active);
    }
}
