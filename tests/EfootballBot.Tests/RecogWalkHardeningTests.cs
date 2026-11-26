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
    public void Decide_InMatch_NoOverlay_FallsBackToIdleTap()
    {
        // 用户 2026-11-25 定：比赛里没有弹窗/跳过提示时，回到最初行为——按挂机节奏随机点（1~4s）。
        // 入场分镜也不再快按：过场由「跳过提示」「右下推进键像素探针」两条兜住，其余一律当比赛画面。
        var ocr = Ocr(("REAL MADRID", 0.30, 0.40, 0.15, 0.04), ("BARCELONA", 0.60, 0.40, 0.15, 0.04));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
    }

    // ---------------- 起始页底栏「X 设置 | Y 球场 | RT 下一步」 ----------------
    // 2026-11-25 实机：OCR 把「下一步」整行漏读时，这页会落到通用 btn_confirm 判定 → HasConfirm
    // → 上移+A，A 落在「Y 球场」上把球场选择打开（用户报的「怎么按了 Y」）。

    [Fact]
    public void MatchStartPage_BottomButtonsWithoutNextStep_StillStartPage()
    {
        var ocr = Ocr(
            ("AC米兰", 0.27, 0.10, 0.10, 0.05),
            ("巴塞罗那", 0.30, 0.16, 0.12, 0.05),
            ("圣西罗球场", 0.26, 0.215, 0.10, 0.03),
            ("设置", 0.24, 0.925, 0.06, 0.03),
            ("球场", 0.52, 0.925, 0.06, 0.03));
        var dets = new[] { new Core.Vision.Yolo.YoloDetection("btn_confirm", 0, 0.92f, 0.78, 0.89, 0.20, 0.07) };
        Assert.Equal(MatchScreen.MatchStartPage, MatchBrain.Classify(dets, ocr));
    }

    [Fact]
    public void MatchStartPage_BottomButtons_ConfirmActionStaysSingle()
    {
        // 判成起始页后，决策必须是「单键确认」（国际服 A / 国服 RT），绝不能是上移确认
        var ocr = Ocr(
            ("圣西罗球场", 0.26, 0.215, 0.10, 0.03),
            ("设置", 0.24, 0.925, 0.06, 0.03),
            ("球场", 0.52, 0.925, 0.06, 0.03));
        var action = MatchBrain.Decide(MatchScreen.MatchStartPage, ocr, 0, inMatchRecently: false);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_InMatch_WithScoreboardClock_DoesNormalIdleTap()
    {
        // 有比分板时钟 = 真正在踢：回到拟人化挂机节奏，别乱按 A 传球
        var ocr = Ocr(("12:34", 0.15, 0.08, 0.08, 0.035), ("RMA", 0.08, 0.08, 0.05, 0.035));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
    }

    // ---------------- 右下角「下一步」弹窗：看见就立刻点 ----------------

    [Theory]
    [InlineData("InMatch")]
    [InlineData("HasConfirm")]
    [InlineData("Unknown")]
    public void RightBottomNextStep_TappedImmediately(string screenName)
    {
        // 用户 2026-11-25：右下角一冒出「下一步」就点，别等节奏（国服 RT / 国际服 A 由场景层分流）
        var ocr = Ocr(("下一步", 0.85, 0.92, 0.10, 0.04));
        var screen = Enum.Parse<MatchScreen>(screenName);
        var action = MatchBrain.Decide(screen, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 250, $"{screenName} 上「下一步」要立刻点（实际 {action.WaitMs}ms）");
        Assert.Contains("立刻点", action.Target);
    }

    [Fact]
    public void RightBottomNextStep_WithCancel_KeepsUpMoveRule()
    {
        // 带「取消」的双按钮弹窗不能盲点 A（会点在取消上），仍走上移规则
        var ocr = Ocr(("下一步", 0.62, 0.80, 0.10, 0.04), ("取消", 0.42, 0.80, 0.10, 0.04));
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.NotEqual("右下角下一步：立刻点", action.Target);
    }
    // ---------------- 黄色比分板：时钟 OCR 抖动（2026-11-25 实机事故） ----------------
    // 国际服教练模式把左上比分板换成黄色小块后，OCR 常读不出半角冒号（读成全角「：」，
    // 或与队名粘成一整块，或干脆把冒号吞掉只剩 "3828"）。旧逻辑于是把整场 Live Match
    // 当成入场动画，350ms 一拍连按 A，球员全程被乱传。

    [Theory]
    [InlineData("38:28")]     // 正常形态
    [InlineData("38：28")]    // OCR 把冒号读成全角
    [InlineData("38;28")]     // OCR 把冒号读成分号
    [InlineData("38 : 28")]   // 词内带空白
    public void HasLiveMatchHud_AcceptsColonJitter(string clock)
    {
        var ocr = Ocr((clock, 0.27, 0.055, 0.07, 0.035), ("FRA", 0.06, 0.055, 0.04, 0.035));
        Assert.True(MatchBrain.HasLiveMatchHud(ocr));
    }

    [Fact]
    public void HasLiveMatchHud_AcceptsGluedClock_WithTeamAbbrev()
    {
        // 冒号被整条吞掉：「38:28」→「3828」；左上同时有队名缩写才认
        var ocr = Ocr(("3828", 0.27, 0.055, 0.07, 0.035), ("FRA", 0.06, 0.055, 0.04, 0.035),
                      ("ALB", 0.20, 0.055, 0.04, 0.035));
        Assert.True(MatchBrain.HasLiveMatchHud(ocr));
    }

    [Fact]
    public void HasLiveMatchHud_RejectsBareNumberWithoutTeams()
    {
        // 光一个 4 位数字（积分/编号）不算时钟——不认，交给慢按试探
        var ocr = Ocr(("3828", 0.20, 0.08, 0.08, 0.035));
        Assert.False(MatchBrain.HasLiveMatchHud(ocr));
        // 秒位越界（>59）也不是时钟
        var bad = Ocr(("3899", 0.20, 0.08, 0.08, 0.035), ("FRA", 0.06, 0.055, 0.04, 0.035));
        Assert.False(MatchBrain.HasLiveMatchHud(bad));
    }

    [Fact]
    public void HasLiveMatchHud_AcceptsSplitClockOrScoreDigits()
    {
        // 时钟被拆成两枚数字词（38 / 28）：仍是比分板
        var split = Ocr(("38", 0.27, 0.055, 0.03, 0.035), ("28", 0.31, 0.055, 0.03, 0.035),
                        ("FRA", 0.06, 0.055, 0.04, 0.035));
        Assert.True(MatchBrain.HasLiveMatchHud(split));
        // 比分「2 - 0」被拆开：同样说明比分板在 → 比赛进行中
        var score = Ocr(("2", 0.10, 0.055, 0.015, 0.035), ("0", 0.14, 0.055, 0.015, 0.035),
                        ("ALB", 0.20, 0.055, 0.04, 0.035));
        Assert.True(MatchBrain.HasLiveMatchHud(score));
    }

    [Fact]
    public void HasLiveMatchHud_IgnoresMenuNumbers()
    {
        // 全局分类仍用严格形态：左上区以外的 4 位数字不许把菜单判成比赛
        var ocr = Ocr(("2401", 0.20, 0.52, 0.08, 0.035), ("FRA", 0.06, 0.55, 0.04, 0.035));
        Assert.False(MatchBrain.HasLiveMatchHud(ocr));
        Assert.NotEqual(MatchScreen.InMatch,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
    }

    [Fact]
    public void Decide_InMatch_BottomPushWordVariant_StillTapsFast()
    {
        // 新版把右下按钮写成词表外的变体（如「点击继续」），整词表没命中也不能干等——
        // 底栏确实印着一枚推进键就 400ms 连点 A
        var ocr = Ocr(
            ("点击继续", 0.78, 0.90, 0.18, 0.045),
            ("FRA", 0.06, 0.055, 0.04, 0.035),
            ("38:28", 0.27, 0.055, 0.07, 0.035));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 500, $"底栏推进键要快拍连点（实际 {action.WaitMs}ms）");
        // 底栏变体走的是「右下角推进键：立刻点」快通道（文案 2026-11-25 由「连点」改为「立刻点」）
        Assert.Contains("立刻点", action.Target);
    }

    [Fact]
    public void Decide_Unknown_InMatchContext_BottomPushWord_TapsFast()
    {
        // 认不出的比赛覆盖层（既不是 InMatch 也不是 HasConfirm）里出现底栏推进键：
        // 也要 500ms 连点 A，不能等到 4s 逃生节奏
        var ocr = Ocr(
            ("下一步", 0.84, 0.90, 0.12, 0.045),
            ("Victor San Bartolomé", 0.56, 0.075, 0.12, 0.02));
        var action = MatchBrain.Decide(MatchScreen.Unknown, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs <= 600, $"未知覆盖层上的推进键要快拍连点（实际 {action.WaitMs}ms）");
    }

    [Fact]
    public void Decide_HasConfirm_InMatch_KeepsTappingA()
    {
        // 用户 2026-11-25 实机：比赛中右下角弹出「下一步」大蓝条，会被判成 HasConfirm。
        // 旧路径 2s → 5s 退避、可能上移（比赛里 Dpad 是战术）甚至 B 逃生（B 是铲球），
        // 表现为「有下一步弹窗反而不一直点了」。比赛内必须一直连点 A。
        var ocr = Ocr(
            ("下一步", 0.84, 0.92, 0.12, 0.045),
            ("FRA", 0.06, 0.055, 0.04, 0.035),
            ("38:28", 0.27, 0.055, 0.07, 0.035));

        var first = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, first.Kind);
        Assert.True(first.WaitMs <= 900, $"比赛内弹窗要快拍连点（实际 {first.WaitMs}ms）");
        // 「下一步」走快通道（Target 文案 2026-11-25 为「右下角下一步：立刻点」）
        Assert.Contains("立刻点", first.Target);

        // 同一个弹窗点到第 20 轮也还是连点，绝不退成 B（比赛中 B 是铲球）
        var late = MatchBrain.Decide(MatchScreen.HasConfirm, ocr,
            MatchBrain.ConfirmEscapeStreak + 5, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, late.Kind);

        // 非比赛上下文（菜单弹窗）仍是老规则：退避 + 到轮数 B 逃生
        var offline = MatchBrain.Decide(MatchScreen.HasConfirm, ocr,
            MatchBrain.ConfirmEscapeStreak, inMatchRecently: false);
        Assert.Equal(MatchActionKind.GamepadBack, offline.Kind);
    }

    [Fact]
    public void Decide_HasConfirm_InMatch_WithoutPushWord_KeepsOldRules()
    {
        // 比赛上下文但帧里没有任何推进词（不是「下一步」弹窗）：不抢老逻辑
        var ocr = Ocr(("FRA", 0.06, 0.055, 0.04, 0.035), ("38:28", 0.27, 0.055, 0.07, 0.035));
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.NotEqual("比赛中弹窗推进键：连点 A 直到消失", action.Target);
    }

    [Fact]
    public void Decide_InMatch_ClockLost_BackToIdleTap()
    {
        // 实机 2026-11-25 那一帧的 OCR 形态：只有球员名/教练名/小图例，没有时钟、没有覆盖层按钮。
        // 用户要求「没有弹窗就 1~4s 随机点击」→ 走 MatchIdleTap（节奏由设置决定），不再快按/慢按
        var ocr = Ocr(
            ("Victor San Bartolomé", 0.56, 0.075, 0.12, 0.02),
            ("Deco", 0.10, 0.885, 0.05, 0.02),
            ("20", 0.10, 0.86, 0.02, 0.02),
            ("14", 0.80, 0.885, 0.02, 0.02));
        var action = MatchBrain.Decide(MatchScreen.InMatch, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
    }

    // ---------------- 弹窗/设置优先于「右下角下一步」（用户 2026-11-25 实机回报两连） ----------------
    // 现场：活动主页弹「奖励已发放至收件箱」，只有一枚屏幕中央的蓝色「确定」。旧版把"确定"也算进
    // 「右下角下一步」快通道（InMatchPushWords 含 确定/确认/OK），于是 200ms 连按 RT/A；可这页
    // RT/A 都不动，只有鼠标点「确定」才关得掉，日志刷满「右下角下一步：立刻点」却原地空转。
    [Fact]
    public void Decide_HasConfirm_CenterConfirmDialog_NotRightBottomNextStep()
    {
        var ocr = Ocr(
            ("奖励已发放至收件箱", 0.36, 0.51, 0.28, 0.05),
            ("确定", 0.45, 0.775, 0.10, 0.045),
            ("前往比赛", 0.86, 0.92, 0.10, 0.03));
        // 赛后 2 分钟内也算 inMatchRecently：这条不再抢快通道，也不走比赛内 600ms 连点
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.DoesNotContain("立刻点", action.Target);
        Assert.NotEqual("比赛中弹窗推进键：连点 A 直到消失", action.Target);
        Assert.True(action.WaitMs >= 1000, $"单键「确定」弹窗要走弹窗阶梯（实际 {action.WaitMs}ms）");
    }

    [Fact]
    public void Decide_HasConfirm_GameSettingsDialog_BeatsNextStep()
    {
        // 游戏启动设定窗（画面模式/分辨率）：确认键是默认焦点，不能被"立刻点"快通道抢走
        var ocr = Ocr(
            ("画面模式", 0.30, 0.40, 0.20, 0.05),
            ("分辨率", 0.30, 0.50, 0.16, 0.05),
            ("确定", 0.45, 0.775, 0.10, 0.045));
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.DoesNotContain("立刻点", action.Target);
    }

    [Fact]
    public void Decide_InMatch_NextStepInsideMatch_StillTapsImmediately()
    {
        // 反向保护：比赛内（时钟在）的「下一步」覆盖层仍走 200ms 快通道——收紧只针对"确定"类弹窗
        var ocr = Ocr(
            ("下一步", 0.84, 0.92, 0.12, 0.045),
            ("FRA", 0.06, 0.055, 0.04, 0.035),
            ("38:28", 0.27, 0.055, 0.07, 0.035));
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, 0, inMatchRecently: true);
        Assert.Equal(MatchActionKind.PadConfirm, action.Kind);
        Assert.Contains("立刻点", action.Target);
        Assert.True(action.WaitMs <= 250, $"比赛内「下一步」仍要立刻点（实际 {action.WaitMs}ms）");
    }
}


