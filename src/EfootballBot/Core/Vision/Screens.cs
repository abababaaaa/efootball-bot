using EfootballBot.Core.Capture;

using System.Text.RegularExpressions;

namespace EfootballBot.Core.Vision;

public enum GameScreen
{
    Unknown,
    Loading,            // 加载 / 黑屏
    TitleScreen,        // 标题画面（点 A 进入）
    Home,               // 游戏主页
    MatchHub,           // 比赛模式选择（我的联赛/活动入口在此）
    EventHub,           // 活动列表（对阵AI/对阵玩家）
    EventInfo,          // 挑战活动详情页（进入/详情/关闭）
    ChallengeList,      // 挑战清单（01/02… 以1胜为目标，含锁定卡）
    ChallengeHome,      // 挑战赛主页（前往比赛 / 教练模式 RS 开关 / 胜场 x/x / 完成的条件）
    EventSettings,      // 活动设置侧滑面板（赛事等级/自动控制/弹性自动控制）
    DailyRewards,       // 每日奖励 / 签到 / 小游戏
    MyLeagueHome,       // 我的联赛主页（前往比赛 / 赛程表 / 联赛排名）
    ActivityDetail,     // 活动详情页（i 图标进入：主要奖励进度 / 活动时间 / 活动说明）
    Schedule,           // 赛程表（比赛日）
    KeyMatchBadge,      // 关键比赛标识页
    PreMatch,           // 赛前设置（难度 / 选手操作 / 开始比赛）
    InMatch,            // 比赛进行中
    HalfTime,           // 中场休息
    FullTime,           // 全场结束
    Result,             // 比赛结果 / 球员经验
    Rewards,            // 奖励领取页
    CampaignBoard,      // 国际服 International Match Campaign 棋盘格活动页（每场赛后弹出，右移光标→下一步）
    ItemsBar,           // R3 道具浮层
    Shop,               // 积分兑换商店（我的积分联赛 / 我的物品联赛）
    ShopConfirm,        // 购买确认
    Dialog,             // 通用确认弹窗
    NoEvent,            // 「没有正在进行的对阵玩家活动」空页（误入对阵玩家 Tab，B 返回）
    Disconnected,       // 断线 / 通信错误
}

public sealed record ScreenKeyword(string Text, double Weight = 1.0, NormRect? Region = null);

public sealed record ScreenDef(GameScreen Screen, ScreenKeyword[] Keywords, double MinScore = 2.0);

/// <summary>屏幕识别锚点默认目录（中 / 英双语，文字均为 OcrText.Norm 后的形态）。</summary>
public static class ScreenCatalog
{
    private static ScreenKeyword K(string zh, string en, double w = 1.0, NormRect? region = null)
        => new(zh, w, region); // 中文为主，英文通过额外关键词列出

    private static ScreenKeyword[] Mix(
        (string text, double w)[]? zh = null,
        (string text, double w)[]? en = null,
        NormRect? region = null)
    {
        var list = new List<ScreenKeyword>();
        if (zh is not null) foreach (var (t, w) in zh) list.Add(new ScreenKeyword(t, w, region));
        if (en is not null) foreach (var (t, w) in en) list.Add(new ScreenKeyword(t, w, region));
        return list.ToArray();
    }

    // 常用区域（归一化）
    private static readonly NormRect TopBar = new(0, 0, 1, 0.18);
    private static readonly NormRect BottomBar = new(0, 0.86, 1, 0.14);
    private static readonly NormRect Center = new(0.15, 0.2, 0.7, 0.6);

