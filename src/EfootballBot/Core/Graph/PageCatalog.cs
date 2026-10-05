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
            MinScore = 4,
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
                new OcrProbe { Text = "建议", Weight = 1.5 },
                new OcrProbe { Text = "前往比赛", Weight = 1.5 },
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
                new OcrProbe { Text = "进入", Weight = 1.5 },
                new OcrProbe { Text = "挑战活动", Weight = 1.5 },
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
                new OcrProbe { Text = "中场", Weight = 2 },
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
                new OcrProbe { Text = "阵容", Weight = 2 },
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
                },
                new VisualProbe
                {
                    Filter = HsvFilter.Green,
                    Roi = NormRect.Full,
                    MinRatio = 0.10,
                    Weight = 5,
                },
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
            MinScore = 2,
            Priority = 30,
            Probes = new EfootballBot.Core.Probe.Probe[]
            {
                new OcrProbe { Text = "登录奖励", Weight = 2.5 },
                new OcrProbe { Text = "签到", Weight = 2 },
                new OcrProbe { Text = "每日小游戏", Weight = 2 },
                new OcrProbe { Text = "每日", Weight = 1.5 },
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
                new OcrProbe { Text = "每日小游戏", Weight = 1.5 },
                new OcrProbe { Text = "额外选项", Weight = 1 },
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
    };
}
