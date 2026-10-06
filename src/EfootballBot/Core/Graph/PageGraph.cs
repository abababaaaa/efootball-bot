using EfootballBot.Core.Input;

namespace EfootballBot.Core.Graph;

/// <summary>导航动作类型。</summary>
public enum ActionType { Press, Dpad, Wait }

/// <summary>单步导航动作。</summary>
public sealed class NavAction
{
    public ActionType Type { get; init; }
    public PadButton? Button { get; init; }
    public PadDir? Direction { get; init; }
    public int? Ms { get; init; }

    public static NavAction Press(PadButton b) => new() { Type = ActionType.Press, Button = b };
    public static NavAction Dpad(PadDir d) => new() { Type = ActionType.Dpad, Direction = d };
    public static NavAction Wait(int ms) => new() { Type = ActionType.Wait, Ms = ms };
}

/// <summary>页面间有向边：From → To，按 Actions 顺序执行，可选条件门控。</summary>
public sealed class Edge
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public NavAction[] Actions { get; init; } = Array.Empty<NavAction>();
    public Func<NavContext, bool>? Condition { get; init; }
    public int Cost { get; init; } = 1;
    /// <summary>真机验证标记：老 8 条边已由 legacy 版本真机验证=true；新回退边=false，待真机逐条确认。</summary>
    public bool Verified { get; init; } = true;
}

/// <summary>BFS 寻路时的上下文，供条件边判断使用。</summary>
public sealed class NavContext
{
    public bool NeedBuyItems { get; set; }
}

/// <summary>
/// 页面导航图：把「主页→比赛菜单→我的联赛」等按键序列声明为 Edge，
/// 引擎用 BFS 自动寻路。半途接续（已在 MatchHub 就不回主页）免费获得。
/// </summary>
public sealed class PageGraph
{
    private readonly Dictionary<string, List<Edge>> _adj = new();

    public PageGraph(IEnumerable<Edge> edges)
    {
        foreach (var e in edges)
        {
            if (!_adj.ContainsKey(e.From)) _adj[e.From] = new();
            _adj[e.From].Add(e);
        }
    }

    /// <summary>BFS 寻路。单点规则：IsTraversable + IsGoal。</summary>
    public List<Edge>? FindPath(string from, string to, NavContext ctx)
    {
        if (from == to) return new();
        var visited = new HashSet<string> { from };
        var queue = new Queue<(string, List<Edge>)>();
        queue.Enqueue((from, new()));

        while (queue.Count > 0)
        {
            var (cur, path) = queue.Dequeue();
            if (!_adj.TryGetValue(cur, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (!IsTraversable(edge, ctx) || visited.Contains(edge.To)) continue;
                var newPath = new List<Edge>(path) { edge };
                if (IsGoal(edge.To, to)) return newPath;
                visited.Add(edge.To);
                queue.Enqueue((edge.To, newPath));
            }
        }
        return null;
    }

    private static bool IsTraversable(Edge e, NavContext ctx) => e.Condition == null || e.Condition(ctx);
    private static bool IsGoal(string page, string target) => page == target;
}
