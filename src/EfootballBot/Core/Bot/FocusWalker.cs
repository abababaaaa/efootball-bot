using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>菜单上的一个可聚焦槽位（卡片/图标）。Aliases 为 FocusCardDetector 可能给出的卡名。</summary>
public sealed record FocusSlot(string Id, double CX, double CY, string[] Aliases);

/// <summary>
/// 菜单焦点导航图：槽位 + 方向键有向边。BFS 寻路；运行时发现的死边可临时摘除。
/// 任何"焦点卡片 + 方向键"菜单声明槽位与边后即可交给 FocusWalker 使用。
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
    /// 同 y 的左右列槽位（赛事信息 x≈0.185 vs 建议 x≈0.505）靠 0.14 列容差区分。
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

/// <summary>
/// 通用焦点卡片步行器：在声明式焦点图上闭环移动高光到目标槽位。
/// 每步：完整观察定位（卡名→最近槽位）→ BFS 选边 → 按键 → 150ms 判稳追踪 →
/// 按实际落点重算；死边（按了没动）自动摘除重寻；高光丢失/无路径调 RecoverAsync（仅 1 次预算）。
/// 连续 2 帧完整观察确认高光在目标槽位才返回 true（调用方随后按 A）。
/// </summary>
public sealed class FocusWalker
{
    private readonly BotEngine _e;
    private readonly FocusGraph _g;
    private readonly CancellationToken _ct;
    private readonly Action<string, LogLevel> _log;
    private readonly Func<FrameData, OcrResult, FocusCard?> _detect;

    public FocusWalker(BotEngine engine, FocusGraph graph, CancellationToken ct,
        Action<string, LogLevel>? log = null,
        Func<FrameData, OcrResult, FocusCard?>? detector = null)
    {
        _e = engine;
        _g = graph;
        _ct = ct;
        _log = log ?? ((_, _) => { });
        _detect = detector ?? FocusCardDetector.Detect;
    }

    /// <summary>高光丢失或图上无路时的恢复动作（通常为焦点归位）。全程最多调用 1 次。</summary>
    public Func<Task>? RecoverAsync { get; init; }

