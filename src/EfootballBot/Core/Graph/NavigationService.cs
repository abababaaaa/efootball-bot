using System.Diagnostics;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Graph;

/// <summary>导航终止结果。</summary>
public enum NavOutcome
{
    /// <summary>已到达目标页。</summary>
    ReachedTarget,

    /// <summary>Unknown 页 B 步进预算耗尽。</summary>
    AbortedUnknown,

    /// <summary>已知图节点无到达目标的路径。</summary>
    AbortedNoPath,

    /// <summary>到达比赛中/赛后页面，停机保护现场。</summary>
    AbortedInMatch,

    /// <summary>卡死（同页 3 跳无变化 / 加载或弹窗等过境态超时）。</summary>
    AbortedStuck,

    /// <summary>导航总超时。</summary>
    AbortedTimeout,
}

/// <summary>导航最终结果。</summary>
public sealed record NavResult(NavOutcome Outcome, GameScreen LastScreen, string Message)
{
    public bool Ok => Outcome == NavOutcome.ReachedTarget;
}

/// <summary>导航预算。</summary>
public sealed record NavLimits(int MaxHops, int UnknownBudget, double TimeoutMinutes);

/// <summary>
/// 导航器对外部世界的依赖：观察画面、执行按键序列、处理弹窗、Unknown 兜底恢复。
/// 生产环境用 <see cref="BotNavIO"/>；单元测试用脚本替身。
/// </summary>
public interface INavIO
{
    Task<EfootballBot.Core.Bot.Observation> ObserveAsync(CancellationToken ct);
    Task RunActionsAsync(IReadOnlyList<NavAction> actions, CancellationToken ct);

    /// <summary>处理 Dialog / Disconnected 弹窗，返回是否执行了动作。</summary>
    Task<bool> HandleTransientAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct);

    /// <summary>Unknown 兜底：识别 OK/确定/物品过期 等按钮点击；无按钮时仅等待（绝不盲按 B）。</summary>
    Task RecoverAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct);
}

/// <summary>生产环境 INavIO：对接 BotEngine / Pad / Overlay。</summary>
public sealed class BotNavIO : INavIO
{
    private readonly BotEngine _engine;

    public BotNavIO(BotEngine engine) => _engine = engine;

    public Task<EfootballBot.Core.Bot.Observation> ObserveAsync(CancellationToken ct) => _engine.ObserveAsync(ct);

    public async Task RunActionsAsync(IReadOnlyList<NavAction> actions, CancellationToken ct)
    {
        foreach (var a in actions)
        {
            switch (a.Type)
            {
                case ActionType.Press:
                    await _engine.Pad.Press(a.Button!.Value);
                    break;
                case ActionType.Dpad:
                    await _engine.Pad.Dpad(a.Direction!.Value);
                    break;
                case ActionType.Wait:
                    await _engine.Pad.Wait(a.Ms!.Value, ct);
                    break;
            }
        }
    }

    public Task<bool> HandleTransientAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
        => _engine.HandlePopupsAsync(obs, ct);

    public Task RecoverAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
        => _engine.RecoverAsync(obs, ct);
}

/// <summary>
/// 全页面闭环导航器（方案 B）：
/// 观察→分类 → 终局守卫 / 到达目标 / 过境处理 / BFS 单跳 / Unknown 有界步进。
/// 每条边只执行一跳，下一轮重观察验证落点，偏差自动重新寻路。
/// </summary>
public sealed class NavigationService
{
    private static readonly GameScreen[] TerminalPages =
    {
        GameScreen.InMatch,
        GameScreen.HalfTime,
        GameScreen.FullTime,
        GameScreen.Result,
        GameScreen.Rewards,
    };

    private readonly PageGraph _graph;
    private readonly INavIO _io;
    private readonly NavLimits _limits;
    private readonly Action<string, LogLevel> _log;
    private readonly Action<NavEvent>? _emit;

    public NavigationService(
        PageGraph graph,
        INavIO io,
        NavLimits limits,
        Action<string, LogLevel> log,
        Action<NavEvent>? emit = null)
    {
        _graph = graph;
        _io = io;
        _limits = limits;
        _log = log;
        _emit = emit;
    }

    public async Task<NavResult> NavigateToAsync(string target, NavContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        int hops = 0;
        int unknownB = 0;
        int loadingWaits = 0;
        int titleTries = 0;
        int disconnectTries = 0;
        int dialogStreak = 0;
        int hopStreak = 0;
        GameScreen? hopStreakPage = null;
        string? expectedTo = null;
        var trail = new List<GameScreen>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed.TotalMinutes >= _limits.TimeoutMinutes)
                return Finish(NavOutcome.AbortedTimeout, trail.LastOrDefault(GameScreen.Unknown),
                    $"导航总超时（{_limits.TimeoutMinutes} 分钟），近期画面：{RecentTrail(trail)}");

            var obs = await _io.ObserveAsync(ct);
            var page = obs.Screen;
            trail.Add(page);

            if (page != GameScreen.Loading) loadingWaits = 0;
            if (page != GameScreen.Disconnected) disconnectTries = 0;
            if (page != GameScreen.Dialog) dialogStreak = 0;

            if (Array.IndexOf(TerminalPages, page) >= 0)
                return Finish(NavOutcome.AbortedInMatch, page,
                    $"到达{page}（比赛/赛后页面），导航停机保护现场，交场景层接管");