    public static readonly IReadOnlyList<ScreenDef> Defaults = new[]
    {
        new ScreenDef(GameScreen.Loading, Mix(
            zh: new[] { ("匹配中",2), ("配对中",2), ("搜索对手",2), ("寻找对手",2), ("正在匹配",2), ("正在搜索",1.5),
                        ("对手连接",2), ("连接对手",2), ("正在连接",1.5), ("已找到对手",2), ("匹配对手",2),
                        ("加载中",1.5), ("正在载入",1.5), ("读取中",1.5), ("请稍候",1.5) },
            en: new[] { ("matching",2), ("searching",2), ("nowloading",2), ("connecting",1.5), ("pleasewait",1.5) }
        ), 1.5),

        new ScreenDef(GameScreen.Disconnected, Mix(
            zh: new[] { ("无法连接",2), ("连接失败",2), ("通信错误",2), ("网络错误",2), ("返回标题",1.5), ("重新连接",1.5), ("确认返回",1) },
            en: new[] { ("communicationerror",2), ("connectionerror",2), ("networkerror",2), ("returntotitle",1.5), ("reconnect",1.5) }
        ), 1.5),

        new ScreenDef(GameScreen.MyLeagueHome, Mix(
            // 「前往比赛」单独出现时可能是挑战赛前画面，必须再命中一个强特征词才算主页
            zh: new[] { ("前往比赛",2.5), ("下一场",2.5), ("联赛排名",2.5), ("赛程表",2), ("阵容",2),
                        ("排名",1.5), ("积分榜",1.5), ("比赛计划",1),
                        ("活动积分",2.5), ("赛事等级",2.5), ("主要奖励",1.5), ("总活动积分",2.5) },
            en: new[] { ("gotomatch",2.5), ("nextmatch",2.5), ("fixtures",2), ("leaguestandings",2), ("squad",2), ("gameplan",1) }
        ), 2.5),

        new ScreenDef(GameScreen.ActivityDetail, Mix(
            // 「活动说明/活动时间」仅详情页有；「主要奖励」主页侧栏也会出现，仅作辅助
            zh: new[] { ("活动说明", 3), ("活动时间", 2.5), ("主要奖励", 1.5) }
        ), 4),

        new ScreenDef(GameScreen.Schedule, Mix(
            zh: new[] { ("比赛日",2), ("选择符合",1.5), ("即将进行",1.5), ("赛程",1.5) },
            en: new[] { ("matchday",2), ("upcoming",1.5), ("fixtures",1.5) }
        ), 2),

        new ScreenDef(GameScreen.KeyMatchBadge, Mix(
            zh: new[] { ("关键比赛",2.5) },
            en: new[] { ("keymatch",2.5), ("keypmatch",2.5) }
        ), 2),

        new ScreenDef(GameScreen.PreMatch, Mix(
            zh: new[] { ("开始比赛",2.5), ("下一步",2.5), ("前往比赛",2.5), ("选择比赛难度",3), ("顶尖球员",1.5), ("超级球星",1.5),
                        ("比赛条件",1.5), ("选手操作",1.5), ("新手",1.5), ("难度",1), ("职业",1), ("比赛计划",1), ("主场",1), ("客场",1) },
            en: new[] { ("kickoff",2.5), ("next",2.5), ("gotomatch",2.5), ("matchconditions",1.5), ("playeroperations",1.5), ("beginner",1.5), ("difficulty",1) }
        ), 2.5),

        new ScreenDef(GameScreen.HalfTime, Mix(
            zh: new[] { ("中场休息",2.5), ("中场",2) },
            en: new[] { ("halftime",2.5) }
        ), 2),

        new ScreenDef(GameScreen.FullTime, Mix(
            zh: new[] { ("全场比赛结束",2.5), ("比赛结束",2) },
            en: new[] { ("fulltime",2.5), ("matchended",2) }
        ), 2),

        new ScreenDef(GameScreen.Result, Mix(
            zh: new[] { ("最佳球员",2), ("球员经验",2), ("获得经验",1.5), ("比赛结果",1.5), ("mvp",1.5),
                        ("球员评价",2.5), ("球员评分",2), ("球员表现",2) },
            en: new[] { ("playerofthematch",2), ("playerexp",2), ("matchresult",1.5), ("mvp",1.5) }
        ), 2),

        new ScreenDef(GameScreen.Rewards, Mix(
            zh: new[] { ("获得奖励",2.0), ("奖励详情",2.0), ("领取奖励",2.0), ("获得",1.0) },
            en: new[] { ("rewards",2), ("youearned",2), ("obtained",1.5) }
        ), 2.5),

        // 国际服 International Match Campaign 棋盘格：每场比赛结束后弹出。
        // 独有词：总积分 / 自选合约 / 当前位置 / 活动期间 / 格子数；英文标题 campaign
        new ScreenDef(GameScreen.CampaignBoard, Mix(
            zh: new[] { ("总积分",3), ("自选合约",3), ("当前位置",2.5), ("活动期间",2.5), ("格子数",2.5) },
            en: new[] { ("campaign",3), ("internationalmatch",2.5) }
        ), 3),

        new ScreenDef(GameScreen.Shop, Mix(
            // 只用商店内页独有的词；「兑换积分/我的联赛积分」在联赛主页右侧预览卡上也有，不能用
            zh: new[] { ("租借球员",3), ("我的联赛物品",3), ("获得各种物品",2.5), ("租借有用",2.5), ("选择要兑换",2.5), ("持有物品",2), ("使用物品",2) },
            en: new[] { ("loanplayer",3), ("pointexchange",3), ("exchangeitems",2.5) }
        ), 2.5),

        new ScreenDef(GameScreen.ShopConfirm, Mix(
            zh: new[] { ("确认购买",2.5), ("购买",1.5), ("兑换",1.5) },
            en: new[] { ("confirmpurchase",2.5), ("purchase",1.5), ("buy",1) }
        ), 2.5),

        new ScreenDef(GameScreen.MatchHub, Mix(
            // 「比赛」是泛词：比赛回放/比赛结束等画面都含它，权重必须低于 MinScore，
            // 不能单词命中（否则比赛中回放帧会被误判成比赛菜单）
            zh: new[] { ("比赛",1), ("我的联赛",2.5), ("快速比赛",2), ("好友对战",2), ("自定义锦标赛",2), ("球员俱乐部",1.5) },
            en: new[] { ("match",1), ("myleague",2.5), ("quickmatch",2), ("friendmatch",1.5) }
        ), 2.5),

        new ScreenDef(GameScreen.EventHub, Mix(
            // 真机文案是「对阵电脑/对阵玩家」（不是“对战”）；「巡回赛」常被 OCR 拆成「巡回」+单字
            zh: new[] { ("对阵电脑",2.5), ("巡回赛",2), ("对阵玩家",1.5), ("挑战赛",1.5),
                        ("巡回",1.5), ("挑战活动",2), ("巡回活动",2), ("剩余时间",1), ("活动",1) },
            en: new[] { ("events",2), ("tour",1.5), ("challengeevent",1.5), ("vsai",1.5) }
        ), 3),

        new ScreenDef(GameScreen.NoEvent, Mix(
            zh: new[] { ("没有正在进行",3), ("正在进行的对阵",2.5), ("对阵玩家活动",2) }
        ), 2.5),

        // 挑战活动全屏详情：「详情」按钮为该页独有（列表卡只有 进入/继续）
        new ScreenDef(GameScreen.EventInfo, Mix(
            zh: new[] { ("详情",3), ("进入",1.5), ("挑战活动",1.5), ("结束倒数",1), ("关闭",1) }
        ), 4),

        // 挑战清单：大编号卡 + 比赛次数上限；锁定卡右下角灰色挂锁（像素检测）
        new ScreenDef(GameScreen.ChallengeList, Mix(
            zh: new[] { ("挑战清单",3), ("比赛次数上限",2), ("以1胜为目标",1.5), ("超级球星",1), ("传奇",1) }
        ), 3),

        // 挑战赛主页：「教练模式/胜场/完成的条件」为该页独有（巡回主页没有）。
        // 也含「前往比赛/赛事等级」，必须优先于 MyLeagueHome / PreMatch 仲裁
        new ScreenDef(GameScreen.ChallengeHome, Mix(
            zh: new[] { ("教练模式",3), ("胜场",2.5), ("完成的条件",2), ("活动条件",2), ("以1胜为目标",1.5),
                        ("完成挑战",2), ("活动信息",2), ("比赛历史",2), ("建议",1.5), ("前往比赛",1.5) }
        ), 4),

        // 活动设置侧滑面板：「弹性自动控制」仅此面板有（巡回主页底部只有「活动设置」提示）
        new ScreenDef(GameScreen.EventSettings, Mix(
            zh: new[] { ("弹性自动控制",3), ("活动设置",1), ("自动控制",2), ("赛事等级",1.5), ("选择的球队",1) }
        ), 4),

        new ScreenDef(GameScreen.DailyRewards, Mix(
            zh: new[] { ("登录奖励",2.5), ("签到",2), ("每日小游戏",2), ("每日",1.5) },
            en: new[] { ("loginbonus",2.5), ("daily",2), ("minigame",2) }
        ), 2),

        new ScreenDef(GameScreen.Home, Mix(
            zh: new[] { ("游戏主菜单",2), ("活动中心",2), ("我的球队",1.5), ("推荐",1.5), ("每日小游戏",1.5), ("额外选项",1), ("合约",1), ("横幅设置",1) },
            en: new[] { ("mainmenu",2), ("myteam",1.5), ("extras",1) }
        ), 2.5),

        new ScreenDef(GameScreen.TitleScreen, Mix(
            zh: new[] { ("点击屏幕",2.5), ("点击开始",2.5) },
            en: new[] { ("presstoenter",2.5), ("touchscreen",2), ("pressany",2) }
        ), 2),

        new ScreenDef(GameScreen.ItemsBar, Mix(
            zh: new[] { ("我的联赛物品",2), ("杰出状态",1.5), ("教练强化",1.5) },
            en: new[] { ("myleagueitems",2), ("excellentcondition",1.5) }
        ), 1.5),

        new ScreenDef(GameScreen.Dialog, Mix(
            zh: new[] { ("确定",1.5), ("确认",1.5), ("完成挑战",2) },
            en: new[] { ("ok",1.5), ("confirm",1.5), ("challengecomplete",2) }
        ), 3), // 必须高置信，避免误触
    };
}

