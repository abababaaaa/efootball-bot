using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、像素探针、弹窗判定。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 本层不许 using / 引用 Core/Bot/Scenarios.*（共享层绝不能反向依赖模块）
//   · 场次记账（MatchesDone/KeyMatches）与比赛内挂机节奏只在本层实现，模块不许自己抄一份
// 新增共享能力：本层加文件；需要模块配合的用接口（如 IModuleLifecycle）反向交给模块。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Match;

// ============================================================

//  共享层：界面模型（谁都能用，谁都不拥有）
//  从「我的联赛」模块里搬出来的——此前国服天梯/国服周活动/AI天梯/种马 都在
//  using ...Scenarios.MyLeague 借用这些类型，等于用「我的联赛的私有件」。
//  现在归属 Core/Bot/Match：模块只依赖共享层，模块之间互不引用。
// ============================================================

/// <summary>我的联赛界面状态（由 YOLO 检测特征 + OCR 文字判定）。</summary>
public enum MatchScreen
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
    /// <summary>我的联赛·首发站位页（比赛计划内阵容/主力球员查看）：侧栏 战术/球队/替补球员…
    /// + 右侧「整体实力」，无「前往比赛」。处置：B 返回联赛主页交还正常流程；
    /// 满级换人巡检可在该页直接开跑（StartOnPlanAsync）。</summary>
    LeagueSquadPage,
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
    /// <summary>国服比赛暂停菜单（Start 键打开）：左列 跳过比赛/比赛回放/重新开始/放弃。
    /// ★必须先于「比赛回放」弹窗判定——菜单含比赛回放词，误判 HasConfirm 会盲按 A 在
    /// 菜单与确认弹窗间死循环。处置见 Decide：有「对局获胜」小字才跳，否则 B 回比赛。</summary>
    CnMatchPause,
    /// <summary>eFootball 2027 启动标题页（v6.1.0+，掉线/重启后回落）：按"确认"按钮 +
    /// 底栏「Y 法律及隐私政策」。防掉线重登入口，处置见 Decide：下→右→A（登录），
    /// 多轮无效改持续按 A；转圈加载帧（同页变暗+转圈）同判此处，加载期按键无效但无害。</summary>
    BootSplash,
    /// <summary>抽取界面（Special Player List 抽卡页，登录奖励链可误入）：球员卡+价格档
    /// （1名球员/10名球员）+「有限次数抽取」+右上 B 关闭。绝不消费——按 B 退出。</summary>
    GachaPage,
    /// <summary>付款确认弹窗（抽取页误按后）：标题「付款确认」+「取消」。光标默认在取消，
    /// 直接按 A 即取消；★绝不能落入 HasConfirm 的「有取消先上移」规则——上方是付款键。</summary>
    PaymentConfirmDialog,
    /// <summary>未登录页（掉线后仅会话失效，游戏停在主菜单壳上）：大字「未登录」+
    /// 「请检查你的网络环境并登录游戏」+右侧 登录/退出游戏 两键。掉线重登链的第二种入口
    /// （不经标题页）：默认焦点在顶部页签而非按钮上，需 下→右→A 才能点到「登录」；
    /// 登录后弹窗链由 LoginRecovery 窗口承接。</summary>
    NotLoggedIn,
    /// <summary>登录后公告白页（实机 2026-10-21：整幅白底 +「eFootball™积分官方网站问题」+
    /// 右上角「B 关闭」）。底下就是主菜单，无 A 推进键——必须 B 关闭，否则主菜单导航盲走。</summary>
    AnnouncementPage,
    /// <summary>「比赛历史」页（联赛/活动主页左列「比赛历史」面板点开）：页首标题「比赛历史」+
    /// 赛事行 + 底栏「返回」。处置见 Decide：B 退回主页。★必须先于通用 btn_confirm——
    /// 底栏「B 返回」蓝框会被 YOLO 判 btn_confirm（实机 2026-10-26 反馈包 0.63 @y0.93），
    /// 误判 HasConfirm 会在没有推进键的列表页上盲按 A 三十轮才逃生。</summary>
    MatchHistory,
    /// <summary>New Playing Style 活动奖励网格页（登录后弹窗链可出现，实机 2026-10-19/21）：
    /// 大字标题「New Playing Style Campaign」+ 奖励磁贴网格（全部已领时也同型）+ 底部
    /// 「继续」默认焦点。按 A 直接推进；判定用标题词、不依赖重登窗口（见 Classify）。</summary>
    CampaignGridPage,
}