            if (page.ToString() == target)
                return Finish(NavOutcome.ReachedTarget, page, $"已到达 {target}");

            switch (page)
            {
                case GameScreen.Loading:
                    if (++loadingWaits > 30)
                        return Finish(NavOutcome.AbortedStuck, page, "加载画面持续超过 90 秒");
                    Emit(NavEventKind.TransitHandled, page.ToString(), "", "加载中：等待不按键");
                    await _io.RunActionsAsync(new[] { NavAction.Wait(3000) }, ct);
                    break;

                case GameScreen.TitleScreen:
                    if (++titleTries > 4)
                        return Finish(NavOutcome.AbortedStuck, page, "标题画面连续 4 次未能进入");
                    Emit(NavEventKind.TransitHandled, page.ToString(), "", "标题画面：按 A 进入");
                    await _io.RunActionsAsync(
                        new[] { NavAction.Press(PadButton.A), NavAction.Wait(4000) }, ct);
                    break;

                case GameScreen.Dialog:
                    if (++dialogStreak > 5)
                        return Finish(NavOutcome.AbortedStuck, page, "连续出现超过 5 个弹窗仍未离开当前画面");
                    Emit(NavEventKind.TransitHandled, page.ToString(), "", "确认弹窗：识别按钮并点击");
                    await _io.HandleTransientAsync(obs, ct);
                    break;

                case GameScreen.Disconnected:
                    if (++disconnectTries > 8)
                        return Finish(NavOutcome.AbortedStuck, page, "断线恢复连续 8 次失败");
                    Emit(NavEventKind.TransitHandled, page.ToString(), "", "断线：确认返回/重连");
                    await _io.HandleTransientAsync(obs, ct);
                    await _io.RunActionsAsync(new[] { NavAction.Wait(3000) }, ct);
                    break;

                case GameScreen.Unknown:
                    expectedTo = null;
                    bool becameKnown = await UnknownStepAsync(obs, ct);
                    if (becameKnown) break;

                    if (++unknownB > _limits.UnknownBudget)
                        return Finish(NavOutcome.AbortedUnknown, page,
                            $"B 步进预算（{_limits.UnknownBudget}）耗尽仍未识别，近期画面：{RecentTrail(trail)}");

                    Emit(NavEventKind.UnknownStep, page.ToString(), "",
                        $"Unknown 第 {unknownB}/{_limits.UnknownBudget} 次 B 步进");
                    await _io.RunActionsAsync(
                        new[] { NavAction.Press(PadButton.B), NavAction.Wait(700) }, ct);
                    break;

                default:
                    var path = _graph.FindPath(page.ToString(), target, ctx);
                    if (path is null)
                        return Finish(NavOutcome.AbortedNoPath, page,
                            $"图节点 {page} 到 {target} 无可达路径");

                    if (expectedTo is not null && page.ToString() != expectedTo)
                        Emit(NavEventKind.Deviation, expectedTo, page.ToString(),
                            $"落点偏差：预期 {expectedTo}，实际 {page}，已重新寻路");
                    expectedTo = null;

                    if (hopStreakPage == page) hopStreak++;
                    else { hopStreakPage = page; hopStreak = 1; }
                    if (hopStreak >= 4)
                        return Finish(NavOutcome.AbortedStuck, page,
                            $"连续 3 跳停留在 {page}，判定卡死");

                    if (++hops > _limits.MaxHops)
                        return Finish(NavOutcome.AbortedStuck, page,
                            $"导航跳数预算（{_limits.MaxHops}）耗尽");

                    var edge = path[0];
                    expectedTo = edge.To;

                    _emit?.Invoke(new NavEvent
                    {
                        Kind = NavEventKind.PlanFound,
                        From = page.ToString(),
                        To = target,
                        Path = path.Select(e => $"{e.From}→{e.To}").ToList(),
                    });
                    Emit(NavEventKind.HopExecuted, edge.From, edge.To,
                        $"执行边 {edge.From}→{edge.To}（{edge.Actions.Length} 个动作）");
                    await _io.RunActionsAsync(edge.Actions, ct);
                    break;
            }
        }
    }

    private async Task<bool> UnknownStepAsync(EfootballBot.Core.Bot.Observation first, CancellationToken ct)
    {
        await _io.RecoverAsync(first, ct);
        for (int i = 0; i < 2; i++)
        {
            await _io.RunActionsAsync(new[] { NavAction.Wait(2500) }, ct);
            var o = await _io.ObserveAsync(ct);
            if (o.Screen != GameScreen.Unknown) return true;
        }
        return false;
    }

    private static string RecentTrail(List<GameScreen> trail)
        => string.Join("→", trail.TakeLast(8).Select(s => s.ToString()));

    private void Emit(NavEventKind kind, string from, string to, string detail)
    {
        _log(detail, LogLevel.Info);
        _emit?.Invoke(new NavEvent { Kind = kind, From = from, To = to, Detail = detail });
    }

    private NavResult Finish(NavOutcome outcome, GameScreen last, string message)
    {
        _log($"导航结束（{outcome}）：{message}",
            outcome == NavOutcome.ReachedTarget ? LogLevel.Success : LogLevel.Error);
        _emit?.Invoke(new NavEvent
        {
            Kind = NavEventKind.Outcome,
            From = last.ToString(),
            Detail = $"{outcome}: {message}",
        });
        return new NavResult(outcome, last, message);
    }
}
