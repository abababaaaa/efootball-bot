using EfootballBot.Core.Input;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Graph;

/// <summary>
/// 页面声明目录：将 ScreenCatalog.Defaults 的 ScreenDef 迁移为声明式 PageDef。
/// 优先级分层：
///   100 — Loading, Disconnected, ActivityDetail, EventSettings, ChallengeHome,
///          EventInfo, ChallengeList, NoEvent, CampaignBoard
///    80 — Result, Rewards, HalfTime, FullTime, TitleScreen（赛后/强退出）
///    50 — MyLeagueHome, PreMatch, Shop, InMatch
///    40 — KeyMatchBadge, ShopConfirm
///    30 — Schedule, DailyRewards
///    20 — ItemsBar
///    10 — MatchHub, EventHub, Home, Dialog
/// </summary>
public static class PageCatalog
{
    public static readonly List<PageDef> Pages = new()
    {
        // ---------- Priority 100 ----------

        new PageDef
        {
            Screen = GameScreen.Loading,
            MinScore = 1.5,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "匹配中", Weight = 2 },
                new OcrProbe { Text = "配对中", Weight = 2 },
                new OcrProbe { Text = "搜索对手", Weight = 2 },
                new OcrProbe { Text = "寻找对手", Weight = 2 },
                new OcrProbe { Text = "正在匹配", Weight = 2 },
                new OcrProbe { Text = "正在搜索", Weight = 1.5 },
                new OcrProbe { Text = "对手连接", Weight = 2 },
                new OcrProbe { Text = "连接对手", Weight = 2 },
                new OcrProbe { Text = "正在连接", Weight = 1.5 },
                new OcrProbe { Text = "已找到对手", Weight = 2 },
                new OcrProbe { Text = "匹配对手", Weight = 2 },
                new OcrProbe { Text = "加载中", Weight = 1.5 },
                new OcrProbe { Text = "正在载入", Weight = 1.5 },
                new OcrProbe { Text = "读取中", Weight = 1.5 },
                new OcrProbe { Text = "请稍候", Weight = 1.5 },
                new OcrProbe { Text = "matching", Weight = 2 },
                new OcrProbe { Text = "searching", Weight = 2 },
                new OcrProbe { Text = "nowloading", Weight = 2 },
                new OcrProbe { Text = "connecting", Weight = 1.5 },
                new OcrProbe { Text = "pleasewait", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Disconnected,
            MinScore = 1.5,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "无法连接", Weight = 2 },
                new OcrProbe { Text = "连接失败", Weight = 2 },
                new OcrProbe { Text = "通信错误", Weight = 2 },
                new OcrProbe { Text = "网络错误", Weight = 2 },
                new OcrProbe { Text = "返回标题", Weight = 1.5 },
                new OcrProbe { Text = "重新连接", Weight = 1.5 },
                new OcrProbe { Text = "确认返回", Weight = 1 },
                new OcrProbe { Text = "communicationerror", Weight = 2 },
                new OcrProbe { Text = "connectionerror", Weight = 2 },
                new OcrProbe { Text = "networkerror", Weight = 2 },
                new OcrProbe { Text = "returntotitle", Weight = 1.5 },
                new OcrProbe { Text = "reconnect", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.ActivityDetail,
            // 「活动说明」是活动详情页底部独占标签（真机 dump 130836 全页仅该强特征）
            MinScore = 3,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "活动说明", Weight = 3 },
                new OcrProbe { Text = "活动时间", Weight = 2.5 },
                new OcrProbe { Text = "主要奖励", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.EventSettings,
            MinScore = 4,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "弹性自动控制", Weight = 3 },
                new OcrProbe { Text = "活动设置", Weight = 1 },
                new OcrProbe { Text = "自动控制", Weight = 2 },
                new OcrProbe { Text = "赛事等级", Weight = 1.5 },
                new OcrProbe { Text = "选择的球队", Weight = 1 },
            }
        },

        new PageDef
        {
            // 「选择赛事等级」模态弹窗（我的联赛开赛前）
            Screen = GameScreen.DifficultySelect,
            MinScore = 4,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "选择赛事等级", Weight = 4 },
                new OcrProbe { Text = "选择符合", Weight = 2.5 },
                new OcrProbe { Text = "难度级别", Weight = 2.5 },
                new OcrProbe { Text = "可赚取", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.ChallengeHome,
            MinScore = 4,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "教练模式", Weight = 3 },
                new OcrProbe { Text = "胜场", Weight = 2.5 },
                new OcrProbe { Text = "完成的条件", Weight = 2 },
                new OcrProbe { Text = "活动条件", Weight = 2 },
                new OcrProbe { Text = "以1胜为目标", Weight = 1.5 },
                new OcrProbe { Text = "完成挑战", Weight = 2 },
                new OcrProbe { Text = "活动信息", Weight = 2 },
                new OcrProbe { Text = "比赛历史", Weight = 2 },
                // 不收录「建议」「前往比赛」：比赛菜单磁贴预览含这两词（真机 19:34 日志
                // 曾以 4.5 越过门槛误判 ChallengeHome）；本页独占词已足够过 MinScore
            }
        },

        new PageDef
        {
            Screen = GameScreen.EventInfo,
            MinScore = 4,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "详情", Weight = 3 },
                // 按按钮计数：卡片列表同排并列多个「进入」（行聚合会并成一条）
                new OcrProbe { Text = "进入", Weight = 1.5, CountWords = true },
                new OcrProbe { Text = "挑战活动", Weight = 1.5 },
                // 活动卡片列表（每张卡带「进入」+「剩余时间」，真机 dump 131240 等 7 帧）：
                // 仅靠 2 个「进入」3 分不够门槛，剩余时间补分；活动中心磁贴页无「进入」
                new OcrProbe { Text = "剩余时间", Weight = 1 },
                new OcrProbe { Text = "结束倒数", Weight = 1 },
                new OcrProbe { Text = "关闭", Weight = 1 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.ChallengeList,
            MinScore = 3,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "挑战清单", Weight = 3 },
                new OcrProbe { Text = "比赛次数上限", Weight = 2 },
                new OcrProbe { Text = "以1胜为目标", Weight = 1.5 },
                new OcrProbe { Text = "超级球星", Weight = 1 },
                new OcrProbe { Text = "传奇", Weight = 1 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.NoEvent,
            MinScore = 2.5,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "没有正在进行", Weight = 3 },
                new OcrProbe { Text = "正在进行的对阵", Weight = 2.5 },
                new OcrProbe { Text = "对阵玩家活动", Weight = 2 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.CampaignBoard,
            MinScore = 3,
            Priority = 100,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "总积分", Weight = 3 },
                new OcrProbe { Text = "自选合约", Weight = 3 },
                new OcrProbe { Text = "当前位置", Weight = 2.5 },
                new OcrProbe { Text = "活动期间", Weight = 2.5 },
                new OcrProbe { Text = "格子数", Weight = 2.5 },
                new OcrProbe { Text = "campaign", Weight = 3 },
                new OcrProbe { Text = "internationalmatch", Weight = 2.5 },
            }
        },

        // ---------- Priority 80 (赛后 / 强退出) ----------

        new PageDef
        {
            Screen = GameScreen.HalfTime,
            MinScore = 2,
            Priority = 80,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "中场休息", Weight = 2.5 },
                // 「中场」单独出现（主页新闻横幅等）不足以判定中场休息，权重压到阈值下，
                // 需与「中场休息」等其他信号同行/同帧共现（MinScore=2）
                new OcrProbe { Text = "中场", Weight = 1.5 },
                new OcrProbe { Text = "halftime", Weight = 2.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.FullTime,
            MinScore = 2,
            Priority = 80,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "全场比赛结束", Weight = 2.5 },
                new OcrProbe { Text = "比赛结束", Weight = 2 },
                new OcrProbe { Text = "fulltime", Weight = 2.5 },
                new OcrProbe { Text = "matchended", Weight = 2 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Result,
            MinScore = 2,
            Priority = 80,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "最佳球员", Weight = 2 },
                new OcrProbe { Text = "球员经验", Weight = 2 },
                new OcrProbe { Text = "获得经验", Weight = 1.5 },
                new OcrProbe { Text = "比赛结果", Weight = 1.5 },
                new OcrProbe { Text = "mvp", Weight = 1.5 },
                new OcrProbe { Text = "球员评价", Weight = 2.5 },
                new OcrProbe { Text = "球员评分", Weight = 2 },
                new OcrProbe { Text = "球员表现", Weight = 2 },
                new OcrProbe { Text = "playerofthematch", Weight = 2 },
                new OcrProbe { Text = "playerexp", Weight = 2 },
                new OcrProbe { Text = "matchresult", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Rewards,
            MinScore = 2.5,
            Priority = 80,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "获得奖励", Weight = 2.0 },
                new OcrProbe { Text = "奖励详情", Weight = 2.0 },
                new OcrProbe { Text = "领取奖励", Weight = 2.0 },
                new OcrProbe { Text = "获得", Weight = 1.0 },
                new OcrProbe { Text = "rewards", Weight = 2 },
                new OcrProbe { Text = "youearned", Weight = 2 },
                new OcrProbe { Text = "obtained", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.TitleScreen,
            MinScore = 2,
            Priority = 80,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "点击屏幕", Weight = 2.5 },
                new OcrProbe { Text = "点击开始", Weight = 2.5 },
                new OcrProbe { Text = "presstoenter", Weight = 2.5 },
                new OcrProbe { Text = "touchscreen", Weight = 2 },
                new OcrProbe { Text = "pressany", Weight = 2 },
            }
        },

        // ---------- Priority 50 ----------

        new PageDef
        {
            Screen = GameScreen.MyLeagueHome,
            MinScore = 2.5,
            Priority = 50,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "前往比赛", Weight = 2.5 },
                new OcrProbe { Text = "下一场", Weight = 2.5 },
                new OcrProbe { Text = "联赛排名", Weight = 2.5 },
                new OcrProbe { Text = "赛程表", Weight = 2 },
                // 不收录「阵容」：活动中心的「阵容召集」磁贴标签+描述会在两行命中（真机 dump
                // 130834 等 3 帧误判 MyLeagueHome），而联赛主页本身无「阵容」字样
                new OcrProbe { Text = "排名", Weight = 1.5 },
                new OcrProbe { Text = "积分榜", Weight = 1.5 },
                new OcrProbe { Text = "比赛计划", Weight = 1 },
                new OcrProbe { Text = "活动积分", Weight = 2.5 },
                new OcrProbe { Text = "赛事等级", Weight = 2.5 },
                new OcrProbe { Text = "主要奖励", Weight = 1.5 },
                new OcrProbe { Text = "总活动积分", Weight = 2.5 },
                new OcrProbe { Text = "gotomatch", Weight = 2.5 },
                new OcrProbe { Text = "nextmatch", Weight = 2.5 },
                new OcrProbe { Text = "fixtures", Weight = 2 },
                new OcrProbe { Text = "leaguestandings", Weight = 2 },
                new OcrProbe { Text = "squad", Weight = 2 },
                new OcrProbe { Text = "gameplan", Weight = 1 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.PreMatch,
            MinScore = 2.5,
            Priority = 50,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "开始比赛", Weight = 2.5 },
                new OcrProbe { Text = "下一步", Weight = 2.5 },
                new OcrProbe { Text = "前往比赛", Weight = 2.5 },
                new OcrProbe { Text = "选择比赛难度", Weight = 3 },
                new OcrProbe { Text = "顶尖球员", Weight = 1.5 },
                new OcrProbe { Text = "超级球星", Weight = 1.5 },
                new OcrProbe { Text = "比赛条件", Weight = 1.5 },
                new OcrProbe { Text = "选手操作", Weight = 1.5 },
                new OcrProbe { Text = "新手", Weight = 1.5 },
                new OcrProbe { Text = "难度", Weight = 1 },
                new OcrProbe { Text = "职业", Weight = 1 },
                new OcrProbe { Text = "比赛计划", Weight = 1 },
                new OcrProbe { Text = "主场", Weight = 1 },
                new OcrProbe { Text = "客场", Weight = 1 },
                new OcrProbe { Text = "kickoff", Weight = 2.5 },
                new OcrProbe { Text = "next", Weight = 2.5 },
                new OcrProbe { Text = "gotomatch", Weight = 2.5 },
                new OcrProbe { Text = "matchconditions", Weight = 1.5 },
                new OcrProbe { Text = "playeroperations", Weight = 1.5 },
                new OcrProbe { Text = "beginner", Weight = 1.5 },
                new OcrProbe { Text = "difficulty", Weight = 1 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Shop,
            MinScore = 2.5,
            Priority = 50,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "租借球员", Weight = 3 },
                new OcrProbe { Text = "我的联赛物品", Weight = 3 },
                new OcrProbe { Text = "获得各种物品", Weight = 2.5 },
                new OcrProbe { Text = "租借有用", Weight = 2.5 },
                new OcrProbe { Text = "选择要兑换", Weight = 2.5 },
                new OcrProbe { Text = "持有物品", Weight = 2 },
                new OcrProbe { Text = "使用物品", Weight = 2 },
                new OcrProbe { Text = "loanplayer", Weight = 3 },
                new OcrProbe { Text = "pointexchange", Weight = 3 },
                new OcrProbe { Text = "exchangeitems", Weight = 2.5 },
            }
        },

        new PageDef
        {
            // 比赛中：视觉信号（机器人图标 + 草皮绿）+ 回放关键词。
            // 注意：新引擎不做旧分类器的复杂时钟/回放/形状组合判定，
            // 此处仅以基础视觉探针近似，供影子模式对比。
            Screen = GameScreen.InMatch,
            MinScore = 0.5,
            Priority = 50,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new VisualProbe
                {
                    Filter = HsvFilter.White,
                    Roi = new NormRect(0.86, 0.005, 0.135, 0.07),
                    MinRatio = 0.015,
                    Weight = 10,
                    // 商店「M 112,980」余额白字落在同 ROI：多词帧/占比过高均排除
                    MaxOcrWords = 6,
                    MaxRatio = 0.6,
                },
                // 草皮绿只是弱佐证：入场过场同样满屏绿，单独不得越过 MinScore（旧分类器
                // 要求 green 与 robot/clock 同时成立），且菜单/多词帧直接归零
                new VisualProbe
                {
                    Filter = HsvFilter.Green,
                    Roi = NormRect.Full,
                    MinRatio = 0.10,
                    Weight = 0.4,
                    MaxOcrWords = 6,
                },
                new ClockHudProbe(),
                new OcrProbe { Text = "回放", Weight = 3 },
                new OcrProbe { Text = "进球", Weight = 3 },
                new OcrProbe { Text = "replay", Weight = 3 },
            }
        },

        // ---------- Priority 40 ----------

        new PageDef
        {
            Screen = GameScreen.KeyMatchBadge,
            MinScore = 2,
            Priority = 40,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "关键比赛", Weight = 2.5 },
                new OcrProbe { Text = "keymatch", Weight = 2.5 },
                new OcrProbe { Text = "keypmatch", Weight = 2.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.ShopConfirm,
            MinScore = 2.5,
            Priority = 40,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "确认购买", Weight = 2.5 },
                new OcrProbe { Text = "购买", Weight = 1.5 },
                new OcrProbe { Text = "兑换", Weight = 1.5 },
                new OcrProbe { Text = "confirmpurchase", Weight = 2.5 },
                new OcrProbe { Text = "purchase", Weight = 1.5 },
                new OcrProbe { Text = "buy", Weight = 1 },
            }
        },

        // ---------- Priority 30 ----------

        new PageDef
        {
            Screen = GameScreen.Schedule,
            MinScore = 2,
            Priority = 30,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "比赛日", Weight = 2 },
                new OcrProbe { Text = "选择符合", Weight = 1.5 },
                new OcrProbe { Text = "即将进行", Weight = 1.5 },
                new OcrProbe { Text = "赛程", Weight = 1.5 },
                new OcrProbe { Text = "matchday", Weight = 2 },
                new OcrProbe { Text = "upcoming", Weight = 1.5 },
                new OcrProbe { Text = "fixtures", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.DailyRewards,
            // 主页「每日小游戏」磁贴只值 2 分，单独不得过门槛（真机 19:34 日志：
            // 「每日小游戏」+泛词「每日」=3.5 曾判 DailyRewards 压过主页）
            MinScore = 3.5,
            Priority = 30,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "登录奖励", Weight = 2.5 },
                new OcrProbe { Text = "签到", Weight = 2 },
                new OcrProbe { Text = "每日小游戏", Weight = 2 },
                // 「每日」过于泛化（主页磁贴/商店均有），不收录
                new OcrProbe { Text = "loginbonus", Weight = 2.5 },
                new OcrProbe { Text = "daily", Weight = 2 },
                new OcrProbe { Text = "minigame", Weight = 2 },
            }
        },

        // ---------- Priority 20 ----------

        new PageDef
        {
            Screen = GameScreen.ItemsBar,
            MinScore = 1.5,
            Priority = 20,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "我的联赛物品", Weight = 2 },
                new OcrProbe { Text = "杰出状态", Weight = 1.5 },
                new OcrProbe { Text = "教练强化", Weight = 1.5 },
                new OcrProbe { Text = "myleagueitems", Weight = 2 },
                new OcrProbe { Text = "excellentcondition", Weight = 1.5 },
            }
        },

        // ---------- Priority 10 ----------

        new PageDef
        {
            Screen = GameScreen.MatchHub,
            MinScore = 2.5,
            Priority = 10,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "比赛", Weight = 1 },
                new OcrProbe { Text = "我的联赛", Weight = 2.5 },
                new OcrProbe { Text = "快速比赛", Weight = 2 },
                new OcrProbe { Text = "好友对战", Weight = 2 },
                // 天梯巅峰赛是比赛菜单好友对战页独占入口（真机 dump 131238，区别于活动磁贴「天梯赛-教练」）
                new OcrProbe { Text = "天梯巅峰赛", Weight = 2 },
                new OcrProbe { Text = "自定义锦标赛", Weight = 2 },
                new OcrProbe { Text = "球员俱乐部", Weight = 1.5 },
                new OcrProbe { Text = "match", Weight = 1 },
                new OcrProbe { Text = "myleague", Weight = 2.5 },
                new OcrProbe { Text = "quickmatch", Weight = 2 },
                new OcrProbe { Text = "friendmatch", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.EventHub,
            MinScore = 3,
            Priority = 10,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "对阵电脑", Weight = 2.5 },
                new OcrProbe { Text = "对阵ai", Weight = 2.5 },
                new OcrProbe { Text = "巡回赛", Weight = 2 },
                new OcrProbe { Text = "对阵玩家", Weight = 1.5 },
                new OcrProbe { Text = "挑战赛", Weight = 1.5 },
                new OcrProbe { Text = "巡回", Weight = 1.5 },
                new OcrProbe { Text = "挑战活动", Weight = 2 },
                new OcrProbe { Text = "巡回活动", Weight = 2 },
                new OcrProbe { Text = "剩余时间", Weight = 1 },
                new OcrProbe { Text = "活动", Weight = 1 },
                new OcrProbe { Text = "events", Weight = 2 },
                new OcrProbe { Text = "tour", Weight = 1.5 },
                new OcrProbe { Text = "challengeevent", Weight = 1.5 },
                new OcrProbe { Text = "vsai", Weight = 1.5 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Home,
            MinScore = 2.5,
            Priority = 10,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "游戏主菜单", Weight = 2 },
                new OcrProbe { Text = "活动中心", Weight = 2 },
                new OcrProbe { Text = "我的球队", Weight = 1.5 },
                new OcrProbe { Text = "推荐", Weight = 1.5 },
                // 顶部导航词（真机 dump 130850 OCR 只抓到导航行）：权重低，需两个以上共现，
                // 比赛菜单/商店等页有更强的高分探针压过
                new OcrProbe { Text = "签约", Weight = 1 },
                new OcrProbe { Text = "商店", Weight = 1 },
                new OcrProbe { Text = "每日小游戏", Weight = 1.5 },
                new OcrProbe { Text = "额外选项", Weight = 1 },
                // OCR 逐字拆开时底部标签只剩「额|外」，行拼接后由此探针兜底
                new OcrProbe { Text = "额外", Weight = 1 },
                new OcrProbe { Text = "合约", Weight = 1 },
                new OcrProbe { Text = "横幅设置", Weight = 1 },
                new OcrProbe { Text = "mainmenu", Weight = 2 },
                new OcrProbe { Text = "myteam", Weight = 1.5 },
                new OcrProbe { Text = "extras", Weight = 1 },
            }
        },

        new PageDef
        {
            Screen = GameScreen.Dialog,
            MinScore = 3,
            Priority = 10,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "确定", Weight = 1.5 },
                new OcrProbe { Text = "确认", Weight = 1.5 },
                new OcrProbe { Text = "完成挑战", Weight = 2 },
                new OcrProbe { Text = "ok", Weight = 1.5 },
                new OcrProbe { Text = "confirm", Weight = 1.5 },
                new OcrProbe { Text = "challengecomplete", Weight = 2 },
            }
        },
    };

    // ================= 导航边（声明式按键序列） =================
    // 节点 id 使用 GameScreen 枚举的字符串名（如 "Home", "MatchHub", "MyLeagueHome"）。
    // 动作序列与等待时间与原 BotEngine.EnterXxxAsync 完全一致。
    // 半途接续（已在 MatchHub 就不回主页）由 BFS 自动获得。

    private static NavAction[] RepeatDpad(PadDir dir, int count, int gapMs)
    {
        var list = new List<NavAction>();
        for (int i = 0; i < count; i++)
        {
            list.Add(NavAction.Dpad(dir));
            list.Add(NavAction.Wait(gapMs));
        }
        return list.ToArray();
    }

    public static readonly Edge[] Edges = new[]
    {
        // ---------- Home → MatchHub ----------
        // 原 EnterMyLeagueAsync / EnterEventHubAsync：
        //   Down(200) → Down(300) → Left(200) → Left(500) → A(2500)
        new Edge
        {
            From = nameof(GameScreen.Home),
            To = nameof(GameScreen.MatchHub),
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Down),  NavAction.Wait(200),
                NavAction.Dpad(PadDir.Down),  NavAction.Wait(300),
                NavAction.Dpad(PadDir.Left),  NavAction.Wait(200),
                NavAction.Dpad(PadDir.Left),  NavAction.Wait(500),
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- MatchHub → MyLeagueHome ----------
        // 原 EnterMyLeagueAsync：Right(350) → Right(500) → A(4500)
        new Edge
        {
            From = nameof(GameScreen.MatchHub),
            To = nameof(GameScreen.MyLeagueHome),
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Right), NavAction.Wait(350),
                NavAction.Dpad(PadDir.Right), NavAction.Wait(500),
                NavAction.Press(PadButton.A), NavAction.Wait(4500),
            },
        },

        // ---------- MatchHub → EventHub ----------
        // 原 EnterEventHubAsync：Up(150)×2 → Left(150)×8 → wait 400 → A(2500)
        new Edge
        {
            From = nameof(GameScreen.MatchHub),
            To = nameof(GameScreen.EventHub),
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Up),    NavAction.Wait(150),
                NavAction.Dpad(PadDir.Up),    NavAction.Wait(150),
            }
            .Concat(RepeatDpad(PadDir.Left, 8, 150))
            .Concat(new[]
            {
                NavAction.Wait(400),
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            })
            .ToArray(),
        },

        // ---------- MyLeagueHome → Schedule ----------
        new Edge
        {
            From = nameof(GameScreen.MyLeagueHome),
            To = nameof(GameScreen.Schedule),
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Right), NavAction.Wait(300),
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- MyLeagueHome → Shop（仅在需要购买道具时可用） ----------
        new Edge
        {
            From = nameof(GameScreen.MyLeagueHome),
            To = nameof(GameScreen.Shop),
            Condition = ctx => ctx.NeedBuyItems,
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Down),  NavAction.Wait(300),
                NavAction.Dpad(PadDir.Right), NavAction.Wait(300),
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- MyLeagueHome → PreMatch（前往比赛：Left 归位 + A） ----------
        // 对应 ScenarioBase.StartMatchFromHomeAsync：Left(250) → A(2500)
        new Edge
        {
            From = nameof(GameScreen.MyLeagueHome),
            To = nameof(GameScreen.PreMatch),
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Left),  NavAction.Wait(250),
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- Schedule → PreMatch ----------
        new Edge
        {
            From = nameof(GameScreen.Schedule),
            To = nameof(GameScreen.PreMatch),
            Actions = new[]
            {
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- EventHub → EventInfo（选中活动卡查看详情） ----------
        new Edge
        {
            From = nameof(GameScreen.EventHub),
            To = nameof(GameScreen.EventInfo),
            Actions = new[]
            {
                NavAction.Press(PadButton.A), NavAction.Wait(2500),
            },
        },

        // ---------- 全量回退边（18 条，Verified=false，待真机逐条确认） ----------

        // 1. MatchHub → Home
        new Edge
        {
            From = nameof(GameScreen.MatchHub),
            To = nameof(GameScreen.Home),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(1000),
            },
        },

        // 2. MyLeagueHome → MatchHub
        new Edge
        {
            From = nameof(GameScreen.MyLeagueHome),
            To = nameof(GameScreen.MatchHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 3. EventHub → MatchHub
        new Edge
        {
            From = nameof(GameScreen.EventHub),
            To = nameof(GameScreen.MatchHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 4. EventInfo → EventHub
        new Edge
        {
            From = nameof(GameScreen.EventInfo),
            To = nameof(GameScreen.EventHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 5. Schedule → MyLeagueHome
        new Edge
        {
            From = nameof(GameScreen.Schedule),
            To = nameof(GameScreen.MyLeagueHome),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 6. PreMatch → MyLeagueHome
        new Edge
        {
            From = nameof(GameScreen.PreMatch),
            To = nameof(GameScreen.MyLeagueHome),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 7. DifficultySelect → PreMatch
        new Edge
        {
            From = nameof(GameScreen.DifficultySelect),
            To = nameof(GameScreen.PreMatch),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(700),
            },
        },

        // 8. Shop → MyLeagueHome（连按 3 次 B：逐层退出商店）
        new Edge
        {
            From = nameof(GameScreen.Shop),
            To = nameof(GameScreen.MyLeagueHome),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(600),
                NavAction.Press(PadButton.B), NavAction.Wait(600),
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 9. ShopConfirm → Shop
        new Edge
        {
            From = nameof(GameScreen.ShopConfirm),
            To = nameof(GameScreen.Shop),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(700),
            },
        },

        // 10. EventSettings → EventInfo
        new Edge
        {
            From = nameof(GameScreen.EventSettings),
            To = nameof(GameScreen.EventInfo),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 11. ChallengeHome → EventInfo
        new Edge
        {
            From = nameof(GameScreen.ChallengeHome),
            To = nameof(GameScreen.EventInfo),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 12. ChallengeList → ChallengeHome
        new Edge
        {
            From = nameof(GameScreen.ChallengeList),
            To = nameof(GameScreen.ChallengeHome),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 13. ActivityDetail → EventHub
        new Edge
        {
            From = nameof(GameScreen.ActivityDetail),
            To = nameof(GameScreen.EventHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 14. NoEvent → EventHub
        new Edge
        {
            From = nameof(GameScreen.NoEvent),
            To = nameof(GameScreen.EventHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 15. CampaignBoard → EventHub（右移焦点到关闭/返回入口后 A 确认）
        new Edge
        {
            From = nameof(GameScreen.CampaignBoard),
            To = nameof(GameScreen.EventHub),
            Verified = false,
            Actions = new[]
            {
                NavAction.Dpad(PadDir.Right), NavAction.Wait(800),
                NavAction.Press(PadButton.A),    NavAction.Wait(2300),
            },
        },

        // 16. DailyRewards → Home
        new Edge
        {
            From = nameof(GameScreen.DailyRewards),
            To = nameof(GameScreen.Home),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 17. ItemsBar → MyLeagueHome
        new Edge
        {
            From = nameof(GameScreen.ItemsBar),
            To = nameof(GameScreen.MyLeagueHome),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(900),
            },
        },

        // 18. KeyMatchBadge → Schedule
        new Edge
        {
            From = nameof(GameScreen.KeyMatchBadge),
            To = nameof(GameScreen.Schedule),
            Verified = false,
            Actions = new[]
            {
                NavAction.Press(PadButton.B), NavAction.Wait(700),
            },
        },
    };
}
