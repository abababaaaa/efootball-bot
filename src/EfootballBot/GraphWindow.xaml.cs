using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;

namespace EfootballBot;

/// <summary>
/// 实时图谱窗口：按模块分组的线性流程链。
/// 每个模块一条从左到右的主流水线，支线节点挂在父节点下方，回环走底部 U 形线，
/// 覆盖层（加载/弹窗/断线/未知）单独一行徽章。当前页绿色脉冲，走过的路径留灰。
/// </summary>
public partial class GraphWindow : Window
{
    private sealed class GraphNode
    {
        public Border Card = null!;
        public double CX, CY, HW, HH;
        public bool Overlay;
    }

    private sealed class GraphEdge
    {
        public Shape Line = null!;   // Line 或 Polyline（回环）
        public Polygon Arrow = null!;
    }

    private readonly Dictionary<GameScreen, GraphNode> _nodes = new();
    private readonly Dictionary<(GameScreen, GameScreen), GraphEdge> _edges = new();
    private readonly Queue<(GameScreen, GameScreen)> _trail = new();
    private GameScreen? _current;
    private (GameScreen, GameScreen)? _activeEdgeKey;
    private string _mode = "myleague";
    private bool _suppressModeEvent;

    private const double NodeW = 88, NodeH = 44;      // 主链卡片
    private const double SubW = 80, SubH = 34;        // 支线/覆盖层卡片
    private const double ChainY = 14;                  // 主链行 y
    private const double LoopY = 72;                   // 回环线最低点 y
    private const double SubY = 88;                    // 支线行 y
    private const double OverlayY = 152;               // 覆盖层行 y

    // ---------------- 模块流程定义 ----------------

    /// <summary>各模块的主流水线（严格线性，从左到右）。</summary>
    private static readonly Dictionary<string, GameScreen[]> ModeChains = new()
    {
        ["myleague"] = new[]
        {
            GameScreen.TitleScreen, GameScreen.Home, GameScreen.MatchHub, GameScreen.MyLeagueHome,
            GameScreen.PreMatch, GameScreen.InMatch, GameScreen.HalfTime, GameScreen.FullTime,
            GameScreen.Result, GameScreen.Rewards,
        },
        ["intlweekly"] = new[]
        {
            GameScreen.TitleScreen, GameScreen.Home, GameScreen.MatchHub, GameScreen.EventHub,
            GameScreen.PreMatch, GameScreen.InMatch, GameScreen.HalfTime, GameScreen.FullTime,
            GameScreen.CampaignBoard,
        },
        ["cndaily"] = new[]
        {
            GameScreen.TitleScreen, GameScreen.Home, GameScreen.EventHub, GameScreen.ChallengeList,
            GameScreen.ChallengeHome, GameScreen.PreMatch, GameScreen.InMatch, GameScreen.FullTime,
            GameScreen.Result, GameScreen.Rewards,
        },
    };

    /// <summary>各模块的支线节点（挂在父节点下方第二行）。</summary>
    private static readonly Dictionary<string, (GameScreen Parent, GameScreen[] Subs)[]> ModeBranches = new()
    {
        ["myleague"] = new[]
        {
            (GameScreen.MyLeagueHome, new[]
            {
                GameScreen.Schedule, GameScreen.Shop, GameScreen.ShopConfirm,
                GameScreen.ActivityDetail, GameScreen.KeyMatchBadge,
            }),
        },
        ["intlweekly"] = new[]
        {
            (GameScreen.EventHub, new[] { GameScreen.EventInfo, GameScreen.NoEvent }),
        },
        ["cndaily"] = new[]
        {
            (GameScreen.ChallengeList, new[] { GameScreen.ActivityDetail }),
            (GameScreen.ChallengeHome, new[] { GameScreen.DailyRewards, GameScreen.EventSettings }),
        },
    };

    /// <summary>各模块的回环边（走底部 U 形线回到主链前段）。</summary>
    private static readonly Dictionary<string, (GameScreen From, GameScreen To)[]> ModeLoops = new()
    {
        ["myleague"] = new[] { (GameScreen.Rewards, GameScreen.MyLeagueHome) },
        ["intlweekly"] = new[] { (GameScreen.CampaignBoard, GameScreen.EventHub) },
        ["cndaily"] = new[] { (GameScreen.Rewards, GameScreen.ChallengeList) },
    };

