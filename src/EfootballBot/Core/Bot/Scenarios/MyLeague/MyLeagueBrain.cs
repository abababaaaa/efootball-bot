using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Capture;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>我的联赛界面状态（由 YOLO 检测特征 + OCR 文字判定）。</summary>
public enum MlScreen
{
    Unknown,    // 无特征界面（过场/加载等）
    MainMenu,   // 游戏主菜单（顶部导航条可见）
    MatchHub,   // 比赛页（OCR 含"我的联赛"，选默认焦点进入）
    ItemList,   // 物品列表页（多行 item_row + 返回按钮）
    HasConfirm, // 存在确定/下一步按钮的页面（弹窗、比赛日结果等）
    LeaguePage, // 我的联赛内部页面（仅返回按钮可见）
    /// <summary>比赛进行中（记分牌时钟可见）。绝不按 B——B 在比赛中是铲球键，只等待比赛结束。</summary>
    InMatch,
    /// <summary>全场结束（比分+球员评价页，含「下一步」）。终场判定：脱离比赛状态，立即按 A。</summary>
    FullTime,
    /// <summary>我的联赛积分页（兑换积分入口，租借球员/我的联赛物品两个选项）。</summary>
    PointsPage,
    /// <summary>物品使用（购买）确认弹窗：「使用物品/消耗我的联赛积分」+ 取消，焦点默认在取消。</summary>
    PurchaseDialog,
    /// <summary>选择赛事等级页（难度选择，覆盖在联赛主页之上，点前往比赛后出现）。</summary>
    DifficultyPage,
    /// <summary>关键比赛弹窗（预告/固定难度，含「OK」按钮）：上移光标点 OK 进入。</summary>
    KeyMatchDialog,
    /// <summary>比赛起始页（队名/球场确认，底部「设置|球场|下一步」）——按 A 开赛，其后进入比赛。</summary>
    MatchStartPage,
    /// <summary>游戏启动标题画面（eFootball 标志 + 「按 X」提示）——按手柄 X 进入。</summary>
    TitleScreen,
    /// <summary>「我的联赛」说明页（整幅白底文档，从主页 i 图标误入）：按 B 返回联赛主页。
    /// 页内正文含「前往比赛」等词，不识别会被误判 LeaguePage 盲按 A 卡死
    /// （实机 bot-20261011-160559 16:07-16:48 卡死 41 分钟）。</summary>
    LeagueInfoPage,
}

public enum MlActionKind
{
    PadEnterMatch,
    PadConfirm,
    GamepadBack,
    /// <summary>比赛选择页右移一格（寻找「我的联赛」磁贴，OCR 未看到焦点在其上时）。</summary>
    PadMoveRight,
    /// <summary>比赛中挂机点击：按 A + 用户设定的随机间隔（挂机节奏，拟人化防零输入判定）。</summary>
    MatchIdleTap,
    /// <summary>弹窗上移确认（购买弹窗焦点默认在取消，需上移点上方带数字的确认按钮）。</summary>
    PadUpConfirm,
    /// <summary>标题画面按 X（eFootball 启动标题的进入键，A 无效）。</summary>
    PadXPress,
    Wait,
}

/// <summary>
/// 一次决策结果。全部动作均为模拟手柄操作（国际服不响应后台鼠标）：
/// PadEnterMatch = 复合导航（下到卡片行 → 左到最左"比赛" → A 进入）；
/// PadConfirm = 手柄 A（确认/下一步/选中焦点项）；GamepadBack = 手柄 B（返回/逃生）。
/// </summary>
public sealed record MlAction(MlActionKind Kind, int WaitMs, string Target = "")
{
    public static MlAction Wait(int ms) => new(MlActionKind.Wait, ms);
}

/// <summary>
/// 我的联赛决策大脑：感知（YOLO 6 类 + OCR）→ 界面分类 → 动作。纯函数，可单测。
/// 动作原则：全程模拟手柄（Dpad 导航 + A 确认 + B 返回）；看不懂就等，
/// 卡住退避等待，无特征界面连续多轮按 B 逃生（跳过过场/退出意外弹窗）。
/// </summary>
public static class MyLeagueBrain
{
    /// <summary>状态判定/点击目标的最低检测置信度。</summary>
    public const float MinScore = 0.45f;

    /// <summary>同一界面连续该轮数后操作退避放慢（导航类界面停手等待）。
    /// 弹窗/推进类界面例外：A/X 是唯一推进键，退避只放慢节奏绝不停止——
    /// 终场后的奖励弹窗常连成一长串且每页同判 HasConfirm（实机 2026-10-16：
    /// ≥9 页弹窗链在第 8 帧退避停手，尾页「物品已送到收件箱」永远等不到 A，空转 8 小时）。</summary>
    public const int StuckStreak = 8;

    /// <summary>导航类界面（主菜单/比赛页/物品/积分页）连续该轮数仍无进展时，从「放慢继续」
    /// 升级为周期性按 B 逃生（清盖层/回上级重新导航）。此前导航屏在 StuckStreak 后永久等待，
    /// OCR 漏读焦点词即无限空转——v2.0.7 弹窗链 8 小时事故同款模式的补齐（2026-10-17 评审）。</summary>
    public const int NavEscapeStreak = 20;

    /// <summary>HasConfirm 弹窗连续该轮数仍无进展时周期性按 B 逃生。正常奖励弹窗链 9+ 页
    /// （每页 1~2 轮）远到不了该值；A 永不生效的「RT 下一步」型页面（实机 2026-10-14 对阵表）
    /// 靠它脱困。数值与国服 CnMyLeague 的 B@30 一致，国服侧其保险先行接管不叠加。</summary>
    public const int ConfirmEscapeStreak = 30;

    /// <summary>无特征界面（比赛/过场）连续该轮数后按 B 逃生。</summary>
    public const int UnknownEscapeStreak = 6;

    /// <summary>
    /// 比赛上下文中的无特征逃生轮数：最近见过比赛特征时（流程帧记忆），无特征界面
    /// 多为镜头切换丢了记分牌时钟——延长观察（~30 轮 ≈ 2 分钟）防误按 B 铲球。
    /// </summary>
    public const int UnknownEscapeStreakInMatch = 30;

