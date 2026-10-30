namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 流程帧记忆：滚动记录最近的界面判定（时间+界面），给决策提供时间上下文。
/// 用途：比赛中镜头切换偶发丢失记分牌时钟 → 单帧判 Unknown，若最近刚见过比赛特征，
/// 不应立刻按 B 逃生（B 在比赛中是铲球键）。
/// </summary>
internal sealed class MlFlowMemory
{
    private readonly List<(DateTime At, MlScreen Screen)> _items = new();
    private readonly TimeSpan _retention;

    internal MlFlowMemory(TimeSpan? retention = null) => _retention = retention ?? TimeSpan.FromMinutes(2);

    internal void Record(MlScreen screen, DateTime at)
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
    internal bool SawWithin(MlScreen screen, TimeSpan window, DateTime now)
        => _items.Any(it => it.Screen == screen && now - it.At <= window);

    /// <summary>
    /// 清空全部记录（重启游戏后调用）：旧会话的 InMatch/MatchStartPage 记忆会让
    /// <see cref="ScenarioShared.TitleGrace"/> 把重进后的真标题页当成「开赛加载画面」而
    /// 一直不按 X——重启后必须从零开始判断。
    /// </summary>
    internal void Clear() => _items.Clear();
}

/// <summary>
/// 比赛场次跟踪：首次 InMatch 视为开赛；全场结束（FullTime，权威终场信号）、回到菜单类界面
/// （LeaguePage/MatchHub/MainMenu）或连续 5 分钟无比赛特征视为一场结束（供统计计数）。
/// Unknown/HasConfirm（重播、结算弹窗）不会误关场次。
/// </summary>
internal sealed class MatchSessionTracker
{
    private bool _active;
    private DateTime _lastInMatchAt;
    private bool _sawKeyMatch;

    internal bool Active => _active;
    /// <summary>本场首次被判为 InMatch 的时间（null = 未开赛）。只打关键比赛用它算时钟走了多久。</summary>
    internal DateTime? StartedAt { get; private set; }
    /// <summary>刚结束的这场是否关键比赛（会话期间见过 KeyMatchDialog/关键比赛字样）。</summary>
    internal bool WasKeyMatch { get; private set; }

    /// <summary>观察一轮；返回 true 表示本轮判定一场比赛结束。
    /// isHome：LeaguePage 是否为真主页（OCR 含前往比赛/下个赛季）——开场过渡帧会被
    /// YOLO btn_back 误判 LeaguePage（实机 2026-10-10 21:23 开场 2 秒误关场次），
    /// 仅真主页才视为"回到菜单"关场。</summary>
    internal bool Observe(MlScreen screen, DateTime now, bool sawKeyMatchHint = false, bool isHome = true)
    {
        if (sawKeyMatchHint) _sawKeyMatch = true;
        if (screen == MlScreen.InMatch)
        {
            if (!_active) StartedAt = now;
            _lastInMatchAt = now;
            _active = true;
            return false;
        }
        if (!_active) return false;

        bool matchOver = screen == MlScreen.FullTime;
        bool backToMenus = screen == MlScreen.MatchHub
            || screen == MlScreen.MainMenu
            || (screen == MlScreen.LeaguePage && isHome);
        bool timedOut = (now - _lastInMatchAt).TotalMinutes >= 5;
        if (matchOver || backToMenus || timedOut)
        {
            _active = false;
            WasKeyMatch = _sawKeyMatch;
            Finish();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 主动作废当前场次（重启游戏跳过普通比赛后调用）：清掉在场状态与关键比赛标记，
    /// 让下一场从干净状态重新计数——否则重启后回到联赛主页会被误判成「一场比赛结束」，
    /// 凭空多算一场完成场次。
    /// </summary>
    internal void Abort()
    {
        _active = false;
        StartedAt = null;
        _lastInMatchAt = DateTime.MinValue;
        _sawKeyMatch = false;
        WasKeyMatch = false;
    }

    private void Finish()
    {
        StartedAt = null;
        _sawKeyMatch = false;
    }
}