    /// <summary>覆盖层徽章（所有模块通用，任何时候可能叠加出现）。</summary>
    private static readonly GameScreen[] Overlays =
    {
        GameScreen.Loading, GameScreen.Dialog, GameScreen.Disconnected, GameScreen.Unknown,
    };

    public GraphWindow()
    {
        InitializeComponent();
        BuildGraph();
    }

    /// <summary>切换模块（挂机启动时自动跟随，也可手动点 Tab）。重建画布。</summary>
    public void SetMode(string mode)
    {
        if (!ModeChains.ContainsKey(mode)) return;
        _mode = mode;
        _suppressModeEvent = true;
        TabModeLeague.IsChecked = mode == "myleague";
        TabModeWeekly.IsChecked = mode == "intlweekly";
        TabModeCn.IsChecked = mode == "cndaily";
        _suppressModeEvent = false;
        BuildGraph();
    }

    private void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 加载期 IsChecked=True 会提前触发本事件，此时 Canvas 字段尚未解析赋值，必须守卫
        if (_suppressModeEvent || Canvas is null) return;
        if (TabModeLeague.IsChecked == true) _mode = "myleague";
        else if (TabModeWeekly.IsChecked == true) _mode = "intlweekly";
        else if (TabModeCn.IsChecked == true) _mode = "cndaily";
        BuildGraph();
    }

    // ---------------- 构建 ----------------

    private void BuildGraph()
    {
        Canvas.Children.Clear();
        _nodes.Clear();
        _edges.Clear();
        _trail.Clear();
        _current = null;
        _activeEdgeKey = null;

        var chain = ModeChains[_mode];

        // 主链节点（线性一排）
        for (int i = 0; i < chain.Length; i++)
            _nodes[chain[i]] = MakeNode(chain[i], 10 + i * (NodeW + 16), ChainY, NodeW, NodeH, false);

        // 支线节点（父节点下方第二行，从父的 x 起向右排）
        if (ModeBranches.TryGetValue(_mode, out var branches))
        {
            foreach (var (parent, subs) in branches)
            {
                if (!_nodes.TryGetValue(parent, out var p)) continue;
                double sx = p.CX - SubW / 2;
                foreach (var sub in subs)
                {
                    _nodes[sub] = MakeNode(sub, sx, SubY, SubW, SubH, false);
                    sx += SubW + 10;
                }
            }
        }

        // 覆盖层徽章行
        double ox = 10;
        foreach (var ov in Overlays)
        {
            _nodes[ov] = MakeNode(ov, ox, OverlayY, SubW, SubH, true);
            ox += SubW + 10;
        }

        // 先画边（下层），再画卡片（上层遮挡中段）
        var pendingCards = new List<(GameScreen S, GraphNode N)>();
        foreach (var kv in _nodes) pendingCards.Add((kv.Key, kv.Value));

        // 主链相邻连线
        for (int i = 0; i < chain.Length - 1; i++)
            AddEdge(chain[i], chain[i + 1], straight: true);

        // 支线：父 → 子
        if (branches is not null)
            foreach (var (parent, subs) in branches)
                foreach (var sub in subs)
                    AddEdge(parent, sub, straight: true);

        // 回环：底部 U 形线
        if (ModeLoops.TryGetValue(_mode, out var loops))
            foreach (var (from, to) in loops)
                AddEdge(from, to, straight: false);

        // 画卡片
        foreach (var (s, n) in pendingCards)
        {
            Canvas.SetLeft(n.Card, n.CX - n.HW);
            Canvas.SetTop(n.Card, n.CY - n.HH);
            Canvas.Children.Add(n.Card);
        }
    }

    private GraphNode MakeNode(GameScreen s, double x, double y, double w, double h, bool overlay)
    {
        var title = new TextBlock
        {
            Text = BotEngine.ScreenName(s),
            FontSize = overlay || h == SubH ? 10 : 12,
            FontWeight = overlay ? FontWeights.Normal : FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty,
            overlay ? "TextSecondary" : "TextPrimary");
        var card = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(overlay ? h / 2 : 8),
            BorderThickness = new Thickness(1.5),
            Child = title,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        card.SetResourceReference(Border.BorderBrushProperty, overlay ? "TextMuted" : "CardBorder");
        return new GraphNode
        {
            Card = card,
            CX = x + w / 2, CY = y + h / 2,
            HW = w / 2, HH = h / 2,
            Overlay = overlay,
        };
    }

    private void AddEdge(GameScreen from, GameScreen to, bool straight)
    {
        if (!_nodes.TryGetValue(from, out var f) || !_nodes.TryGetValue(to, out var t)) return;
        GraphEdge edge = straight ? BuildStraightEdge(f, t) : BuildLoopEdge(f, t);
        _edges[(from, to)] = edge;
        Canvas.Children.Add(edge.Line);
        Canvas.Children.Add(edge.Arrow);
    }

    /// <summary>直线边：源中心 → 目标卡片边界 + 箭头。</summary>
    private GraphEdge BuildStraightEdge(GraphNode from, GraphNode to)
    {
        double dx = to.CX - from.CX, dy = to.CY - from.CY;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) len = 1;
        double ux = dx / len, uy = dy / len;
        var sp = RectEdgePoint(from.CX, from.CY, from.HW, from.HH, ux, uy);
        var tp = RectEdgePoint(to.CX, to.CY, to.HW, to.HH, -ux, -uy);

        var line = new Line { X1 = sp.X, Y1 = sp.Y, X2 = tp.X, Y2 = tp.Y };
        var arrow = MakeArrow(tp, ux, uy);
        var edge = new GraphEdge { Line = line, Arrow = arrow };
        SetEdgeDefault(edge);
        return edge;
    }

    /// <summary>回环边：从源卡底部向下 → 水平 → 向上回到目标卡底部的 U 形线。</summary>
    private GraphEdge BuildLoopEdge(GraphNode from, GraphNode to)
    {
        double yBottom = from.CY + from.HH;
        var poly = new Polyline();
        poly.Points.Add(new Point(from.CX, yBottom));
        poly.Points.Add(new Point(from.CX, LoopY));
        poly.Points.Add(new Point(to.CX, LoopY));
        poly.Points.Add(new Point(to.CX, to.CY + to.HH));
        // 箭头朝上（指向目标卡底部）
        var arrow = MakeArrow(new Point(to.CX, to.CY + to.HH), 0, -1);
        var edge = new GraphEdge { Line = poly, Arrow = arrow };
        SetEdgeDefault(edge);
        return edge;
    }

    private static Polygon MakeArrow(Point tip, double ux, double uy)
    {
        double px = -uy, py = ux;
        var baseC = new Point(tip.X - ux * 9, tip.Y - uy * 9);
        var arrow = new Polygon();
        arrow.Points.Add(tip);
        arrow.Points.Add(new Point(baseC.X + px * 3.5, baseC.Y + py * 3.5));
        arrow.Points.Add(new Point(baseC.X - px * 3.5, baseC.Y - py * 3.5));
        return arrow;
    }

    private static Point RectEdgePoint(double cx, double cy, double hw, double hh, double ux, double uy)
    {
        double kx = Math.Abs(ux) < 1e-6 ? double.MaxValue : hw / Math.Abs(ux);
        double ky = Math.Abs(uy) < 1e-6 ? double.MaxValue : hh / Math.Abs(uy);
        double k = Math.Min(kx, ky);
        return new Point(cx + ux * k, cy + uy * k);
    }

    // ---------------- 状态样式 ----------------

    private void SetEdgeDefault(GraphEdge e)
    {
        var b = (Brush)FindResource("ModuleDot");
        e.Line.Stroke = b; e.Line.StrokeThickness = 1.0;
        e.Arrow.Fill = b;
    }

    private void SetEdgeTrail(GraphEdge e)
    {
        var b = (Brush)FindResource("TextMuted");
        e.Line.Stroke = b; e.Line.StrokeThickness = 1.5;
        e.Arrow.Fill = b;
    }

    private void SetEdgeActive(GraphEdge e)
    {
        var b = (Brush)FindResource("AccentGlow");
        e.Line.Stroke = b; e.Line.StrokeThickness = 2.5;
        e.Arrow.Fill = b;
    }

    private static void PulseStart(Border b)
    {
        var anim = new DoubleAnimation(1.0, 0.7, new Duration(TimeSpan.FromSeconds(0.8)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        b.BeginAnimation(OpacityProperty, anim);
    }

    private static void PulseStop(Border b)
    {
        b.BeginAnimation(OpacityProperty, null);
        b.Opacity = 1.0;
    }

    // ---------------- 驱动 ----------------

    /// <summary>画面事件驱动图谱：更新当前页高亮、路径轨迹、判定依据。</summary>
    public void OnScreen(GameScreen screen, double score, string? evidence)
    {
        string name = BotEngine.ScreenName(screen);
        TxtCurrent.Text = $"当前画面：{name}（{score:F1}）";
        TxtDetail.Text = $"判定依据：{(string.IsNullOrWhiteSpace(evidence) ? "—" : evidence)}";
        DotCurrent.Fill = (Brush)FindResource("AccentGlow");

        if (!_nodes.ContainsKey(screen) || _current == screen) return;

        if (_current is GameScreen prev && _nodes.TryGetValue(prev, out var prevNode))
        {
            if (_activeEdgeKey is { } aek && _edges.TryGetValue(aek, out var prevEdge))
                SetEdgeTrail(prevEdge);

            var key = (prev, screen);
            if (_edges.TryGetValue(key, out var edge))
            {
                SetEdgeActive(edge);
                _activeEdgeKey = key;
                _trail.Enqueue(key);
                while (_trail.Count > 20)
                {
                    var old = _trail.Dequeue();
                    if (old != _activeEdgeKey && !_trail.Contains(old)
                        && _edges.TryGetValue(old, out var oe))
                        SetEdgeDefault(oe);
                }
            }
            else _activeEdgeKey = null;

            PulseStop(prevNode.Card);
            prevNode.Card.BorderBrush = (Brush)FindResource("TextMuted");
            prevNode.Card.BorderThickness = new Thickness(1.5);
            prevNode.Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        }

        var node = _nodes[screen];
        node.Card.BorderBrush = (Brush)FindResource("AccentGlow");
        node.Card.BorderThickness = new Thickness(2.5);
        node.Card.SetResourceReference(Border.BackgroundProperty, "AccentLight");
        PulseStart(node.Card);
        _current = screen;
    }

    /// <summary>开始新场景时重置（保留当前模块选择，清空轨迹与高亮）。</summary>
    public void ResetGraph()
    {
        _trail.Clear();
        _activeEdgeKey = null;
        if (_current is GameScreen cur && _nodes.TryGetValue(cur, out var n))
            PulseStop(n.Card);
        _current = null;
        foreach (var node in _nodes.Values)
        {
            node.Card.SetResourceReference(Border.BorderBrushProperty, node.Overlay ? "TextMuted" : "CardBorder");
            node.Card.BorderThickness = new Thickness(1.5);
            node.Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        }
        foreach (var e in _edges.Values) SetEdgeDefault(e);
        TxtCurrent.Text = "当前画面：—";
        TxtDetail.Text = "判定依据：—";
        DotCurrent.Fill = (Brush)FindResource("EnvDotIdle");
    }

    // ---------------- 可视化事件处理骨架（Worker→GUI 管道事件消费） ----------------

    /// <summary>探针 Top-5 候选事件：更新底部详情栏展示候选页面与分数。</summary>
    public void OnProbe(string topCandidatesJson)
    {
        try
        {
            var cands = System.Text.Json.JsonSerializer.Deserialize<List<ProbeCandidateDto>>(topCandidatesJson);
            if (cands is null || cands.Count == 0) return;
            var lines = cands.Select(c => $"{c.screen}({c.score:F1})");
            TxtDetail.Text = $"探针候选：{string.Join(" ｜ ", lines)}";
        }
        catch
        {
            /* JSON 解析失败不影响图谱展示 */
        }
    }

    /// <summary>导航寻路事件：高亮路径（已由 OnScreen 串联轨迹处理，此处仅记录详情）。</summary>
    public void OnNav(string from, string to)
    {
        TxtDetail.Text = $"导航：{from} → {to}";
    }

    /// <summary>比赛阶段切换事件：在顶部当前页旁显示当前比赛阶段。</summary>
    public void OnFlow(string stage, string? deadline)
    {
        var dl = string.IsNullOrEmpty(deadline) ? "" : $"（死线 {DateTime.Parse(deadline):HH:mm:ss}）";
        TxtDetail.Text = $"比赛阶段：{stage}{dl}";
    }

    /// <summary>探针候选 DTO（仅用于 JSON 反序列化）。</summary>
    private sealed class ProbeCandidateDto
    {
        public string screen { get; set; } = "";
        public double score { get; set; }
        public string evidence { get; set; } = "";
    }
}
