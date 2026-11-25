using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;

using EfootballBot.Core.Bot.Match;
namespace EfootballBot.Tests;

/// <summary>
/// 识别走查（2026-11-18）回归：新版按钮文案/行位置变化时的判定。
///
/// 这一批断言锁的是「语义不变、只放宽写法」——旧判定把文案与 y 阈值写死在每个分支里，
/// 版本一换整条链一起失效（用户反馈：不自动开始比赛、比赛中下一步不点）。
/// 每一条都同时给出「该认的必须认」与「不该认的仍然不认」，防放宽过头。
/// </summary>
public class RecogWalkHardeningTests
{
    private static OcrResult Ocr(params (string text, double x, double y, double w, double h)[] words)
        => new(words.Select(w => new OcrWord(w.text, w.x, w.y, w.w, w.h)).ToList(), 1, 1);

    // ---------------- 比赛起始页 ----------------

    [Fact]
    public void MatchStartPage_BottomKeyRenamed_StillRecognized()
    {
        // 新版底栏写「开始比赛」而不是「下一步」，且整行并到 y≈0.86
        var ocr = Ocr(
            ("GS", 0.08, 0.10, 0.05, 0.03),
            ("皇家马德里", 0.30, 0.42, 0.16, 0.04),
            ("eFootballStadium", 0.32, 0.52, 0.24, 0.04),
            ("开始比赛", 0.48, 0.855, 0.12, 0.04));
        Assert.Equal(MatchScreen.MatchStartPage,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void MatchStartPage_BottomRowMovedUp_StillRecognized()
    {
        // 「下一步」行下移到 y≈0.83（旧判定要求 >0.85 → 漏判）
        var ocr = Ocr(
            ("下一步", 0.50, 0.815, 0.10, 0.035),
            ("球场", 0.42, 0.815, 0.05, 0.035));
        Assert.Equal(MatchScreen.MatchStartPage,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void MatchStartPage_NoStadiumWord_NotClassified()
    {
        // 只放宽键名，不放弃「球场」证据：普通列表页底部有继续键也不算起始页
        var ocr = Ocr(
            ("继续", 0.50, 0.86, 0.06, 0.035),
            ("比赛历史", 0.30, 0.10, 0.14, 0.04),
            ("2026-11-01", 0.40, 0.30, 0.12, 0.035));
        Assert.NotEqual(MatchScreen.MatchStartPage,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    // ---------------- 终场页 ----------------

    [Fact]
    public void FullTime_NewWording_Recognized()
    {
        var ocr = Ocr(
            ("比赛结束", 0.44, 0.395, 0.12, 0.04),
            ("2-1", 0.45, 0.47, 0.10, 0.05),
            ("下一步", 0.49, 0.88, 0.10, 0.04));
        Assert.Equal(MatchScreen.FullTime,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void FullTime_MatchEndWordLowOnScreen_NotFullTime()
    {
        // 比赛中下方的提示条（如"比赛结束"滚动提示 / 回放字幕）不算终场
        var ocr = Ocr(
            ("比赛结束", 0.42, 0.72, 0.14, 0.04),
            ("0:57", 0.09, 0.09, 0.06, 0.03));
        Assert.NotEqual(MatchScreen.FullTime,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    // ---------------- 过场跳过 ----------------

    [Fact]
    public void SkipOverlay_NewWording_Recognized()
    {
        foreach (var word in new[] { "回放", "快进", "跳过动画", "跳过", "重播" })
            Assert.True(MatchBrain.IsSkipOverlay(Ocr((word, 0.86, 0.62, 0.08, 0.03))), word);
    }

    [Fact]
    public void SkipOverlay_PauseMenuEntry_StillExcluded()
    {
        // 暂停菜单的「比赛回放」不是过场提示：按 A 会重开比赛
        Assert.False(MatchBrain.IsSkipOverlay(Ocr(("比赛回放", 0.12, 0.55, 0.12, 0.035))));
        // 国服右上角「跳过比赛」横幅走暂停链路，同样排除
        Assert.False(MatchBrain.IsSkipOverlay(Ocr(("跳过比赛", 0.80, 0.06, 0.14, 0.035))));
    }

    [Fact]
    public void SkipOverlay_SkipMovedUp_Recognized()
    {
        // SKIP 提示在新版贴在右中偏上（y≈0.50）：旧阈值 y>0.55 漏判
        Assert.True(MatchBrain.IsSkipOverlay(Ocr(("SKIP", 0.89, 0.485, 0.06, 0.03))));
    }

    // ---------------- 比赛中下一步 ----------------

    [Fact]
    public void InMatchAdvance_PushWords()
    {
        foreach (var word in new[] { "下一步", "继续", "确认", "确定", "继续比赛" })
            Assert.True(MatchBrain.IsInMatchAdvance(Ocr((word, 0.50, 0.78, 0.10, 0.04))), word);
        Assert.True(MatchBrain.IsInMatchAdvance(Ocr(("0K", 0.50, 0.78, 0.05, 0.04))));
    }

    [Fact]
    public void InMatchAdvance_PlayerNameWithOkSubstring_NotAdvance()
    {
        // Koke / Opoku 这类球员名含 "ok" 子串，不能当成 OK 按钮
        var ocr = Ocr(("Koke", 0.30, 0.80, 0.07, 0.03), ("Opoku", 0.60, 0.80, 0.08, 0.03));
        Assert.False(MatchBrain.IsInMatchAdvance(ocr));
    }

    [Fact]
    public void Decide_InMatch_NextStepPressedFast()
    {
        var ocr = Ocr(("下一步", 0.50, 0.78, 0.10, 0.04));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 500, $"推进键节奏应压到 500ms 以内，实际 {action.WaitMs}ms");
    }

    [Fact]
    public void Decide_LeaguePage_BottomPushWordPressed()
    {
        // 主页按钮换了文案（既不是前往比赛也不是下个赛季），底栏仍有一枚推进键 → 按 A 而不是干等
        var ocr = Ocr(
            ("我的联赛", 0.30, 0.12, 0.14, 0.04),
            ("继续", 0.50, 0.88, 0.06, 0.035));
        var action = MatchBrain.Decide(MatchScreen.LeaguePage, ocr, 0, inMatchRecently: false);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_LeaguePage_NoKeyAtAll_StillWaitsThenEscapes()
    {
        var ocr = Ocr(("赛程表", 0.30, 0.12, 0.10, 0.04), ("第 3 轮", 0.42, 0.30, 0.10, 0.035));
        Assert.Equal(MatchActionKind.Wait, MatchBrain.Decide(MatchScreen.LeaguePage, ocr, 0).Kind);
        Assert.Equal(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.LeaguePage, ocr, MatchBrain.StuckStreak).Kind);
    }

    // ---------------- 比赛页磁贴焦点 ----------------

    [Fact]
    public void Decide_MatchHub_FocusWordUnreadable_KeepsMovingRight_NeverBlindA()
    {
        // 磁贴标签被右缘裁成碎片（我），读不出「我的联赛」。
        // v8.2.0：旧逻辑在 MatchHubFocusBlindStreak 轮后盲按 A（「试进当前卡」），实机证明会
        // 误进第一张锦标赛卡（2026-11-23）。新逻辑始终右移换焦点，绝不盲按 A；
        // 到 NavEscapeStreak 才走周期性 B 重导航。
        var ocr = Ocr(("比赛", 0.10, 0.12, 0.08, 0.04), ("我", 0.93, 0.55, 0.03, 0.03));
        Assert.Equal(MatchActionKind.PadMoveRight, MatchBrain.Decide(MatchScreen.MatchHub, ocr, 2).Kind);
        Assert.Equal(MatchActionKind.PadMoveRight,
            MatchBrain.Decide(MatchScreen.MatchHub, ocr, MatchBrain.MatchHubFocusBlindStreak).Kind);
        Assert.Equal(MatchActionKind.PadMoveRight,
            MatchBrain.Decide(MatchScreen.MatchHub, ocr, MatchBrain.StuckStreak).Kind);
    }

    [Fact]
    public void Classify_MatchHub_TileRenamed_StillMatchHub()
    {
        // 磁贴行换成「友谊赛 / eFootball™联赛」写法（无 锦标赛/活动/自定义/我的联赛 整词）
        var ocr = Ocr(
            ("比赛", 0.10, 0.10, 0.08, 0.04),
            ("友谊赛", 0.28, 0.42, 0.10, 0.04),
            ("eFootball联赛", 0.52, 0.42, 0.20, 0.04));
        Assert.Equal(MatchScreen.MatchHub,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    // ---------------- 画面不变 → 换招 ----------------

    private static FrameData MakeFrame(int w, int h, params (double X0, double Y0, double X1, double Y1, byte Lum)[] rects)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 240; bgra[i + 1] = 240; bgra[i + 2] = 240; bgra[i + 3] = 255;
        }
        foreach (var r in rects)
            for (int y = (int)(r.Y0 * h); y < (int)(r.Y1 * h); y++)
                for (int x = (int)(r.X0 * w); x < (int)(r.X1 * w); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = r.Lum; bgra[i + 1] = r.Lum; bgra[i + 2] = r.Lum;
                }
        return new FrameData(bgra, w, h, 0);
    }

    [Fact]
    public void Decide_HasConfirm_RepositionIneffective_FallsBackToDirectConfirm()
    {
        // 取消在上（白底高光）、推进键在下：PopupBrain 首轮给出「下×1 + A」
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 255),
            (0.40, 0.66, 0.60, 0.78, 40));
        var ocr = Ocr(("取消", 0.50, 0.54, 0.05, 0.04), ("确认", 0.50, 0.72, 0.05, 0.04));

        Assert.Equal(MatchActionKind.PadDownConfirm,
            MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, frame: frame).Kind);
        // 同一屏连续多轮仍不动 → 判定高光读反，改用直接 A（绝不对同一处重复同一个无效动作）
        Assert.Equal(MatchActionKind.PadConfirm,
            MatchBrain.Decide(MatchScreen.HasConfirm, ocr, MatchBrain.ConfirmRepositionRounds, frame: frame).Kind);
    }

    [Fact]
    public void Decide_Unknown_TriesConfirmBeforeBackEscape()
    {
        var ocr = Ocr(("加载中", 0.5, 0.5, 0.10, 0.04));
        Assert.Equal(MatchActionKind.Wait, MatchBrain.Decide(MatchScreen.Unknown, ocr, 0, false).Kind);
        Assert.Equal(MatchActionKind.PadConfirm,
            MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.UnknownProbeAStreak, false).Kind);
        // 到逃生轮数仍先走 B（不把 A 试探变成无限按）
        Assert.Equal(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.UnknownEscapeStreak, false).Kind);
    }

    [Fact]
    public void FindBottomPushWord_IgnoresBackHints()
    {
        // 底栏 B/X/Y 说明不是推进键
        var ocr = Ocr(
            ("B返回", 0.10, 0.93, 0.08, 0.03),
            ("X详情", 0.30, 0.93, 0.08, 0.03),
            ("Y当前位置", 0.50, 0.93, 0.10, 0.03));
        Assert.Null(MatchBrain.FindBottomPushWord(ocr));
    }

    // ---------------- 开赛入场与过场动画快速跳过 ----------------

    [Fact]
    public void Decide_InMatch_NoScoreboard_SkipsAnimationFast()
    {
        // 比赛上下文 + 无比分板时钟 = 入场/过场分镜（队名特写）：快按 A 切断，等不超过 500ms
        var ocr = Ocr(("REAL MADRID", 0.30, 0.40, 0.15, 0.04), ("BARCELONA", 0.60, 0.40, 0.15, 0.04));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 500);
    }

    [Fact]
    public void Decide_InMatch_WithScoreboardClock_DoesNormalIdleTap()
    {
        // 有比分板时钟 = 真正在踢：回到拟人化挂机节奏，别乱按 A 传球
        var ocr = Ocr(("12:34", 0.15, 0.08, 0.08, 0.035), ("RMA", 0.08, 0.08, 0.05, 0.035));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
    }
}