    public async Task<bool> GotoAsync(string targetId, int maxSteps = 12)
    {
        var target = _g.Slot(targetId) ?? throw new ArgumentException($"目标槽位未声明：{targetId}");
        bool recoverUsed = false;
        var edgeFails = new Dictionary<(string, PadDir), int>();

        // 起步用一次完整 OCR 观察定位（唯一允许读 OCR 缓存的地方；之后全部走新鲜帧判稳）
        FocusSlot? cur = await LocateAsync(initial: true);
        if (cur is null)
        {
            if (!await TryRecoverAsync(recoverUsed)) return false;
            recoverUsed = true;
            edgeFails.Clear();
            cur = await LocateAsync(initial: true);
            if (cur is null) return false;
        }

        for (int step = 0; step < maxSteps; step++)
        {
            _log($"第{step + 1}步：高光=「{cur!.Id}」" +
                 (cur.Id == _g.PhotoSlotId ? "（照片槽位推定：未检出白块）" : ""), LogLevel.Info);

            // 到达目标：必须再用两帧按键后新鲜画面复核高光确实停住（防路过/动画残影误判）
            if (cur.Id == targetId)
            {
                if (await ConfirmAtAsync(target))
                {
                    _log($"高光连续判稳停在「{targetId}」，可以确认", LogLevel.Info);
                    return true;
                }
                // 复核未过：高光实际已离开目标（扫过/被动画带开），用新鲜帧重新定位，不当作无路径
                _log("目标处复核未通过，重新定位高光", LogLevel.Info);
                cur = await LocateAsync(initial: false) ?? await LocateAsync(initial: true);
                if (cur is null)
                {
                    if (!await TryRecoverAsync(recoverUsed)) return false;
                    recoverUsed = true;
                    cur = await LocateAsync(initial: true);
                    if (cur is null) return false;
                }
                continue;
            }

            var path = _g.FindPath(cur.Id, targetId);
            if (path is null || path.Count == 0)
            {
                _log($"图上无路径 {cur.Id}→{targetId}，{(recoverUsed || RecoverAsync is null ? "放弃" : "归位并恢复边后重算")}", LogLevel.Info);
                if (!await TryRecoverAsync(recoverUsed)) return false;
                recoverUsed = true;
                edgeFails.Clear();
                cur = await LocateAsync(initial: true);
                if (cur is null) return false;
                continue;
            }

            var (dir, nextId) = path[0];
            _log($"→ {dir}（{cur.Id}→{nextId}，距目标 {path.Count} 步）", LogLevel.Info);

            // 按键瞬间的时间戳：之后只接受比它更新的帧，杜绝用按键前的旧缓存帧重复按方向键
            long pressTick = Environment.TickCount64;
            await _e.Pad.Dpad(dir);

            var wr = await WatchSettleAsync(pressTick, target);

            if (wr.Kind == WatchKind.Reached)
            {
                // Watch 判稳在目标处，再独立复核一帧（高光可能正在穿过目标）
                if (await ConfirmAtAsync(target))
                {
                    _log($"高光判稳停在「{targetId}」，可以确认", LogLevel.Info);
                    return true;
                }
                _log("高光扫过目标未停住，继续从目标槽位重算", LogLevel.Info);
                cur = target;
                edgeFails.Clear();
                continue;
            }

            // 落点：停稳坐标映射；全程无白块=高光仍在照片槽位（赛程表聚焦时无大白块，用户硬规则）
            FocusSlot? landed = wr.Kind == WatchKind.NoSignal
                ? (_g.PhotoSlotId is null ? null : _g.Slot(_g.PhotoSlotId))
                : MapSlot(wr.X, wr.Y);

            if (landed is null)
            {
                _log("按键后未能定位高光落点，重新定位", LogLevel.Info);
                cur = await LocateAsync(initial: false) ?? await LocateAsync(initial: true);
                if (cur is null)
                {
                    if (!await TryRecoverAsync(recoverUsed)) return false;
                    recoverUsed = true;
                    cur = await LocateAsync(initial: true);
                    if (cur is null) return false;
                }
                continue;
            }

            // 死边：停稳位置没离开原槽位（按了没动）
            if (landed.Id == cur.Id)
            {
                var key = (cur.Id, dir);
                edgeFails[key] = edgeFails.GetValueOrDefault(key) + 1;
                // 照片槽位无白块是常态，"无信号"不能证明方向键没生效——不摘边，只重试同一方向，3 次后归位
                if (cur.Id == _g.PhotoSlotId)
                {
                    if (edgeFails[key] < 3)
                    {
                        _log($"照片槽位上 {dir} 后仍无白块，重试 {dir}（第 {edgeFails[key]} 次）", LogLevel.Info);
                        continue;
                    }
                    _log("照片槽位连续 3 次无响应，归位后重试", LogLevel.Warn);
                    if (!await TryRecoverAsync(recoverUsed)) return false;
                    recoverUsed = true;
                    edgeFails.Clear();
                    cur = await LocateAsync(initial: true);
                    if (cur is null) return false;
                    continue;
                }
                if (edgeFails[key] >= 2)
                {
                    _log($"边 {cur.Id}→{dir} 连续失败，摘除重算", LogLevel.Info);
                    _g.RemoveEdge(cur.Id, dir);
                }
                else
                {
                    _log($"边 {cur.Id}→{dir} 疑似未生效（第 {edgeFails[key]} 次），重判", LogLevel.Info);
                }
                continue;
            }

            edgeFails.Clear();
            cur = landed;
        }

        _log($"步行 {maxSteps} 步仍未到达「{targetId}」", LogLevel.Warn);
        return false;
    }

    private async Task<bool> TryRecoverAsync(bool alreadyUsed)
    {
        if (alreadyUsed || RecoverAsync is null) return false;
        _g.RestoreRemoved();
        await RecoverAsync();
        return true;
    }

    /// <summary>
    /// 定位当前高光槽位。initial=true 走完整 ObserveAsync（含 OCR，可读白卡标题）；
    /// 否则只读最新帧做纯像素几何定位（不受 OCR 缓存旧帧影响）。
    /// 用户硬规则：检不出任何高光白块时，光标视为在照片槽位（赛程表聚焦只有细亮边、无大白块）。
    /// </summary>
    private async Task<FocusSlot?> LocateAsync(bool initial)
    {
        FocusCard? card;
        if (initial)
        {
            var o = await _e.ObserveAsync(_ct);
            card = _detect(o.Frame, o.Ocr);
        }
        else
        {
            await _e.Pad.Wait(120, _ct);
            var f = _e.LatestFrame;
            card = f is null ? null : _detect(f, OcrResult.Empty);
        }
        if (card is not null) return _g.SlotByName(card.Name) ?? _g.Nearest(card.CX, card.CY);
        return _g.PhotoSlotId is null ? null : _g.Slot(_g.PhotoSlotId);
    }

    /// <summary>按几何位置映射槽位（先名后几何，i 小白块靠名精确命中）。</summary>
    private FocusSlot? MapSlot(double x, double y) => _g.Nearest(x, y);

