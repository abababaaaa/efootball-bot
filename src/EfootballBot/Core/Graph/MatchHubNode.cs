using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Graph;

/// <summary>比赛页卡片 Id（同时是 FocusGraph 槽位 Id）。</summary>
public static class MatchHubCards
{
    public const string Event = "event";          // 活动
    public const string EventLeague = "eleague";  // eFootball™ 联赛
    public const string MyLeague = "myleague";    // 我的联赛
    public const string Friend = "friend";        // 好友对战
    public const string Friendly = "friendly";    // 友谊赛
    public const string Custom = "custom";        // 自定义锦标赛
    public const string Quick = "quick";          // 快速配对
}

/// <summary>
/// 比赛页节点（带目标参数）：观察聚焦卡亮描边 + 框内标签定身份，
/// 在 FocusGraph 上 BFS 给出下一步方向键，每步复核落点。
/// 活动 / eFootball™联赛 / 我的联赛为合法路径卡；其余 4 卡为禁用卡（OffTrack），
/// BFS 自动给出走回目标的最短路径（同时考虑向左和向上）。
/// </summary>
public sealed class MatchHubNode : INavNode
{
    private readonly string _target;
    private readonly FocusGraph _graph = BuildGraph();

    public MatchHubNode(string targetCard)
    {
        if (BuildGraph().Slot(targetCard) is null)
            throw new ArgumentException($"未知比赛页卡片：{targetCard}");
        _target = targetCard;
    }

    public int StepBudget => 10;

    public NodeState Check(EfootballBot.Core.Bot.Observation obs)
        => Decide(IsMatchHubPage(obs), MatchHubCardLocator.Locate(obs), _target);

    /// <summary>纯判定。page=false 或 card=null 时一律 Uncertain。</summary>
    internal static NodeState Decide(bool page, string? card, string target)
    {
        if (!page || card is null) return NodeState.Uncertain;
        if (card == target) return NodeState.Anchored;
        if (card is MatchHubCards.Event or MatchHubCards.EventLeague or MatchHubCards.MyLeague)
            return NodeState.Transit;
        return NodeState.OffTrack;
    }

    public IReadOnlyList<NavAction> NextStep(EfootballBot.Core.Bot.Observation obs)
    {
        string? card = MatchHubCardLocator.Locate(obs);
        if (card is null) return Array.Empty<NavAction>();
        var path = _graph.FindPath(card, _target);
        if (path is null || path.Count == 0) return Array.Empty<NavAction>();
        return new[] { NavAction.Dpad(path[0].Dir), NavAction.Wait(500) };
    }

    public string Describe(EfootballBot.Core.Bot.Observation obs)
    {
        string? card = MatchHubCardLocator.Locate(obs);
        return card is null ? "比赛页卡片信号无效" : $"比赛页 / {CardDisplayName(card)}";
    }

    internal static string CardDisplayName(string id) => id switch
    {
        MatchHubCards.Event => "活动",
        MatchHubCards.EventLeague => "eFootball™联赛",
        MatchHubCards.MyLeague => "我的联赛",
        MatchHubCards.Friend => "好友对战",
        MatchHubCards.Friendly => "友谊赛",
        MatchHubCards.Custom => "自定义锦标赛",
        MatchHubCards.Quick => "快速配对",
        _ => id,
    };

    /// <summary>页面锚点：左上黄色「比赛」标题 + 左下「返回」按钮。</summary>
    private static bool IsMatchHubPage(EfootballBot.Core.Bot.Observation obs)
    {
        bool title = obs.Ocr.MergedWords.Any(w =>
            w.CenterX < 0.20 && w.CenterY < 0.18
            && OcrText.Norm(w.Text).Equals("比赛"));
        bool back = obs.Ocr.MergedWords.Any(w =>
            w.CenterX < 0.30 && w.CenterY > 0.85
            && OcrText.Norm(w.Text).Contains("返回"));
        return title && back;
    }

