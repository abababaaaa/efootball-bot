using EfootballBot.Core.Bot.Modules;

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
/// 一场比赛在引擎这一侧需要处理的一切（共享层，各模块都调它）：
/// <list type="bullet">
/// <item><b>开赛判定</b>：首次判到 InMatch（受「须先见菜单」门与 90s 重开冷却约束）；</item>
/// <item><b>场内挂机节奏</b>：按用户设置的「挂机节奏」区间随机点 A（拟人化，防零输入判定）；</item>
/// <item><b>终场收尾</b>：终场页（权威）/ 回到菜单（可信）/ 5 分钟无比赛特征（超时，不计场次）；</item>
/// <item><b>场次记账</b>：MatchesDone / KeyMatches / 微信动态推送，统一口径一处改。</item>
/// </list>
///
/// 为什么单独抽出来：这段逻辑此前在 国服天梯 / AI天梯 / 国际服周活动 / 国服周活动 /
/// 我的联赛 里各抄了一份（连日志文案都是复制粘贴），改一处必须改五处，漏一处就是
/// 「某个模块场次虚高/漏计」。现在模块只在自己的 RunOnce 里调一次 <see cref="Tick"/>
/// 和 <see cref="IdleTapAsync"/>，比赛怎么算完全由这里决定。
///
/// 线程模型：只在场景决策循环（单线程）里调用，无锁。
/// </summary>
public sealed class MatchPlay
{
    private readonly BotEngine _engine;

    private readonly Action<string, LogLevel> _log;

    private readonly string _module;

    private readonly MatchSessionTracker _session = new();

    private readonly Random _rnd = new();

    private DateTime _lastIdleTapLogAt = DateTime.MinValue;

    /// <summary>「菜单类关场被拦下」日志的节流时间戳（进球 CG 可能连续几帧都像菜单，别刷屏）。</summary>
    private DateTime _lastMenuVetoLogAt = DateTime.MinValue;
    /// <param name="engine">引擎（取挂机节奏配置、累计场次、微信动态）。</param>
    /// <param name="module">日志前缀里的模块名（如「天梯巅峰赛」）。</param>
    /// <param name="log">日志回调（用引擎的 LogInternal）。</param>
    public MatchPlay(BotEngine engine, string module, Action<string, LogLevel> log)
    {
        _engine = engine;
        _module = module;
        _log = log;
    }