    /// <summary>
    /// 到达复核：900ms 窗口内取按键后新帧，累计 2 次高光在目标槽位半径内才算停稳。
    /// 检不出白块的帧忽略（白块检测偶发漏帧；调用前 WatchSettle 已判稳，漏检不等于离开）；
    /// 一旦检出高光在其他槽位，立即判失败（高光真的移走了）。
    /// </summary>
    private async Task<bool> ConfirmAtAsync(FocusSlot target)
    {
        long t0 = Environment.TickCount64;
        int hits = 0;
        for (int t = 0; t < 900; t += 100)
        {
            await _e.Pad.Wait(100, _ct);
            var f = _e.LatestFrame;
            if (f is null || f.TimestampMs <= t0) continue;   // 只要复核开始后的新帧
            var c = _detect(f, OcrResult.Empty);
            if (c is null) continue;                          // 漏检帧忽略
            if (AtSlot(c, target)) { if (++hits >= 2) return true; }
            else return false;                                // 高光在别的槽位=真离开了
        }
        return false;
    }

    private static bool AtSlot(FocusCard c, FocusSlot s)
        => Math.Abs(c.CY - s.CY) < 0.085 && Math.Abs(c.CX - s.CX) < 0.16
        || CardCoversSlot(c, s);

    private static bool CardCoversSlot(FocusCard card, FocusSlot slot)
        => slot.CX >= card.X - 0.01 && slot.CX <= card.X + card.W + 0.01
        && slot.CY >= card.Y - 0.01 && slot.CY <= card.Y + card.H + 0.01;

    private enum WatchKind { Reached, Settled, NoSignal }

    private readonly record struct WatchResult(WatchKind Kind, double X, double Y)
    {
        public static readonly WatchResult Reached = new(WatchKind.Reached, 0, 0);
        public static readonly WatchResult NoSignal = new(WatchKind.NoSignal, 0, 0);
    }

    /// <summary>
    /// 按键后判稳：只轮询比 pressTick 更新的帧（TimestampMs 门控，彻底避开按键前旧缓存帧），
    /// 每 120ms 检测一次纯像素高光位置，最长 1500ms：
    /// 连续 2 次位移&lt;0.02 判为停稳，再补 1 帧（130ms 后）复核位置不变，防止在过渡动画中途误判；
    /// 停稳位置在目标半径→Reached，否则→Settled(落点)；全程无白块→NoSignal（外层按用户规则
    /// 推定为照片槽位「赛程表」——该卡聚焦时只有细亮边没有大白块）。
    /// 注意：移动途中扫过目标不返回，必须真正停稳——避免"路过兑换积分"。
    /// </summary>
    private async Task<WatchResult> WatchSettleAsync(long pressTick, FocusSlot target)
    {
        double? lx = null, ly = null;
        int stable = 0;

        for (int t = 0; t < 1500; t += 120)
        {
            await _e.Pad.Wait(120, _ct);
            var f = _e.LatestFrame;
            if (f is null || f.TimestampMs <= pressTick) continue;  // 只要按键后的新帧
            var c = _detect(f, OcrResult.Empty);
            if (c is null) continue;   // 无白块=高光在照片槽位或动画中，不参与判稳

            bool movedLittle = lx is not null && ly is not null
                && Math.Max(Math.Abs(c.CX - lx.Value), Math.Abs(c.CY - ly.Value)) < 0.02;
            if (movedLittle && ++stable >= 2 && await VerifyStillAsync(pressTick, c.CX, c.CY))
            {
                // 连续 2 次不动 + 补帧复核仍不动 → 真正停稳
                bool atTarget = Math.Abs(c.CY - target.CY) < 0.085
                    && Math.Abs(c.CX - target.CX) < 0.16;
                return atTarget ? WatchResult.Reached : new WatchResult(WatchKind.Settled, c.CX, c.CY);
            }
            if (!movedLittle) stable = 0;
            lx = c.CX; ly = c.CY;
        }

        return lx is null ? WatchResult.NoSignal : new WatchResult(WatchKind.Settled, lx.Value, ly!.Value);
    }

    /// <summary>判稳补帧：再等 130ms 取新帧，高光位置仍在 (x,y) 0.02 邻域内才确认真停住。</summary>
    private async Task<bool> VerifyStillAsync(long pressTick, double x, double y)
    {
        await _e.Pad.Wait(130, _ct);
        var f = _e.LatestFrame;
        if (f is null || f.TimestampMs <= pressTick) return false;
        var c = _detect(f, OcrResult.Empty);
        return c is not null
            && Math.Max(Math.Abs(c.CX - x), Math.Abs(c.CY - y)) < 0.02;
    }
}