    /// <summary>
    /// 比赛页 FocusGraph（真机边规则）：
    /// 上排：活动 ⇄ eFootball™联赛 ⇄ 我的联赛 ⇄ 好友对战 ⇄ 友谊赛
    /// 上下：我的联赛 ⇅ 自定义锦标赛；好友对战 ⇅ 快速配对
    /// 下排：自定义锦标赛 ⇄ 快速配对
    /// 硬保证：eFootball™联赛 Right 固定到我的联赛。
    /// </summary>
    internal static FocusGraph BuildGraph() => new FocusGraph()
        .AddSlot(MatchHubCards.Event, 0.20, 0.50)
        .AddSlot(MatchHubCards.EventLeague, 0.49, 0.55)
        .AddSlot(MatchHubCards.MyLeague, 0.78, 0.45)
        .AddSlot(MatchHubCards.Friend, 1.05, 0.45)
        .AddSlot(MatchHubCards.Friendly, 1.32, 0.45)
        .AddSlot(MatchHubCards.Custom, 0.78, 0.75)
        .AddSlot(MatchHubCards.Quick, 1.05, 0.75)
        // 上排链
        .AddEdge(MatchHubCards.Event, PadDir.Right, MatchHubCards.EventLeague)
        .AddEdge(MatchHubCards.EventLeague, PadDir.Left, MatchHubCards.Event)
        .AddEdge(MatchHubCards.EventLeague, PadDir.Right, MatchHubCards.MyLeague)
        .AddEdge(MatchHubCards.MyLeague, PadDir.Left, MatchHubCards.EventLeague)
        .AddEdge(MatchHubCards.MyLeague, PadDir.Right, MatchHubCards.Friend)
        .AddEdge(MatchHubCards.Friend, PadDir.Left, MatchHubCards.MyLeague)
        .AddEdge(MatchHubCards.Friend, PadDir.Right, MatchHubCards.Friendly)
        .AddEdge(MatchHubCards.Friendly, PadDir.Left, MatchHubCards.Friend)
        // 上下排
        .AddEdge(MatchHubCards.MyLeague, PadDir.Down, MatchHubCards.Custom)
        .AddEdge(MatchHubCards.Custom, PadDir.Up, MatchHubCards.MyLeague)
        .AddEdge(MatchHubCards.Friend, PadDir.Down, MatchHubCards.Quick)
        .AddEdge(MatchHubCards.Quick, PadDir.Up, MatchHubCards.Friend)
        // 下排链
        .AddEdge(MatchHubCards.Custom, PadDir.Right, MatchHubCards.Quick)
        .AddEdge(MatchHubCards.Quick, PadDir.Left, MatchHubCards.Custom);
}

/// <summary>
/// 比赛页聚焦卡定位：检测亮描边圆角外框 → 读框内底部标签定卡片 Id。
/// 描边 / 标签对不上或检不到描边时返回 null（调用方按 Uncertain 处理，绝不靠猜）。
/// </summary>
internal static class MatchHubCardLocator
{
    private static readonly (string Id, string Norm)[] Labels =
    {
        (MatchHubCards.Event, "活动"),
        (MatchHubCards.EventLeague, "efootball联赛"),
        (MatchHubCards.MyLeague, "我的联赛"),
        (MatchHubCards.Friend, "好友对战"),
        (MatchHubCards.Friendly, "友谊赛"),
        (MatchHubCards.Custom, "自定义锦标赛"),
        (MatchHubCards.Quick, "快速配对"),
    };

    public static string? Locate(EfootballBot.Core.Bot.Observation obs)
    {
        var f = obs.Frame;
        if (f is null || f.Bgra.Length == 0) return null;
        if (!TryDetectOutline(f, out NormRect box)) return null;
        return ReadLabel(obs.Ocr, box);
    }