/// <summary>对一帧 OCR 结果做屏幕打分分类。</summary>
public sealed class ScreenClassifier
{
    private readonly List<ScreenDef> _defs;

    // 比赛锁定（迟滞）：一旦确认进入比赛，回放/特写/转圈等弱帧一律保持 InMatch，
    // 只在强退出信号出现时解锁，避免比赛中 InMatch↔Unknown 乱跳、橙色 OCR 刷屏
    private bool _holdMatch;

    public ScreenClassifier(IEnumerable<ScreenDef>? overrides = null)
    {
        _defs = (overrides ?? ScreenCatalog.Defaults).ToList();
    }

    /// <summary>重置迟滞状态（每次开始挂机前调用，防止上一次的比赛锁定带入新场景）。</summary>
    public void Reset() => _holdMatch = false;

    public (GameScreen Screen, double Score, List<string> Evidence) Classify(OcrResult ocr, FrameData? frame)
    {
        var raw = RawClassify(ocr, frame);

        if (!_holdMatch)
        {
            if (raw.Screen == GameScreen.InMatch) _holdMatch = true;
            return raw;
        }

        // 已锁定比赛：强退出信号才放行原始判定
        if (IsStrongMatchExit(raw))
        {
            _holdMatch = false;
            return raw;
        }

        // 弱帧（回放特写 Unknown / KONAMI 转圈 Loading / 直接 InMatch）：保持比赛中
        if (raw.Screen == GameScreen.InMatch) return raw;
        var ev = new List<string> { "hold" };
        if (raw.Evidence.Count > 0) ev.Add(raw.Evidence[0]);
        return (GameScreen.InMatch, 3, ev);
    }