public enum MatchActionKind
{
    PadEnterMatch,
    PadConfirm,
    GamepadBack,
    /// <summary>B 逃生失败多次（画面不变）后换招：先按上移把焦点从「取消」移到确认键，
    /// 再按 A 确认，最后按 B 返回。用于"反复 B 却关不掉"的胶着页——直接按 A 可能落在
    /// 「取消」上导致继续死循环，必须先上移避开取消。</summary>
    GamepadBackAfterConfirm,
    /// <summary>比赛选择页右移一格（寻找「我的联赛」磁贴，OCR 未看到焦点在其上时）。</summary>
    PadMoveRight,
    /// <summary>比赛中挂机点击：按 A + 用户设定的随机间隔（挂机节奏，拟人化防零输入判定）。</summary>
    MatchIdleTap,
    /// <summary>弹窗上移确认（购买弹窗焦点默认在取消，需上移点上方带数字的确认按钮）。</summary>
    PadUpConfirm,
    /// <summary>弹窗下移确认（推进键在光标下方：取消在上、确认在下那类弹窗，见 PopupBrain）。</summary>
    PadDownConfirm,
    /// <summary>标题画面按 X（eFootball 启动标题的进入键，A 无效）。</summary>
    PadXPress,
    /// <summary>暂停键（Start）：国服比赛中右上角出现「跳过比赛✅」横幅时打开暂停菜单
    /// （能否跳过由菜单里的「对局获胜」小字二次把关）。</summary>
    PadStartPress,
    /// <summary>登录序列（下→右→A）：eFootball 2027 标题页与未登录页共用——默认焦点在
    /// 页签行，下进卡片行、右进右侧按钮列、A 落在「登录」上。掉线防卡死恢复动作，
    /// 见 BootSplash / NotLoggedIn。</summary>
    PadLoginNav,
    Wait,
}

/// <summary>
/// 一次决策结果。全部动作均为模拟手柄操作（国际服不响应后台鼠标）：
/// PadEnterMatch = 复合导航（下到卡片行 → 左到最左"比赛" → A 进入）；
/// PadConfirm = 手柄 A（确认/下一步/选中焦点项）；GamepadBack = 手柄 B（返回/逃生）。
/// </summary>
public sealed record MatchAction(MatchActionKind Kind, int WaitMs, string Target = "")
{
    public static MatchAction Wait(int ms) => new(MatchActionKind.Wait, ms);
}

/// <summary>
/// 共享决策大脑（模型部分）：界面枚举 + 动作枚举 + 阈值常量。
/// 阈值集中在这里——各模块的「卡住/逃生」节奏是同一套口径，调一次全模块一致。
/// </summary>
public static partial class MatchBrain
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
    /// <summary>按 B 逃生连续失败这么多次（画面/分类不变）后，升级为「上 → A → B」换招。
    /// 针对 B 关不掉的确认弹窗/胶着页：先上移把焦点从取消移到确认键，再 A 确认，最后 B 退出
    /// （直接 A 会落在取消键上继续死循环）。之后每 3 次 B 再换一次招。</summary>
    public const int BackFailEscalations = 3;
    /// <summary>HasConfirm 弹窗连续该轮数仍无进展时周期性按 B 逃生。正常奖励弹窗链 9+ 页
    /// （每页 1~2 轮）远到不了该值；A 永不生效的「RT 下一步」型页面（实机 2026-10-14 对阵表）
    /// 靠它脱困。数值与国服 CnMyLeague 的 B@30 一致，国服侧其保险先行接管不叠加。</summary>
    public const int ConfirmEscapeStreak = 30;
    /// <summary>无特征界面（比赛/过场）连续该轮数后按 B 逃生。</summary>
    public const int UnknownEscapeStreak = 6;
    /// <summary>比赛页连续该轮数读不到焦点词（磁贴标签被裁/OCR 漏读）就按 A 试进当前卡，
    /// 不再无限右移（见 Decide 的 MatchHub 分支，识别走查 2026-11-18）。</summary>
    public const int MatchHubFocusBlindStreak = 6;
    /// <summary>
    /// 高光挪格（上/下×1 + A）连续该轮数画面仍不变 → 判定高光读反，改用「直接 A」。
    /// 为什么需要（2026-11-18 走查）：挪格方向来自 **亮度** 判据（PopupBrain 的按钮底色探针），
    /// HDR 开启 / 显卡色彩配置 / 低对比主题下亮度会整体抬升或压暗，高光可能读反——读反时
    /// 「挪一格 + A」正好落在「取消」上，弹窗关掉又重开，画面一动不动。玩家看到的现象就是
    /// 「卡在同一屏、下一步不点」。同一个无效动作最多重复这么多轮，之后退回历史行为直接 A。
    /// </summary>
    public const int ConfirmRepositionRounds = 2;
    /// <summary>
    /// 「水印页」（只有 logo/版本号可读的加载与过场）最多等这么多轮再不按键地干等，
    /// 之后才交回常规 A 试探 / B 逃生。~3~6s/轮 ≈ 1.5 分钟，够慢机器走完国服开赛加载；
    /// 真卡死（无网络的黑加载页）也仍会走到 B 逃生，不会无限等。
    /// </summary>
    public const int WatermarkWaitGraceStreak = 20;
    /// <summary>
    /// 认不出的界面连续该轮数后按一次 A 试探。A 在各页都是「选中/推进」，比空等到 B 逃生
    /// 更接近人手；按下后画面若推进，下一轮自然换判定，若没推进由 B 逃生收尾。
    /// </summary>
    public const int UnknownProbeAStreak = 3;
    /// <summary>eFootball 2027 标题页前几轮走「下→右→A」登录序列的轮数上限：超过后改
    /// 持续按 A（页面提示按「确认」按钮）。用户实机序列（2026-10-19）：下、右、A=登录。</summary>
    public const int BootLoginNavRounds = 3;
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

    // ---------------- 难度名（界面判定与难度页导航共用） ----------------
    /// <summary>难度选项（与游戏内「选择赛事等级」一一对应，顺序=列表行序第 1~7 行）。</summary>
    public static readonly string[] Difficulties =
        { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };
}