    /// <summary>
    /// 比赛中超时逃生轮数：~6s/轮 × 300 ≈ 30 分钟（一场比赛 20~25 分钟）。
    /// 比赛中绝不主动按键，仅当远超一场比赛时长仍停在比赛画面（如重播暂停等输入）才 B 逃生。
    /// </summary>
    public const int InMatchEscapeStreak = 300;

    /// <summary>
    /// 记分牌时钟词（独立短词，如 0:57 / 45:00）。只匹配时钟形态——时钟是比赛 UI 固定元素，
    /// 形态稳定；比分/球队名/球员名每场都变，一律不作为判定点（用户约定 2026-10-10）。
    /// </summary>
    private static readonly Regex ClockStandalone = new(@"^\d{1,3}:[0-5]\d$", RegexOptions.Compiled);

    /// <summary>比分框内嵌时钟（OCR 常把时钟与队名合并成一个词，如 "GS 14:47 e"）。</summary>
    private static readonly Regex ClockEmbedded = new(@"\d{1,3}:[0-5]\d", RegexOptions.Compiled);

    /// <summary>
    /// 比分框区（左上，x&lt;0.45 / y&lt;0.25）：时钟词落在此区 = 比赛进行中（主判据）。
    /// 实机（2026-10-10 16:22 截图）：比分框时钟 "0:31" @y≈0.087。
    /// </summary>
    private static bool HasScoreboard(OcrResult ocr) =>
        ocr.Words.Any(w => w.CenterX < 0.45 && w.CenterY < 0.25 &&
            (ClockStandalone.IsMatch(w.Text.Trim()) ||
             (w.Text.Trim().Length <= 12 && ClockEmbedded.IsMatch(w.Text))));