    /// <summary>当前是否在比赛中（引擎据此把手柄诊断挡在比赛外）。</summary>
    public bool InMatch => _session.Active;
    /// <summary>底层场次跟踪器（模块需要读用时/关键比赛等细节时用；程序集内可见）。</summary>
    internal MatchSessionTracker Session => _session;
    /// <summary>本场首次判到 InMatch 的时间（null = 未开赛）。</summary>
    public DateTime? StartedAt => _session.StartedAt;
    /// <summary>场景识别到自己的主页/菜单时调用：解除「必须先见菜单才能开下一场」的门。</summary>
    public void MarkAtMenu() => _session.MarkAtMenu();
    /// <summary>主动作废当前场次（重启游戏跳过普通比赛后调用）。</summary>
    public void Abort() => _session.Abort();
    /// <summary>
    /// 每轮调用一次：喂当前界面判定，返回本轮的「比赛状态变化」。
    /// 开赛/终场/超时的日志与场次记账都在内部完成，调用方只看返回值。
    /// </summary>
    /// <param name="screen">共享界面判定结果。</param>
    /// <param name="now">本轮时间。</param>
    /// <param name="sawKeyMatchHint">本轮是否见到关键比赛证据（弹窗/文案）。</param>
    /// <param name="isHome">LeaguePage 是否为真主页（见 <see cref="MatchSessionTracker.Observe"/>）。</param>
    /// <param name="startNote">开赛日志的补充说明（各模块措辞不同的小差异，只有这一句可定制）。</param>
    public MatchTick Tick(MatchScreen screen, DateTime now, bool sawKeyMatchHint = false,
        bool isHome = false, string startNote = "挂机等待比赛结束（按节奏随机点击）")
    {
        bool wasActive = _session.Active;
        bool ended = _session.Observe(screen, now, sawKeyMatchHint, isHome);
        if (!wasActive && _session.Active)
            _log($"[{_module}] 比赛开始：{startNote}", LogLevel.Info);
        // 菜单类关场被最小感知时长拦下：让日志说清「哪一帧被判成菜单、本场走了多久」，
        // 下次再有「进球 CG 当比赛结束」的反馈，不必从上百行时间戳里倒推（2026-11-20）。
        if (!ended && _session.MenuCloseSuppressed && now - _lastMenuVetoLogAt > TimeSpan.FromSeconds(30))
        {
            _lastMenuVetoLogAt = now;
            _log($"[{_module}] 判到「{screen}」但本场才走了 {_session.LastDurationText}（<1 分钟）——按识别误判处理，不计场次（真正的比赛结束会见到终场页或回菜单）",
                LogLevel.Warn);
        }
        if (!ended) return new MatchTick(false, false, false, false, _session.LastDurationText);
        string duration = _session.LastDurationText;
        if (!_session.LastEndTrusted)
        {
            // 超时收尾：5 分钟没再见到比赛特征（赛后回放/结算页卡住，或画面判定丢了）。
            // 不外计场次——用户 2026-11-13 反馈「哪儿来的 76 场」，虚高就是这么来的。
            _log($"[{_module}] 5 分钟没再出现比赛特征，关闭本场（用时 {duration}）但未计入场次——没见到终场页，请带这份日志反馈",
                LogLevel.Warn);
            return new MatchTick(false, true, false, false, duration);
        }
        bool key = _session.WasKeyMatch;
        _engine.Stats.MatchesDone++;
        if (key) _engine.Stats.KeyMatches++;
        // 结尾补关场依据：Menu = 回到菜单判结束（没见过终场页），FullTime = 见过终场页。
        // 之前只写「用时 xx:xx」，出现场次对不上时无法判断是哪条路径关的场（2026-11-20）。
        string reason = _session.LastEndReason switch
        {
            MatchEndReason.FullTime => "终场页",
            MatchEndReason.Menu => $"回到 {screen}",
            _ => "超时",
        };
        _log($"[{_module}] 一场比赛结束（{(key ? "关键比赛，" : "")}用时 {duration}，累计 {_engine.Stats.MatchesDone} 场；判定依据：{reason}）",
            LogLevel.Success);
        _engine.ReportMatchDone(key);
        return new MatchTick(false, true, true, key, duration);
    }

    /// <summary>
    /// 比赛内随机点击：按 A，返回下一轮该等待的毫秒数（用户「挂机节奏」区间的随机值）。
    /// 节奏被压到「挂机节奏」区间内是刻意的——比赛中 A 是传球/逼抢，不会暂停或退出。
    /// </summary>
    public async Task<int> IdleTapAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _engine.Pad.Confirm();
        var t = _engine.Config.Timing;
        int min = Math.Min(t.IdleTapMinMs, t.IdleTapMaxMs);
        int max = Math.Max(t.IdleTapMinMs, t.IdleTapMaxMs);
        int waitMs = _rnd.Next(min, max + 1);
        // 60s 节流：比赛动辄 20 分钟，每拍一条日志会把日志刷爆
        if ((DateTime.Now - _lastIdleTapLogAt).TotalMinutes >= 1)
        {
            _lastIdleTapLogAt = DateTime.Now;
            _log($"[{_module}] 比赛挂机中：按用户节奏随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
        }
        return waitMs;
    }
}

/// <summary>
/// 一轮 <see cref="MatchPlay.Tick"/> 的结果：调用方据此做「场次达标自动停」「赛后复位
/// 自己的状态机」等模块私有收尾——比赛怎么判定不由模块管，收尾动作各模块自理。
/// </summary>
/// <param name="Started">本轮刚开赛。</param>
/// <param name="Ended">本轮判到一场结束（含超时收尾）。</param>
/// <param name="EndedTrusted">这次结束可信（真见到终场页/回到菜单），可信的才会计入场次。</param>
/// <param name="KeyMatch">刚结束的这场是关键比赛。</param>
/// <param name="DurationText">刚结束这场的用时 mm:ss。</param>
public readonly record struct MatchTick(bool Started, bool Ended, bool EndedTrusted, bool KeyMatch, string DurationText);




