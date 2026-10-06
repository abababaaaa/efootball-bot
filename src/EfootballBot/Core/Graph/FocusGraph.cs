using EfootballBot.Core.Input;

namespace EfootballBot.Core.Graph;

/// <summary>菜单上的一个可聚焦槽位（卡片/图标）。Aliases 为检测器可能给出的卡名。</summary>
public sealed record FocusSlot(string Id, double CX, double CY, string[] Aliases);

/// <summary>
/// 菜单焦点导航图：槽位 + 方向键有向边。BFS 寻路；运行时发现的死边可临时摘除。
/// 任何"焦点卡片 + 方向键"菜单声明槽位与边后即可使用。
/// </summary>
public sealed class FocusGraph
{
    private readonly Dictionary<string, FocusSlot> _slots = new();
    // 初始边（不可变快照）+ 运行时摘除集合：RestoreRemoved 可恢复全部边
    private readonly Dictionary<string, Dictionary<PadDir, string>> _edgesInit = new();
    private readonly HashSet<(string From, PadDir Dir)> _removed = new();

    /// <summary>全图唯一的"照片槽位"：聚焦时不变白（无大白块）的卡，靠状态推定定位。</summary>
    public string? PhotoSlotId { get; private set; }

    public FocusGraph AddSlot(string id, double cx, double cy, params string[] aliases)
    {
        _slots[id] = new FocusSlot(id, cx, cy, aliases);
        _edgesInit[id] = new Dictionary<PadDir, string>();
        return this;
    }

    /// <summary>把已声明的槽位标记为全图唯一的"照片槽位"（聚焦时不变白，靠状态推定定位）。</summary>
    public FocusGraph MarkPhotoSlot(string id)
    {
        if (!_slots.ContainsKey(id)) throw new ArgumentException($"照片槽位未声明：{id}");
        if (PhotoSlotId is not null) throw new InvalidOperationException("一张焦点图至多声明一个照片槽位");
        PhotoSlotId = id;
        return this;
    }

    public FocusGraph AddEdge(string from, PadDir dir, string to)
    {
        if (!_slots.ContainsKey(from) || !_slots.ContainsKey(to))
            throw new ArgumentException($"边引用了未声明的槽位：{from}→{to}");
        _edgesInit[from][dir] = to;
        _removed.Remove((from, dir));
        return this;
    }

    /// <summary>运行时摘除一条走不通的边（按了方向键但焦点没动）。</summary>
    public void RemoveEdge(string from, PadDir dir) => _removed.Add((from, dir));

    /// <summary>恢复运行时摘除的全部边（归位重试时用）。</summary>
    public void RestoreRemoved() => _removed.Clear();

    private IEnumerable<KeyValuePair<PadDir, string>> EdgesOf(string id)
        => _edgesInit[id].Where(e => !_removed.Contains((id, e.Key)));

    public FocusSlot? Slot(string id) => _slots.GetValueOrDefault(id);

    /// <summary>按检测器给出的卡名（中文别名）找槽位。</summary>
    public FocusSlot? SlotByName(string cardName)
    {
        foreach (var s in _slots.Values)
            foreach (var a in s.Aliases)
                if (string.Equals(a, cardName, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// 按归一化中心找最近槽位：行（y）主导、列（x）宽容。
    /// 右列菜单卡聚焦白块的水平位置随卡宽/UI 版式抖动大，但同一行 y 稳定；
    /// 同 y 的左右列槽位靠列容差区分。
    /// </summary>
    public FocusSlot? Nearest(double cx, double cy, double maxDy = 0.10, double maxDx = 0.14)
    {
        FocusSlot? best = null;
        double bestScore = double.MaxValue;
        foreach (var s in _slots.Values)
        {
            double dy = Math.Abs(s.CY - cy), dx = Math.Abs(s.CX - cx);
            if (dy > maxDy || dx > maxDx) continue;
            double score = dy * 3.0 + dx;   // 行位置主导，列位置仅在同行间微调
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>BFS 最短路径：返回 (方向, 落点槽位Id) 序列；不可达返回 null；同点返回空表。</summary>
    public List<(PadDir Dir, string To)>? FindPath(string from, string to)
    {
        if (from == to) return new List<(PadDir, string)>();
        var prev = new Dictionary<string, (string From, PadDir Dir)> { [from] = ("", default) };
        var q = new Queue<string>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var (dir, next) in EdgesOf(cur))
            {
                if (prev.ContainsKey(next)) continue;
                prev[next] = (cur, dir);
                if (next == to)
                {
                    var path = new List<(PadDir, string)>();
                    var n = to;
                    while (n != from)
                    {
                        var (p, d) = prev[n];
                        path.Add((d, n));
                        n = p;
                    }
                    path.Reverse();
                    return path;
                }
                q.Enqueue(next);
            }
        }
        return null;
    }
}