    /// <summary>
    /// 比赛锁定中的强退出信号：
    /// ① 赛后画面（结果/奖励/全场/中场）——命中各自阈值即可；
    /// ② 断线 / 标题画面（会话异常，需要专属处理）；
    /// ③ Dialog ≥3 分（明确确认弹窗）；
    /// ④ 其他任何菜单/活动页得分 ≥4（真机真实落地：主页5+、列表6、清单5）。
    /// </summary>
    private static bool IsStrongMatchExit((GameScreen Screen, double Score, List<string> Ev) r)
    {
        switch (r.Screen)
        {
            case GameScreen.Result:
            case GameScreen.Rewards:
            case GameScreen.FullTime:
            case GameScreen.HalfTime:
            case GameScreen.Disconnected:
            case GameScreen.TitleScreen:
                return true;
            case GameScreen.Dialog:
                return r.Score >= 3;
            case GameScreen.InMatch:
            case GameScreen.Unknown:
            case GameScreen.Loading:
                return false;
            default:
                return r.Score >= 4;
        }
    }

    private (GameScreen Screen, double Score, List<string> Evidence) RawClassify(OcrResult ocr, FrameData? frame)
    {
        var scores = new Dictionary<GameScreen, (double Score, List<string> Ev)>();

        // 赛后奖励弹窗（获得活动积分 / 收件箱通知）只盖屏幕中央，主页右上角图标仍外露，
        // 会误触发 InMatch 的 HUD 检测。弹窗特征词唯一，直接按巡回赛主页处理（场景按 A 连清）。
        if (HasRewardPopup(ocr))
            return (GameScreen.MyLeagueHome, 5, new List<string> { "reward-popup" });

        // 「使用固定智能辅助设置的活动」模态弹窗（盖在挑战赛 EventInfo 等页面上）：
        // 仅一个「确定」1.5 分达不到 Dialog 阈值，而背景的「挑战赛/剩余时间/活动」
        // 会误中 EventHub → SelectAiEventAsync 在弹窗遮挡下扫不到图标 → 误结束挂机。
        // 弹窗标题唯一，强制按 Dialog 处理，PassThrough 会点「确定」清除
        if (HasFixedAssistPopup(ocr))
            return (GameScreen.Dialog, 5, new List<string> { "fixed-assist-popup" });

        foreach (var def in _defs)
        {
            double score = 0;
            var ev = new List<string>();
            foreach (var kw in def.Keywords)
            {
                foreach (var w in ocr.FindAll(kw.Text))
                {
                    double gain = kw.Weight;
                    if (kw.Region is NormRect r && r.Contains(w.CenterX, w.CenterY)) gain += 0.5;
                    score += gain;
                    ev.Add(kw.Text);
                }
            }
            if (score >= def.MinScore)
                scores[def.Screen] = (score, ev);
        }

        // 国际服比赛菜单页（主页→「比赛」磁贴的落点）弱识别兜底：该页大字被 OCR 逐字拆开
        // （比|赛|活|动|参|加），目录里「比赛/我的联赛/自定义锦标赛」一个都不命中，
        // 整页判未知 → WaitForAny 超时 → 导航反复重试。组合信号：
        // 左上有「比」单字标题 + 全文拼接含「比赛」与「参加」（卡片/横幅用语），
        // 且没有任何其他屏幕已命中。
        if (scores.Count == 0
            && ocr.Words.Any(w => OcrText.Norm(w.Text) == "比"
                && w.CenterX < 0.22 && w.CenterY < 0.20)
            && ocr.ContainsJoined("比赛")
            && ocr.ContainsJoined("参加"))
        {
            scores[GameScreen.MatchHub] = (2.5, new List<string> { "joined-hub" });
        }

        // 国服主大厅顶部合并行「推荐比赛签约商店」含“比赛”，会同时命中比赛菜单；
        // 「活动中心」是主大厅独有（比赛菜单页没有），出现时 Home 优先
        if (scores.ContainsKey(GameScreen.Home) && scores.ContainsKey(GameScreen.MatchHub)
            && ocr.Contains("活动中心"))
            scores.Remove(GameScreen.MatchHub);

        // 冲突消解：活动详情页优先于联赛主页（主页侧栏也含“主要奖励”）；
        // 「活动说明」是详情页独有，命中即可靠
        if (scores.ContainsKey(GameScreen.ActivityDetail))
            scores.Remove(GameScreen.MyLeagueHome);

        // 冲突消解：活动设置面板打开时，背后的巡回主页（前往比赛/比赛计划）仍可被 OCR 读到，
        // 面板独有词命中时以面板为准
        if (scores.ContainsKey(GameScreen.EventSettings))
        {
            scores.Remove(GameScreen.MyLeagueHome);
            scores.Remove(GameScreen.MatchHub);
        }

        // 冲突消解：「没有正在进行的对阵玩家活动」空页优先于活动列表（空页也含“对阵玩家/活动”）
        if (scores.ContainsKey(GameScreen.NoEvent))
        {
            scores.Remove(GameScreen.EventHub);
            scores.Remove(GameScreen.MatchHub);
        }

        // 冲突消解：挑战活动详情页 / 挑战清单优先于活动列表与赛前页
        if (scores.ContainsKey(GameScreen.EventInfo))
        {
            scores.Remove(GameScreen.EventHub);
            scores.Remove(GameScreen.MatchHub);
        }
        if (scores.ContainsKey(GameScreen.ChallengeList))
        {
            scores.Remove(GameScreen.PreMatch);
            scores.Remove(GameScreen.EventHub);
        }

        // 冲突消解：挑战赛主页（教练模式/胜场独有）优先于巡回主页与赛前页
        // （挑战赛主页也含「前往比赛/赛事等级」，会同时命中 MyLeagueHome）
        if (scores.ContainsKey(GameScreen.ChallengeHome))
        {
            scores.Remove(GameScreen.MyLeagueHome);
            scores.Remove(GameScreen.PreMatch);
            scores.Remove(GameScreen.Schedule);
        }

        // 冲突消解：国际活动棋盘页右下有「下一步」，会误中赛前页；
        // 顶部活动期间的日期（10:00 / 09:59）也会误触发比赛时钟，棋盘页特征命中时以棋盘页为准
        if (scores.ContainsKey(GameScreen.CampaignBoard))
        {
            scores.Remove(GameScreen.PreMatch);
            scores.Remove(GameScreen.MatchHub);
            scores.Remove(GameScreen.InMatch);
        }

        // 证据门：MyLeagueHome 只凭「前往比赛/阵容/排名/比赛计划」等共享词不算数
        // （挑战活动赛前画面也有「前往比赛」），必须再命中主页强特征词
        if (scores.TryGetValue(GameScreen.MyLeagueHome, out var mlScore)
            && !mlScore.Ev.Any(e => e is "下一场" or "联赛排名" or "赛程表" or "积分榜"
                or "活动积分" or "总活动积分" or "赛事等级"
                or "nextmatch" or "leaguestandings"))
        {
            scores.Remove(GameScreen.MyLeagueHome);
        }

        // 冲突消解：联赛主页优先于比赛菜单（联赛主页也含“比赛/我的联赛”字样）
        if (scores.ContainsKey(GameScreen.MyLeagueHome))
            scores.Remove(GameScreen.MatchHub);

        // 联赛主页含「兑换积分」预览卡：主页特征（前往比赛/赛程表）在时，主页优先于商店
        if (scores.ContainsKey(GameScreen.MyLeagueHome))
            scores.Remove(GameScreen.Shop);

        // 积分商店页优先于比赛菜单/道具浮层
        if (scores.ContainsKey(GameScreen.Shop))
        {
            scores.Remove(GameScreen.MatchHub);
            scores.Remove(GameScreen.ItemsBar);
        }

        // 赛前页 vs 联赛主页仲裁（两者都可能含「前往比赛」）：
        // 有巡回/联赛强特征词（活动积分/赛事等级/下一场/排名赛程）→ 主页；否则赛前页
        if (scores.ContainsKey(GameScreen.PreMatch))
        {
            if (scores.TryGetValue(GameScreen.MyLeagueHome, out var homeScore))
            {
                bool tourMarker = homeScore.Ev.Any(e => e is "活动积分" or "总活动积分"
                    or "赛事等级" or "下一场" or "联赛排名" or "赛程表" or "积分榜"
                    or "nextmatch" or "leaguestandings");
                if (tourMarker) scores.Remove(GameScreen.PreMatch);
                else scores.Remove(GameScreen.MyLeagueHome);
            }
            scores.Remove(GameScreen.Schedule);
            scores.Remove(GameScreen.MatchHub);
        }

        // 比赛中：OCR 文本极少 + 顶部 HUD 白色像素稳定。
        // 已有主页/详情页强特征时不触发（主页右上角白房子等图标会误中该检测）
        if (frame is not null && !scores.ContainsKey(GameScreen.InMatch)
            && !scores.ContainsKey(GameScreen.MyLeagueHome)
            && !scores.ContainsKey(GameScreen.ActivityDetail))
        {
            // 任一菜单/活动页特征存在时，所有比赛中信号一律不触发
            // （棋盘活动页顶部日期 10:00 会误中时钟，右上角图标会误中机器人）
            var menuScreens = new[]
            {
                GameScreen.Home, GameScreen.MatchHub, GameScreen.EventHub, GameScreen.EventInfo,
                GameScreen.ChallengeList, GameScreen.ChallengeHome, GameScreen.ActivityDetail, GameScreen.PreMatch,
                GameScreen.Schedule, GameScreen.Shop, GameScreen.ShopConfirm, GameScreen.DailyRewards,
                GameScreen.EventSettings, GameScreen.KeyMatchBadge, GameScreen.MyLeagueHome,
                GameScreen.CampaignBoard, GameScreen.NoEvent,
            };
            bool hasMenu = menuScreens.Any(scores.ContainsKey);

            // 条件1：右上角机器人图标（AI 操控标识）—— 小区域白色像素聚集。
            // 菜单页（比赛菜单/活动列表）右上角图标也会误中，而菜单页 OCR 词很多；
            // 比赛直播中 HUD 文本极少，机器人图标单独命中时要求整屏词数 ≤6
            var robotRoi = new NormRect(0.86, 0.005, 0.995, 0.075);
            double robotWhite = FrameAnalyzer.ColorRatio(frame, HsvFilter.White, robotRoi);
            bool robotIcon = !hasMenu && robotWhite is > 0.015 and < 0.6 && ocr.Words.Count <= 6;

            // 条件2：顶部记分牌比赛时钟（如 21:34 / 45:00 / 90:34）。
            // 冒号常被 OCR 拆成独立词，且同一行各词 CenterY 有 1~2px 抖动，
            // 全局排序会把数字顺序拼乱（91/:/2/1 → 9112:），先按 Y 聚类成行再按 X 拼接。
            // 菜单页门控必须在正则之前：活动期间日期 10:00 同样形如时钟
            bool clock = false;
            if (!hasMenu)
            {
                var topWords = ocr.Words.Where(w => w.Y < 0.18).OrderBy(w => w.CenterY).ToList();
                var rows = new List<List<OcrWord>>();
                foreach (var w in topWords)
                {
                    List<OcrWord>? hit = null;
                    foreach (var r in rows)
                    {
                        double rowH = r.Average(x => x.H);
                        if (Math.Abs(r.Average(x => x.CenterY) - w.CenterY) <= Math.Max(rowH, w.H) * 0.55)
                        { hit = r; break; }
                    }
                    if (hit is null) rows.Add(new List<OcrWord> { w }); else hit.Add(w);
                }
                clock = rows.Any(r =>
                {
                    var byX = r.OrderBy(x => x.X).ToList();
                    string joined = string.Concat(byX.Select(x => x.Text));
                    // 读法1：冒号词存在（含全角）
                    if (global::System.Text.RegularExpressions.Regex.IsMatch(joined, @"\d{1,2}[:：]\d{2}"))
                        return true;
                    // 读法2：冒号被 OCR 完全漏识（真机 22:05 →「22」「05」两词）：
                    // 行内两个相邻的 2 位纯数字词、x 间隙 ≤0.012（比分是 1 位数不会误中）
                    for (int k = 0; k + 1 < byX.Count; k++)
                    {
                        var a = byX[k]; var b = byX[k + 1];
                        if (a.Text.Length == 2 && b.Text.Length == 2
                            && a.Text.All(char.IsDigit) && b.Text.All(char.IsDigit)
                            && b.X - (a.X + a.W) <= 0.012)
                            return true;
                    }
                    return false;
                });
            }

            // 条件3：比赛回放 / 进球庆祝字幕（国服同款判定）。
            // OCR 常把中文逐字拆开，用全文拼接匹配。
            // 中场/全场/结果/奖励等赛后画面优先，不覆盖
            bool postMatch = scores.ContainsKey(GameScreen.HalfTime)
                || scores.ContainsKey(GameScreen.FullTime)
                || scores.ContainsKey(GameScreen.Result)
                || scores.ContainsKey(GameScreen.Rewards);
            // 回放信号足够明确（「回放」「进球」字样），即使 hasMenu 有弱命中也不应完全屏蔽。
            // 真正被菜单页遮挡的回放帧极少（菜单页几乎不含「回放」）。
            bool replay = (ocr.ContainsJoined("回放") || ocr.ContainsJoined("进球")) && !postMatch;

            // 条件4：草皮占比（专用 hue 68~160，真机草皮偏黄绿 hue≈77，通用绿色滤镜会漏）
            double pitch = FrameAnalyzer.PitchRatio(frame);
            bool greenPitch = !hasMenu && !postMatch && pitch >= 0.13;

            // InMatch 判定收紧：greenPitch（球场绿）不再单独触发，必须与 robotIcon/clock/replay
            // 至少一个同时满足。入场动画/球场过场时球场绿占比也高，但缺少机器人图标和时钟，
            // 单信号误判会导致比赛计时提前、过早开始周期按 A。
            // 例外：replay 信号足够明确，单独即可判 InMatch（回放帧必须被跳过）。
            bool inMatchSignal = robotIcon || clock || replay || (greenPitch && (robotIcon || clock));
            if (inMatchSignal)
            {
                var ev = new List<string>();
                if (robotIcon) ev.Add($"robot={robotWhite:F3}");
                if (clock) ev.Add("clock");
                if (replay) ev.Add("replay");
                if (greenPitch) ev.Add($"pitch={pitch:F2}");
                scores[GameScreen.InMatch] = (3, ev);
            }
        }

        // 赛前球场过场（OCR 常把阵容表识别为乱码，131 张截图校准）：
        // 左侧白色箭头按钮 或 右下蓝色长条按钮；收紧 ROI 后所有菜单页均低于阈值。
        // 判 PreMatch → 场景层进入比赛流程后统一按 A 推进，绝不按 B。
        // 注意：比赛结束画面也有「下一步」白色大按钮，需先排除赛后画面
        // 同时排除周活动主页（有「完成挑战」或「活动信息」等特征词）
        bool isWeeklyHub = ocr.ContainsAny(new[] { "完成挑战", "活动信息", "比赛历史", "建议" });
        if (frame is not null
            && !scores.ContainsKey(GameScreen.Result)
            && !scores.ContainsKey(GameScreen.Rewards)
            && !scores.ContainsKey(GameScreen.FullTime)
            && !scores.ContainsKey(GameScreen.HalfTime)
            && !scores.Values.Any(v => v.Score >= 4)
            && !isWeeklyHub)
        {
            // 比赛结束画面特征：比分牌（如 1-0）+ 球员评分列表 + 左侧「下一步」大按钮
            bool hasScore = ocr.Words.Any(w => Regex.IsMatch(w.Text, @"^\d+-\d+$"));
            bool hasPlayerRatings = ocr.Words.Count(w => Regex.IsMatch(w.Text, @"^\d+\.\d$")) >= 3;
            bool postMatchScreen = hasScore && hasPlayerRatings;
            
            if (!postMatchScreen)
            {
                double wBtn = FrameAnalyzer.ButtonWhiteRatio(frame, new NormRect(0.02, 0.28, 0.26, 0.42));
                double bBtn = FrameAnalyzer.ButtonBlueRatio(frame, new NormRect(0.84, 0.89, 0.15, 0.07));
                if (wBtn >= 0.30 || bBtn >= 0.45)
                    return (GameScreen.PreMatch, 4, new List<string> { $"next:w={wBtn:F2}/b={bBtn:F2}" });
            }
        }

        // 视觉硬判定（不依赖 OCR）：草皮占比 ≥0.13 的只是候选，不能单独判 InMatch。
        // 入场动画/球场特写时草皮占比同样高，但缺少 AI 图标和比赛时钟，
        // 硬判会导致比赛计时提前、挂机逻辑过早启动。必须同时有 OCR 信号支撑。
        // 注意：此分支现在由条件4的 greenPitch（≥0.13）在打分阶段处理，此处不再单独返回。

        // 赛前/赛后过渡：深蓝黑底 + 中央 KONAMI 白色点状转圈（OCR 可能误识出弱菜单词，
        // 导致误判 PreMatch/InMatch）。特征明确时强制 Loading，优先级最高。
        if (frame is not null && IsLoadingSpinner(ocr, frame))
            return (GameScreen.Loading, 2, new List<string> { "spinner" });

        // 加载：几乎无文字 + 整体很暗
        if (frame is not null && scores.Count == 0 && ocr.Words.Count <= 2)
        {
            double lum = FrameAnalyzer.MeanLuminance(frame);
            if (lum < 22) scores[GameScreen.Loading] = (2, new List<string> { $"lum={lum:F0}" });
        }

        if (scores.Count == 0)
            return (GameScreen.Unknown, 0, new List<string>());

        // 比赛中有强信号（≥2.5）时，Dialog/PreMatch 不应覆盖 InMatch（避免比赛 HUD 被误判为弹窗）
        if (scores.TryGetValue(GameScreen.InMatch, out var inMatch) && inMatch.Score >= 2.5)
        {
            if (scores.Remove(GameScreen.Dialog, out var d))
                inMatch.Ev.Add($"suppressDialog({d.Score:F1})");
            if (scores.Remove(GameScreen.PreMatch, out var p))
                inMatch.Ev.Add($"suppressPreMatch({p.Score:F1})");
        }

        var best = scores.OrderByDescending(kv => kv.Value.Score).First();
        return (best.Key, best.Value.Score, best.Value.Ev);
    }

