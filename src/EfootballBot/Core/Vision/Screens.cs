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
    DifficultySelect,   // 「选择赛事等级」模态弹窗（7 档难度单选 + 取消/确认，我的联赛开赛前）
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
    CampaignBoard,      // 国际服 International Match Campaign 棋盘格活动页
    ItemsBar,           // R3 道具浮层
    Shop,               // 积分兑换商店
    ShopConfirm,        // 购买确认
    Dialog,             // 通用确认弹窗
    NoEvent,            // 「没有正在进行的对阵玩家活动」空页
    Disconnected,       // 断线 / 通信错误
}
