using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本层职责：单个挂机模块自己的菜单流程与私有状态机（只认自己的界面）。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用别的模块的 *Scenario（只读借用别人的 *Brain.Classify 认残留页面是允许的）
//   · 不许自己写场次记账 / 比赛内挂机节奏 —— 一律走 MatchPlay.Tick / IdleTapAsync
//   · 共享界面判定与决策一律用 Core/Bot/Match 的 MatchBrain，不要复制一份
// 新增模块：ModuleIds 加常量 + BotModuleCatalog 加一条记录，再写本目录的状态机。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

/// <summary>周活动菜单侧界面（WeeklyBrain 判定）。None = 非周活动界面，交给 MyLeagueBrain 分类。</summary>
public enum WkScreen
{
    /// <summary>非周活动界面（主菜单/比赛中/共用弹窗等）。</summary>
    None,
    /// <summary>活动列表页（对阵AI / 对阵玩家 两个页签）。</summary>
    EventsList,
    /// <summary>活动介绍页（巡回活动大图，光标默认在「进入」）。</summary>
    EventIntro,
    /// <summary>进入活动时的弹窗（「设置专用比赛计划」等，光标默认在确认）。</summary>
    EntryPopup,
    /// <summary>活动主页（左上「前往比赛」+ 右栏活动积分/主要奖励进度）。</summary>
    EventHome,
    /// <summary>活动主页左列「比赛历史」面板点开的赛事列表页（标题「比赛历史」在页首，
    /// 列表 + 底栏「B 返回」）。开赛前如果没有把焦点归位到「前往比赛」，A 会点开该页。</summary>
    MatchHistory,
    /// <summary>活动设置浮层（按 X 呼出，右半屏：赛事等级/自动控制/弹性自动控制）。</summary>
    EventSettings,
    /// <summary>左列「建议」被 A 点开后的建议面板（浅色大卡盖住主页中部，右上角「B 关闭」）。
    /// 列表视图标题「建议列表」；点开一条后是详情视图（只剩关闭标题 + 分析师署名）。</summary>
    AdvicePanel,
    /// <summary>挑战活动「挑战清单」弹窗（进入挑战活动后出现，01/02… 编号卡纵排，
    /// 已完成卡右上有勾、未解锁卡右下有锁）。挑战子流程的菜单侧。</summary>
    ChallengeList,
    /// <summary>挑战解锁弹窗「挑战N已解锁 / 通过此全新挑战测试自己的技巧! / OK」（2026-11-08
    /// 国际服实机）：盖在挑战清单上，只有一个 OK 键。背后「挑战清单」标题、卡号 01/02、
    /// 底栏「返回」全部仍被 OCR 读到，整屏会被误判成可操作的清单——必须单独分类先按 A 清掉。</summary>
    ChallengeUnlockPopup,
    /// <summary>单场周活动「选择赛事等级」整页（2026-10-30 用户截图 + issue #1）：页首大标题
    /// + 左列难度行（新手~传奇，每屏 6 行需下滚）+ 右栏该等级奖励（主要奖励/等级奖励）。
    /// 进入活动卡后先弹「请选择赛事等级」说明弹窗（OK），再在本页选等级开赛。</summary>
    EventDifficulty,
    /// <summary>挑战活动「选择地区」页（2026-11-08 国际服「Road to CAF AFCON 2027」新子内容）：
    /// 左列大区列表 + 右侧「选择地区。」提示 + 底栏「B 返回 / Y 随机」。</summary>
    RegionSelect,
    /// <summary>同一子内容第二步「选择球队」页：左侧国旗网格 + 右侧固定框「选择球队。」
    /// （可租借球员列表 / 活动适合程度）+ 底栏「Y 随机」。</summary>
    TeamSelect,
    /// <summary>球队选择后的确认弹窗「要使用&lt;队名&gt;吗？」：纵排 进入(上) / 取消(下)，
    /// ★默认焦点在「取消」——直接 A 会取消回选球队页（用户 2026-11-08 实机）。</summary>
    TeamConfirm,
    /// <summary>阵容挑战（Team Effort）主页（2026-11-19 用户国际服实机三帧）：左列
    /// 「前往比赛」+「赛程」面板（对手徽章） + 「建议」；右栏「下一场比赛 / 赛事等级 /
    /// 奖励（挑战1..3 三段）」；底栏 B 返回 / X 活动设置 / Y 比赛计划。
    /// 与 <see cref="EventHome"/> 的区别：**没有**「主要奖励 / 总活动积分 / 胜场积分 /
    /// 球员加分」积分栏——所以 eventHomeBody 恒不命中，旧逻辑会把它漏成 None，
    /// 交给共享决策后既不开赛也不报错，整夜停在主页。</summary>
    TeamEffortHome,
    /// <summary>阵容挑战入场弹窗「球员卡限制 / OK」（2026-11-19 用户国际服实机帧）：盖在
    /// 阵容挑战主页上，只有一枚蓝色 OK 键，固定副标题「在之前胜场中使用的球员卡,将无法在
    /// 后续比赛中使用。」。进入活动后必弹一次，A 关掉之后主页才可操作。</summary>
    PlayerCardRestrictPopup,
    /// <summary>赛后「无法进行比赛」弹窗（2026-11-22 用户国际服实机帧）：标题「无法进行比赛」+
    /// 固定副标题「比赛计划中包含一张不能在后续比赛中使用的球员卡。」+ 单枚 OK，盖在阵容挑战
    /// 主页上（主页词透过暗化背景仍可读）。它是「比赛计划失效」的**结果**，不是开赛失败：
    /// A 关掉后必须去底栏 Y 比赛计划修阵容（见 <see cref="SquadLineupEdit"/>），否则再按
    /// 前往比赛只会再弹一次，永远开不了赛。</summary>
    MatchPlanInvalidPopup,
    /// <summary>比赛计划编辑页（主页底栏 Y「比赛计划」打开，2026-11-22 用户实机三帧）：
    /// 中区是 5-3-2 阵容板（球员卡 + GK 光标框），左列侧边栏「战术 / 球队 / 替补球员 /
    /// 后备球员 / 自动配置球员」，底栏「X 实时更新评价 / Y 球员详细数据」+ 右上「B 完成」，
    /// 右栏「整体实力 数值」。没有「前往比赛 / 赛程」，与主页互不抢判。</summary>
    SquadLineupEdit,
    /// <summary>「自动配置球员」的选项弹窗（同页，2026-11-22 用户实机规则）：盖在比赛计划
    /// 编辑页上，选项含「重视能力值」，需要下拉光标选中后点下一步。背后侧边栏
    /// 「自动配置球员」与底栏「实时更新评价」仍可读，故必须单独分类（否则会落回
    /// SquadLineupEdit 再按一次 A，把弹窗选项重复确认）。</summary>
    AutoConfigOptions,
}