    /// <summary>是否为赛后奖励弹窗（积分弹窗 / 收件箱通知），这些弹窗只出现在巡回赛主页之上。</summary>
    private static bool HasRewardPopup(OcrResult ocr)
        => ocr.Contains("获得的活动积分")
           || ocr.Contains("距离下个奖励")
           || ocr.Contains("物品已送到收件箱")
           || ocr.Contains("送到收件箱");

    /// <summary>
    /// 是否为「使用固定智能辅助设置的活动」模态弹窗（标题常被 OCR 逐字拆开，
    /// 如「固定/智能/辅助/设置」，用全文拼接匹配；副标题「智能辅助固定为关闭」同理）。
    /// </summary>
    private static bool HasFixedAssistPopup(OcrResult ocr)
        => ocr.ContainsJoined("固定智能辅助")
           || ocr.ContainsJoined("智能辅助固定");

    /// <summary>
    /// KONAMI 过渡加载画面：整屏很暗（均值亮度&lt;30）、OCR 词极少（≤5）且无比赛时钟，
    /// 中央偏右（转圈中心约 0.81,0.30）有白色点状转圈（小面积白像素聚集）。
    /// </summary>
    private static bool IsLoadingSpinner(OcrResult ocr, FrameData frame)
    {
        if (ocr.Words.Count > 5) return false;
        if (ocr.Words.Any(w =>
            global::System.Text.RegularExpressions.Regex.IsMatch(w.Text, @"^\d{1,2}[:：]\d{2}$")))
            return false;
        if (FrameAnalyzer.MeanLuminance(frame) > 30) return false;

        var spinnerRoi = new NormRect(0.74, 0.24, 0.14, 0.12);
        double white = FrameAnalyzer.ColorRatioExact(frame, HsvFilter.White, spinnerRoi);
        return white is >= 0.004 and <= 0.08;
    }
}
