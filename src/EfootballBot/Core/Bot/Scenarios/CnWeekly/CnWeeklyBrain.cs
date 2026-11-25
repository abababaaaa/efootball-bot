using System.Text.RegularExpressions;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
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

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

/// <summary>国服周活动界面（CnWeeklyBrain 判定）。None = 非国服周活动界面，交给 MyLeagueBrain 分类。</summary>
public enum CwScreen
{
    /// <summary>非国服周活动界面（比赛中/终场/共用弹窗等）。</summary>
    None,
    /// <summary>国服主页·推荐页签（顶部 推荐/比赛/签约/商店 + 天梯巅峰赛/活动中心 等磁贴）。</summary>
    HomeRecommended,
    /// <summary>国服主页·比赛页签（顶部页签 + 比赛活动/天梯巅峰赛/我的联赛/好友对战 卡片）。</summary>
    HomeMatch,
    /// <summary>比赛活动列表（对阵电脑 / 对阵玩家 两个页签 + 活动卡）。</summary>
    EventsList,
    /// <summary>活动介绍页（活动大图 + 进入/详情 按钮 + B 关闭）。</summary>
    EventIntro,
    /// <summary>对阵表页（小组赛…决赛 页签 + X 重置 + RT 下一步）。</summary>
    Bracket,
    /// <summary>活动主页（左 球队阵容/比赛历史/建议 + 右 下场比赛/下个对手 + RT 教练入场）。</summary>
    EventHome,
    /// <summary>球队阵容页（球队阵容列表 + 球员加分 + B 完成 + RS 自动配置球员）。</summary>
    Squad,
    /// <summary>RS 自动配置球员菜单（无变更/重视能力值/活动加成，盖在阵容页上）。</summary>
    SquadMenu,
    /// <summary>巡回赛型活动主页（球星训练等，2026-10-17 实机：主按钮 前往比赛 + 总活动积分/
    /// 比赛历史/建议，底栏 B 返回 + X 活动设置；无杯赛型的 教练入场/球队阵容）。</summary>
    EventHomeTour,
    /// <summary>挑战卡详情页（2026-10-17 第二周实机 绿茵传奇挑战赛）：巡回赛主页样
    /// （前往比赛+活动设置）+ 挑战专属词「完成挑战…」。独立分类不借道 EventHomeTour：
    /// ★绝不进 i 信息弹窗（主要奖励 x/x 是本周全部活动卡合计进度，对单个活动卡无意义）、
    /// 不核验巡回赛活动设置；通关判定=右栏胜场 N/N 或反复进入无比赛，A 前往比赛后由
    /// 游戏分叉进清单/难度页/比赛，挑战子流程自会接住。</summary>
    ChallengeCardHome,
    /// <summary>活动设置面板（巡回赛主页 X 打开的右侧盖层：选择的球队/教练模式/教练模式内控制，
    /// 底部 B 完成。主页词透过暗化背景仍可读，必须先于主页判定）。</summary>
    EventSettings,
    /// <summary>挑战型活动「挑战清单」选卡页（2026-10-17 实机 cn_challenge_list：标题 挑战清单 +
    /// 01/02… 编号卡横排一屏 3 张，已通关卡右上绿勾、未解锁卡右下灰锁，底栏 B 返回）。
    /// 挑战子流程的菜单侧，归位第一张后逐卡右扫。</summary>
    ChallengeList,
    /// <summary>「选择比赛难度」页（2026-10-17 实机 cn_tour_difficulty：前往比赛后弹出，5 张
    /// 难度卡横排 + 底栏 B 返回/RT 确定；光标所在卡白底高亮=当前所选）。与我的联赛
    /// 「选择赛事等级」不同词不同布局，须独立判定。</summary>
    Difficulty,
    /// <summary>巡回赛主页 i 图标打开的信息弹窗（2026-10-17 实机 cn_tour_reward_popup：
    /// 白底大面板，主要奖励 N/M 进度 + 活动时间/活动说明，右上 B 关闭）。主要奖励是否
    /// 拿满以此弹窗内 x/x 为准——主页勾只表示已有主要奖励可领，不代表拿满。</summary>
    RewardPopup,
    /// <summary>教练选择页（2026-11-04 实机 晋级挑战：进入活动后先签教练）：标题
    /// 「请选择您的教练」+ 教练卡横排（姓名/国旗/评价大字数字/进攻风格战术板），底栏 B 返回。
    /// 处置：选评价数字更大的那张卡按 A 进详情（评价每周给的两个教练不同，只比数字）。</summary>
    CoachSelect,
    /// <summary>教练资料页（签下前）：黄字教练名 + 年龄/国家·地区/球队/类别 +
    /// 球队风格熟练度表 + 底栏「RT 确认选择」。处置：A 下一步（不生效改 RT）。</summary>
    CoachDetail,
    /// <summary>签下确认弹窗（盖在教练资料页上）：「签下确认」标题 + 说明 +
    /// 确认（默认焦点，蓝色高亮）/取消 两键。处置：A 确认。</summary>
    SignConfirm,
}

