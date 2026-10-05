using EfootballBot.Core.Flow;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 可视化面板事件载荷集合。Worker 侧派发，GUI 图谱面板消费。
/// 四类事件：screen（画面判定）、probe（探针 Top-5 候选）、nav（导航寻路）、flow（比赛阶段）。
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

/// <summary>导航寻路事件：起点 + 终点 + 路径边描述。</summary>
public sealed class NavEvent
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>路径上每条边的 From→To 描述，如 ["Home→MatchHub", "MatchHub→MyLeagueHome"]。</summary>
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
}

/// <summary>比赛流程阶段事件：当前阶段 + 阶段死线（如比赛超时时间戳）。</summary>
public sealed class FlowEvent
{
    public MatchStage Stage { get; init; }
    /// <summary>阶段死线 ISO 字符串；无死线时为 null。</summary>
    public string? Deadline { get; init; }
}
