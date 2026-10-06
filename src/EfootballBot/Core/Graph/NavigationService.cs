using EfootballBot.Core.Bot;
using EfootballBot.Core.Input;

namespace EfootballBot.Core.Graph;

/// <summary>
/// 统一闭环导航执行器：观察 → 判定 → 动作 → 复核，直到锚定成功或预算耗尽停机。
/// 所有页面节点共用同一个闭环，节点之间只有判定与下一步的差异。
/// </summary>
public sealed class NavigationService
{
    private readonly BotEngine _engine;
    private readonly Pad _pad;
    private readonly Action<string, LogLevel> _log;

    public NavigationService(BotEngine engine, Pad pad, Action<string, LogLevel> log)
    {
        _engine = engine;
        _pad = pad;
        _log = log;
    }

    /// <summary>
    /// 闭环导航直到 node 锚定。返回 true=锚定成功；
    /// false=预算耗尽或连续 3 次信号不确定，已记录错误并停机。
    /// </summary>
    public async Task<bool> EnsureAnchoredAsync(INavNode node, CancellationToken ct)
    {
        int actions = 0;
        int uncertain = 0;

        while (actions < node.StepBudget)
        {
            ct.ThrowIfCancellationRequested();
            var obs = await _engine.ObserveAsync(ct);

            switch (node.Check(obs))
            {
                case NodeState.Anchored:
                    _log($"导航锚定成功：{node.Describe(obs)}", LogLevel.Success);
                    return true;

                case NodeState.Uncertain:
                    if (++uncertain >= 3)
                    {
                        _log($"连续 3 次信号不确定，导航终止：{node.Describe(obs)}", LogLevel.Error);
                        return false;
                    }
                    _log($"导航信号不确定（{uncertain}/3），等待不操作：{node.Describe(obs)}", LogLevel.Warn);
                    await _pad.Wait(300, ct);
                    break;

                case NodeState.Transit:
                    uncertain = 0;
                    await ExecuteAsync(node.NextStep(obs), ct);
                    actions++;
                    break;

                case NodeState.OffTrack:
                    uncertain = 0;
                    _log($"检测到非目标内容，导航返回：{node.Describe(obs)}", LogLevel.Warn);
                    await ExecuteAsync(node.NextStep(obs), ct);
                    actions++;
                    break;
            }
        }

        _log($"导航预算（{node.StepBudget} 步）耗尽，已停机", LogLevel.Error);
        return false;
    }

    private async Task ExecuteAsync(IReadOnlyList<NavAction> steps, CancellationToken ct)
    {
        foreach (var action in steps)
        {
            switch (action.Type)
            {
                case ActionType.Press:
                    await _pad.Press(action.Button!.Value);
                    break;
                case ActionType.Dpad:
                    await _pad.Dpad(action.Direction!.Value);
                    break;
                case ActionType.Wait:
                    await _pad.Wait(action.Ms!.Value, ct);
                    break;
            }
        }
    }
}
