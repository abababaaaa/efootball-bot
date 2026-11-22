using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

using EfootballBot.Core.Bot.Match;
namespace EfootballBot.Tests;

// 流程帧记忆 + 比赛场次跟踪（纯逻辑）
public class MlFlowMemoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 16, 0, 0);

    [Fact]
    public void SawWithin_WindowSemantics()
    {
        var m = new MatchFlowMemory();
        m.Record(MatchScreen.InMatch, T0);
        Assert.True(m.SawWithin(MatchScreen.InMatch, TimeSpan.FromSeconds(60), T0.AddSeconds(59)));
        Assert.False(m.SawWithin(MatchScreen.InMatch, TimeSpan.FromSeconds(60), T0.AddSeconds(61)));
        Assert.False(m.SawWithin(MatchScreen.LeaguePage, TimeSpan.FromSeconds(60), T0.AddSeconds(1)));
    }

    [Fact]
    public void Record_PrunesExpired()
    {
        var m = new MatchFlowMemory(TimeSpan.FromMinutes(2));
        m.Record(MatchScreen.InMatch, T0);                    // 会过期
        m.Record(MatchScreen.LeaguePage, T0.AddMinutes(3));   // 触发清理
        // 用大窗口验证：若 T0 的 InMatch 未被清理，1 小时窗口内必命中
        Assert.False(m.SawWithin(MatchScreen.InMatch, TimeSpan.FromHours(1), T0.AddMinutes(3)));
        Assert.True(m.SawWithin(MatchScreen.LeaguePage, TimeSpan.FromHours(1), T0.AddMinutes(3)));
    }

    // ---------------- 比赛场次跟踪 ----------------

    [Fact]
    public void MatchSession_StartThenMenu_Ends()
    {
        var t = new MatchSessionTracker();
        Assert.False(t.Observe(MatchScreen.HasConfirm, T0));           // 赛前确认页不开赛
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(1))); // 开赛
        Assert.True(t.Active);
        Assert.False(t.Observe(MatchScreen.Unknown, T0.AddMinutes(2)));  // 重播不关场
        Assert.False(t.Observe(MatchScreen.HasConfirm, T0.AddMinutes(3))); // 结算弹窗不关场
        Assert.True(t.Observe(MatchScreen.LeaguePage, T0.AddMinutes(4)));  // 回联赛主页 → 一场结束
        Assert.False(t.Active);
    }

    [Fact]
    public void MatchSession_FullTime_EndsImmediately()
    {
        // 全场结束是权威终场信号：见到即关场计数，不等回菜单
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(20)));
        Assert.False(t.Active);
    }

    [Fact]
    public void MatchSession_Timeout_EndsButNotTrusted()
    {
        // 用户 2026-11-13「哪儿来的 76 场」：赛后回放/结算页卡住 → 连续 5 分钟没再出现
        // 比赛特征就被当成一场结束。这种收尾不可信，不得计入完成场次；用时仍记下来给日志
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        t.Observe(MatchScreen.Unknown, T0.AddMinutes(1));
        Assert.True(t.Observe(MatchScreen.Unknown, T0.AddMinutes(6)));
        Assert.False(t.Active);
        Assert.Equal(MatchEndReason.Timeout, t.LastEndReason);
        Assert.False(t.LastEndTrusted);
        Assert.Equal(TimeSpan.FromMinutes(6), t.LastDuration);
    }

    [Fact]
    public void MatchSession_FullTimeAndMenu_AreTrusted()
    {
        var a = new MatchSessionTracker();
        a.Observe(MatchScreen.InMatch, T0);
        a.Observe(MatchScreen.FullTime, T0.AddMinutes(9));
        Assert.True(a.LastEndTrusted);
        Assert.Equal(MatchEndReason.FullTime, a.LastEndReason);
        Assert.Equal(TimeSpan.FromMinutes(9), a.LastDuration);

        var b = new MatchSessionTracker();
        b.Observe(MatchScreen.InMatch, T0);
        b.Observe(MatchScreen.MatchHub, T0.AddMinutes(8));
        Assert.True(b.LastEndTrusted);
        Assert.Equal(MatchEndReason.Menu, b.LastEndReason);
    }

    [Fact]
    public void MatchSession_KeyMatchHint_CountedAsKey()
    {
        // 关键比赛预告/弹窗（sawKeyMatchHint）→ 关场时 WasKeyMatch=true
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.KeyMatchDialog, T0, sawKeyMatchHint: true);
        t.Observe(MatchScreen.InMatch, T0.AddMinutes(1));
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(25)));
        Assert.True(t.WasKeyMatch);
    }

    [Fact]
    public void Decide_Unknown_InMatchContext_PopupConfirms()
    {
        // 比赛上下文中的弹窗（右上角通知等带 下一步/OK/确认）→ 直接 A
        var ocr = Ocr(("下一步", 0.5, 0.3));
        var action = MatchBrain.Decide(MatchScreen.Unknown, ocr, 2, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void MatchSession_KickoffBlipLeaguePage_DoesNotClose()
    {
        // 开场过渡帧误判 LeaguePage（无前往比赛词）→ 不关场（实机 21:23 开场 2 秒误计数）
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.False(t.Observe(MatchScreen.LeaguePage, T0.AddSeconds(2), isHome: false));
        Assert.True(t.Active);
        // 回到真主页（含前往比赛）才关场。时间用 2 分钟：本场不足 60 秒的菜单类关场
        // 一律按识别误判拦下（2026-11-20 进球 CG 被判 MatchHub 虚计一场），30 秒那版会被拦。
        Assert.True(t.Observe(MatchScreen.LeaguePage, T0.AddMinutes(2), isHome: true));
    }

    // ---------------- 只打关键比赛：Abort / StartedAt ----------------

    [Fact]
    public void MatchSession_StartedAt_TracksFirstInMatch()
    {
        // 只打关键比赛用它算「时钟走了多久」：首帧 InMatch 记时间，后续 InMatch 不刷新
        var t = new MatchSessionTracker();
        Assert.Null(t.StartedAt);
        t.Observe(MatchScreen.InMatch, T0);
        Assert.Equal(T0, t.StartedAt);
        t.Observe(MatchScreen.InMatch, T0.AddSeconds(20));
        Assert.Equal(T0, t.StartedAt);              // 不刷新
        t.Observe(MatchScreen.Unknown, T0.AddSeconds(21));
        Assert.Equal(T0, t.StartedAt);              // 重播帧不关场也不清
    }

    [Fact]
    public void MatchSession_Abort_ForgetsCurrentMatch()
    {
        // 杀进程跳过普通比赛后：作废当前场——否则重启回联赛主页会被误算成「一场结束」
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.KeyMatchDialog, T0, sawKeyMatchHint: true);
        t.Observe(MatchScreen.InMatch, T0.AddMinutes(1));
        Assert.True(t.Active);

        t.Abort();
        Assert.False(t.Active);
        Assert.Null(t.StartedAt);
        Assert.False(t.WasKeyMatch);

        // 作废后，回到联赛主页不应再触发「一场结束」
        Assert.False(t.Observe(MatchScreen.LeaguePage, T0.AddMinutes(2), isHome: true));
    }

    [Fact]
    public void MatchSession_Abort_ClearsKeyMatchFlagForNextMatch()
    {
        // 跳过的这场是普通比赛，Abort 后下一场不得继承关键比赛标记
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        t.Abort();
        t.Observe(MatchScreen.InMatch, T0.AddMinutes(5));
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(25)));
        Assert.False(t.WasKeyMatch);
    }

    [Fact]
    public void FlowMemory_Clear_ResetsEverything()
    {
        // 重启游戏后清记忆：否则旧会话的 InMatch 记忆会让 TitleGrace 把真标题页
        // 当成「开赛加载画面」而一直不按 X（重启后永远回不到游戏）
        var m = new MatchFlowMemory();
        m.Record(MatchScreen.InMatch, T0);
        m.Record(MatchScreen.FullTime, T0.AddSeconds(1));
        m.Clear();
        Assert.False(m.SawWithin(MatchScreen.InMatch, TimeSpan.FromHours(1), T0.AddSeconds(2)));
        Assert.False(m.SawWithin(MatchScreen.FullTime, TimeSpan.FromHours(1), T0.AddSeconds(2)));
    }

    [Fact]
    public void Decide_LeaguePage_NoKeywords_StuckBacks()
    {
        // 联赛子页（赛程表等，无任何推进关键词）卡死 → 连续多轮后按 B 返回自愈
        var action = MatchBrain.Decide(MatchScreen.LeaguePage, OcrResult.Empty, MatchBrain.StuckStreak);
        Assert.Equal(MatchActionKind.GamepadBack, action.Kind);
        // 未到阈值先等待
        Assert.Equal(MatchActionKind.Wait, MatchBrain.Decide(MatchScreen.LeaguePage, OcrResult.Empty, 0).Kind);
    }

    [Fact]
    public void Classify_TitleScreen_ByFewFootballWords()
    {
        // 实机 2026-10-11：启动标题画面 OCR 仅 eFootball 标志 + 「按 X」——
        // 此前被角区探针误判 InMatch 盲按 A（A 无法离开标题画面）
        var ocr = Ocr(("eFootbal", 0.3, 0.1), ("X", 0.5, 0.4), ("eFootbalX", 0.6, 0.4));
        Assert.Equal(MatchScreen.TitleScreen,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr, topRightHasContent: true));
        var action = MatchBrain.Decide(MatchScreen.TitleScreen, ocr, 0);
        Assert.Equal(MatchActionKind.PadXPress, action.Kind);
    }

    [Fact]
    public void Classify_SettingsDialog_MapsToHasConfirm()
    {
        // 启动/更新后的「设定」窗（画面模式/分辨率）→ 按 A 确认继续
        var ocr = Ocr(("画面模式", 0.3, 0.2), ("分辨率", 0.3, 0.4), ("确定", 0.6, 0.9), ("取消", 0.3, 0.9));
        Assert.Equal(MatchScreen.HasConfirm,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_InMatchFrame_NotTitleScreen()
    {
        // 比赛中的 eFootball 水印帧（DeFothall 无 b、词数多）不得误判为标题画面
        var ocr = Ocr(("PFoothallTM", 0.1, 0.05), ("trendyol", 0.3, 0.05), ("GS", 0.5, 0.06),
            ("EYU", 0.6, 0.06), ("5:09", 0.7, 0.06), ("DeFothallX", 0.8, 0.4));
        Assert.NotEqual(MatchScreen.TitleScreen,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void Decide_LeaguePage_NextSeason_Confirms()
    {
        // 赛季结束：前往比赛位置变为「下个赛季」→ 按 A 进入下一赛季
        var ocr = Ocr(("下个赛季", 0.126, 0.244), ("赛程表", 0.102, 0.383));
        var action = MatchBrain.Decide(MatchScreen.LeaguePage, ocr, 0);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.Contains("下个赛季", action.Target);
    }

    [Fact]
    public void MatchSession_Timeout_Ends()
    {
        var t = new MatchSessionTracker();
        Assert.False(t.Observe(MatchScreen.InMatch, T0));
        // 卡死在无特征界面 5 分钟 → 超时关场
        Assert.True(t.Observe(MatchScreen.Unknown, T0.AddMinutes(5).AddSeconds(1)));
        Assert.False(t.Active);
    }

    [Fact]
    public void MatchSession_SecondMatch_CountsAgain()
    {
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.MainMenu, T0.AddMinutes(2)));
        Assert.False(t.Observe(MatchScreen.MainMenu, T0.AddMinutes(3))); // 关场只报一次
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(10))); // 第二场开赛
        Assert.True(t.Observe(MatchScreen.MatchHub, T0.AddMinutes(35))); // 第二场结束
    }

    [Fact]
    public void MatchSession_RestartWithinCooldown_NotANewMatch()
    {
        // 用户 2026-11-13「完成场次 76、游戏战绩 29 场」：结算/回放页被角区兜底误判成
        // InMatch 时，关场后一两秒就"又开一场"，一场被打成两三场。冷却期内不开新场。
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(9)));
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(9).AddSeconds(20))); // 结算残留帧
        Assert.False(t.Active);
        // 结算链里反复出现的比赛帧都不开新场：必须先回主页（门 5 分钟才自动解除）
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(12)));
        Assert.False(t.Active);
        Assert.False(t.Observe(MatchScreen.MainMenu, T0.AddMinutes(15)));            // 回菜单解除门
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(16)));             // 真·下一场
        Assert.True(t.Active);
    }

    [Fact]
    public void MatchSession_PlayAgainFromMenu_CountsAgain()
    {
        // 正常的「回主页 → 再开一场」：菜单解除门后立即开赛（不必等冷却）
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddMinutes(9)));
        Assert.False(t.Observe(MatchScreen.MatchHub, T0.AddMinutes(10)));   // 已关场：菜单不再报结束
        Assert.False(t.Observe(MatchScreen.InMatch, T0.AddMinutes(11)));
        Assert.True(t.Active);
    }

    // ---------------- 比赛上下文决策 ----------------

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    [Fact]
    public void Decide_Unknown_InMatchContext_Patient()
    {
        // 比赛上下文下的少词画面 = 开赛入场/过场分镜（字幕、队名等），快按 A 跳过（不是 B——
        // B 是铲球键，无论过场还是逃生都不能在这种情况下误按到比赛里）。
        // 前几轮（streak<8）走开赛过场快跳 PadConfirm；到 UnknownEscapeStreakInMatch(30) 仍认不出
        // 才算导航失控，GamepadBack 逃生。整条链路始终不按 B 进比赛乱踢。
        var ocr = Ocr(("过场字幕", 0.5, 0.5));
        Assert.Equal(MatchActionKind.PadConfirm,
            MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.UnknownEscapeStreak, inMatchRecently: true).Kind);
        Assert.Equal(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.UnknownEscapeStreakInMatch, inMatchRecently: true).Kind);
    }

    [Fact]
    public void Decide_Unknown_WithoutContext_EscapesAtDefaultStreak()
    {
        // 非比赛上下文（赛前过场）：默认 6 轮逃生，快速跳过
        var ocr = Ocr(("过场字幕", 0.5, 0.5));
        Assert.Equal(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.UnknownEscapeStreak, inMatchRecently: false).Kind);
    }
}


