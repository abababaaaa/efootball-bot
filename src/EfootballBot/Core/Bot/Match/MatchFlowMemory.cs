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
/// 流程帧记忆：滚动记录最近的界面判定（时间+界面），给决策提供时间上下文。
/// 用途：比赛中镜头切换偶发丢失记分牌时钟 → 单帧判 Unknown，若最近刚见过比赛特征，
/// 不应立刻按 B 逃生（B 在比赛中是铲球键）。
/// </summary>
internal sealed class MatchFlowMemory
{
    private readonly List<(DateTime At, MatchScreen Screen)> _items = new();

    private readonly TimeSpan _retention;

    internal MatchFlowMemory(TimeSpan? retention = null) => _retention = retention ?? TimeSpan.FromMinutes(2);

    internal void Record(MatchScreen screen, DateTime at)
    {
        _items.Add((at, screen));
        Prune(at);
    }

    private void Prune(DateTime now)
    {
        // 按时间追加，过期项只会出现在头部
        int i = 0;
        while (i < _items.Count && now - _items[i].At > _retention) i++;
        if (i > 0) _items.RemoveRange(0, i);
    }

    /// <summary>窗口内是否出现过该界面。</summary>
    internal bool SawWithin(MatchScreen screen, TimeSpan window, DateTime now)
        => _items.Any(it => it.Screen == screen && now - it.At <= window);
    /// <summary>
    /// 清空全部记录（重启游戏后调用）：旧会话的 InMatch/MatchStartPage 记忆会让
    /// <see cref="MatchShared.TitleGrace"/> 把重进后的真标题页当成「开赛加载画面」而
    /// 一直不按 X——重启后必须从零开始判断。
    /// </summary>
    internal void Clear() => _items.Clear();
}

/// <summary>
/// 比赛场次跟踪：首次 InMatch 视为开赛；全场结束（FullTime，权威终场信号）、回到菜单类界面
/// （LeaguePage/MatchHub/MainMenu）或连续 5 分钟无比赛特征视为一场结束（供统计计数）。
/// Unknown/HasConfirm（重播、结算弹窗）不会误关场次。
/// </summary>
/// <summary>一场比赛是怎么收尾的。</summary>
internal enum MatchEndReason
{
    /// <summary>还没结束过。</summary>
    None,
    /// <summary>见到终场页（比分 + 球员评价）——权威信号。</summary>
    FullTime,
    /// <summary>直接回到比赛页/主菜单——没看到终场页也算结束（弹窗链被跳过的场景）。</summary>
    Menu,
    /// <summary>连续 5 分钟没再出现比赛特征：可能是赛后回放/结算页卡住，也可能画面判定丢了。
    /// 这种「结束」不可靠，调用方不得计入完成场次。</summary>
    Timeout,
}

internal sealed class MatchSessionTracker
{
    private bool _active;

    private DateTime _lastInMatchAt;

    private DateTime _lastEndAt = DateTime.MinValue;

    private bool _sawKeyMatch;
    /// <summary>
    /// 关场后的重开冷却：结算页/回放页偶尔会被角区兜底误判成 InMatch，关场后一两秒就
    /// "又开一场"，一场比赛被反复计数。真实流程里两场之间必然要回主页/选难度/加载，
    /// 远不止这么多秒——见用户 2026-11-13 反馈「完成场次 76、游戏战绩 29 场」。
    /// </summary>
    internal static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(90);
    /// <summary>
    /// 关场后必须先见到菜单/主页，才允许开下一场。结算链（回放、统计、奖励弹窗）里
    /// 角区探针经常把某一帧判成 InMatch，靠时间冷却挡不住——结算可以拖好几分钟。
    /// 真实流程里两场之间必然回到主页/比赛页，所以这个门是可靠的。
    /// 场景层在识别到自己的主页时调 <see cref="MarkAtMenu"/>（AiLadder 的组别主页
    /// 传 isHome:false，Observe 内部判不出来）。
    /// </summary>
    private bool _needMenuBeforeRestart; // 会话开始时（可能半途接管在比赛中）允许直接开赛
    private DateTime _needMenuSince = DateTime.MinValue;
    /// <summary>「须见菜单」的门最多挡这么久：某些场景（国服周活动/天梯巅峰赛）没有显式的
    /// 主页回调，死守会变成永远不开赛。结算链一般 1~3 分钟，5 分钟足够兜住。</summary>
    internal static readonly TimeSpan MenuGateMaxWait = TimeSpan.FromMinutes(5);
    /// <summary>
    /// 「回到菜单」关场的最小时长：60 秒。
    /// 真比赛一场 6~20 分钟，绝无可能 60 秒内打完；但进球庆祝/回放 CG 只要几秒就能骗出一次
    /// 菜单类判定（2026-11-20 实机 00:34:35：进球后庆祝 CG 被判 MatchHub，一场走了 21 秒的
    /// 「比赛」被计入完成场次，用户反馈「进球后 CG 误判成比赛结束」）。这条门只挡单帧误判，
    /// 不挡真流程——真回菜单的场次用时都远超这个数。
    /// 拦下的那一轮不改任何状态：继续观察，真回到菜单的话「5 分钟无比赛特征」会兜底收尾。
    /// </summary>
    internal static readonly TimeSpan MenuCloseMinDuration = TimeSpan.FromSeconds(60);

