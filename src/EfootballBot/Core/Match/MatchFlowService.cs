using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Match;

/// <summary>比赛流程轮询结果。</summary>
public enum MatchPhase
{
    /// <summary>检测到右下角蓝色推进按钮，已按 A。</summary>
    Advanced,
    /// <summary>未检测到推进按钮，等待中（比赛进行中 AI 代打 / 过场动画播放中）。</summary>
    Waiting,
}

/// <summary>
/// 通用比赛流程模块（国际服 / 国服共用）。
/// 核心策略（不依赖左上角队徽、队名等会变化的元素）：
/// 1. 最高优先级：右下角蓝色「下一步 / 确定 / OK / 跳过」大按钮，检测到立即按 A；
/// 2. 无按钮：不做任何操作（比赛中由 AI 代打，过场等动画播完按钮自然出现）。
/// </summary>
public sealed class MatchFlowService
{
    private readonly BotEngine _engine;

    /// <summary>两次按 A 之间的最小冷却，避免同一页被连续确认。</summary>
    private static readonly TimeSpan AdvanceCooldown = TimeSpan.FromSeconds(3);
    private DateTime _lastAdvance = DateTime.MinValue;

    public MatchFlowService(BotEngine engine) => _engine = engine;

    /// <summary>执行一轮比赛流程观察与推进。</summary>
    public async Task<MatchPhase> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);

        // 1. 右下角蓝色推进按钮（下一步 / 确定 / OK / 跳过）—— 最高优先级
        if (FrameAnalyzer.FindBlueAdvanceButton(obs.Frame, out double cy))
        {
            if (DateTime.Now - _lastAdvance >= AdvanceCooldown)
            {
                _engine.LogInternal($"检测到蓝色推进按钮（y={cy:F2}），按 A 推进", LogLevel.Info);
                await _engine.Pad.Confirm();
                _lastAdvance = DateTime.Now;
                await Task.Delay(1500, ct);
                return MatchPhase.Advanced;
            }

            // 冷却中：上一帧刚按过，等待页面切换
            return MatchPhase.Advanced;
        }

        // 2. 无推进按钮：比赛中 / 过场动画中，不操作
        return MatchPhase.Waiting;
    }
}