    /// <summary>
    /// 分类当前界面。<paramref name="topRightHasContent"/> 来自右上角像素探针（Scenario 计算）：
    /// 比赛画面右上角是球场/AI 徽标区（非黑，实机菜单帧右上角为纯黑），
    /// 作为"比赛中"的辅助判据——仅在所有菜单类判定都未命中时兜底，防止误伤亮色菜单页。
    /// <paramref name="allowCornerInMatch"/> = 流程记忆里最近真的见过比赛（场景层传入）：
    /// 菜单页右上角也可能有内容（2026-10-11 用户实机：国际服联赛主页奖励徽章点亮角区探针，
    /// 被兜底成 InMatch 盲按 A 误开新比赛），无比赛上下文时角区探针不作为比赛证据。
    /// <paramref name="mostlyLightPage"/> 来自整幅亮度探针（Scenario 计算）：中央区域大面积
    /// 白色 = 「我的联赛」说明页（全流程唯一白底页面，深色主题下强特征）。
    /// </summary>
    public static MlScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr,
        bool topRightHasContent = false, bool allowCornerInMatch = true, bool mostlyLightPage = false)
    {
        int Count(string name) => dets.Count(d => d.ClassName == name && d.Score >= MinScore);

        // 选择赛事等级页（难度选择）：置顶判定——该页覆盖在联赛主页上，画面同时含
        // 前往比赛/赛程表等主页词，不先判会被 LeaguePage 抢走盲按 A（实机 difficulty_2~4）
        if (ocr.Lines.Any(l => l.CenterY < 0.15 && l.Text.Contains("选择赛事等级")))
            return MlScreen.DifficultyPage;

        // 关键比赛弹窗（预告 P2「下一场是关键比赛」/ 固定难度 P3「即将进行的是关键比赛」）：
        // 含「关键比赛」+「OK」。必须先于 MatchHub 兜底——弹窗文案带"我的联赛积分"字样会被误判。
        // 「重要比赛」为同义别名（用户反馈用词，防游戏文案改版）；OK 是白底白字小字，OCR 常
        // 漏读或读成 0K（用户 2026-10-15 实测：传奇级弹窗整帧漏掉 OK → 落 HasConfirm 盲按 A
        // → 按在默认焦点「取消」上 → 前往比赛↔弹窗死循环），词级做 O/0 归一再认一次
        if ((ocr.ContainsJoined("关键比赛") || ocr.ContainsJoined("重要比赛")) && HasOkButton(ocr))
            return MlScreen.KeyMatchDialog;

        // 比赛起始页（实机 match_start_page）：底部行「设置|球场|下一步」+ 队名/球场名
        // （eFootball™ Stadium）。先于通用 btn_confirm：判定为起始页可带上"即将开赛"上下文
        if (ocr.Lines.Any(l => l.CenterY > 0.85 && l.Text.Contains("下一步")) &&
            (ocr.ContainsJoined("球场") || ocr.ContainsJoined("Stadium")))
            return MlScreen.MatchStartPage;

        // 游戏启动设定窗（画面模式/分辨率，实机 2026-10-11：启动/更新后弹「设定」需确认一次）
        // 与启动标题画面（eFootball 标志 + 「按 X」提示）：都必须先于 InMatch 角区兜底——
        // 否则标题画面的标志内容会触发角区探针被误判 InMatch 盲按 A（A 无法离开标题画面）
        if (ocr.ContainsJoined("画面模式") || ocr.ContainsJoined("分辨率"))
            return MlScreen.HasConfirm; // 设定窗：默认光标在确认，按 A 保留当前设置继续
        // 标题画面：几乎只有 eFootball 标志 + X 提示（纯 ASCII，无中文）；
        // 比赛页的「eFootball™联赛」磁贴也含 footbal——必须有中文词排除
        if (ocr.Words.Count is > 0 and <= 5 &&
            ocr.Words.All(w => !w.Text.Any(ch => ch >= 0x2E80)) &&
            ocr.ContainsJoined("footbal"))
            return MlScreen.TitleScreen;

        if (Count("main_menu") >= 1) return MlScreen.MainMenu;
        if (Count("item_row") >= 2 && Count("btn_back") >= 1) return MlScreen.ItemList;

        // 全场结束（FullTime）终场判定：顶部「全场」+ 比分/球员评价页（实机 2026-10-10 截图：
        // "全场"@(0.62,0.074)、比分 2-2、下一步按钮）。优先于一切比赛判定——
        // 该页右上角有内容、且可能残留比分形态词，不先判会被误归 InMatch 继续随机点击
        if (ocr.Words.Any(w => w.CenterY < 0.35 && w.Text.Replace(" ", "").Contains("全场")))
            return MlScreen.FullTime;

        // 物品购买确认弹窗：标题「使用物品」/「消耗我的联赛积分」+「取消」，
        // 焦点默认在取消——必须上移点上方带数字（积分价格）的按钮（实机 2026-10-10 flow_8）。
        // 先于 YOLO btn_confirm：该弹窗也检出确认按钮，直接按 A 会点到「取消」
        if (ocr.ContainsJoined("取消") &&
            (ocr.ContainsJoined("使用物品") || ocr.ContainsJoined("消耗我的联赛积分")))
            return MlScreen.PurchaseDialog;

        // 难度选择页兜底：标题「选择赛事等级」被 OCR 漏读时，用「确认按钮 + 纵向难度名列表」识别。
        // （实机 bot-20261011-140943：该页 OCR 30 词含难度名、btn_confirm 0.83，但标题词漏读 →
        //   被判 HasConfirm 盲按 A，用默认难度开赛——用户反馈「不能自动选难度」）
        if (Count("btn_confirm") >= 1 && HasDifficultyList(ocr))
            return MlScreen.DifficultyPage;

        // 确认按钮优先于比赛判定：比赛结算/中场页面若同时出现时钟与确定键，按 A 推进
        if (Count("btn_confirm") >= 1) return MlScreen.HasConfirm;

        // 赛中/赛后弹窗（2026-10-10 实机截图）：「今天的进球」「比赛回放」弹出即按 A 下一步，
        // 必须先于比赛判定——这两种弹窗背景仍是球场（角区有内容），否则被判 InMatch 干等
        if (ocr.ContainsJoined("今天的进球") || ocr.ContainsJoined("比赛回放"))
            return MlScreen.HasConfirm;

        // 我的联赛积分页：顶部标题「我的联赛积分」+「租借球员/我的联赛物品」两个选项
        // （实机 flow_5：标题 @y0.10；联赛主页里的"我的联赛积分"字样都在 y>0.4，不冲突）
        bool pointsTitle = ocr.Lines.Any(l => l.CenterY < 0.2 && l.Text.Contains("我的联赛积分"));
        if (pointsTitle && ocr.ContainsJoined("租借球员"))
            return MlScreen.PointsPage;

        // 比赛主判据①：左上比分框（队名缩写+时钟/比分）
        if (HasScoreboard(ocr)) return MlScreen.InMatch;

        // 「我的联赛」说明页（整幅白底文档，从主页 i 图标误入）：必须先于一切 LeaguePage
        // 判定——页内正文含「前往比赛」字样，误判 LeaguePage 会盲按 A 卡死
        // （实机 bot-20261011-160559 16:07-16:48 卡死 41 分钟，预览 OCR 30 词/btn_back 0.67）
        if (mostlyLightPage && ocr.Words.Count >= 5)
            return MlScreen.LeagueInfoPage;

        // 比赛选择页（我的联赛所在磁贴行）。必须先于 btn_back 判定：该页有返回按钮，
        // 否则被误判 LeaguePage 无限等待（2026-10-10 实机复现：比赛页卡死不选我的联赛）。
        // 我的联赛内部页虽也含「我的联赛」字样，但无独立「比赛」标题行 + 磁贴行特征词。
        if (IsMatchHub(ocr)) return MlScreen.MatchHub;

        // 我的联赛主页 OCR 兜底（YOLO 漏检返回按钮时）：「前往比赛/下个赛季」只出现在
        // 联赛主页（下个赛季 = 赛季结束替换词）。难度页/关键比赛弹窗/起始页均已先判，
        // 覆盖"弹窗叠在主页上、主页词同时可见"的情况（2026-10-11 用户实机回归）。
        if (ocr.ContainsJoined("前往比赛") || ocr.ContainsJoined("下个赛季"))
            return MlScreen.LeaguePage;

        if (Count("btn_back") >= 1) return MlScreen.LeaguePage;

        // 比赛页无 YOLO 训练特征时的兜底：含"我的联赛"且非主菜单（主菜单含"退出游戏"）
        bool hasMyLeague = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("我的联赛"));
        bool hasExit = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("退出游戏"));
        if (hasMyLeague && !hasExit) return MlScreen.MatchHub;

        // 比赛辅助判据②：右上角（AI 徽标区）有画面内容——重播/庆祝/镜头切换等
        // 无比分框可读的比赛帧兜底为比赛中（只等待不按键）；菜单右上角为纯黑不会命中。
        // 无比赛上下文（allowCornerInMatch=false）时不兜底——菜单页右上角也可能有内容，
        // 此时宁可判 Unknown 等一拍（逃生/推进逻辑自会处理），绝不把菜单当比赛盲按 A。
        if (topRightHasContent && allowCornerInMatch) return MlScreen.InMatch;

        return MlScreen.Unknown;
    }

    /// <summary>
    /// 比赛选择页判定：顶部标题行恰为「比赛」（短行、y&lt;0.25）+ 磁贴行特征词
    /// （锦标赛/自定义锦标赛/活动/我的联赛，用户实机两帧验证：焦点不在时看不到我的联赛，
    /// 焦点在时预览区显示我的联赛+描述，两态都有磁贴行词）。
    /// </summary>
    private static bool IsMatchHub(OcrResult ocr)
    {
        bool hasTitle = ocr.Lines.Any(l => l.Text == "比赛" && l.CenterY < 0.25);
        if (!hasTitle) return false;
        return ocr.ContainsJoined("锦标赛") || ocr.ContainsJoined("自定义")
            || ocr.ContainsJoined("活动") || ocr.ContainsJoined("我的联赛");
    }

    /// <summary>OCR 里是否出现「纵向排列的 ≥2 个难度名」（难度列表页特征；
    /// 关键比赛受限页只剩 2 行也命中。单难度词（如球员评价里的"传奇"字样）不构成列表。</summary>
    private static bool HasDifficultyList(OcrResult ocr) =>
        Difficulties
            .Select(d => ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains(d)))
            .OfType<OcrWord>()
            .Select(w => Math.Round(w.CenterY, 1))
            .Distinct()
            .Count() >= 2;

    /// <summary>
    /// OK 按钮词证据：拼接文含 OK（大小写不敏感，覆盖被并进邻词的情况），或某词做 O/0
    /// 归一后恰为 OK（OCR 白底白字小字常读成 0K）。P1 受限难度页含「关键比赛」副标题但
    /// 无 OK 类词，不会被本证据误判成关键比赛弹窗（keymatch_p1 实测帧守）。
    /// </summary>
    public static bool HasOkButton(OcrResult ocr) =>
        ocr.ContainsJoined("OK") ||
        ocr.Words.Any(w => OcrText.Norm(w.Text).Replace("0", "O")
            .Equals("OK", StringComparison.OrdinalIgnoreCase));

    /// <summary>难度选择页特征（标题行或 ≥2 难度名列表）：确认键是默认焦点，不上移改难度。</summary>
    private static bool IsDifficultyChoice(OcrResult ocr) =>
        ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("选择赛事等级")) || HasDifficultyList(ocr);

    /// <summary>游戏启动设定窗（画面模式/分辨率）：确认键是默认焦点，不上移。</summary>
    private static bool IsGameSettingsDialog(OcrResult ocr) =>
        ocr.ContainsJoined("画面模式") || ocr.ContainsJoined("分辨率");

    /// <summary>
    /// 按当前界面 + OCR + 连续轮数决策。<paramref name="inMatchRecently"/> 来自流程帧记忆
    /// （最近刚见过比赛特征）：为 true 时无特征界面按比赛过场处理，延长 B 逃生耐心。
    /// </summary>
    public static MlAction Decide(MlScreen screen, OcrResult ocr, int screenStreak, bool inMatchRecently = false)
    {
        switch (screen)
        {
            case MlScreen.MainMenu:
            {
                // 复合导航：Dpad 下（确保焦点在底部卡片行）→ 左 ×6（推到最左"比赛"，到头多按无害）→ A 进入。
                // 超过 StuckStreak 只放慢继续导航；到 NavEscapeStreak 仍无效说明有识别不到的盖层/
                // 焦点异常，按 B 清一次盖层（主菜单 B 最坏情况是弹退出确认窗 → HasConfirm 上移+确认
                // 退出 → 标题画面 X 重进，仍可自愈），此后每 5 轮一次——好过静默死等。
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return new MlAction(MlActionKind.GamepadBack, 2400, "主菜单导航失效，B 清盖层重试");
                return new MlAction(MlActionKind.PadEnterMatch,
                    screenStreak >= StuckStreak ? 5000 : 3000, "主菜单→比赛卡片");
            }
            case MlScreen.MatchHub:
            {
                // 右移选中「我的联赛」磁贴：进入比赛页后默认焦点不在其上（实机需右移一次）。
                // OCR 预览区（y<0.6，磁贴行标签在 y≈0.72）看到「我的联赛」= 焦点已到 → A 进入；
                // 否则右移一格，下一轮观察确认——避免盲按 A 进错模式。
                // 焦点词被 OCR 漏读时会无限右移，≥NavEscapeStreak 按 B 回主菜单重新导航。
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return new MlAction(MlActionKind.GamepadBack, 2400, "比赛页导航失效，B 回主菜单重导航");
                bool focused = ocr.Lines.Any(l => l.Text.Contains("我的联赛") && l.CenterY < 0.6);
                return focused
                    ? new MlAction(MlActionKind.PadConfirm,
                        screenStreak >= StuckStreak ? 5000 : 2500, "比赛页→我的联赛")
                    : new MlAction(MlActionKind.PadMoveRight,
                        screenStreak >= StuckStreak ? 5000 : 1500, "比赛页右移→我的联赛");
            }
            case MlScreen.ItemList:
            {
                // 物品列表：A 选中当前焦点行；多轮无效（≥NavEscapeStreak）B 退出由购买状态机重导航
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return new MlAction(MlActionKind.GamepadBack, 2400, "物品列表多轮无进展，B 退出重进");
                return new MlAction(MlActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "物品列表焦点行");
            }
            case MlScreen.HasConfirm:
            {
                // 带「取消」的弹窗（关键比赛 P3：OK 在上、取消在下且是默认焦点；购买弹窗同型）：
                // 盲按 A 正好按在取消上把弹窗关掉。先上移再 A——单按钮页上移无害，双按钮弹窗
                // 恰好从取消挪到确认键。难度选择页除外（其确认键就是默认焦点，上移反而进选项
                // 列表改难度）；启动设定窗除外（按 A 保留设置继续）。
                // 超过退避轮数只放慢不停手：弹窗链的第 9+ 页仍需要 A 推进（见 StuckStreak 注释）。
                // 到 ConfirmEscapeStreak 仍无进展（A 永不生效的 RT 型页）：B 退出该页自愈。
                if (screenStreak >= ConfirmEscapeStreak && (screenStreak - ConfirmEscapeStreak) % 5 == 0)
                    return new MlAction(MlActionKind.GamepadBack, 2400, "弹窗久按无效，B 逃生");
                bool slow = screenStreak >= StuckStreak;
                if (ocr.ContainsJoined("取消") && !IsDifficultyChoice(ocr) && !IsGameSettingsDialog(ocr))
                    return new MlAction(MlActionKind.PadUpConfirm, slow ? 5000 : 2000, "弹窗确认（上移避开取消）");
                return new MlAction(MlActionKind.PadConfirm, slow ? 5000 : 2000, "确定/下一步");
            }
            case MlScreen.LeaguePage:
            {
                // 「前往比赛」= 联赛主页主按钮；「下个赛季」= 赛季结束替换词（实机 next_season：
                // 前往比赛位置变为"下个赛季"，另有"前往下个赛季"，含"下个赛季"即覆盖）
                var go = FindLine(ocr, "下一步", "开始比赛", "继续", "前往比赛", "下个赛季");
                if (go is not null)
                    return new MlAction(MlActionKind.PadConfirm, 2500, go);
                // 无任何可推进关键词（赛程表/联赛子页等，实机 2026-10-10 21:18 卡死 4 分钟）：
                // 连续多轮无关键词 → 按 B 返回上一页自愈，绝不无限等待
                return screenStreak >= StuckStreak
                    ? new MlAction(MlActionKind.GamepadBack, 3000, "联赛页无关键词，B 返回自愈")
                    : MlAction.Wait(2500);
            }
            case MlScreen.FullTime:
            {
                // 终场：脱离比赛状态，立即按 A（下一步）。结算/里程碑多页链同理退避只放慢
                return new MlAction(MlActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2000, "全场结束→下一步");
            }

            case MlScreen.KeyMatchDialog:
            {
                // 关键比赛弹窗：光标上移一格点 OK（实机 P2/P3：OK 上方/下方默认位不对齐）。
                // 上移+A 是唯一关法，退避只放慢不停手
                return new MlAction(MlActionKind.PadUpConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "关键比赛→OK");
            }

            case MlScreen.MatchStartPage:
            {
                // 起始页：按 A（下一步）开赛；比赛画面出现后由比分框时钟进入 InMatch（自动点击）
                return new MlAction(MlActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 3000, "起始页→下一步开赛");
            }

            case MlScreen.TitleScreen:
            {
                // 标题画面：进入键是手柄 X（A 无效），按 X 进入主菜单。
                // 场景层的 30s 宽限（区分加载画面）期间不按键，宽限后必须持续按 X——
                // 退避停手会让真标题（掉线/游戏重启）永久卡死
                return new MlAction(MlActionKind.PadXPress,
                    screenStreak >= StuckStreak ? 5000 : 3000, "标题画面→X");
            }

            case MlScreen.LeagueInfoPage:
            {
                // 误入「我的联赛」说明页：B 返回联赛主页。连测 2 轮（streak≥1）再退，
                // 防界面切换的白屏过渡帧误判后误按 B 退出联赛。
                if (screenStreak < 1) return MlAction.Wait(1200);
                return new MlAction(MlActionKind.GamepadBack, 2500, "说明页→B 返回主页");
            }

            case MlScreen.PurchaseDialog:
            {
                // 购买弹窗：焦点默认在「取消」→ 上移一格点上方带数字（积分价）的确认按钮。
                // 上移+A 是唯一买法，退避只放慢不停手
                return new MlAction(MlActionKind.PadUpConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "购买确认（上移→数字按钮）");
            }

            case MlScreen.PointsPage:
            {
                // 积分页：进入「我的联赛物品」（右选项）。导航序列由场景层校验重试；
                // ≥NavEscapeStreak 按 B 回退重新导航（此前永久等待）
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return new MlAction(MlActionKind.GamepadBack, 2400, "积分页导航失效，B 回退重导航");
                return new MlAction(MlActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "积分页→我的联赛物品");
            }

            case MlScreen.InMatch:
            {
                // 比赛中超时兜底：远超一场比赛时长仍停留（重播暂停等）才 B 逃生
                if (screenStreak >= InMatchEscapeStreak)
                    return new MlAction(MlActionKind.GamepadBack, 3000, "比赛超时 B 逃生");
                // 比赛中挂机：按 A + 用户设定的随机间隔（设置→挂机节奏），
                // 拟人化避免全程零输入；A 在比赛中是传球/逼抢，不会暂停或退出
                return new MlAction(MlActionKind.MatchIdleTap, 0, "比赛中随机点击");
            }

            default:
            {
                // 比赛上下文中的弹窗（用户实测 2026-10-10：右上角通知/满级提示等带
                // 「下一步/OK/确认」按钮）→ 直接按 A 推进，不等待
                if (inMatchRecently &&
                    (ocr.ContainsJoined("下一步") || ocr.ContainsJoined("OK") || ocr.ContainsJoined("确认")))
                    return new MlAction(MlActionKind.PadConfirm, 2500, "比赛弹窗→下一步");

                // 比赛/过场/加载：连续多轮无特征时按 B 逃生。
                // 完全无感知（OCR 0 词，典型：捕获黑屏/驱动异常）时绝不盲按 B
                // （2026-10-10 15:48 实机：黑帧下盲按 B 9 次），只等待。
                // 比赛上下文（最近见过比赛特征）：多为镜头切换丢时钟，延长逃生耐心防铲球。
                int escapeAt = inMatchRecently ? UnknownEscapeStreakInMatch : UnknownEscapeStreak;
                if (screenStreak >= escapeAt && ocr.Words.Count > 0)
                    return new MlAction(MlActionKind.GamepadBack, 3000, "B 键逃生");
                return MlAction.Wait(4000);
            }
        }
    }

    /// <summary>在 OCR 排版行里找包含任一关键词的行，返回命中词。</summary>
    private static string? FindLine(OcrResult ocr, params string[] keywords)
    {
        foreach (var line in ocr.Lines)
        {
            var text = line.Text.Replace(" ", "");
            foreach (var kw in keywords)
                if (text.Contains(kw))
                    return kw;
        }
        return null;
    }

    // ---------------- 我的联赛道具购买 ----------------

    /// <summary>道具类别（与游戏内「我的联赛物品」页一一对应，名称随用户设置 UI）。</summary>
    public enum MlItemKind { ExpBoost, CoachBoost, ConditionBoost }

    /// <summary>一个待购道具目标：类别 + 档位（x2/x3/x4；普通/好/极佳；教练强化无档位置空）。</summary>
    public sealed record MlItemTarget(MlItemKind Kind, string Level = "");

    /// <summary>道具行匹配结果：行文本 + 该行在物品列表区的序号（0 起，自上而下）+ 是否使用中（已购买）。</summary>
    public sealed record MlItemRow(int Index, string Text, double CenterY, bool InUse);

    /// <summary>
    /// 物品页固定行序（用户提供，2026-10-10）：
    /// 第1~3行=状态加成（普通+/好+/杰出），第4~6行=经验值加成（2x/3x/4x），第13行=主教练强化。
    /// 导航以固定行号为准（顶部锚定后下移），OCR 仅做「使用中」预检。
    /// </summary>
    public static int ExpectedItemRow(MlItemTarget target) => target.Kind switch
    {
        MlItemKind.ConditionBoost => target.Level switch { "好+" => 2, "杰出" => 3, _ => 1 },
        MlItemKind.ExpBoost => target.Level switch { "3x" => 5, "4x" => 6, _ => 4 },
        MlItemKind.CoachBoost => 13,
        _ => 1,
    };

    /// <summary>目标按物品页行序升序排序（如 杰出3 → 4x6 → 教练13 依次购买，增量下移不回顶）。</summary>
    public static List<MlItemTarget> SortByItemRow(IEnumerable<MlItemTarget> targets)
        => targets.OrderBy(ExpectedItemRow).ToList();

    // ---------------- 难度选择页 ----------------

    /// <summary>难度选项（与游戏内「选择赛事等级」一一对应，顺序=列表行序第 1~7 行）。</summary>
    public static readonly string[] Difficulties =
        { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };

    /// <summary>难度名 → 列表行号（1~7，自上而下）。未知名返回 1。</summary>
    public static int DifficultyRowNumber(string name)
    {
        int i = Array.IndexOf(Difficulties, name);
        return i >= 0 ? i + 1 : 1;
    }

    /// <summary>
    /// 该难度页是否按游戏默认直接确认（不改选难度）：关键比赛的难度由游戏指定——
    /// 预告上下文（<paramref name="nextMatchIsKey"/>：预告弹窗/主页预告）或页面受限横幅
    /// （OCR 含「关键比赛」）任一命中即尊重默认。用户 2026-10-12 实机反馈
    /// （bot-20261012-015835 02:00）：受限页曾被当普通页导航改选（按了↑），要求关键
    /// 比赛一律不按↑。
    /// </summary>
    public static bool RespectGameDifficulty(bool nextMatchIsKey, OcrResult ocr) =>
        nextMatchIsKey || ocr.ContainsJoined("关键比赛");

    /// <summary>
    /// 检出当前难度页实际可用的选项（按 Y 自上而下）。普通页 7 项全有；
    /// 关键比赛受限页只出现高档位若干项（实机 P1：仅 超级球星+传奇 2 项）。
    /// 导航公式按实际项数 N 自适应：上×(N+1-k)。
    /// </summary>
    public static List<string> DetectDifficultyOptions(OcrResult ocr) =>
        Difficulties
            .Select(d => (Name: d, Word: ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains(d))))
            .Where(t => t.Word is not null)
            .OrderBy(t => t.Word!.CenterY)
            .Select(t => t.Name)
            .ToList();

    /// <summary>
    /// 检测游戏内当前已选难度（蓝勾）：各难度词左右两侧窗口（±0.06、y±0.03）的蓝色像素数
    /// （B&gt;120 且高于 R/G 40+），合计最大且 ≥20 的行为选中（实机三帧：传奇24 / 新手151 / 新手179，
    /// 未选中行全部为 0）。返回 null = 未检出。
    /// </summary>
    public static string? DetectSelectedDifficulty(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;

        string? best = null;
        int bestBlue = 0;
        foreach (var diff in Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int blue = 0;
            for (int y = (int)((word.CenterY - 0.03) * h); y < (int)((word.CenterY + 0.03) * h); y += 2)
            {
                for (int side = 0; side < 2; side++)
                {
                    double xa = side == 0 ? word.CenterX - word.W / 2 - 0.06 : word.CenterX + word.W / 2;
                    double xb = side == 0 ? word.CenterX - word.W / 2 : word.CenterX + word.W / 2 + 0.06;
                    for (int x = Math.Max(0, (int)(xa * w)); x < Math.Min(w, (int)(xb * w)); x += 2)
                    {
                        if (y < 0 || y >= h) continue;
                        int i = (y * w + x) * 4;
                        if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40) blue++;
                    }
                }
            }
            if (blue > bestBlue) { bestBlue = blue; best = diff; }
        }
        return bestBlue >= 20 ? best : null;
    }

    /// <summary>
    /// 在物品列表 OCR 行中找目标道具行。物品区为页面主体（y 0.22~0.90），
    /// 按类别关键词 + 档位词匹配（x2/x3/x4 匹配数字位；状态档位匹配中文词）。
    /// 返回 null 表示本页没有该道具。同一行文本不可变（固定 UI 词），比分/球队名不参与。
    /// </summary>
    public static MlItemRow? FindItemRow(OcrResult ocr, MlItemTarget target)
    {
        // 物品区候选行（排除标题/底栏），按 Y 自上而下编号
        var rows = ocr.Lines
            .Where(l => l.CenterY is > 0.22 and < 0.90)
            .OrderBy(l => l.CenterY)
            .ToList();

        bool IsUse(OcrLine l) => l.Text.Contains("使用中");
        int wantExp = int.Parse(ExpDigit(target.Level)!);

        // 命中优先级（实机 itempage_inuse：已购行显示「使用中」且变灰）：
        // ① 已拥有：同类「使用中」行且档位足够（4x 在用满足 2x 需求）→ 返回 InUse=true（跳过购买）
        // ② 精确档位的可购行（非使用中）
        // ③ 类别兜底（档位词没读到，非使用中）
        OcrLine? hit = target.Kind switch
        {
            MlItemKind.ExpBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("经验值") && IsUse(l) &&
                                          LevelDigit(l.Text) is { } d && int.Parse(d) >= wantExp)
                ?? rows.FirstOrDefault(l => l.Text.Contains("经验值") && !IsUse(l) &&
                                             LevelDigit(l.Text) == ExpDigit(target.Level))
                ?? rows.FirstOrDefault(l => l.Text.Contains("经验值") && !IsUse(l)),
            MlItemKind.CoachBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("教练") && IsUse(l))
                ?? rows.FirstOrDefault(l => l.Text.Contains("教练") && !IsUse(l)),
            MlItemKind.ConditionBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("状态") && IsUse(l) &&
                                          CondRank(l.Text) >= CondRank(target.Level))
                ?? rows.FirstOrDefault(l => l.Text.Contains("状态") && !IsUse(l) && l.Text.Contains(CondWord(target.Level)))
                ?? rows.FirstOrDefault(l => l.Text.Contains("状态") && !IsUse(l)),
            _ => null,
        };
        if (hit is null) return null;
        int index = rows.IndexOf(hit);
        if (index < 0) return null;

        // 「使用中」也可能以独立标记行出现在同行带（Y 相近）
        bool inUse = IsUse(hit) ||
                     rows.Any(r => !ReferenceEquals(r, hit) &&
                                   Math.Abs(r.CenterY - hit.CenterY) < 0.05 && IsUse(r));
        return new MlItemRow(index, hit.Text, hit.CenterY, inUse);
    }

    /// <summary>行文本里的加成数字位（游戏格式"4x"，OCR 偶见"x4"；无则 null）。</summary>
    private static string? LevelDigit(string lineText)
    {
        var t = lineText.Replace("×", "x").Replace("X", "x");
        var m = Regex.Match(t, @"(\d)\s*x|x\s*(\d)");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static string? ExpDigit(string level) =>
        level switch { "3x" => "3", "4x" => "4", _ => "2" };

    /// <summary>状态档位词（OCR 常把「普通+」的加号丢掉，匹配时去掉加号）。</summary>
    private static string CondWord(string level) => level.TrimEnd('+');

    /// <summary>状态档位序：普通 0 &lt; 好 1 &lt; 杰出 2（高档位满足低档位需求）。</summary>
    internal static int CondRank(string word) => word switch
    {
        var w when w.Contains("杰出") => 2,
        var w when w.Contains("好") => 1,
        _ => 0,
    };

    /// <summary>
    /// 联赛主页右侧「使用的物品」面板（右摇杆拨出）：判定目标道具是否已被拥有。
    /// 经验值看档位数字（高档满足低档）；状态看档位序；教练看关键词。
    /// 实机（flow_3）：面板行如 "Exp经验值4x"、"我的联赛积分物品剩余比赛场次"。
    /// </summary>
    public static bool IsOwned(OcrResult ocr, MlItemTarget target) => target.Kind switch
    {
        MlItemKind.ExpBoost => ocr.Lines.Any(l =>
            l.Text.Contains("经验值") && LevelDigit(l.Text) is { } d && int.Parse(d) >= int.Parse(ExpDigit(target.Level)!)),
        MlItemKind.CoachBoost => ocr.Lines.Any(l => l.Text.Contains("教练")),
        MlItemKind.ConditionBoost => ocr.Lines.Any(l =>
            l.Text.Contains("状态") && CondRank(l.Text) >= CondRank(target.Level)),
        _ => false,
    };

    /// <summary>联赛主页右侧面板当前显示的是「使用的物品」（右摇杆拨出的状态）。</summary>
    public static bool ShowsItemsPanel(OcrResult ocr) => ocr.ContainsJoined("使用的物品");

    /// <summary>联赛主页右侧面板当前显示的是「挑战」（默认状态，需右拨切换）。</summary>
    public static bool ShowsChallengePanel(OcrResult ocr) => ocr.ContainsJoined("挑战");

    // ---------------- 赛后经验值页（球员数判定） ----------------

    /// <summary>
    /// 赛后「经验值」页（实机 2026-10-17 截图 myleague_exp_result_page.png）：比赛推进链中的
    /// 一页——页首大字标题「经验值」+ 球员格（每格「等级 N/N」+「加成:+N%」两行）+
    /// 底部「Y 查看经验值」提示 + 右下「下一步」。判据只用固定 UI 词，球员名不参与：
    /// 主判「查看经验值」（全流程唯一）；底部提示漏读时兜底=页首标题「经验值」（y&lt;0.2）
    /// 且页内确有球员行——物品页行/主页面板行同样含「经验值」字样但都在 y&gt;0.2，单凭标题会误判。
    /// </summary>
    public static bool IsExpResultPage(OcrResult ocr)
    {
        if (ocr.ContainsJoined("查看经验值")) return true;
        return ocr.Lines.Any(l => l.CenterY < 0.2 && l.Text.Contains("经验值")) &&
               CountExpResultPlayers(ocr) > 0;
    }

    /// <summary>同一球员「等级 N/N」与「加成:+N%」两行的 Y 差上限（实机 ≈0.03；不同球员行距 ≥0.12）。</summary>
    private const double ExpPlayerLineGap = 0.08;

    /// <summary>左右两列球员格的 X 分界（两列中心距 ≈0.43，列内词 X 抖动 ≲0.05）。</summary>
    private const double ExpPlayerColumnGap = 0.2;

    /// <summary>
    /// 数经验值页显示的球员数：2 列网格每格一名球员，格内「等级 N/N」+「加成:+N%」两行，
    /// OCR 漏读其一仍按现存词计。先按 X 聚成左右列，列内按 Y 聚球员（相邻两行 Y 差小归同人，
    /// 上下球员行距大分人）。「等级/加成」是每格固定 UI 词；球员名/头像数每场都变，禁作判定点。
    /// </summary>
    public static int CountExpResultPlayers(OcrResult ocr)
    {
        var words = ocr.Words
            .Where(w =>
            {
                var t = OcrText.Norm(w.Text);
                return t.Contains("等级") || t.Contains("加成");
            })
            .OrderBy(w => w.CenterX)
            .ToList();
        if (words.Count == 0) return 0;

        int count = 0;
        int i = 0;
        while (i < words.Count)
        {
            // 按 X 聚列（锚=列内最左词）
            double colLeft = words[i].CenterX;
            var column = new List<OcrWord>();
            for (; i < words.Count && words[i].CenterX - colLeft < ExpPlayerColumnGap; i++)
                column.Add(words[i]);

            // 列内按 Y 聚球员：相邻词 Y 差 ≤0.08 归同一名（等级/加成两行差 ≈0.03）
            column.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
            double? lastY = null;
            foreach (var w in column)
            {
                if (lastY is null || w.CenterY - lastY.Value > ExpPlayerLineGap)
                    count++;
                lastY = w.CenterY;
            }
        }
        return count;
    }

    // ---------------- 联赛主页光标检测与智能导航 ----------------

    /// <summary>主页可聚焦磁贴（固定 UI 词；「i」为前往比赛旁的信息图标，无文字不可检）。</summary>
    public static readonly (string Keyword, string Name)[] HomeTiles =
    {
        ("前往比赛", "前往比赛"), ("赛程表", "赛程表"), ("兑换积分", "兑换积分"),
        ("赛季纪", "赛季纪录"), ("赛事", "赛事信息"), ("建议", "建议"),
    };

    /// <summary>
    /// 检测联赛主页当前聚焦的磁贴：采样各磁贴文字框的平均亮度（聚焦变白）。
    /// 阈值取「全部磁贴亮度的中位数 + 22」的相对判定（抗捕获整体明暗漂移；实机数据：
    /// 聚焦 183~200 / 赛程表弱高光 133 / 未聚焦 90~105，中位数≈95 → 阈值≈117 全覆盖）。
    /// 「i」图标（前往比赛词右侧锚定区域）聚焦时亮 165~211（未聚焦 19~114）。
    /// 返回 null = 未检出。
    /// </summary>
    public static string? DetectFocusedHomeTile(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;

        double RegionAvg(double x0, double x1, double y0, double y1)
        {
            long sum = 0; int n = 0;
            for (int y = (int)(h * y0); y < (int)(h * y1); y += 2)
                for (int x = (int)(w * x0); x < (int)(w * x1); x += 2)
                {
                    int i = (y * w + x) * 4;
                    sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? 0 : (double)sum / n;
        }

        var scores = new List<(string Name, double Avg)>();
        OcrWord? goWord = null;
        foreach (var (kw, name) in HomeTiles)
        {
            var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(kw));
            if (word is null) continue;
            if (name == "前往比赛") goWord = word;
            double avg = RegionAvg(word.CenterX - word.W * 0.6, word.CenterX + word.W * 0.6,
                                   word.CenterY - word.H * 0.6, word.CenterY + word.H * 0.6);
            scores.Add((name, avg));
        }

        if (scores.Count == 0) return null;

        // 相对阈值：中位数 + 22（下限 112），最高者过线即聚焦（中位数按全部磁贴算）
        var sorted = scores.Select(s => s.Avg).OrderBy(v => v).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double threshold = Math.Max(112, median + 22);

        // 赛程表不参与聚焦竞争：其亮度不含聚焦信息——用户 2026-10-11 逐磁贴标定帧实测，
        // 光标在任意磁贴时赛程表均恒亮 ≈182（未读赛程内容自带高亮 ≈ 聚焦亮度；
        // 开发机弱高光 133 同样不可区分聚焦）。聚焦判定只在其余磁贴中取最亮；
        // 光标真在赛程表时按"未检出"返回，由导航约定（未检出=按赛程表）承接。
        var candidates = scores.Where(s => s.Name != "赛程表").ToList();
        if (candidates.Count == 0)
        {
            DumpFocusDiag(scores, threshold, 0, null);
            return null;
        }
        var best = candidates.OrderByDescending(s => s.Avg).First();

        // i 图标亮度：前往比赛词右侧锚定（右缘 +0.07~+0.16、y±0.04 —— 图标实际范围，
        // 实机网格扫描亮区 x0.25~0.31/y0.215~0.285；用户标定帧：i 聚焦时该区亮 208，
        // 前往比赛聚焦时磁贴自身亮块在 +0.03~0.08 不污染本锚定区）
        double iAvg = 0;
        if (goWord is not null)
        {
            double x0 = goWord.CenterX + goWord.W / 2 + 0.07;
            iAvg = RegionAvg(x0, x0 + 0.09, goWord.CenterY - 0.04, goWord.CenterY + 0.04);
        }

        string? verdict;
        if (best.Avg >= threshold)
        {
            verdict = best.Name;
        }
        else if (goWord is not null && (iAvg >= 140 || (iAvg >= 125 && iAvg >= best.Avg + 25)))
        {
            verdict = "i"; // 「i」图标：文字磁贴全暗才算（原判据，用户标定帧验证有效）
        }
        else
        {
            verdict = null;
        }

        DumpFocusDiag(scores, threshold, iAvg, verdict);
        return verdict;
    }

    /// <summary>焦点检测诊断输出（场景接走打 Debug 日志；2s 节流）。
    /// 导航错判时日志会给出每个磁贴的实际亮度/i 图标亮度/阈值/最终判定，据此定位。</summary>
    public static Action<string>? FocusDiag { get; set; }
    private static long _lastFocusDiagTick;

    private static void DumpFocusDiag(IReadOnlyList<(string Name, double Avg)> scores,
        double threshold, double iAvg, string? verdict)
    {
        long now = Environment.TickCount64;
        if (now - _lastFocusDiagTick < 2000) return;
        _lastFocusDiagTick = now;
        var detail = string.Join(" ", scores.Select(s => $"{s.Name}={s.Avg:F0}"));
        FocusDiag?.Invoke($"焦点检测：{detail} i图标={iAvg:F0} 阈值={threshold:F0} → {(verdict ?? "未检出")}");
    }

    /// <summary>
    /// 主页导航图（用户确认边，2026-10-11 修订）：**前往比赛出发第一步必须向下进赛程表；
    /// 前往比赛 --右--> i**（i 在前往比赛右边，切记）——因此前往比赛 --下--> 赛程表、
    /// i --下--> 赛程表、i --左--> 前往比赛（右边的反向）；
    /// 赛程表 --右--> 大概率赛季纪录/小概率兑换积分（**不确定边，盲导航勿走**）；赛季纪录 --左--> 赛程表、--上--> 兑换积分；
    /// 兑换积分 --左--> 赛程表、--下--> 赛季纪录、**--上--> 回卷第一行（2026-10-11 深夜实测：
    /// 可能落 i，而 i --下--> 赛程表不是兑换积分——兑换积分上方禁按 Up）**；
    /// 赛事信息 --上--> 赛程表、--右--> 建议；建议 --左--> 赛事信息、--上--> 赛季纪录。
    /// 兑换积分唯一确定性到达路线：赛事信息 --右--> 建议 --上--> 赛季纪录 --上--> 兑换积分（全确定边）。
    /// 未确认方向一律不走（赛事信息下/左、建议右、前往比赛左上均无路）。
    /// 焦点未检出（null）：赛程表聚焦亮度不可检（恒亮），null 大概率=光标在赛程表——
    /// 按「未检出=赛程表」约定导航：去兑换积分按 Right，回前往比赛按 Up。
    /// 返回下一步方向；null = 已在目标。
    /// </summary>
    public static PadDir? PlanHomeNavStep(string? focus, string target)
    {
        if (focus == target) return null;
        return target switch
        {
            "兑换积分" => focus switch
            {
                null or "赛程表" => PadDir.Right,             // 未检出=赛程表：右（大概率赛季纪录，再验证）
                "前往比赛" or "i" => PadDir.Down,            // 前往比赛出发第一步必须向下进赛程表（右是 i）
                "赛季纪录" or "兑换积分" => PadDir.Up,        // 赛季纪录--上-->兑换积分
                "赛事信息" => PadDir.Up,                      // 上→赛程表（下/左无路）
                "建议" => PadDir.Up,                          // 上→赛季纪录
                _ => PadDir.Right,                            // 未知磁贴：按赛程表行处理
            },
            "前往比赛" => focus switch
            {
                null or "赛程表" => PadDir.Up,                // 未检出=赛程表：上→前往比赛
                "i" => PadDir.Left,                           // i--左-->前往比赛（前往比赛--右-->i 的反向）
                "兑换积分" or "赛季纪录" => PadDir.Left,      // 左→赛程表（上方向未确认）
                "赛事信息" => PadDir.Up,                      // 上→赛程表
                "建议" => PadDir.Up,                          // 上→赛季纪录
                _ => PadDir.Up,
            },
            _ => null,
        };
    }
}