    internal bool Active => _active;
    /// <summary>本场首次被判为 InMatch 的时间（null = 未开赛）。只打关键比赛用它算时钟走了多久。</summary>
    internal DateTime? StartedAt { get; private set; }
    /// <summary>刚结束的这场是否关键比赛（会话期间见过 KeyMatchDialog/关键比赛字样）。</summary>
    internal bool WasKeyMatch { get; private set; }
    /// <summary>最近一场的实际用时（首次 InMatch → 关场）。诊断「一场 20 分钟」这类反馈用：
    /// 日志里直接给 mm:ss，不用再从几百行时间戳里倒推。</summary>
    internal TimeSpan? LastDuration { get; private set; }
    /// <summary>最近一场是怎么判「结束」的。超时（游戏画面里一直没再出现比赛特征）
    /// 不等于真打完——用户 2026-11-13 反馈「哪儿来的 76 场」，多出来的场次必须靠这条区分。</summary>
    internal MatchEndReason LastEndReason { get; private set; } = MatchEndReason.None;
    /// <summary>这次「结束」是否可信（见到终场页或直接回到菜单）。超时不算——
    /// 调用方据此决定要不要计入完成场次。</summary>
    internal bool LastEndTrusted => LastEndReason is MatchEndReason.FullTime or MatchEndReason.Menu;
    /// <summary>本轮是否因为「本场才刚开始」把一次菜单类关场拦下了（供 MatchPlay 打日志：
    /// 用户在日志里能看到「判到 MatchHub 但只走了 00:21，按误判处理」而不是凭空少一场）。</summary>
    internal bool MenuCloseSuppressed { get; private set; }
    /// <summary>最近一场用时的 mm:ss 文本（无记录时 --:--）。
    /// 负时长（系统时钟被校正/回拨导致 now &lt; started）显示为「时长异常」，
    /// 不再输出 "-4 分钟"这种让人以为踢了负场、实为记账时钟问题的误导文字。</summary>
    internal string LastDurationText => LastDuration is TimeSpan d
        ? (d < TimeSpan.Zero ? "时长异常(时钟回拨)" : $"{(int)d.TotalMinutes:00}:{d.Seconds:00}")
        : "--:--";
    /// <summary>观察一轮；返回 true 表示本轮判定一场比赛结束。
    /// isHome：LeaguePage 是否为真主页（OCR 含前往比赛/下个赛季）——开场过渡帧会被
    /// YOLO btn_back 误判 LeaguePage（实机 2026-10-10 21:23 开场 2 秒误关场次），
    /// 仅真主页才视为"回到菜单"关场。</summary>
    internal bool Observe(MatchScreen screen, DateTime now, bool sawKeyMatchHint = false, bool isHome = true)
    {
        if (sawKeyMatchHint) _sawKeyMatch = true;
        MenuCloseSuppressed = false;
        // 回到菜单/主页：解除「必须先见菜单才能开下一场」的门
        bool atMenu = screen == MatchScreen.MatchHub || screen == MatchScreen.MainMenu ||
                      (screen == MatchScreen.LeaguePage && isHome);
        if (atMenu) _needMenuBeforeRestart = false;
        if (screen == MatchScreen.InMatch)
        {
            if (!_active)
            {
                // 结算链里的"比赛帧"（回放/统计/奖励页误判）：没回菜单、或还在冷却期内，
                // 都不是新的一场——用户 2026-11-13「76 场 vs 战绩 29 场」就是这么来的
                if (_needMenuBeforeRestart && now - _needMenuSince < MenuGateMaxWait) return false;
                if (now - _lastEndAt < RestartCooldown) return false;
                StartedAt = now;
            }
            _lastInMatchAt = now;
            _active = true;
            return false;
        }
        if (!_active) return false;
        bool matchOver = screen == MatchScreen.FullTime;
        bool backToMenus = screen == MatchScreen.MatchHub
            || screen == MatchScreen.MainMenu
            || (screen == MatchScreen.LeaguePage && isHome);
        bool timedOut = (now - _lastInMatchAt).TotalMinutes >= 5;
        // 「回到菜单」类关场的最小感知时长（2026-11-20 用户：进球庆祝 CG 被判 MatchHub，
        // 一场 21 秒的「比赛」凭空计入完成场次）。真比赛不可能 60 秒打完，所以这里拦下的
        // 一定是误判：不改任何状态、不解须见菜单的门，继续观察；若是真的回到菜单，
        // 5 分钟无比赛特征会照常兜底收尾（那条路径本来就「不计场次」）。
        if (backToMenus && !timedOut && StartedAt is DateTime started
            && now - started < MenuCloseMinDuration)
        {
            backToMenus = false;
            MenuCloseSuppressed = true;
        }
        if (matchOver || backToMenus || timedOut)
        {
            _active = false;
            _needMenuBeforeRestart = true;
            _needMenuSince = now;
            WasKeyMatch = _sawKeyMatch;
            LastEndReason = matchOver ? MatchEndReason.FullTime
                : backToMenus ? MatchEndReason.Menu
                : MatchEndReason.Timeout;
            _lastEndAt = now;
            Finish(now);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 主动作废当前场次（重启游戏跳过普通比赛后调用）：清掉在场状态与关键比赛标记，
    /// 让下一场从干净状态重新计数——否则重启后回到联赛主页会被误判成「一场比赛结束」，
    /// 凭空多算一场完成场次。
    /// </summary>
    /// <summary>场景层识别到自己的主页/菜单时调用：解除「须先见菜单」的门。</summary>
    internal void MarkAtMenu() => _needMenuBeforeRestart = false;

    internal void Abort()
    {
        _active = false;
        _lastEndAt = DateTime.MinValue;
        _needMenuBeforeRestart = false;
        StartedAt = null;
        _lastInMatchAt = DateTime.MinValue;
        _sawKeyMatch = false;
        WasKeyMatch = false;
    }

    private void Finish(DateTime now)
    {
        if (StartedAt is DateTime started) LastDuration = now - started;
        StartedAt = null;
        _sawKeyMatch = false;
    }
}




