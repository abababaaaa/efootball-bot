using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 可视化面板事件载荷集合。Worker 侧派发，GUI 图谱面板消费。
/// 三类事件：screen（画面判定）、probe（探针 Top-5 候选）、nav（导航寻路）。
/// </summary>

/// <summary>画面判定事件：当前页面 + 分类置信度 + 判定依据。</summary>
public sealed class ScreenEvent
{
    public GameScreen Screen { get; init; }
    public double Score { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}

/// <summary>探针候选条目：页面名 + 原始分 + 命中证据。</summary>
public sealed class ProbeCandidate
{
    public GameScreen Screen { get; init; }
    public double RawScore { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}

/// <summary>探针引擎事件：Top-5 候选（按优先级/分数降序）。</summary>
public sealed class ProbeEvent
{
    public IReadOnlyList<ProbeCandidate> TopCandidates { get; init; } = Array.Empty<ProbeCandidate>();
}

/// <summary>导航事件类型。</summary>
public enum NavEventKind
{
    /// <summary>BFS 找到路径（含完整 Path）。</summary>
    PlanFound,

    /// <summary>执行一条边。</summary>
    HopExecuted,

    /// <summary>边执行后落点不是预期页面，已从实际页重新寻路。</summary>
    Deviation,

    /// <summary>Unknown 页按了一次 B。</summary>
    UnknownStep,

    /// <summary>过境态被处理（等待加载/点标题/确认弹窗/断线重连）。</summary>
    TransitHandled,

    /// <summary>导航终止（成功或各类停机）。</summary>
    Outcome,
}

/// <summary>导航寻路事件：起点 + 终点 + 路径边描述 + 事件类型与详情。</summary>
public sealed class NavEvent
{
    public NavEventKind Kind { get; init; } = NavEventKind.PlanFound;
    public string From { get; init; } = "";
    public string To { get; init; } = "";

    /// <summary>路径上每条边的 From→To 描述，如 ["Home→MatchHub", "MatchHub→MyLeagueHome"]。</summary>
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

    /// <summary>人类可读详情（日志/面板用）。</summary>
    public string Detail { get; init; } = "";
}