    /// <summary>
    /// 检测聚焦卡白色描边：
    /// ① 逐行找长白色段（≥15% 帧宽）并按 y 聚成水平边带；
    /// ② 配对两条 x 跨度接近、纵向间隔 0.25~0.78 的边带；
    /// ③ 复核两侧竖边白像素支撑，取支撑最强的候选。
    /// 上排大卡与下排小卡均覆盖。
    /// </summary>
    private static bool TryDetectOutline(FrameData f, out NormRect box)
    {
        var region = new NormRect(0, 0.12, 1, 0.86);
        var (px, py, pw, ph) = FrameAnalyzer.PixelRect(f, region);
        var b = f.Bgra;

        // ① 逐行长白色段
        var segs = new List<(int Y, int X0, int X1)>();
        for (int yy = 0; yy < ph; yy++)
        {
            int row = (py + yy) * f.Width * 4;
            int runStart = -1, runLen = 0;
            for (int xx = 0; xx <= pw; xx++)
            {
                bool white = false;
                if (xx < pw)
                {
                    int i = row + (px + xx) * 4;
                    white = FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], HsvFilter.White);
                }
                if (white)
                {
                    if (runStart < 0) runStart = xx;
                    runLen++;
                }
                else if (runStart >= 0)
                {
                    if (runLen >= pw * 0.15) segs.Add((yy, runStart, xx - 1));
                    runStart = -1; runLen = 0;
                }
            }
        }

        // 按 y 聚成水平边带（相邻 ≤4 行、x 重叠 ≥60%）
        var bands = new List<(int Y0, int Y1, int X0, int X1)>();
        foreach (var s in segs.OrderBy(s => s.Y))
        {
            var band = bands.LastOrDefault();
            if (bands.Count > 0 && s.Y - band.Y1 <= 4
                && Overlap(s.X0, s.X1, band.X0, band.X1) >= 0.6)
            {
                bands[^1] = (band.Y0, s.Y, Math.Min(s.X0, band.X0), Math.Max(s.X1, band.X1));
            }
            else
            {
                bands.Add((s.Y, s.Y, s.X0, s.X1));
            }
        }

        // ② ③ 配对 + 竖边复核
        box = default;
        double bestSupport = 0;
        for (int i = 0; i < bands.Count; i++)
        {
            for (int j = i + 1; j < bands.Count; j++)
            {
                var top = bands[i]; var bottom = bands[j];
                double gap = (bottom.Y0 - top.Y1) / (double)f.Height;
                if (gap < 0.25 || gap > 0.78) continue;
                if (Math.Abs(top.X0 - bottom.X0) > pw * 0.06
                    || Math.Abs(top.X1 - bottom.X1) > pw * 0.06) continue;

                int x0 = (top.X0 + bottom.X0) / 2;
                int x1 = (top.X1 + bottom.X1) / 2;
                double support = VerticalSupport(f, px, py, x0, x1, top.Y1, bottom.Y0);
                if (support > bestSupport)
                {
                    bestSupport = support;
                    box = new NormRect(
                        (px + x0) / (double)f.Width,
                        (py + top.Y0) / (double)f.Height,
                        (x1 - x0) / (double)f.Width,
                        (bottom.Y1 - top.Y0) / (double)f.Height);
                }
            }
        }

        return bestSupport >= 0.30;
    }

    /// <summary>两条竖边在间隙内多 y 采样的白像素比例（0~1）。</summary>
    private static double VerticalSupport(FrameData f, int px, int py, int x0, int x1, int yTop, int yBottom)
    {
        var b = f.Bgra;
        int span = Math.Max(1, yBottom - yTop);
        int samples = Math.Max(6, span / 12);
        int hit = 0, n = 0;
        for (int k = 0; k <= samples; k++)
        {
            int yy = py + yTop + span * k / samples;
            if (yy >= f.Height) continue;
            int row = yy * f.Width * 4;
            foreach (int xx in new[] { x0, x1 })
            {
                // 在该 x 左右 2px 内任一白即算
                bool white = false;
                for (int d = -2; d <= 2; d++)
                {
                    int xxx = px + xx + d;
                    if (xxx < 0 || xxx >= f.Width) continue;
                    int i = row + xxx * 4;
                    if (FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], HsvFilter.White))
                    {
                        white = true;
                        break;
                    }
                }
                n++;
                if (white) hit++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    private static double Overlap(int a0, int a1, int b0, int b1)
    {
        int inter = Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
        int min = Math.Min(a1 - a0, b1 - b0);
        return min <= 0 ? 0 : inter / (double)min;
    }

    /// <summary>读描边框内下半部的卡片标签。</summary>
    private static string? ReadLabel(OcrResult ocr, NormRect box)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (!box.Contains(w.CenterX, w.CenterY)) continue;
            if (w.CenterY < box.Y + box.H * 0.45) continue; // 标签在卡片下半部
            string t = OcrText.Norm(w.Text);
            foreach (var (id, n) in Labels)
            {
                // 2 字词只接受精确相等；长词允许包含（OCR 可能带尾巴）
                if (t.Equals(n) || (n.Length >= 3 && t.Contains(n)))
                    return id;
            }
        }
        return null;
    }
}
