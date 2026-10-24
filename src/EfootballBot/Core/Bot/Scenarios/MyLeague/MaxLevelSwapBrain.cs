using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>满级自动换人涉及的界面（OCR 固定词判定；可变内容一律不作判定点）。</summary>
public enum SwapScreen
{
    /// <summary>都不是（加载/过场/意外界面）。</summary>
    None,
    /// <summary>比赛计划·首发站位页（侧栏 战术/球队/替补球员…，光标默认在门将）。</summary>
    Lineup,
    /// <summary>球员详情数据页（底部「X 球员动作」，数据栏 等级 x/x + 能力点）。</summary>
    Detail,
    /// <summary>「球员动作」弹窗（等级训练完成/能力点分配/…/锁定 + B 取消）。</summary>
    ActionPopup,
    /// <summary>能力点分配页（底部「Y 推荐分配」）。</summary>
    Alloc,
    /// <summary>后备球员列表页（左下「剩余球员数」）。</summary>
    Bench,
    /// <summary>「排序」弹窗（球员姓名…当前等级 + 取消/顺序，右上方向箭头）。</summary>
    Sort,
    /// <summary>「筛选」页（俱乐部球队/…/卡片类型/比赛风格 + 完成）。</summary>
    Filter,
    /// <summary>「卡片类型」子页（普通/状态火热/精选/…/不可用的球员 单选列表）。</summary>
    CardType,
}

/// <summary>球员详情数据栏读取结果。</summary>
/// <param name="Level">当前等级（x/x 的 x）。</param>
/// <param name="LevelMax">等级上限（x/x 的分母）。</param>
/// <param name="Points">剩余能力点（详情页橙色数字）。</param>
public sealed record PlayerStatus(int Level, int LevelMax, int Points)
{
    /// <summary>等级已练满且能力点还没用完（自动换人的目标球员）。</summary>
    public bool NeedsSwap => Level == LevelMax && Points > 0;
}

/// <summary>
/// 满级自动换人的纯判定（OCR 词帧 + 像素探针，全部可单测）。
/// 流程依据用户 2026-10-24 实机配方：联赛主页 Y 开比赛计划 → 光标默认门将，
/// 沿 ↑←↑→→→→→↓← 依次巡检 11 人；满级（等级 x/x）一律换人——能力点未用完先 X 球员动作
/// 分配能力点、已用完直接换（用户 2026-10-24 修正），再 A 选中 → 侧栏后备球员 →
/// 排序（当前等级·升序）→ 筛选（勾高阶卡类型）→ A 换入等级最低的后备球员。
/// </summary>
public static partial class MaxLevelSwapBrain
{
    // ---------------- 界面判定 ----------------

    /// <summary>
    /// 首发站位页：侧栏「替补球员 / 自动配置球员」为该页独有（后备球员页标题虽有
    /// 「后备球员」但无侧栏文字项；排序/筛选弹窗先于本判定）。整体实力 + 战术 做兜底证据。
    /// </summary>
    public static bool IsLineup(OcrResult ocr) =>
        ocr.ContainsJoined("替补球员") || ocr.ContainsJoined("自动配置球员") ||
        (ocr.ContainsJoined("整体实力") && ocr.ContainsJoined("战术"));

