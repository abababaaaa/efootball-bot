using EfootballBot.Core.Config;
using EfootballBot.Core.Logging;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 作息调度器：模拟真人作息，消除「7×24 在线」这一服务端行为检测的最强信号。
/// - 每天只在活跃时段内运行（支持跨午夜窗口，如 22:00–02:00）
/// - 启停时间每日随机漂移 ±DriftMinutes（真人不会每天同一秒上线）
/// - 每日累计挂机时长到达上限后休息到次日（DailyCapHours，0 = 不限）
/// - 运行中偶尔「短暂离开」2~5 分钟（每 30~70 分钟一次），模拟真人被现实打断
/// 引擎主循环每轮调用 <see cref="ShouldRunNow"/>；返回 false 时引擎复位手柄并空转等待。
/// </summary>
public sealed class SessionScheduler
{
    private readonly ScheduleConfig _cfg;
    private readonly Action<string, LogLevel> _log;
    private readonly Random _rnd;

    private readonly TimeSpan _start;
    private readonly TimeSpan _end;

    private DateTime _lastDate = DateTime.MinValue;
    private TimeSpan _drift;
    private TimeSpan _acc;
    private DateTime? _lastTick;
    private bool _running = true;
    private DateTime _nextBreakAt = DateTime.MaxValue;
    private DateTime _breakUntil = DateTime.MinValue;

    /// <summary>本次调用是否刚从运行转入休息（引擎借此复位手柄残留按键）。</summary>
    public bool PauseJustBegan { get; private set; }

    /// <summary>当前状态摘要（供日志/统计展示）。</summary>
    public string Note { get; private set; } = "";

    public SessionScheduler(ScheduleConfig cfg, Action<string, LogLevel> log, Random? rnd = null)
    {
        _cfg = cfg;
        _log = log;
        _rnd = rnd ?? new Random();
        (_start, _end) = (ParseTime(cfg.StartTime, "09:00"), ParseTime(cfg.EndTime, "23:30"));
    }

    private static TimeSpan ParseTime(string text, string fallback)
    {
        if (TimeSpan.TryParseExact(text, @"hh\:mm", null, out var t) && t < TimeSpan.FromDays(1)) return t;
        return TimeSpan.ParseExact(fallback, @"hh\:mm", null);
    }

    /// <summary>评估当前时刻是否应挂机运行。内部完成时长累计与状态转换日志。</summary>
    public bool ShouldRunNow(DateTime now)
    {
        if (!_cfg.Enabled) return true;

        RollDayIfNeeded(now);

        // 活跃时间内的运行时长累计（引擎按秒级粒度轮询，累计误差可忽略）
        if (_running && _lastTick is { } t && now > t)
            _acc += now - t;
        _lastTick = now;

        bool should = InWindow(now.TimeOfDay) && !CapReached() && now >= _breakUntil;

        // 运行中偶尔短暂离开（真实感停顿，不打断挂机流程状态）
        if (should && now >= _nextBreakAt)
        {
            int breakMin = _rnd.Next(2, 6);
            _breakUntil = now.AddMinutes(breakMin);
            _nextBreakAt = now.AddMinutes(_rnd.Next(30, 71));
            _log($"作息调度：模拟真人短暂离开 {breakMin} 分钟（{now:HH:mm} 起）", LogLevel.Info);
            should = false;
        }

        if (should != _running)
        {
            PauseJustBegan = !should;
            _running = should;
            if (should)
            {
                // 进入运行态才开始排程下一次短暂离开（离上次运行起点 30~70 分钟）
                _nextBreakAt = now.AddMinutes(_rnd.Next(30, 71));
                _breakUntil = DateTime.MinValue;
                _log($"作息调度：进入活跃时段（今日 {_start + _drift:hh\\:mm}–{_end + _drift:hh\\:mm}），恢复挂机", LogLevel.Info);
            }
            else if (CapReached())
                _log($"作息调度：今日挂机已达上限 {_cfg.DailyCapHours:0.#} 小时，休息至明天 {_start + _drift:hh\\:mm} 自动恢复", LogLevel.Warn);
            else if (now < _breakUntil)
                _log($"作息调度：短暂离开至 {_breakUntil:HH:mm}", LogLevel.Info);
            else
                _log($"作息调度：超出活跃时段（今日 {_start + _drift:hh\\:mm}–{_end + _drift:hh\\:mm}），明天 {_start + _drift:hh\\:mm} 自动恢复", LogLevel.Warn);
        }
        else
        {
            PauseJustBegan = false;
        }

        Note = _running
            ? $"运行中（今日已挂机 {_acc.TotalHours:0.0}/{_cfg.DailyCapHours:0.#}h）"
            : $"作息休息中（今日 {_start + _drift:hh\\:mm}–{_end + _drift:hh\\:mm}，已挂机 {_acc.TotalHours:0.0}h）";
        return should;
    }

    private void RollDayIfNeeded(DateTime now)
    {
        if (now.Date == _lastDate) return;
        _lastDate = now.Date;
        _acc = TimeSpan.Zero;
        _breakUntil = DateTime.MinValue;
        int drift = Math.Clamp(_cfg.DriftMinutes, 0, 120);
        _drift = TimeSpan.FromMinutes(Math.Round((_rnd.NextDouble() * 2 - 1) * drift));
        if (drift > 0)
            _log($"作息调度：今日活跃时段 {_start + _drift:hh\\:mm}–{_end + _drift:hh\\:mm}（含 ±{drift} 分钟随机漂移），上限 {_cfg.DailyCapHours:0.#} 小时", LogLevel.Info);
    }

    private bool CapReached() => _cfg.DailyCapHours > 0 && _acc.TotalHours >= _cfg.DailyCapHours;

    /// <summary>当前时刻（含漂移后）是否在活跃窗口内。start &gt; end 视为跨午夜窗口。</summary>
    private bool InWindow(TimeSpan t)
    {
        TimeSpan s = _start + _drift, e = _end + _drift;
        s -= TimeSpan.FromDays((int)(s / TimeSpan.FromDays(1)));
        e -= TimeSpan.FromDays((int)(e / TimeSpan.FromDays(1)));
        return s <= e ? t >= s && t < e : t >= s || t < e;
    }
}
