using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Match;

/// <summary>比赛流程轮询结果。</summary>
public enum MatchPhase
{
    /// <summary>本轮按了 A（蓝色推进按钮立即响应，或按挂机节奏轻按）。</summary>
    Advanced,
    /// <summary>本轮未操作，等待中（比赛进行中 AI 代打 / 过场动画播放中）。</summary>
    Waiting,
}

/// <summary>
/// 通用比赛流程模块（国际服 / 国服共用）。
/// 核心策略（不依赖左上角队徽、队名等会变化的元素）：
/// 1. 最高优先级：右下角蓝色「下一步 / 确定 / OK / 跳过」大按钮，检测到立即按 A，
///    不受挂机节奏间隔影响；
/// 2. 无按钮且启用节奏轻按时：按用户设置的随机区间（Timing.IdleTapMinMs~MaxMs）
///    轻按 A，跳过比赛回放 / 庆祝 / 过场，贯穿比赛中与赛后弹窗，直到比赛状态结束；
/// 3. 未启用节奏轻按（未知画面兜底）：只响应蓝按钮，绝不盲目按键。
/// </summary>
public sealed class MatchFlowService
{
    private readonly BotEngine _engine;

    /// <summary>蓝色推进按钮两次确认之间的最小冷却，避免同一按钮被连按。</summary>
    private static readonly TimeSpan AdvanceCooldown = TimeSpan.FromMilliseconds(800);

    /// <summary>未到节奏点时，观察循环的最长等待（毫秒），防止期间出现蓝按钮漏检。</summary>
    private const int MaxObservationGapMs = 1500;

    private DateTime _lastAdvance = DateTime.MinValue;
    private DateTime _nextIdleTapAt = DateTime.Now;

    public MatchFlowService(BotEngine engine) => _engine = engine;

    /// <summary>
    /// 执行一轮比赛流程观察与推进。
    /// </summary>
    /// <param name="idleTapEnabled">
    /// true：比赛中 / 赛后弹窗，按挂机节奏随机轻按 A；
    /// false：未知画面兜底，只响应蓝色按钮。
    /// </param>
    /// <returns>阶段与建议的下一轮等待毫秒数。</returns>
    public async Task<(MatchPhase Phase, int DelayMs)> RunOnceAsync(bool idleTapEnabled, CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);

        // 1. 右下角蓝色推进按钮 —— 最高优先级，立即响应
        if (FrameAnalyzer.FindBlueAdvanceButton(obs.Frame, out double cy))
        {
            if (DateTime.Now - _lastAdvance >= AdvanceCooldown)
            {
                _engine.LogInternal($"检测到蓝色推进按钮（y={cy:F2}），立即按 A", LogLevel.Info);
                await _engine.Pad.Confirm();
                _lastAdvance = DateTime.Now;
                ScheduleNextIdleTap();
            }
            return (MatchPhase.Advanced, 700);
        }

        // 2. 无推进按钮：按挂机节奏随机轻按 A（仅比赛流程中）
        if (idleTapEnabled)
        {
            var now = DateTime.Now;
            if (now >= _nextIdleTapAt)
            {
                _engine.LogInternal("比赛流程：按挂机节奏轻按 A（跳过回放/过场）", LogLevel.Debug);
                await _engine.Pad.Confirm();
                _lastAdvance = DateTime.Now;
                ScheduleNextIdleTap();
                return (MatchPhase.Advanced, 700);
            }

            // 未到节奏点：睡到下次轻按，但中途保持观察频率以便蓝按钮一出现就响应
            int waitMs = (int)Math.Ceiling((_nextIdleTapAt - now).TotalMilliseconds);
            waitMs = Math.Clamp(waitMs, 300, MaxObservationGapMs);
            return (MatchPhase.Waiting, waitMs);
        }

        // 3. 未知画面：不盲目按键
        return (MatchPhase.Waiting, 1500);
    }

    /// <summary>按用户设置的区间随机安排下一次轻按 A 的时刻。</summary>
    private void ScheduleNextIdleTap()
    {
        int min = _engine.Config.Timing.IdleTapMinMs;
        int max = _engine.Config.Timing.IdleTapMaxMs;
        if (max < min) (min, max) = (max, min);
        if (max <= 0) { _nextIdleTapAt = DateTime.MaxValue; return; }

        int interval = Random.Shared.Next(min, max + 1);
        _nextIdleTapAt = DateTime.Now.AddMilliseconds(interval);
        _engine.LogInternal($"下次节奏轻按：{interval / 1000.0:0.#} 秒后", LogLevel.Debug);
    }
}