    /// <summary>球员详情页：底部「X 球员动作」为主判据，「惯用脚」（数据栏固定列）兜底。</summary>
    public static bool IsDetail(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") || ocr.ContainsJoined("惯用脚");

    /// <summary>球员动作弹窗：详情页底部也有「球员动作」四字，弹窗必须叠加「取消」同屏才成立。</summary>
    public static bool IsActionPopup(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") && ocr.ContainsJoined("取消");

    /// <summary>能力点分配页：底部「Y 推荐分配」为唯一词（弹窗项只有「能力点分配」）。</summary>
    public static bool IsAlloc(OcrResult ocr) => ocr.ContainsJoined("推荐分配");

    /// <summary>后备球员列表页：左下「剩余球员数: N」唯一；兜底 标题+排除侧栏词
    /// （首发站位页侧栏也印「后备球员」）。</summary>
    public static bool IsBench(OcrResult ocr) =>
        ocr.ContainsJoined("剩余球员数") || ocr.ContainsJoined("剩余球员") ||
        (ocr.ContainsJoined("后备球员") && !ocr.ContainsJoined("替补球员") &&
         !ocr.ContainsJoined("自动配置球员"));

    /// <summary>排序弹窗：「当前等级」列表项 + 「取消」。先于筛选（弹窗盖在列表上，底层词可能漏读进来）。</summary>
    public static bool IsSort(OcrResult ocr) =>
        ocr.ContainsJoined("当前等级") && ocr.ContainsJoined("取消");

    /// <summary>卡片类型子页：单选列表「状态火热 / 不可用的球员」为该页独有，先于筛选页判定
    /// （筛选页也印「卡片类型」行）。</summary>
    public static bool IsCardType(OcrResult ocr) =>
        ocr.ContainsJoined("状态火热") || ocr.ContainsJoined("不可用的球员");

    /// <summary>筛选页：行首「俱乐部球队」为主判据；标题「筛选」+ 行尾「比赛风格」兜底。</summary>
    public static bool IsFilter(OcrResult ocr) =>
        ocr.ContainsJoined("俱乐部球队") ||
        (ocr.ContainsJoined("筛选") && ocr.ContainsJoined("比赛风格"));

    public static SwapScreen Classify(OcrResult ocr)
    {
        if (IsActionPopup(ocr)) return SwapScreen.ActionPopup;
        if (IsAlloc(ocr)) return SwapScreen.Alloc;
        if (IsCardType(ocr)) return SwapScreen.CardType;
        if (IsSort(ocr)) return SwapScreen.Sort;
        if (IsFilter(ocr)) return SwapScreen.Filter;
        if (IsBench(ocr)) return SwapScreen.Bench;
        if (IsDetail(ocr)) return SwapScreen.Detail;
        if (IsLineup(ocr)) return SwapScreen.Lineup;
        return SwapScreen.None;
    }

    // ---------------- 详情页数据栏读取（等级 x/x + 能力点） ----------------

    /// <summary>
    /// 读取数据栏：找「等级」「能力点」表头词（y 带 <paramref name="headYMin"/>~0.52），
    /// 取正下方 <paramref name="valueMaxDy"/> 内、横向 ±0.06 的词拼出 23/26 与 44。
    /// 裁切放大帧上相对几何不同，调大 <paramref name="valueMaxDy"/>。
    /// 任一要素读不出返回 null（调用方重试/裁切兜底，绝不盲猜）。
    /// </summary>
    public static PlayerStatus? ReadPlayerStatus(OcrResult ocr, double headYMin = 0.28, double valueMaxDy = 0.10)
    {
        var (pair, pts) = ReadParts(ocr, headYMin, valueMaxDy);
        if (pair is null || pts is null) return null;
        return new PlayerStatus(pair.Value.Cur, pair.Value.Max, pts.Value);
    }

    /// <summary>分要素读取：等级 (cur,max) 与能力点。实机有的球员某列 OCR 读不出，
    /// 调用方对失败列单独裁切重 OCR 后合并（<see cref="MergeParts"/>）。</summary>
    public static ((int Cur, int Max)? Pair, int? Points) ReadParts(
        OcrResult ocr, double headYMin = 0.28, double valueMaxDy = 0.10)
    {
        var lvHead = FindHead(ocr, "等级", headYMin);
        var ptHead = FindHead(ocr, "能力点", headYMin);
        var lv = lvHead is null ? null : JoinBelow(ocr, lvHead, ptHead, valueMaxDy);
        var pt = ptHead is null ? null : JoinBelow(ocr, ptHead, lvHead, valueMaxDy);
        return (ParseLevelPair(lv), ParseSingleNumber(pt));
    }

    /// <summary>全帧直读优先，失败要素用裁切重读结果补齐（同要素直读值优先）。</summary>
    public static ((int Cur, int Max)? Pair, int? Points) MergeParts(
        ((int Cur, int Max)? Pair, int? Points) direct, ((int Cur, int Max)? Pair, int? Points) crop)
        => (direct.Pair ?? crop.Pair, direct.Points ?? crop.Points);

    /// <summary>等级列文本 → (cur,max)。OCR 可能拆成 "23" + "/26" 或丢斜杠拼成 "2326"——
    /// 丢斜杠时按两位数等分/单数×自身兜底（等级上限常见 24~37）。</summary>
    internal static (int Cur, int Max)? ParseLevelPair(string? lv)
    {
        if (string.IsNullOrEmpty(lv)) return null;
        var m = SlashNum().Match(lv);
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out int cur) &&
            int.TryParse(m.Groups[2].Value, out int max) &&
            cur is >= 0 and <= 200 && max is >= 1 and <= 200 && cur <= max)
            return (cur, max);
        // 斜杠丢失：纯数字 3~4 位按「cur ≤ max」拆（537 → 5/37 而非 53/7；2729 → 27/29）
        string digits = new(lv.Where(char.IsDigit).ToArray());
        if (digits.Length == 3 &&
            int.TryParse(digits[..1], out int a3) && int.TryParse(digits[1..], out int b3) &&
            a3 <= b3 && a3 >= 0 && b3 is >= 1 and <= 99)
            return (a3, b3);
        if (digits.Length == 4 &&
            int.TryParse(digits[..2], out int a4) && int.TryParse(digits[2..], out int b4) &&
            a4 <= b4 && a4 >= 0 && b4 is >= 1 and <= 99)
            return (a4, b4);
        return null;
    }