/// <summary>挑战清单里一张可见卡的解析结果。Index 为卡上大字编号（1 起）；
/// Slot 为屏幕卡位（0=左卡 1=右卡，翻页时同一卡位的卡会换）；Locked/Checked 为该卡位的像素探针。</summary>
public sealed record WkChallengeCard(int Index, int Slot, bool Locked, bool Checked);
/// <summary>活动设置浮层的解析结果。null = 该项 OCR 未读出（保守：读不到就不动）。</summary>
public sealed record WkActivitySettings(string? Level, bool? AutoControl, bool? FlexibleAutoControl)
{
    public bool AllReadable => Level is not null && AutoControl is not null && FlexibleAutoControl is not null;
}

/// <summary>
/// 周活动决策大脑：菜单侧界面分类 + 活动设置解析 + 完成态探针。纯函数，可单测。
/// 判定只用固定 UI 词（对阵AI/前往比赛/活动设置/自动控制…）与固定几何区；
/// 活动名/卡片图片/积分数字每周轮换，一律不作判定点（可变内容禁令）。
/// 比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用 MyLeagueBrain。
/// </summary>
public static partial class WeeklyBrain
{
    // ---------------- 界面分类 ----------------
    /// <summary>
    /// 周活动界面分类。先于 MatchBrain.Classify 执行——活动主页也含「前往比赛」，
    /// 不先判会被误归 LeaguePage（与关键比赛弹窗被 MatchHub 兜底抢判同一坑）。
    /// 判定顺序：球员卡限制弹窗 → 无法进行比赛弹窗 → 自动配置选项 → 比赛计划编辑页 →
    /// 挑战解锁弹窗 → 挑战清单 → 建议面板 → 比赛历史 →
    /// 盖层弹窗 → 活动设置 → 入场弹窗 → 活动主页 → 阵容挑战主页 → 活动列表 → 活动介绍。
    /// </summary>
    public static WkScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr,
        bool topRightHasContent = false, bool matchContext = false, bool mostlyLightPage = false)
    {
        // 阵容挑战入场弹窗「球员卡限制 / OK」（2026-11-19 用户国际服实机帧）：进入活动后
        // 必弹一次，只有一枚 OK。★必须最先判——弹窗盖在主页上，主页词（前往比赛/赛程/
        // 赛事等级）仍会被 OCR 读到，落主页分支会去按 X/前往比赛，全部被单键弹窗吃掉。
        if (IsPlayerCardRestrictPopup(ocr))
            return WkScreen.PlayerCardRestrictPopup;
        // 赛后「无法进行比赛」弹窗（2026-11-22 用户国际服实机帧）：同样盖在阵容挑战主页上，
        // 主页词（前往比赛/赛程/赛事等级/奖励）全部可读 → 不先判会被 popupOnTop 归成
        // EntryPopup 盲按 A：弹窗是关掉了，但比赛计划仍然是失效状态，下一轮按前往比赛又弹，
        // 整夜「弹窗→前往比赛→弹窗」零场次。先单独认出来，A 之后转去修比赛计划。
        if (IsMatchPlanInvalidPopup(ocr))
            return WkScreen.MatchPlanInvalidPopup;
        // 比赛计划编辑页（底栏 Y 打开）与其「自动配置球员」选项弹窗（2026-11-22 用户实机）：
        // 两页都没有「前往比赛 / 赛程」，但都带左上侧边栏「自动配置球员」。
        // ★选项弹窗先判：弹窗背后侧边栏/底栏仍可读，落编辑页分支会再按一次 A 重复确认。
        if (IsAutoConfigOptionsPopup(ocr))
            return WkScreen.AutoConfigOptions;
        if (IsSquadLineupEdit(ocr))
            return WkScreen.SquadLineupEdit;
        // 挑战解锁弹窗（「挑战N已解锁 / OK」盖在挑战清单上，2026-11-08 国际服实机）：
        // ★必须先于挑战清单判定——弹窗半透明盖层下清单标题/卡号/底栏「返回」全部可读，
        // 先判清单会把弹窗当成可操作清单：归位/移卡被单按钮弹窗吃掉（方向键无效），
        // 卡位锁探针还被弹窗浅灰底误判成「锁定」→ 回撤退 B → B 退不掉 → 死循环。
        if (IsChallengeUnlockPopup(ocr))
            return WkScreen.ChallengeUnlockPopup;
        // 挑战清单弹窗（挑战活动进入后的卡片列表）：标题「挑战清单」为强特征，最先判定。
        // 无标题兜底：卡位行两枚 H≥0.08 的大字卡号（普通文字 H≈0.04，只有卡编号这么高）
        // 分布在左右两卡位 + 底栏「返回」词。不先判会被共享分类兜成 LeaguePage（页面只有
        // btn_back，无可推进词 → 死等/瞎退）。标题词漏读时卡号特征防僵局。
        if (ocr.ContainsJoined("挑战清单") || HasChallengeListCardNumbers(ocr))
            return WkScreen.ChallengeList;
        // 建议面板（活动主页左列「建议」被 A 点开，实机 2026-10-28 反馈包）：浅色大卡盖住
        // 主页中部，但左列「前往比赛/比赛历史」「建议 有未读建议」与右栏「胜场积分」全部
        // 仍然可见 → 旧逻辑一直被 eventHomeBody 抢判成 EventHome，随后按 A（面板吃掉，或
        // 打开一条建议详情）和按 X（无活动设置）全部无效；「多次未生效」的 B 逃生也只把
        // 详情退成列表、退不回主页 → 三十轮原地打转、永远进不了比赛。
        // ★必须先于「盖层弹窗」——面板右上角「B 关闭」蓝色按钮会被 YOLO 判成 btn_confirm，
        // 落到弹窗门控会变 EntryPopup 继续盲按 A。
        if (IsAdvicePanel(ocr))
            return WkScreen.AdvicePanel;
        // 比赛历史页（活动主页左列「比赛历史」面板点开后）：页首大标题「比赛历史」+ 底栏「返回」。
        // ★必须在任何弹窗/主页判定之前——该页只有列表行与返回，没有推进词，落到共享分类会
        // 被 YOLO 误检的 btn_confirm（实机 2026-10-26 反馈包：0.63 @列表行）判成 HasConfirm，
        // 共享决策在不可推进的列表页上盲按 A 三十轮才 B 逃生 → 回主页又按 A 再进历史，死循环。
        // 活动主页的「比赛历史」是左列面板标签（y≈0.38），不在页首区，用 y&lt;0.22 区分；
        // 再用「无前往比赛」硬否决——主页恒有左上「前往比赛」，历史页没有，
        // 防活动改版把历史面板标签挪高后把主页误判成历史页（那会 B 退回列表死循环）。
        if (!ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("返回") &&
            ocr.Words.Any(w => w.CenterY < 0.22 && w.NormText.Contains("比赛历史")))
            return WkScreen.MatchHistory;
        // 挑战活动新增前置流程（2026-11-08 国际服「Road to CAF AFCON 2027」子内容）：
        // 介绍页按进入 → 「选择地区」→「选择球队」→ 确认弹窗「要使用<队名>吗？」→ 挑战清单。
        // 前两页只有底栏「B 返回 / Y 随机」，没有任何推进词，落共享分类会被判 Unknown 原地干等；
        // 确认弹窗只有「进入 / 取消」，同样没有推进词。三页都在这里接管。
        // ★确认弹窗必须最先判：弹窗盖在球队页上，背后「选择球队。」仍会被 OCR 读到。
        if (IsTeamConfirmPopup(ocr))
            return WkScreen.TeamConfirm;
        if (IsRegionSelectPage(ocr))
            return WkScreen.RegionSelect;
        if (IsTeamSelectPage(ocr))
            return WkScreen.TeamSelect;
        // 单场周活动「选择赛事等级」整页（2026-10-30 用户三张截图 + GitHub issue #1 手机照：
        // 该页 wk=None、ml=DifficultyPage，周活动状态机无人接管 → 日志只刷「界面：DifficultyPage」
        // 原地干等，选不了难度）。★先于盖层弹窗门控——入场说明弹窗（请选择赛事等级+OK）盖在
        // 页上时标题与左列难度词仍然可读，落 popupOnTop 门控会因缺主页词判 None；
        // ★左列难度词必须限定 x&lt;0.25：活动设置浮层的赛事等级下拉（居中对话框）标题同名，
        // 靠 x 区分避免抢 LevelDropdown 阶段的下拉样式。
        if (IsEventDifficultyPage(ocr))
            return WkScreen.EventDifficulty;
        // 盖层弹窗探测（赛后结算/奖励领取等多步弹窗，用户 2026-10-12 实机教训）：
        // 主页词透过暗化背景仍会被 OCR 读到，误判 EventHome 后按 X/前往比赛全部无效
        //（弹窗挡着，被误判"设置打不开/开赛未生效"）。弹窗自带推进词或确认按钮，
        // 而纯主页没有（前往比赛不被 YOLO 判 btn_confirm、底栏无 OK/下一步/确认，实测帧验证）。
        // 命中 → 一律先按 A 清弹窗，清完才允许开始「已回到主页」的后续判断。
        //「OK」按整词精确匹配（FindButton），防英文球员名/卡面词含 "ok" 子串误触发。
        // YOLO btn_confirm 只认屏幕中部（y<0.85）：主页底栏「B 返回」的蓝色高亮框会被
        // 判成 btn_confirm（实机 Titani 帧 0.91 @(0.12,0.93)），把干净主页误门控成
        // EntryPopup → A 在主页上=前往比赛直接开下一场（实机：终场后 8s 进下一场、
        // 完成态从未被评估）。弹窗按钮都在屏幕中部，底栏确认框不算弹窗证据。
        bool popupOnTop =
            ocr.ContainsJoined("下一步") || ocr.ContainsJoined("确认") ||
            ocr.FindButton("OK") is not null ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MatchBrain.MinScore && d.CenterY < 0.85);
        bool settingsTitle = ocr.Words.Any(w => w.CenterY < 0.2 && w.Text.Replace(" ", "").Contains("活动设置"));
        bool eventHomeBody = ocr.ContainsJoined("前往比赛") &&
            (ocr.ContainsJoined("总活动积分") || ocr.ContainsJoined("胜场积分") ||
             ocr.ContainsJoined("球员加分") || ocr.ContainsJoined("主要奖励"));
        // 阵容挑战主页（2026-11-19 用户实机）：另一套主页版式，没有积分栏，判据换固定 UI 词。
        bool teamEffortHomeBody = IsTeamEffortHome(ocr);
        bool eventsListTabs = HasMatchTabRow(ocr);
        bool introMark = ocr.ContainsJoined("进入") && ocr.ContainsJoined("详情");
        // 介绍页不参与弹窗门控：其白色「进入」按钮本身会被 YOLO 判 btn_confirm，
        // 而介绍页无论有无弹窗按 A 都是正确动作（光标默认在进入）
        if (popupOnTop && (settingsTitle || eventHomeBody || teamEffortHomeBody || eventsListTabs))
            return WkScreen.EntryPopup;
        // 活动设置浮层（按 X 呼出，盖在活动主页右半屏）：标题「活动设置」在顶部（y<0.2）。
        // 必须先于活动主页——浮层打开时「前往比赛」仍在左侧可见。
        // 【实测坑】活动主页底栏有「X 活动设置」按钮词（y≈0.93），不限 y 会把主页误判成设置浮层
        if (settingsTitle)
            return WkScreen.EventSettings;
        // 入场弹窗（「设置专用比赛计划」+ OK；可能连续多枚）：先于活动主页——
        // 弹窗背后「前往比赛」仍可见，不先判会被 EventHome 抢走漏点确认
        if (ocr.ContainsJoined("专用比赛计划"))
            return WkScreen.EntryPopup;
        // 活动主页：左上「前往比赛」+ 右栏固定词（总活动积分/胜场积分/球员加分/主要奖励）。
        // 我的联赛主页没有这组词，与 LeaguePage 区分开
        if (eventHomeBody)
            return WkScreen.EventHome;
        // 阵容挑战主页：同一层级的活动主页，但版式不同（无积分栏）。★必须在活动列表/介绍页
        // 之前判——该页底栏有「比赛计划」，正文有「奖励」，都不构成列表页证据；反过来
        // 列表页没有「下一场比赛 / 赛程 / 前往比赛」三件套，互不抢判。
        if (teamEffortHomeBody)
            return WkScreen.TeamEffortHome;
        // 活动列表页：顶部「对阵AI / 对阵玩家」页签（进入比赛 hub 的活动板块后默认页签为对阵AI）
        // ★页签词必须限定在顶部页签行（实机 y≈0.08）：介绍页正文写着「这是一个对阵AI活动…」
        // （实机 2026-10-29 反馈视频，y≈0.26），不限位置会让介绍页被判成列表页 → 场景层每轮
        // 「进入槽位 → ↓ 把焦点从进入挪到详情 → ←×6 → A」打开详情面板，活动永远进不去。
        if (eventsListTabs)
            return WkScreen.EventsList;
        // 活动介绍页（巡回活动大图 / 挑战活动大图）：「进入」+「详情」两枚按钮同屏。
        // ★只用「进入」+「详情」做判据，不用「巡回活动」——列表卡上也印着「巡回活动」标签
        //（实机列表帧 y≈0.23 三张卡各一枚），单靠该词会把列表页误判成介绍页。
        if (introMark)
            return WkScreen.EventIntro;
        return WkScreen.None;
    }

    /// <summary>
    /// 顶部页签行是否有「对阵AI / 对阵玩家」（活动列表页的页签）。
    /// ★必须限定 y&lt;0.16：介绍页正文写着「这是一个对阵AI活动…」（实机 2026-10-29 反馈视频
    /// 帧 y≈0.26），全局子串匹配会把介绍页误判成活动列表，场景层随后每轮 ↓ 把焦点从「进入」
    /// 挪到「详情」并反复 A 打开详情面板，活动永远进不去（日志「进入槽位 0」连刷二十余次）。
    /// 实机列表帧页签在 y≈0.08；页签词可能被逐字拆开，故用词级各自匹配而非整句拼接。
    /// </summary>
    private static bool HasMatchTabRow(OcrResult ocr)
        => ocr.Words.Any(w => w.CenterY < 0.16 &&
               (w.NormText.Contains("对阵AI", StringComparison.OrdinalIgnoreCase) ||
                w.NormText.Contains("对阵玩家")));


    /// <summary>比赛计划编辑页左列侧边栏的固定项（自上而下）；「自动配置球员」恒为最后一项。</summary>
    public static readonly string[] SquadSidebarItems = { "球队", "替补球员", "后备球员", "自动配置球员" };
    /// <summary>侧边栏聚焦行白底亮度（实机 autoconfig 帧：聚焦行中位亮度≈235，未聚焦≈47）。</summary>
    private const int SidebarFocusedLuma = 120;
    /// <summary>聚焦行与次亮行的最小亮度差（实机差值 &gt;180，留足余量）。</summary>
    private const int SidebarFocusedLumaGap = 40;


    // 比赛页磁贴定位已抽到共享层 Core.Bot.Match.HubTileLocator（周活动/AI天梯共用）。

    /// <summary>介绍页直接 A 连续这么多次都没推进 → 不论探针怎么说都先上移归位（自愈兜底：
    /// 探针误读成「进入」而实际光标在「详情」时，绝不能永远重复同一个无效动作）。</summary>
    public const int IntroEnterTriesBeforeNudge = 4;
    /// <summary>聚焦按钮判定阈值：聚焦行是白底（实机中位亮度 242），未聚焦是深色半透明（实机 49）。</summary>
    private const int IntroFocusedLuma = 120;
    /// <summary>两个按钮都偏亮/偏暗时，靠亮度差判胜出（实机差值 &gt;150，留足余量）。</summary>
    private const int IntroFocusedLumaGap = 40;


    // ---------------- 选择页推进规则（用户 2026-11-08 实机规则） ----------------
    /// <summary>随机按完后的过渡宽限帧数：同页连续观测满这么多帧才认「随机没把页面带走」
    /// （地区 → 球队的转场约 1~2s，一帧就判会抢在转场前把 A 按掉）。</summary>
    public const int PickConfirmGraceFrames = 2;
    /// <summary>随机按过之后最多再按几次 A（下一步）；超了 B 退出该子流程，绝不在同一页来回按。</summary>
    public const int MaxPickNextAttempts = 3;
    /// <summary>子流程内 B 退出累计上限；超了停止挂机（退出受阻，交人工看游戏状态）。</summary>
    public const int MaxPickEscapes = 3;


    // ---------------- 挑战清单卡片扫描 ----------------
    /// <summary>卡位右缘 x 坐标（与活动列表卡行同几何；实机 challenge_list 帧：
    /// 卡1 右缘≈0.351、卡2 右缘≈0.637，锁定图标在卡右下角）。</summary>
    private static readonly double[] ChallengeSlotRights = { 0.352, 0.645 };


    /// <summary>在右半屏（x&gt;0.45）找标签词下方的值词（值在标签下一行，y 偏移 0.015~0.13）。</summary>
    private static OcrWord? ValueBelow(OcrResult ocr, double labelY, Func<string, bool> match)
        => ocr.Words.FirstOrDefault(w =>
            w.CenterX > 0.45 && w.CenterY > labelY + 0.015 && w.CenterY < labelY + 0.13 &&
            match(w.Text.Replace(" ", "")));
    /// <summary>
    /// 解析活动设置浮层当前值：赛事等级（难度词）/ 自动控制（开/关）/ 弹性自动控制（开/关）。
    /// 任何一项读不到返回 null——调用方保守处理（绝不盲按，防误翻转）。
    /// </summary>
    public static WkActivitySettings ParseActivitySettings(OcrResult ocr)
    {
        // 「弹性自动控制」包含「自动控制」子串，先匹配弹性行再匹配普通行，普通行要求整行就是「自动控制」。
        // 行级匹配失败时回退词级：挑战活动设置浮层较窄，背后卡详情页的同高文字会并进同一
        // 排版行（实机 challenge_settings 帧：自动控制行读成「完成的条件自动控制」，整行
        // 精确匹配落空），而词本身仍是独立词「自动控制」。
        var flexLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("弹性"));
        var autoLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "") == "自动控制");
        var levelLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("赛事等级"));
        double? autoY = autoLabel?.CenterY ?? LabelWordY(ocr, "自动控制", exact: true);
        double? flexY = flexLabel?.CenterY ?? LabelWordY(ocr, "弹性", exact: false);
        double? levelY = levelLabel?.CenterY ?? LabelWordY(ocr, "赛事等级", exact: false);
        string? level = null;
        if (levelY is not null)
        {
            var v = ValueBelow(ocr, levelY.Value, t => MatchBrain.Difficulties.Any(d => t.Contains(d)));
            if (v is not null)
                level = MatchBrain.Difficulties.First(d => v.Text.Replace(" ", "").Contains(d));
        }
        bool? ParseSwitch(double? labelY)
        {
            if (labelY is null) return null;
            var v = ValueBelow(ocr, labelY.Value, t => t == "开" || t == "关");
            return v is null ? null : v.Text.Replace(" ", "") == "开";
        }
        return new WkActivitySettings(level, ParseSwitch(autoY), ParseSwitch(flexY));
    }

    /// <summary>词级标签回退：找文本匹配的独立词的纵坐标。exact=true 要求整词相等
    /// （「自动控制」不得命中「弹性自动控制」词），false 为子串包含。</summary>
    private static double? LabelWordY(OcrResult ocr, string keyword, bool exact)
        => ocr.Words
            .FirstOrDefault(w => exact
                ? w.NormText == keyword
                : w.NormText.Contains(keyword))
            ?.CenterY;
    // ---------------- 完成态探针（2026-10-13 用户实机截图校准） ----------------
    /// <summary>活动卡右缘 x 坐标（槽位 0~3；实机 weekly_list_greencheck 帧：卡1 右缘≈0.351）。
    /// 第 4 卡大部分在屏幕外，勾区采样不到自然返回 false（进入后由主页完成态判定兜底）。</summary>
    private static readonly double[] CardRightEdges = { 0.352, 0.643, 0.946, 1.24 };
    /// <summary>区域内绿色像素计数（共享实现见 <see cref="RegionProbe.CountGreen"/>）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGreenRef(f, x0, x1, y0, y1);
    /// <summary>
    /// 活动列表卡右上角绿色勾（该活动主要奖励已完成，不进入）：勾形徽章固定在卡右上角。
    /// 实机 weekly_list_greencheck 帧：卡1 勾区 157 绿像素，未完成卡 0——阈值 50。
    /// </summary>
    public static bool DetectGreenCheck(FrameData frame, int slot)
    {
        if (slot < 0 || slot >= CardRightEdges.Length) return false;
        double right = CardRightEdges[slot];
        return CountGreen(frame, right - 0.065, right - 0.005, 0.150, 0.220) >= 50;
    }

    /// <summary>
    /// 活动主页右栏「主要奖励已获得」：
    /// ① OCR「获得的主要奖励」（完成态标题；进行中是「离获得主要奖励还差…」，无「的」字，
    ///   子串天然不冲突）；
    /// ② 备份：右栏主要奖励行的绿色勾（实机 weekly_reward_done 帧：勾区 100 绿像素；
    ///   注意别把左侧代币图标圈进来——图标在 x≈0.70，勾在 x≈0.94）。
    /// </summary>
    public static bool IsMainRewardDone(OcrResult ocr, FrameData frame)
        => ocr.ContainsJoined("获得的主要奖励") ||
           CountGreen(frame, 0.920, 0.965, 0.635, 0.715) >= 50;
    /// <summary>右栏是否显示「离获得主要奖励还差…」（进行中，可继续刷）——仅用于日志与守卫。</summary>
    public static bool MainRewardPending(OcrResult ocr) => ocr.ContainsJoined("离获得主要奖励");
    /// <summary>难度档位（与我的联赛「选择赛事等级」共用一套词，周活动赛事等级下拉一致）。</summary>
    public static string[] Levels => MatchBrain.Difficulties;
}