/// <summary>
/// 国服周活动决策大脑（菜单侧）：界面分类。纯函数，可单测。
/// 判定只用固定 UI 词（对阵电脑/教练入场/前往比赛/活动设置/球队阵容列表/无变更/活动加成…）
/// 与固定几何；活动名（如「实况足球 冠军赛」「球星训练 巡回赛」）、奖励内容、对手队名每周轮换，
/// 一律不作判定点（可变内容禁令）。比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用
/// MyLeagueBrain。国服菜单全程 OCR 判定（国服 YOLO 模型未训练，btn_confirm 仅作弹窗门控辅助证据）。
/// </summary>
public static partial class CnWeeklyBrain
{
    // ---------------- 界面分类 ----------------
    /// <summary>
    /// 国服周活动界面分类。先于 MatchBrain.Classify 执行。
    /// 判定顺序：签下确认弹窗 → 盖层弹窗门控 → i 信息弹窗 → 教练选择/教练资料 →
    /// 挑战清单 → 选择比赛难度 → RS 菜单
    /// （盖在阵容页上）→ 阵容页 → 活动设置面板（盖在巡回赛主页右半）→ 巡回赛型活动主页
    /// → 杯赛型活动主页 → 对阵表 → 活动介绍 → 活动列表 → 主页（比赛页签 → 推荐页签）。
    /// </summary>
    public static CwScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr)
    {
        bool squadBody = ocr.ContainsJoined("球队阵容列表") || ocr.ContainsJoined("自动配置球员");
        bool eventHomeBody = ocr.ContainsJoined("教练入场") ||
            (ocr.ContainsJoined("比赛历史") && ocr.ContainsJoined("下场比赛"));
        // 巡回赛型（2026-10-17 实机 cn_tour_event_settings.png）：主页=前往比赛+底栏「X 活动设置」；
        // 设置面板=教练模式 +（选择的球队|完全托管）。两代主页都含「比赛历史」，面板盖住主页右半，
        // 故面板与巡回赛主页都必须先于杯赛型主页判定
        bool settingsBody = ocr.ContainsJoined("教练模式") &&
            (ocr.ContainsJoined("选择的球队") || ocr.ContainsJoined("完全托管"));
        bool tourHomeBody = ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("活动设置");
        // 挑战卡详情页：巡回赛主页样/杯赛主页样 + 挑战专属词「完成挑战」（2026-10-17 第二周实机）
        bool challengeCardBody = (tourHomeBody || eventHomeBody) && ocr.ContainsJoined("完成挑战");
        // 对阵表特征收紧：底栏「重置」（y > 0.85），或「冠军杯」同屏含赛程阶段/推进词（防活动列表/介绍页/联赛页含「重置」或活动名撞「冠军杯」）
        bool bottomReset = ocr.Words.Any(w => w.CenterY > 0.85 && w.NormText.Contains("重置"));
        bool bracketStage = ocr.ContainsJoined("小组赛") || ocr.ContainsJoined("淘汰赛") ||
                            ocr.ContainsJoined("决赛") || ocr.ContainsJoined("下一步");
        bool bracketBody = bottomReset || (ocr.ContainsJoined("冠军杯") && bracketStage);
        // 比赛活动列表：顶部「对阵电脑 / 对阵玩家」两枚页签。★必须限定在屏幕顶部（页签行
        // y≈0.08）：比赛页的「我的联赛」卡面海报里也有「对阵电脑」四个字（实机 cn_home_match
        // y≈0.19，回归测试抓到过），不限位置会把主页误判成活动列表。
        // ★且只读到一枚页签也算（2026-11-05 新 UI 白药丸底页签常漏读一枚，两枚缺整页就落
        // None → 共享流程当主菜单无限重放主页导航，永远到不了活动列表）；
        // 再兜底「加入 + 返回 + 奖励」三词同屏（新 UI 卡按钮是 加入，介绍页是 进入+详情 无奖励
        // 图标行，挑战清单无 加入，不冲突）。
        bool listBody = HasTopTabWord(ocr, "对阵电脑") || HasTopTabWord(ocr, "对阵玩家") ||
                        (ocr.ContainsJoined("加入") && ocr.ContainsJoined("返回") &&
                         ocr.ContainsJoined("奖励"));
        bool introBody = ocr.ContainsJoined("详情") && ocr.ContainsJoined("进入");
        // 挑战清单选卡页：标题「挑战清单」强特征；标题漏读时大字卡号带+底栏返回兜底
        bool challengeBody = ocr.ContainsJoined("挑战清单") || HasChallengeListCardNumbers(ocr);
        // 选择比赛难度页：标题强特征；标题漏读时 同行 ≥3 个难度名 + 确定词 兜底
        bool difficultyBody = ocr.ContainsJoined("选择比赛难度") || HasCnDifficultyCardRow(ocr);
        // i 信息弹窗（主要奖励 x/x）：主要奖励 头 + 活动说明/活动时间（主页右栏虽有
        // 获得的主要奖励 字样透底，但无 活动说明/活动时间，不冲突）
        bool rewardPopupBody = ocr.ContainsJoined("主要奖励") &&
            (ocr.ContainsJoined("活动说明") || ocr.ContainsJoined("活动时间"));
        // 教练签下链（2026-11-04 实机 晋级挑战）：选择页标题 / 资料页底栏「RT 确认选择」 /
        // 签下确认弹窗。资料页的「确认选择」是底栏按钮（y≈0.93），与弹窗推进词 确认 不吃词
        bool signConfirmBody = ocr.ContainsJoined("签下确认");
        bool coachSelectBody = ocr.ContainsJoined("请选择您的教练") || HasCoachRatingRow(ocr);
        bool coachDetailBody = ocr.ContainsJoined("确认选择") || ocr.ContainsJoined("球队风格熟练度");
        // 签下确认弹窗：盖在教练资料页上（资料页的「确认选择」底栏与球队风格熟练度仍可读），
        // 且自带中部「确认/取消」推进词——必须先于盖层门控判定，否则会被门控成 None
        // 交给共享流程，A 的落点（确认 vs 取消）不确定
        if (signConfirmBody)
            return CwScreen.SignConfirm;
        // 盖层弹窗门控（同国际服周活动实机教训）：主页词透过暗化背景仍会被 OCR 读到，
        // 误判回菜单页会把无效按键当"未生效"反复重按。弹窗自带推进词（下一步/确认/整词OK，
        // 或 YOLO 检出确认按钮）且背景有周活动界面证据 → 交共享流程按 A 清弹窗。
        // 推进词必须在屏幕中部（y&lt;0.82）：对阵表底栏「RT 下一步」、活动主页底栏等
        // 自带按钮词在 y≈0.93，不限 y 会把干净的对阵表/主页全门控成弹窗（对阵表会因此
        // 被判 None → 共享流程 Unknown → B 逃生退回介绍页，死循环）。
        bool popupOnTop = CenterPopupWord(ocr) ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MatchBrain.MinScore && d.CenterY < 0.82);
        if (popupOnTop && (squadBody || eventHomeBody || tourHomeBody || settingsBody ||
                           bracketBody || listBody || introBody || challengeBody || difficultyBody ||
                           challengeCardBody || coachSelectBody || coachDetailBody))
            return CwScreen.None;
        // i 信息弹窗：盖在巡回赛主页上的白底大面板，先于主页判定（主页左半被盖，
        // 前往比赛 不可见，但保险起见仍先判）
        if (rewardPopupBody)
            return CwScreen.RewardPopup;
        // 教练签下链（先选择页后资料页；两页互斥，顺序仅表优先级）：
        // 落 None 会被共享分类当 LeaguePage 盲按/瞎退，教练永远签不下来
        if (coachSelectBody)
            return CwScreen.CoachSelect;
        if (coachDetailBody)
            return CwScreen.CoachDetail;
        // 挑战清单选卡页：必须先于其余判定——页面只有底栏「返回」，落 None 会被共享分类
        // 盲等/瞎退（用户 2026-10-17 实机卡死同款）。标题词漏读时卡号特征防僵局
        if (challengeBody)
            return CwScreen.ChallengeList;
        // 选择比赛难度页：底栏 B 返回/RT 确定，落 None 会被共享分类当 LeaguePage 卡死
        //（等不到推进词 → B 退回 → 前往比赛 → 本页，死循环）。标题词漏读时难度卡行兜底
        if (difficultyBody)
            return CwScreen.Difficulty;
        // RS 自动配置球员菜单：盖在阵容页上（无变更/活动加成 为菜单专属词），必须先于阵容页
        if (ocr.ContainsJoined("无变更") && ocr.ContainsJoined("活动加成"))
            return CwScreen.SquadMenu;
        // 球队阵容页：左上「球队阵容列表」或底栏「RS 自动配置球员」（球员加分两页都有，不作主判据）
        if (squadBody)
            return CwScreen.Squad;
        // 活动设置面板：盖在巡回赛主页右半，主页词透背景仍可读，先于两个主页判定
        if (settingsBody)
            return CwScreen.EventSettings;
        // 挑战卡详情页（2026-10-17 第二周实机 绿茵传奇挑战赛）：巡回赛主页样（前往比赛+
        // 活动设置）+ 挑战专属词「完成挑战」，先于普通巡回赛主页——场景层挑战子流程
        // 靠它把卡页与真巡回赛主页区分开（i 弹窗核验/设置核验都是巡回赛专属）
        if (challengeCardBody)
            return CwScreen.ChallengeCardHome;
        // 巡回赛型活动主页：主按钮「前往比赛」+ 底栏「X 活动设置」（杯赛型主页没有这两个词；
        // 我的联赛主页虽有前往比赛但无活动设置，不冲突）。先于杯赛型主页——两代都含「比赛历史」，
        // 巡回赛主页右侧若也出现「下场比赛」会误中杯赛判定
        if (tourHomeBody)
            return CwScreen.EventHomeTour;
        // 活动主页：底栏「RT 教练入场」（周活动专属词）；左栏 球队阵容/比赛历史/建议 磁贴
        // 与右栏「下场比赛」为备份（教练入场 OCR 漏读时）
        if (eventHomeBody)
            return CwScreen.EventHome;
        // 活动介绍页：进入 + 详情 按钮同屏（列表卡只有「进入」，无「详情」）
        if (introBody)
            return CwScreen.EventIntro;
        // 比赛活动列表：顶部「对阵电脑 / 对阵玩家」页签
        if (listBody)
            return CwScreen.EventsList;
        // 对阵表页：底栏「X 重置」独有；「冠军杯」+ 阶段词备份
        if (bracketBody)
            return CwScreen.Bracket;
        // 国服主页：顶部页签行 推荐/比赛/签约/商店（同一行，y&lt;0.15），按内容词分页签
        if (HasTopTabs(ocr))
        {
            if (ocr.ContainsJoined("好友对战") || ocr.ContainsJoined("我的联赛") || ocr.ContainsJoined("比赛活动"))
                return CwScreen.HomeMatch;
            if (ocr.ContainsJoined("活动中心") || ocr.ContainsJoined("我的球队") || ocr.ContainsJoined("设置"))
                return CwScreen.HomeRecommended;
        }
        return CwScreen.None;
    }

    /// <summary>屏幕中部（y&lt;0.82）的弹窗推进词：下一步/确认/确定，或整词 OK（防英文词含 ok 子串）。</summary>
    public static bool CenterPopupWord(OcrResult ocr)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY >= 0.82) continue;
            string t = w.Text.Replace(" ", "");
            if (t is "下一步" or "确认" or "确定") return true;
            if (t.Equals("OK", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>顶部页签行通用特征（共享实现见 <see cref="RegionProbe.HasTopTabs"/>）。</summary>
    public static bool HasTopTabs(OcrResult ocr) => RegionProbe.HasTopTabs(ocr);
    /// <summary>
    /// 是否「国服主页样」页面（推荐/比赛两个页签的任一内容可由下面的词判定）。用于主页导航
    /// 前的安全检查：只有确认是主页才允许按 上×2→下→左→A 的盲序列；识别不出的页面（过场、
    /// 弹窗、进错模式）按这套按键会把焦点推到不可控的地方（2026-11-05 实机：未知页被当主菜单
    /// 无限重放三拍导航）。页签行本身、首页磁贴、比赛页卡片词任一组命中即认。
    /// </summary>
    public static bool LooksLikeCnHome(OcrResult ocr) =>
        HasTopTabs(ocr) ||
        ocr.ContainsJoined("活动中心") || ocr.ContainsJoined("我的球队") ||
        ocr.ContainsJoined("天梯巅峰赛") || ocr.ContainsJoined("比赛活动") ||
        ocr.ContainsJoined("训练手册");
    /// <summary>活动列表页顶部页签词（限定 y&lt;0.15 的页签行：比赛页「我的联赛」卡面海报里
    /// 同样印着「对阵电脑」，不限位置会误判）。</summary>
    private static bool HasTopTabWord(OcrResult ocr, string keyword)
    {
        foreach (var w in ocr.MergedWords)
            if (w.CenterY < 0.15 &&
                w.NormText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>比赛活动列表页特征（同 <see cref="Classify"/> 的列表判定，供场景层单独查询）。</summary>
    public static bool LooksLikeEventList(OcrResult ocr) =>
        HasTopTabWord(ocr, "对阵电脑") || HasTopTabWord(ocr, "对阵玩家") ||
        (ocr.ContainsJoined("加入") && ocr.ContainsJoined("返回") && ocr.ContainsJoined("奖励"));
    /// <summary>RS 菜单中「活动加成」选项行号（0 起，自上而下：无变更/重视能力值/重视能力值心有灵犀/活动加成）。
    /// 光标默认在第 1 项「无变更」（实机帧验证），按 Down×3 到「活动加成」。</summary>
    public const int EventBonusOptionIndex = 3;
    /// <summary>活动主页左栏磁贴固定行序（球队阵容/比赛历史/建议）。光标默认在第 1 项「球队阵容」，
    /// 按 A 直接进入阵容页（实机帧：球队阵容磁贴白色高亮为默认焦点）。</summary>
    public const int SquadTileIndex = 0;

    // ---------------- 巡回赛活动设置核验（只读） ----------------
    /// <summary>
    /// 活动设置面板是否已满足全自动开赛（实机 2026-10-17 cn_tour_event_settings.png）：
    /// 教练模式=开 且 教练模式内控制=完全托管。值的排版为标签行下方独立 pill 行，
    /// 「开」是单字词；OCR 把标签与值并成一行（「教练模式开」）时同样认得出（含 内控制
    /// 的是下面那行控制设置，须排除）。只读不改——面板里盲按 A 可能落在「选择的球队」上
    /// 误开换队。OCR 漏读「开」会误报未就绪：只影响提示，不触发任何按键。
    /// </summary>
    public static bool TourSettingsReady(OcrResult ocr)
    {
        if (!ocr.ContainsJoined("教练模式") || !ocr.ContainsJoined("完全托管"))
            return false;
        foreach (var w in ocr.MergedWords)
        {
            string t = w.NormText;
            if (t == "开") return true;
            if (t.Contains("教练模式") && !t.Contains("内控制") && t.Contains("开")) return true;
        }
        return false;
    }


    /// <summary>卡号中心到卡左缘的内边距（实机 cn_challenge_list 帧：卡号 x=0.42、
    /// 卡右缘=0.348 → 内边距≈0.072）。同一内边距从下一张卡号反推本卡右缘。
    /// ⚠ 反推值偏大即探针扫到卡边框描边（亮卡边框有大量 90~220 灰过渡像素）误报锁，
    /// 探针右界须再内收 0.01 兜住推导误差。</summary>
    private const double ChallengeNumberInset = 0.07;


    // ---------------- 活动卡指纹（防「刷满退列表→槽位+1→又进同一张卡」空转） ----------------
    /// <summary>卡名噪声词：页面顶部固定字（游戏名/模式名/表头/按钮），不参与卡名判定。</summary>
    private static readonly string[] CardNameNoise =
    {
        "实况足球在线", "教练模式", "总活动积分", "赛事等级奖励", "球员加分", "胜场积分",
        "前往比赛", "比赛历史", "上一场比赛", "活动设置", "距离下个奖励",
    };


}