    /// <summary>详情页能力点值（纯数字 44/0；O/0 归一；丢斜杠的 0/86 会读成 086 → 仍取整段）。读不出 null。</summary>
    internal static int? ParseSingleNumber(string? pt)
    {
        if (string.IsNullOrEmpty(pt)) return null;
        string p = pt.ToUpperInvariant().Replace('O', '0');
        int slash = p.IndexOf('/');
        string digits = new((slash >= 0 ? p[..slash] : p).Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 3 && int.TryParse(digits, out int v) && v >= 0 ? v : null;
    }

    /// <summary>能力点分配页剩余点数（左栏「能力点」表头正下方 72/72 → 取斜杠前段）。读不出返回 null。</summary>
    public static int? ReadRemainingPoints(OcrResult ocr)
    {
        var head = FindHead(ocr, "能力点", 0.16);
        var raw = head is null ? null : JoinBelow(ocr, head, null, 0.12);
        return ParseSingleNumber(raw);
    }

    /// <summary>找表头词（含 keyword、y 带内；同 y 取最靠左）。</summary>
    internal static OcrWord? FindHead(OcrResult ocr, string keyword, double headYMin) =>
        ocr.Words
            .Where(w => w.NormText.Contains(keyword) && w.CenterY >= headYMin && w.CenterY <= 0.52)
            .OrderBy(w => w.CenterY).ThenBy(w => w.CenterX)
            .FirstOrDefault();

    /// <summary>
    /// 拼出表头正下方 <paramref name="valueMaxDy"/> 内的值文本。候选词按「离哪个表头更近」归属：
    /// 实机相邻列（等级 23/26 与 能力点 44）横向只差 ~0.05，固定 ± 容差会把邻列的
    /// 「/26」并进能力点值（实测定位于 2026-10-24 测试），必须就近归属。
    /// </summary>
    internal static string? JoinBelow(OcrResult ocr, OcrWord head, OcrWord? otherHead, double valueMaxDy)
    {
        var words = ocr.Words
            .Where(w => w.CenterY > head.CenterY + 0.005 && w.CenterY <= head.CenterY + valueMaxDy)
            .Where(w =>
            {
                double dx = Math.Abs(w.CenterX - head.CenterX);
                if (dx > 0.06) return false;
                return otherHead is null || dx <= Math.Abs(w.CenterX - otherHead.CenterX);
            })
            .OrderBy(w => w.CenterX)
            .Select(w => w.Text)
            .ToList();
        var text = string.Concat(words).Replace(" ", "");
        return text.Length == 0 ? null : text;
    }

    // ---------------- 排序弹窗方向箭头（像素探针） ----------------

    /// <summary>
    /// 排序弹窗右上角方向箭头：↑ = 当前等级升序（列表首位为最低等级，换人目标）。
    /// 实机 2026-10-24（1280×720 用户截图）：箭头为「菱形头 + 细杆」emoji 形态，
    /// 菱形最宽行恰好跨在探针区域中线上——按固定中线分割上下半恒近对称（实机 ↓ 140/154）。
    /// 正确做法：先框出亮像素包围盒，再按包围盒中线分——↑ 的宽行在上半（约 2.4:1），
    /// ↓ 在下半（约 0.6:1），阈值 1.5。读不出返回 null（调用方按升序处理并告警）。
    /// </summary>
    public static bool? IsSortArrowUp(FrameData f)
    {
        var (top, bottom, total) = DiagSortArrow(f);
        if (total < 40) return null;
        if (top >= bottom * 3 / 2 + 3) return true;   // 上半明显更亮 → ↑
        if (bottom >= top * 3 / 2 + 3) return false;  // 下半明显更亮 → ↓
        return null;
    }

    /// <summary>箭头探针明细（包围盒上半/下半亮像素数），校准实机帧用。</summary>
    internal static (int Top, int Bottom, int Total) DiagSortArrow(FrameData f)
    {
        if (f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4) return (0, 0, 0);
        int x0 = (int)(f.Width * 0.92), x1 = (int)(f.Width * 0.98);
        int y0 = (int)(f.Height * 0.04), y1 = (int)(f.Height * 0.20);
        var rowLit = new int[y1 - y0];
        int firstRow = -1, lastRow = -1;
        for (int y = y0; y < y1; y++)
        {
            int lit = 0;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                if (f.Bgra[i] < 120 && f.Bgra[i + 1] < 120 && f.Bgra[i + 2] < 120) continue;
                lit++;
            }
            rowLit[y - y0] = lit;
            if (lit >= 2) { if (firstRow < 0) firstRow = y; lastRow = y; }
        }
        int total = rowLit.Sum();
        // 包围盒太矮（杂点/未渲染箭头）：不算方向
        if (firstRow < 0 || lastRow - firstRow < 12) return (0, 0, total);
        int mid = (firstRow + lastRow) / 2;
        int top = 0, bottom = 0;
        for (int y = firstRow; y <= lastRow; y++)
        {
            if (y <= mid) top += rowLit[y - y0];
            else bottom += rowLit[y - y0];
        }
        return (top, bottom, top + bottom);
    }

    [global::System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,3})\s*/\s*(\d{1,3})")]
    private static partial global::System.Text.RegularExpressions.Regex SlashNum();
}
