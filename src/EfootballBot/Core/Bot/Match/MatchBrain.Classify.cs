using System.Text.RegularExpressions;
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

/// <summary>
/// 共享决策大脑（分类部分）：YOLO 6 类 + OCR → 界面类型。纯函数，可单测。
/// 分类顺序有硬约束（弹窗先于比赛、终场先于比赛、掉线链先于菜单），
/// 改动必须保持这些「先判」关系，否则会把菜单当比赛盲按 A。
/// </summary>
public static partial class MatchBrain
{
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
    /// 分类当前界面。<paramref name="topRightHasContent"/> 来自右上角像素探针（Scenario 计算）：
    /// 比赛画面右上角是球场/AI 徽标区（非黑，实机菜单帧右上角为纯黑），
    /// 作为"比赛中"的辅助判据——仅在所有菜单类判定都未命中时兜底，防止误伤亮色菜单页。
    /// <paramref name="allowCornerInMatch"/> = 流程记忆里最近真的见过比赛（场景层传入）：
    /// 菜单页右上角也可能有内容（2026-10-11 用户实机：国际服联赛主页奖励徽章点亮角区探针，
    /// 被兜底成 InMatch 盲按 A 误开新比赛），无比赛上下文时角区探针不作为比赛证据。
    /// <paramref name="mostlyLightPage"/> 来自整幅亮度探针（Scenario 计算）：中央区域大面积
    /// 白色 = 「我的联赛」说明页（全流程唯一白底页面，深色主题下强特征）。
    /// <paramref name="loginRecovery"/> = 流程记忆里最近（~100s）见过 BootSplash 标题页或
    /// NotLoggedIn 未登录页（场景层传入，见 MatchShared.LoginRecovery）：门控掉线重登后的
    /// 弹窗链识别（登录奖励/物品到收件箱等每周轮换页面，只认推进按钮词不认
    /// 具体内容）与加载期 OCR 退化的标志帧兜底。New Playing Style 网格页不在门控内
    /// （标题词唯一，有专用判定）。
    /// <paramref name="pitchLike"/> 来自草地绿占比探针（Scenario 计算，
    /// <see cref="ScreenProbe.LooksLikePitch"/>）：画面还是球场（庆祝/回放过场）时一律按
    /// 比赛中处理，先于一切菜单类判定——2026-11-20 实机进球 CG 被判 MatchHub 导致虚计一场。
    /// </summary>
    public static MatchScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr,
        bool topRightHasContent = false, bool allowCornerInMatch = true, bool mostlyLightPage = false,
        bool loginRecovery = false, bool pitchLike = false)
    {
        int Count(string name) => dets.Count(d => d.ClassName == name && d.Score >= MinScore);
        // 选择赛事等级页（难度选择）：置顶判定——该页覆盖在联赛主页上，画面同时含
        // 前往比赛/赛程表等主页词，不先判会被 LeaguePage 抢走盲按 A（实机 difficulty_2~4）
        if (ocr.Lines.Any(l => l.CenterY < 0.15 && l.Text.Contains("选择赛事等级")))
            return MatchScreen.DifficultyPage;
        // 关键比赛弹窗（预告 P2「下一场是关键比赛」/ 固定难度 P3「即将进行的是关键比赛」）：
        // 含「关键比赛」+「OK」。必须先于 MatchHub 兜底——弹窗文案带"我的联赛积分"字样会被误判。
        // 「重要比赛」为同义别名（用户反馈用词，防游戏文案改版）；OK 是白底白字小字，OCR 常
        // 漏读或读成 0K（用户 2026-10-15 实测：传奇级弹窗整帧漏掉 OK → 落 HasConfirm 盲按 A
        // → 按在默认焦点「取消」上 → 前往比赛↔弹窗死循环），词级做 O/0 归一再认一次
        if ((ocr.ContainsJoined("关键比赛") || ocr.ContainsJoined("重要比赛")) && HasOkButton(ocr))
            return MatchScreen.KeyMatchDialog;
        // 我的联赛·首发站位页（比赛计划内：用户手动停在阵容/主力球员查看页时的落点）：
        // 侧栏 战术/球队/替补球员/后备球员/自动配置球员 + 右侧「整体实力」。
        // ★先于 btn_confirm/LeaguePage 兜底——该页无「前往比赛」，旧分类落 Unknown 无操作。
        // 处置见 Decide：B 返回联赛主页交还正常流程；满级换人巡检见
        // MaxLevelSwapController.StartOnPlanAsync（该页可直接开跑）。
        if (!ocr.ContainsJoined("前往比赛") && !ocr.ContainsJoined("下个赛季") &&
            (ocr.ContainsJoined("替补球员") || ocr.ContainsJoined("自动配置球员")) &&
            ocr.ContainsJoined("整体实力"))
            return MatchScreen.LeagueSquadPage;
        // 比赛起始页（实机 match_start_page）：底部行「设置|球场|下一步」+ 队名/球场名
        // （eFootball™ Stadium）。先于通用 btn_confirm：判定为起始页可带上"即将开赛"上下文。
        // 2026-11-18 识别走查：旧判定「y>0.85 且含下一步」两个条件都写死——底栏键名换成
        // 开始比赛/开赛、或整行被并读下移到 y≈0.83，任一处不合就整页漏判，帧随即落
        // HasConfirm（上移再按）或 Unknown（等待→B 逃生），用户看到的就是「不自动开始比赛」。
        // 现在：底栏任一推进键（词表见 PushWords）+ 球场/Stadium/体育场 即判起始页。
        if ((ocr.ContainsJoined("球场") || ocr.ContainsJoined("Stadium") ||
             ocr.ContainsJoined("体育场")) &&
            (FindBottomPushWord(ocr) is not null ||
             ocr.Lines.Any(l => l.CenterY > 0.80 && l.Text.Contains("下一步"))))
            return MatchScreen.MatchStartPage;
        // ---- 防掉线重登链（eFootball 2027 v6.1.0 实机 8 帧，2026-10-19）：----
        // 付款确认弹窗（抽取页误按后弹出）：标题「付款确认」+「取消」。★必须最先判——
        // 弹窗底下就是抽取页（查看全部/名球员词同屏），且含「取消」若落入 HasConfirm 的
        // 「有取消先上移」规则会把光标挪到上方的金币付款键上按 A（花金币事故）
        if (ocr.ContainsJoined("付款确认"))
            return MatchScreen.PaymentConfirmDialog;
        // 抽取界面（登录奖励链可误入的 Special Player List 抽卡页）：「有限次数抽取」唯一；
        // 兜底「查看全部」+「名球员」（1名球员/10名球员价格档）双词同屏。内容随活动轮换
        // （European Clubs Midfielders 等标题不参与判定）。处置=B 退出，绝不消费
        if (ocr.ContainsJoined("有限次数抽取") ||
            (ocr.ContainsJoined("查看全部") && ocr.ContainsJoined("名球员")))
            return MatchScreen.GachaPage;
        // 登录后公告白页（实机 2026-10-21 用户截图：「eFootball™积分官方网站问题」+ 正文
        // 「我们对此问题造成的不便深表歉意」+ 右上角「B 关闭」提示）。底下就是主菜单，
        // YOLO main_menu 会命中 → 不先判会被主菜单导航（下×2 盲走）；也不能落 HasConfirm
        // （B 关闭不是 A 推进键，上移+A 会按到页面其它位置）。判定与 loginRecovery 无关：
        // 该类公告随时可能弹，标题词 OCR 碎裂时用正文固定句「深表歉意」兜底。
        if (ocr.ContainsJoined("官方网站问题") || ocr.ContainsJoined("深表歉意"))
            return MatchScreen.AnnouncementPage;
        // 未登录页（掉线重登链第二入口，实机 2026-10-19：大字「未登录」+「请检查你的网络
        // 环境并登录游戏」+ 登录/退出游戏 两键）。必须先于一切菜单判定——该页带主菜单页签/
        // 磁贴词，会被 YOLO main_menu 判成 MainMenu 盲走 下×2 导航（落点不可控，最坏按在
        // 「退出游戏」上直接退出游戏）；也先于 BootSplash（两页都是掉线入口但处置不同）
        if (ocr.ContainsJoined("未登录") || ocr.ContainsJoined("网络环境"))
            return MatchScreen.NotLoggedIn;
        // New Playing Style 活动奖励网格页（登录后弹窗链成员，实机 campaign_grid 帧）：
        // 大字标题「New Playing Style Campaign」在页首 + 奖励磁贴网格 + 底部「继续」。
        // ★不门控 loginRecovery：标题在网格页是页首大字，且该页可能落在链中靠后、
        // 重登 100s 窗口已过期（登录加载慢时）。
        // ★标题必须在高处（y&lt;0.35）——主菜单横幅同样含该标题词（实机 2026-10-21：
        // 收件箱弹窗背后菜单横幅 @y0.68 被整行读出），不限位置会把弹窗链页抢判成网格页；
        // 再与推进词任一（该页恒有「继续」）构成双证据
        if (ocr.Lines.Any(l => l.CenterY < 0.35 &&
                l.Text.Replace(" ", "").Contains("PlayingStyle", StringComparison.OrdinalIgnoreCase)) &&
            (ocr.ContainsJoined("继续") || HasOkButton(ocr) ||
             ocr.ContainsJoined("下一步") || ocr.ContainsJoined("确定")))
            return MatchScreen.CampaignGridPage;
        // 掉线重登后的弹窗链：登录奖励「继续」/ 物品已送到收件箱「OK」/
        // 上一场比赛被放弃「OK」——页面每周轮换不认具体词，只认推进按钮词按 A。
        // （New Playing Style 网格页已有上方标题专用判定，不再走此门控块）
        // ★必须 loginRecovery 门控（最近见过标题页），否则正常流程里的同名词（联赛页
        // 「继续」等）会被抢判。该块先于 BootSplash：弹窗盖在标题页上时按 A 先关弹窗
        if (loginRecovery &&
            (ocr.ContainsJoined("继续") || HasOkButton(ocr) ||
             ocr.ContainsJoined("下一步") || ocr.ContainsJoined("确定")))
            return MatchScreen.HasConfirm;
        // eFootball 2027 标题页：主判底栏「法律及隐私政策」（此页唯一）；副判「按确认按钮」
        // 拼接词 + 页脚版权/版本词双证据；兜底 loginRecovery 期内只剩 ASCII 标志词的加载帧
        // ——否则旧 TitleScreen 规则（纯 ASCII≤5 词含 footbal）抢判按 X，X 在此页无效会
        // 永久卡死（防掉线恢复绝不能停在这里）
        // ★兜底必须限定「稀疏帧 + 无主菜单特征」：主菜单/比赛页的 eFootball™ 磁贴同样含
        // footbal，登录成功后 100s 重登窗口内的主菜单会被判成标题页，无限执行下右A
        // （实机 2026-10-21 用户反馈「标题页判断和未登录走混了」）。
        if (ocr.ContainsJoined("法律及隐私政策") ||
            (ocr.ContainsJoined("确认按钮") &&
             (ocr.ContainsJoined("footbal") || ocr.ContainsJoined("konami") || ocr.ContainsJoined("版本"))) ||
            (loginRecovery && Count("main_menu") == 0 && ocr.Words.Count <= 8 &&
             ocr.ContainsJoined("footbal")))
            return MatchScreen.BootSplash;
        // 游戏启动设定窗（画面模式/分辨率，实机 2026-10-11：启动/更新后弹「设定」需确认一次）
        // 与启动标题画面（eFootball 标志 + 「按 X」提示）：都必须先于 InMatch 角区兜底——
        // 否则标题画面的标志内容会触发角区探针被误判 InMatch 盲按 A（A 无法离开标题画面）
        if (ocr.ContainsJoined("画面模式") || ocr.ContainsJoined("分辨率"))
            return MatchScreen.HasConfirm; // 设定窗：默认光标在确认，按 A 保留当前设置继续
        // 标题画面：几乎只有 eFootball 标志 + X 提示（纯 ASCII，无中文）；
        // 比赛页的「eFootball™联赛」磁贴也含 footbal——必须有中文词排除
        if (ocr.Words.Count is > 0 and <= 5 &&
            ocr.Words.All(w => !w.Text.Any(ch => ch >= 0x2E80)) &&
            ocr.ContainsJoined("footbal"))
            return MatchScreen.TitleScreen;
        if (Count("main_menu") >= 1) return MatchScreen.MainMenu;
        if (Count("item_row") >= 2 && Count("btn_back") >= 1) return MatchScreen.ItemList;
        // 全场结束（FullTime）终场判定：顶部「全场」+ 比分/球员评价页（实机 2026-10-10 截图：
        // "全场"@(0.62,0.074)、比分 2-2、下一步按钮）。优先于一切比赛判定——
        // 该页右上角有内容、且可能残留比分形态词，不先判会被误归 InMatch 继续随机点击
        // 2026-11-18 识别走查：新版终场页/结算首屏会写成「比赛结束 / 终场 / MATCH END」，
        // 标题行也可能落到 y≈0.40（旧阈值 0.35 直接漏）。漏判后果最重——结算链没有 A 推进，
        // 场次只能靠 MatchSessionTracker 的 5 分钟超时收尾，日志里就是「一把挂机很久」。
        // 「全场」保留原 0.35 阈值（比赛转播 HUD 也印"全场"，只在顶部时才当终场）。
        // 「比赛历史」列表页里的「比赛已结束 / 终场比分」不算终场——那一页的处理方式是 B 退回
        if (!ocr.ContainsJoined("比赛历史") &&
            ocr.Lines.Any(l =>
                (l.CenterY < 0.35 && l.Text.Contains("全场")) ||
                (l.CenterY < 0.45 &&
                 (l.Text.Contains("比赛结束") || l.Text.Contains("终场") ||
                  l.Text.Contains("赛事结束") ||
                  l.Text.Replace(" ", "").Contains("MATCHEND", StringComparison.OrdinalIgnoreCase) ||
                  l.Text.Replace(" ", "").Contains("FULLTIME", StringComparison.OrdinalIgnoreCase)))))
            return MatchScreen.FullTime;
        // 国服比赛暂停菜单（Start 键打开）：「跳过比赛」+ (比赛回放|重新开始|放弃) 左列词同屏。
        // ★必须先于「比赛回放」→HasConfirm 弹窗判定——暂停菜单含比赛回放词，误判 HasConfirm
        // 会盲按 A 在菜单↔确认弹窗间死循环；也先于记分牌 InMatch（菜单顶部有时钟/比分）
        if (ocr.ContainsJoined("跳过比赛") &&
            (ocr.ContainsJoined("比赛回放") || ocr.ContainsJoined("重新开始") || ocr.ContainsJoined("放弃")))
            return MatchScreen.CnMatchPause;
        // 物品购买确认弹窗：标题「使用物品」/「消耗我的联赛积分」+「取消」，
        // 焦点默认在取消——必须上移点上方带数字（积分价格）的按钮（实机 2026-10-10 flow_8）。
        // 先于 YOLO btn_confirm：该弹窗也检出确认按钮，直接按 A 会点到「取消」
        if (ocr.ContainsJoined("取消") &&
            (ocr.ContainsJoined("使用物品") || ocr.ContainsJoined("消耗我的联赛积分")))
            return MatchScreen.PurchaseDialog;
        // 难度选择页兜底：标题「选择赛事等级」被 OCR 漏读时，用「确认按钮 + 纵向难度名列表」识别。
        // （实机 bot-20261011-140943：该页 OCR 30 词含难度名、btn_confirm 0.83，但标题词漏读 →
        //   被判 HasConfirm 盲按 A，用默认难度开赛——用户反馈「不能自动选难度」）
        if (Count("btn_confirm") >= 1 && HasDifficultyList(ocr))
            return MatchScreen.DifficultyPage;
        // 比赛历史页（联赛/活动主页左列「比赛历史」面板误点开）：页首标题「比赛历史」+ 底栏
        // 「返回」，无前往比赛。★必须先于通用 btn_confirm 兜底——底栏「B 返回」蓝框会被 YOLO
        // 判 btn_confirm（实机 2026-10-26 反馈包 0.63 @y0.93），误判 HasConfirm 会在该页
        // 盲按 A 三十轮（该页没有任何 A 推进键）才 B 逃生，期间完全不开赛
        if (!ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("返回") &&
            ocr.Words.Any(w => w.CenterY < 0.22 && w.NormText.Contains("比赛历史")))
            return MatchScreen.MatchHistory;
        // 确认按钮优先于比赛判定：比赛结算/中场页面若同时出现时钟与确定键，按 A 推进。
        // ★例外：「跳过比赛✅」横幅右端的绿勾会被 YOLO 判成 btn_confirm（2026-11-06 国服实机
        //   0.84~0.89）。它固定在右上角（中心 x>0.85 / y<0.15），真正的确认按钮在画面中部/底部，
        //   两个区域互不重叠。不剔掉这枚检测，HasConfirm 会抢先吃掉 InMatch 的跳过分支——bot 按 A
        //   而不是按暂停键，跳过永远不发生（用户反馈「国服跳过检测失效」）。
        //   只在同屏有「跳过比赛」时剔除，其余页面的判定逐字不变。
        int confirmBtns = Count("btn_confirm");
        if (ocr.ContainsJoined("跳过比赛")) confirmBtns -= BannerCornerConfirms(dets);
        if (confirmBtns >= 1) return MatchScreen.HasConfirm;
        // 赛中/赛后弹窗（2026-10-10 实机截图）：「今天的进球」「比赛回放」弹出即按 A 下一步，
        // 必须先于比赛判定——这两种弹窗背景仍是球场（角区有内容），否则被判 InMatch 干等
        if (ocr.ContainsJoined("今天的进球") || ocr.ContainsJoined("比赛回放"))
            return MatchScreen.HasConfirm;
        // 我的联赛积分页：顶部标题「我的联赛积分」+「租借球员/我的联赛物品」两个选项
        // （实机 flow_5：标题 @y0.10；联赛主页里的"我的联赛积分"字样都在 y>0.4，不冲突）
        bool pointsTitle = ocr.Lines.Any(l => l.CenterY < 0.2 && l.Text.Contains("我的联赛积分"));
        if (pointsTitle && ocr.ContainsJoined("租借球员"))
            return MatchScreen.PointsPage;
        // 比赛主判据①：左上比分框（队名缩写+时钟/比分）
        if (HasScoreboard(ocr)) return MatchScreen.InMatch;
        // 「我的联赛」说明页（整幅白底文档，从主页 i 图标误入）：必须先于一切 LeaguePage
        // 判定——页内正文含「前往比赛」字样，误判 LeaguePage 会盲按 A 卡死
        // （实机 bot-20261011-160559 16:07-16:48 卡死 41 分钟，预览 OCR 30 词/btn_back 0.67）
        if (mostlyLightPage && ocr.Words.Count >= 5)
            return MatchScreen.LeagueInfoPage;
        // 草地绿守卫（2026-11-20 实机 00:34:35 用户反馈「进球后 CG 误判成比赛结束」）：
        // 进球庆祝/回放/镜头切换都是满屏草地的 3D 场景，OCR 只剩零星碎片词，却可能凑巧满足
        // 下面任一菜单类判定（IsMatchHub 的磁贴词 + 顶部含「比赛」的行极松），一旦被判菜单，
        // 场次跟踪器就把这帧当成「回到菜单」关场——一场 21 秒的「比赛」凭空计入完成场次。
        // 菜单/主页/终场页都是深色 UI 面板，中央草地绿实机全部 ≤4%，比赛帧 ≥65%（标定见
        // ScreenProbe.LooksLikePitch）。画面还是球场 → 一律按比赛中处理，处置走 InMatch 分支
        // （跳过横幅/推进键/挂机节奏），比当成菜单盲走安全。
        if (pitchLike) return MatchScreen.InMatch;
        // 比赛选择页（我的联赛所在磁贴行）。必须先于 btn_back 判定：该页有返回按钮，
        // 否则被误判 LeaguePage 无限等待（2026-10-10 实机复现：比赛页卡死不选我的联赛）。
        // 我的联赛内部页虽也含「我的联赛」字样，但无独立「比赛」标题行 + 磁贴行特征词。
        if (IsMatchHub(ocr)) return MatchScreen.MatchHub;
        // 我的联赛主页 OCR 兜底（YOLO 漏检返回按钮时）：「前往比赛/下个赛季」只出现在
        // 联赛主页（下个赛季 = 赛季结束替换词）。难度页/关键比赛弹窗/起始页均已先判，
        // 覆盖"弹窗叠在主页上、主页词同时可见"的情况（2026-10-11 用户实机回归）。
        if (ocr.ContainsJoined("前往比赛") || ocr.ContainsJoined("下个赛季"))
            return MatchScreen.LeaguePage;
        if (Count("btn_back") >= 1) return MatchScreen.LeaguePage;
        // 比赛页无 YOLO 训练特征时的兜底：含"我的联赛"且非主菜单（主菜单含"退出游戏"）
        bool hasMyLeague = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("我的联赛"));
        bool hasExit = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("退出游戏"));
        if (hasMyLeague && !hasExit) return MatchScreen.MatchHub;
        // 比赛辅助判据②：右上角（AI 徽标区）有画面内容——重播/庆祝/镜头切换等
        // 无比分框可读的比赛帧兜底为比赛中（只等待不按键）；菜单右上角为纯黑不会命中。
        // 无比赛上下文（allowCornerInMatch=false）时不兜底——菜单页右上角也可能有内容，
        // 此时宁可判 Unknown 等一拍（逃生/推进逻辑自会处理），绝不把菜单当比赛盲按 A。
        if (topRightHasContent && allowCornerInMatch) return MatchScreen.InMatch;
        return MatchScreen.Unknown;
    }

    /// <summary>
    /// 比赛选择页判定（新旧 UI 双形态）：
    /// ① 强证据：磁贴词「锦标赛」+ eFootball™联赛卡描述「争取升级至更高组别」同屏——
    ///   后者全游戏唯一出现在比赛页（AI 天梯/我的联赛共用此页），新 UI 2026-10-21 实拍帧验证。
    /// ② 标题行：旧 UI 精确「比赛」（y&lt;0.25）；新 UI 美术字标题行会并读进卡片角标碎片
    ///   （实拍整行读成「比赛小V1100000Rank」，精确等值恒假 → 卡死在比赛页循环 B 逃），
    ///   放宽为「行含比赛且不含联赛主页同高行的 contaminator 词」——我的联赛主页 y&lt;0.25 有
    ///   「排名比赛记录」「前往比赛…」两行（前往/排名/记录），必须排除否则主页被抢判成比赛页。
    /// 磁贴行特征词（锦标赛/自定义锦标赛/活动/我的联赛）仍是前置门槛。
    /// </summary>
    private static bool IsMatchHub(OcrResult ocr)
    {
        // 2026-11-18 识别走查：磁贴行文案随赛季轮换（eFootball™联赛 / 友谊赛 等），
        // 旧词表只有四个词，全换掉时整页判不出 → 右移/等待僵局。补两个同行的稳定词。
        bool hasTileWords = ocr.ContainsJoined("锦标赛") || ocr.ContainsJoined("自定义")
            || ocr.ContainsJoined("活动") || ocr.ContainsJoined("我的联赛")
            || ocr.ContainsJoined("友谊") || ocr.ContainsJoined("联赛");
        if (!hasTileWords) return false;
        if (ocr.ContainsJoined("锦标赛") && ocr.ContainsJoined("争取升级至更高组别"))
            return true;
        return ocr.Lines.Any(l => l.CenterY < 0.25 &&
            l.Text.Replace(" ", "").Contains("比赛") &&
            !l.Text.Contains("前往") && !l.Text.Contains("排名") && !l.Text.Contains("记录"));
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
        ocr.Words.Any(w => w.NormText.Replace("0", "O")
            .Equals("OK", StringComparison.OrdinalIgnoreCase));
    /// <summary>难度选择页特征（标题行或 ≥2 难度名列表）：确认键是默认焦点，不上移改难度。</summary>
    /// <summary>
    /// 比赛中的过场动画提示（进球回放 / 庆祝 / 入场）：出现「跳过」就算。
    /// 排除「跳过比赛」——那是国服右上角横幅（PadStartPress→暂停菜单，见 InMatch 分支），
    /// 两者语义不同，混在一起会把跳过链路打断。「重播/回放」是国际服过场页的固定字，
    /// 「SKIP」按整词（避免球员名含 skip 子串）。
    /// 2026-11-18 识别走查补三处漏判：
    /// ① 「回放」（旧版只认「重播」，新版过场提示写「回放/观看回放」）——但排除暂停菜单的
    ///    「比赛回放」（那是菜单项，按 A 会重开比赛）；
    /// ② 「快进 / 跳过动画 / 按任意键（跳过）」等同类提示；
    /// ③ SKIP 的位置阈值从 y&gt;0.55 放宽到 y&gt;0.45——新版提示贴在画面右中偏上，
    ///    旧阈值直接漏判，整段动画照播（用户 2026-11-13「一场 20 分钟」的另一半）。
    /// </summary>
    public static bool IsSkipOverlay(OcrResult ocr)
    {
        if (ocr.ContainsJoined("跳过比赛") || ocr.ContainsJoined("比赛回放")) return false;
        if (ocr.ContainsJoined("跳过") || ocr.ContainsJoined("重播") || ocr.ContainsJoined("回放") ||
            ocr.ContainsJoined("快进") || ocr.ContainsJoined("按任意键"))
            return true;
        return ocr.Words.Any(w =>
            w.CenterY > 0.45 && w.NormText.Equals("SKIP", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 通用推进键（识别走查 2026-11-18） ----------------
    //
    // 背景：比赛流程里「按 A 推进」的语义是稳定的，但按钮文案随版本变
    // （下一步 → 开始比赛 / 开赛 / 进入比赛；继续 → 继续比赛 / 进入下一轮）。
    // 旧逻辑把「下一步」写死在每个判定点里，新版换文案后整条链一起失效：
    // 起始页判不出 → 不开赛；终场页判不出 → 结算链靠 5 分钟超时收尾（"一把很久"）。
    // 下面这份词表只作为「页面上确实有一枚推进键」的证据，命中后执行的动作仍是 A，
    // 不引入任何新按键语义；不含 返回/关闭/取消/详情/位置（那是 B/X/Y 的说明）。
    /// <summary>底部推进键词表（页面底栏/dialog 底部的按钮行）。拉丁词按整词匹配（见下）。</summary>
    public static readonly IReadOnlyList<string> PushWords = new[]
    {
        "下一步", "下一步骤", "点击继续", "继续比赛", "继续", "确认", "确定",
        "开始比赛", "开赛", "开始", "进入比赛", "进入", "前往比赛", "前往",
        "领取", "接受", "跳过",
        "NEXT", "START", "CONTINUE", "CONFIRM", "PLAY", "ENTER", "SKIP",
    };
    /// <summary>比赛中覆盖层上的推进键（语义确定、误按无副作用：比赛中 A 是传球/逼抢）。</summary>
    private static readonly string[] InMatchPushWords =
    {
        "下一步", "下一步骤", "继续比赛", "继续", "确认", "确定", "领取",
    };
    /// <summary>
    /// 拉丁词整词匹配（避免 Opoku 含 ok、Koke 含 ok 之类的子串误判），中文走子串包含
    /// （OCR 常把图标记成词尾杂字，如「跳过參」）。
    /// </summary>
    private static bool PushWordHit(string normLine, string word) =>
        word.All(char.IsAsciiLetter)
            ? normLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(t => t.Equals(word, StringComparison.OrdinalIgnoreCase))
              || normLine.Equals(word, StringComparison.OrdinalIgnoreCase)
            : normLine.Contains(word, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// 底栏（y ≥ <paramref name="yMin"/>）里命中的第一枚推进键，没有则 null。
    /// 用途：词表变了也能「看见按钮就按」，而不是干等到 B 逃生。
    /// </summary>
    public static string? FindBottomPushWord(OcrResult ocr, double yMin = 0.80)
    {
        foreach (var line in ocr.Lines)
        {
            if (line.CenterY < yMin) continue;
            foreach (var word in PushWords)
                if (PushWordHit(line.Text, word)) return word;
        }
        return null;
    }

    /// <summary>
    /// 比赛中覆盖层/小弹窗上的推进键（下一步/继续/确认/OK）。命中即快按 A。
    /// OK 走整词 O/0 归一（白底白字小字常读成 0K），不用拼接子串——球员名 Koke/Opoku 会误命中。
    /// </summary>
    public static bool IsInMatchAdvance(OcrResult ocr)
    {
        if (InMatchPushWords.Any(ocr.ContainsJoined)) return true;
        return ocr.Words.Any(w => w.NormText.Replace("0", "O")
            .Equals("OK", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDifficultyChoice(OcrResult ocr) =>
        ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("选择赛事等级")) || HasDifficultyList(ocr);
    /// <summary>游戏启动设定窗（画面模式/分辨率）：确认键是默认焦点，不上移。</summary>
    private static bool IsGameSettingsDialog(OcrResult ocr) =>
        ocr.ContainsJoined("画面模式") || ocr.ContainsJoined("分辨率");
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
}




