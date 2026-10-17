using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 我的联赛决策大脑（全手柄版）：Classify（YOLO+OCR）与 Decide（纯手柄动作）纯函数单测
public class MyLeagueBrainTests
{
    private static YoloDetection Det(string cls, float score)
        => new(cls, 0, score, 0.4, 0.4, 0.2, 0.1);

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    // ---------------- Classify ----------------

    [Fact]
    public void Classify_MainMenu()
    {
        var dets = new[] { Det("main_menu", 0.9f), Det("player_card", 0.8f) };
        Assert.Equal(MyLeagueBrain.Classify(dets, OcrResult.Empty), MlScreen.MainMenu);
    }

    [Fact]
    public void Classify_ItemList_NeedsTwoRowsAndBack()
    {
        var dets = new[] { Det("item_row", 0.9f), Det("item_row", 0.8f), Det("btn_back", 0.9f) };
        Assert.Equal(MlScreen.ItemList, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_SingleRow_IsNotItemList()
    {
        var dets = new[] { Det("item_row", 0.9f), Det("btn_back", 0.9f) };
        // 单行不足列表特征 → 落入 LeaguePage（仅返回按钮可见）
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_HasConfirm_BeatsLeaguePage()
    {
        var dets = new[] { Det("btn_confirm", 0.9f), Det("btn_back", 0.8f) };
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_LowScore_Ignored()
    {
        var dets = new[] { Det("main_menu", 0.3f), Det("btn_confirm", 0.3f) };
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_MatchHub_ByOcr_WithoutExit()
    {
        // 比赛页无 YOLO 特征：OCR 含"我的联赛"且无主菜单"退出游戏"
        var ocr = Ocr(("我的联赛", 0.3, 0.2), ("赛季目标", 0.6, 0.5));
        Assert.Equal(MlScreen.MatchHub, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_MatchHub_WithBackButton_NotShadowedToLeaguePage()
    {
        // 实机回归（2026-10-10）：比赛选择页有返回按钮 + 播放器卡，曾被误判 LeaguePage 卡死。
        // 判定依据：独立短标题行「比赛」+ 磁贴行特征词（锦标赛/活动）
        var dets = new[] { Det("btn_back", 0.93f), Det("player_card", 0.89f) };
        var ocr = Ocr(("比赛", 0.10, 0.11), ("锦标赛", 0.11, 0.73), ("活动", 0.40, 0.72),
            ("eFootballTM联赛", 0.76, 0.72), ("返回", 0.10, 0.93));
        Assert.Equal(MlScreen.MatchHub, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_MatchHub_FocusedMyLeague_AlsoMatchHub()
    {
        // 右移后焦点在我的联赛：预览区出现"我的联赛"标题（实机帧 matchhub_user_2）
        var dets = new[] { Det("btn_back", 0.93f), Det("player_card", 0.89f) };
        var ocr = Ocr(("比赛", 0.10, 0.11), ("我的联赛", 0.72, 0.37),
            ("在整个赛季中对抗AI", 0.74, 0.42), ("自定义锦标赛", 0.75, 0.72), ("返回", 0.10, 0.93));
        Assert.Equal(MlScreen.MatchHub, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_LeagueInternal_WithMyLeagueText_StaysLeaguePage()
    {
        // 实机回归（224927_423）：我的联赛内部主页也含"我的联赛/比赛日/前往比赛"等字样，
        // 但无独立「比赛」标题行 → 必须仍是 LeaguePage，不能右移乱按
        var dets = new[] { Det("btn_back", 0.9f), Det("player_card", 0.7f) };
        var ocr = Ocr(("排名比赛记录最近5场比赛", 0.62, 0.24), ("前往比赛2积分5517胜4平3负", 0.52, 0.29),
            ("F0OTBALL8FOOYBALL93250我的联赛积分保持1场不失球", 0.39, 0.47), ("比赛日25B期间", 0.43, 0.53),
            ("返回设置比赛计划", 0.34, 0.94));
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_MainMenuOcr_WithExit_IsNotMatchHub()
    {
        // 主菜单特有"退出游戏"，即使 OCR 含"我的联赛"（banner 文字）也不算比赛页
        var ocr = Ocr(("我的联赛", 0.3, 0.5), ("退出游戏", 0.8, 0.75));
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_Empty_IsUnknown()
    {
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), OcrResult.Empty));
    }

    [Fact]
    public void Classify_InMatch_ByClockWord()
    {
        // 实机回归（2026-10-10 15:59 会话）：比赛中 OCR 见 队名缩写+时钟+球员名，无 YOLO 特征
        var ocr = Ocr(("GS", 0.1, 0.05), ("BJK", 0.3, 0.05), ("0:57", 0.2, 0.06), ("LeandroTrossard", 0.5, 0.8));
        Assert.Equal(MlScreen.InMatch, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_ScoreAlone_NotInMatch()
    {
        // 用户约定（2026-10-10）：比分/球队名/球员名每场可变，一律不作为判定点——
        // 只有比分形态词（如 1-0）不触发比赛判定；时钟（固定 UI）才是比分框主判据
        var ocr = Ocr(("AME", 0.08, 0.09), ("GS", 0.20, 0.09), ("1-0", 0.15, 0.088), ("LeandroTrossard", 0.5, 0.4));
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_ClockOutsideScoreboard_NotPrimarySignal()
    {
        // 时钟词出现在比分框区之外（如页面中部文本）不触发比赛主判据
        var ocr = Ocr(("0:57", 0.8, 0.9));
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_TopRightContent_Alone_IsInMatch()
    {
        // 辅助判据：右上角（AI 徽标区）有画面内容 → 比赛中（重播/丢时钟帧兜底）
        Assert.Equal(MlScreen.InMatch,
            MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), OcrResult.Empty, topRightHasContent: true));
    }

    [Fact]
    public void Classify_TopRightContent_NeverBeatsMenuChecks()
    {
        // 辅助判据排在所有菜单判定之后：亮色菜单页（右上也有内容）仍按菜单处理
        var dets = new[] { Det("btn_back", 0.9f) };
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(dets, OcrResult.Empty, topRightHasContent: true));
    }

    [Fact]
    public void Classify_LeaguePage_ByGoMatchWord_WhenYoloMissesBackButton()
    {
        // 2026-10-11 用户实机回归：国际服联赛主页 YOLO 未检出返回按钮，右上角奖励徽章
        // 点亮角区探针 → 被兜底成 InMatch 盲按 A 误开新比赛。「前往比赛」只在联赛主页出现
        var ocr = Ocr(("ManchesterB", 0.2, 0.05), ("前往比赛", 0.15, 0.75), ("赛程表", 0.4, 0.9));
        Assert.Equal(MlScreen.LeaguePage,
            MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr, topRightHasContent: true));
    }

    [Fact]
    public void Classify_NextSeasonWord_AlsoLeaguePage()
    {
        // 赛季结束：前往比赛位置替换为「下个赛季」
        var ocr = Ocr(("下个赛季", 0.15, 0.75));
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_TopRightContent_WithoutMatchContext_IsUnknown()
    {
        // 无比赛上下文时角区探针不作为比赛证据（2026-10-11 用户实机回归：
        // 菜单页右上角内容被兜底成 InMatch 盲按 A）
        Assert.Equal(MlScreen.Unknown,
            MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), OcrResult.Empty,
                topRightHasContent: true, allowCornerInMatch: false));
    }

    [Fact]
    public void Classify_DifficultyPage_StillBeatsGoMatchWord()
    {
        // 难度选择页覆盖在联赛主页上（主页「前往比赛」词仍可见）：必须先判难度页
        var ocr = Ocr(("选择赛事等级", 0.5, 0.08), ("前往比赛", 0.15, 0.75), ("职业", 0.3, 0.4));
        Assert.Equal(MlScreen.DifficultyPage,
            MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr, topRightHasContent: true));
    }

    [Fact]
    public void Classify_DifficultyPage_ByList_WhenTitleMissed()
    {
        // 2026-10-11 用户实机回归（bot-20261011-140943）：难度页标题「选择赛事等级」被
        // OCR 漏读，页面上有难度名列表 + btn_confirm 0.83 → 被判 HasConfirm 盲按 A，
        // 用户配置的难度被跳过（「不能自动选难度」）。列表 + 确认按钮必须判难度页。
        var dets = new[] { Det("btn_confirm", 0.83f), Det("btn_back", 0.28f) };
        var ocr = Ocr(("前往比赛", 0.2, 0.9), ("新手", 0.5, 0.30), ("业余", 0.5, 0.38), ("传奇", 0.5, 0.62));
        Assert.Equal(MlScreen.DifficultyPage, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_SingleDifficultyWord_StillHasConfirm()
    {
        // 单个难度词（如赛后评价页出现"传奇"字样）不构成难度列表，仍按确认按钮推进
        var dets = new[] { Det("btn_confirm", 0.9f) };
        var ocr = Ocr(("传奇", 0.5, 0.4), ("球员评价", 0.5, 0.2));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_LeagueInfoPage_WhiteDocument_NotLeaguePage()
    {
        // 2026-10-11 用户实机回归（bot-20261011-160559 16:07-16:48 卡死 41 分钟）：
        // 误入「我的联赛」说明页（整幅白底文档），正文含「前往比赛」→ 被判 LeaguePage
        // 盲按 A。白底 + btn_back → 必须判 LeagueInfoPage（B 返回主页）。
        var dets = new[] { Det("btn_back", 0.67f) };
        var ocr = Ocr(("我的联赛", 0.5, 0.08), ("前往比赛", 0.4, 0.5), ("赛季", 0.6, 0.6), ("积分", 0.5, 0.7), ("奖励", 0.55, 0.8));
        Assert.Equal(MlScreen.LeagueInfoPage,
            MyLeagueBrain.Classify(dets, ocr, mostlyLightPage: true));
    }

    [Fact]
    public void Classify_DarkPage_WithSameWords_StillLeaguePage()
    {
        // 同样的词出现在深色联赛主页上（mostlyLightPage=false）→ 仍判 LeaguePage
        var dets = new[] { Det("btn_back", 0.67f) };
        var ocr = Ocr(("我的联赛", 0.5, 0.08), ("前往比赛", 0.4, 0.5), ("赛季", 0.6, 0.6), ("积分", 0.5, 0.7), ("奖励", 0.55, 0.8));
        Assert.Equal(MlScreen.LeaguePage,
            MyLeagueBrain.Classify(dets, ocr, mostlyLightPage: false));
    }

    [Fact]
    public void Decide_LeagueInfoPage_WaitsThenBacks()
    {
        // 连测 2 轮再按 B：第 1 轮（streak=0）等待，防白屏过渡帧误退；第 2 轮按 B 返回
        Assert.Equal(MlActionKind.Wait, MyLeagueBrain.Decide(MlScreen.LeagueInfoPage, OcrResult.Empty, 0).Kind);
        var back = MyLeagueBrain.Decide(MlScreen.LeagueInfoPage, OcrResult.Empty, 1);
        Assert.Equal(MlActionKind.GamepadBack, back.Kind);
    }

    [Fact]
    public void Classify_InMatch_BeatsBackButton()
    {
        var dets = new[] { Det("btn_back", 0.9f) };
        var ocr = Ocr(("2:19", 0.2, 0.06));
        Assert.Equal(MlScreen.InMatch, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_ResultConfirm_WithClock_StillHasConfirm()
    {
        // 结算/中场页同时有时钟与确定键：优先按 A 推进
        var dets = new[] { Det("btn_confirm", 0.9f) };
        var ocr = Ocr(("90:00", 0.2, 0.06));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_MenuCountdown_NotInMatch()
    {
        // 菜单倒计时（HH:MM:SS）不是记分牌时钟形态
        var ocr = Ocr(("23:59:59", 0.5, 0.5));
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Decide_InMatch_IdleTaps()
    {
        // 比赛中按用户设定的随机间隔点击 A（挂机节奏）——不再纯等待；
        // B 绝不按（铲球），超时才逃生
        var ocr = Ocr(("0:57", 0.2, 0.06));
        var action = MyLeagueBrain.Decide(MlScreen.InMatch, ocr, 50);
        Assert.Equal(MlActionKind.MatchIdleTap, action.Kind);
    }

    [Fact]
    public void Decide_InMatch_TimeoutEscapes()
    {
        // 远超一场比赛时长（~30 分钟）仍停留才 B 逃生（重播暂停等卡死兜底）
        var action = MyLeagueBrain.Decide(MlScreen.InMatch, OcrResult.Empty, MyLeagueBrain.InMatchEscapeStreak);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }

    [Fact]
    public void Classify_Popup_TodaysGoals_ImmediatelyConfirms()
    {
        // 实机回归（2026-10-10）：进球弹窗「今天的进球：1」叠加在比赛画面上，出现即按 A
        var ocr = Ocr(("AME", 0.083, 0.088), ("GS 14:47 e", 0.261, 0.088), ("今天的进球：1", 0.099, 0.169));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr, topRightHasContent: true));
    }

    [Fact]
    public void Classify_Popup_MatchReplay_ImmediatelyConfirms()
    {
        // 实机回归（2026-10-10）：比赛回放弹窗出现即按 A 下一步
        var ocr = Ocr(("比赛回放", 0.161, 0.099), ("trendyol", 0.68, 0.31));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr, topRightHasContent: true));
    }

    [Fact]
    public void Classify_Scoreboard_EmbeddedClock_IsInMatch()
    {
        // OCR 常把时钟与队名合并（"GS 14:47 e"）——比分框区内子串匹配仍判比赛中
        var ocr = Ocr(("AME", 0.083, 0.088), ("GS 14:47 e", 0.261, 0.088));
        Assert.Equal(MlScreen.InMatch, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_FullTime_ByKeyword_BeatsInMatchSignals()
    {
        // 实机回归（2026-10-10）：终场页顶部「全场」+ 比分 2-2 + 右上有内容——判 FullTime
        var ocr = Ocr(("全场", 0.62, 0.074), ("2-2", 0.619, 0.143), ("下一步", 0.132, 0.287));
        Assert.Equal(MlScreen.FullTime, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr, topRightHasContent: true));
    }

    [Fact]
    public void Classify_FullTime_WithConfirmButton_StillFullTime()
    {
        // YOLO 也检出确认按钮时仍优先 FullTime（更具体的状态，驱动场次关场）
        var dets = new[] { Det("btn_confirm", 0.94f) };
        var ocr = Ocr(("全场", 0.62, 0.074), ("下一步", 0.132, 0.287));
        Assert.Equal(MlScreen.FullTime, MyLeagueBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Decide_FullTime_ConfirmsNext()
    {
        var ocr = Ocr(("全场", 0.62, 0.074));
        var action = MyLeagueBrain.Decide(MlScreen.FullTime, ocr, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.Contains("下一步", action.Target);
    }

    // ---------------- Decide ----------------

    [Fact]
    public void Decide_MainMenu_EnterMatchNavigation()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadEnterMatch, action.Kind);
    }

    [Fact]
    public void Decide_MainMenu_Stuck_SlowsDownNotStops()
    {
        // 导航屏退避放慢继续导航；永久等待已改为 NavEscapeStreak 后周期性 B 逃生
        //（v2.0.7 弹窗链 8 小时空转同款模式，2026-10-17 评审补齐）
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadEnterMatch, action.Kind);
        Assert.True(action.WaitMs >= 5000, "退避后应放慢节奏");
    }

    [Fact]
    public void Decide_MatchHub_NotFocused_MovesRight()
    {
        // 默认焦点不在我的联赛（OCR 预览区看不到）→ 右移一格再观察
        var action = MyLeagueBrain.Decide(MlScreen.MatchHub, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadMoveRight, action.Kind);
    }

    [Fact]
    public void Decide_MatchHub_FocusedMyLeague_Confirms()
    {
        // 预览区（y<0.6）看到我的联赛 = 焦点已到 → A 进入
        var ocr = Ocr(("我的联赛", 0.72, 0.37), ("比赛", 0.10, 0.11));
        var action = MyLeagueBrain.Decide(MlScreen.MatchHub, ocr, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_ItemList_ConfirmsFocusedRow()
    {
        var action = MyLeagueBrain.Decide(MlScreen.ItemList, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_HasConfirm_Confirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    // ---------------- 弹窗链退避不停手（实机 2026-10-16 回归） ----------------
    // 终场后奖励弹窗链 ≥9 页且每页同判 HasConfirm：第 8 帧退避停手后，尾页
    // 「物品已送到收件箱 30,000 GP」永远等不到 A，挂机空转 8 小时。
    // 弹窗/推进类界面退避只放慢节奏，A/X 必须继续按。

    [Fact]
    public void Decide_HasConfirm_Stuck_StillConfirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.True(action.WaitMs >= 5000, "退避后应放慢节奏");
    }

    [Fact]
    public void Decide_HasConfirm_Stuck_WithCancel_StillUpConfirms()
    {
        var ocr = Ocr(("取消", 0.5, 0.6));
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, ocr, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind);
        Assert.True(action.WaitMs >= 5000, "退避后应放慢节奏");
    }

    [Fact]
    public void Decide_FullTime_Stuck_StillConfirms()
    {
        var ocr = Ocr(("全场", 0.62, 0.074));
        var action = MyLeagueBrain.Decide(MlScreen.FullTime, ocr, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_KeyMatchDialog_Stuck_StillUpConfirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.KeyMatchDialog, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind);
    }

    [Fact]
    public void Decide_PurchaseDialog_Stuck_StillUpConfirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.PurchaseDialog, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind);
    }

    [Fact]
    public void Decide_MatchStartPage_Stuck_StillConfirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MatchStartPage, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_TitleScreen_Stuck_StillPressesX()
    {
        // 真标题（掉线/重启）：场景层 30s 宽限只护加载帧，宽限后必须持续按 X，
        // 退避停手会让标题画面永久卡死
        var action = MyLeagueBrain.Decide(MlScreen.TitleScreen, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.PadXPress, action.Kind);
    }

    [Fact]
    public void Decide_NavigationScreens_Stuck_SlowContinue_EscapeAtNav()
    {
        // 导航屏：StuckStreak 后放慢继续原动作（不再 Wait 停手）；到 NavEscapeStreak
        // 周期性（每 5 轮）按 B 逃生，其余轮继续导航
        Assert.Equal(MlActionKind.PadEnterMatch,
            MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, MyLeagueBrain.StuckStreak).Kind);
        Assert.Equal(MlActionKind.PadMoveRight,
            MyLeagueBrain.Decide(MlScreen.MatchHub, OcrResult.Empty, MyLeagueBrain.StuckStreak).Kind);
        Assert.Equal(MlActionKind.PadConfirm,
            MyLeagueBrain.Decide(MlScreen.ItemList, OcrResult.Empty, MyLeagueBrain.StuckStreak).Kind);
        Assert.Equal(MlActionKind.PadConfirm,
            MyLeagueBrain.Decide(MlScreen.PointsPage, OcrResult.Empty, MyLeagueBrain.StuckStreak).Kind);

        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, MyLeagueBrain.NavEscapeStreak).Kind);
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.MatchHub, OcrResult.Empty, MyLeagueBrain.NavEscapeStreak).Kind);
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.ItemList, OcrResult.Empty, MyLeagueBrain.NavEscapeStreak).Kind);
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.PointsPage, OcrResult.Empty, MyLeagueBrain.NavEscapeStreak).Kind);
        Assert.Equal(MlActionKind.PadEnterMatch,
            MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, MyLeagueBrain.NavEscapeStreak + 1).Kind);
    }

    [Fact]
    public void Decide_HasConfirm_EscapeAt30_PressesBack()
    {
        // A 永不生效的「RT 下一步」型页（实机 2026-10-14 对阵表连按 A 8 次不动）：
        // ConfirmEscapeStreak（30）轮后周期性 B 逃生；之前仍按 A 推进（弹窗链 9+ 页到不了）
        var before = MyLeagueBrain.Decide(MlScreen.HasConfirm, OcrResult.Empty, MyLeagueBrain.ConfirmEscapeStreak - 1);
        Assert.Equal(MlActionKind.PadConfirm, before.Kind);
        var escape = MyLeagueBrain.Decide(MlScreen.HasConfirm, OcrResult.Empty, MyLeagueBrain.ConfirmEscapeStreak);
        Assert.Equal(MlActionKind.GamepadBack, escape.Kind);
    }

    [Fact]
    public void Decide_LeaguePage_NextStep_Confirms()
    {
        var ocr = Ocr(("下一步", 0.80, 0.91));
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, ocr, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.Contains("下一步", action.Target);
    }

    [Fact]
    public void Decide_LeaguePage_NoButton_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_EarlyStreak_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, OcrResult.Empty, 3);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_EscapeStreak_GamepadBack()
    {
        // 有 OCR 感知（过场字幕等）时才按 B 逃生
        var ocr = Ocr(("比赛回放", 0.16, 0.14));
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, ocr, MyLeagueBrain.UnknownEscapeStreak);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_BlindPerception_NeverPressesBack()
    {
        // 完全无感知（OCR 0 词，典型：捕获黑屏）绝不盲按 B（2026-10-10 黑帧实机回归）
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, OcrResult.Empty, MyLeagueBrain.UnknownEscapeStreak * 3);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }
}
