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
    /// <summary>「排序」弹窗（球员姓名/位置/整体评价/… + 取消/顺序，右上方向箭头）。</summary>
    Sort,
    /// <summary>「筛选」页（俱乐部球队/…/卡片类型/比赛风格 + 完成）。</summary>
    Filter,
    /// <summary>「卡片类型」子页（普通/状态火热/精选/…/不可用的球员 单选列表）。</summary>
    CardType,
}

/// <summary>球员详情数据栏读取结果。</summary>
/// <param name="Level">当前等级（x/x 的 x）。</param>
/// <param name="LevelMax">等级上限（x/x 的分母）。★0 = 上限读不出（当前等级远低于
/// 可能上限时调用方按「未满级」处理，见 <see cref="MaxLevelSwapController"/>）。</param>
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
/// 排序（整体评价·升序）→ 筛选（勾高阶卡类型）→ A 换入整体评价最低的后备球员。
/// </summary>
public static partial class MaxLevelSwapBrain
{
    // ---------------- 界面判定 ----------------

    /// <summary>
    /// 首发站位页：侧栏「替补球员 / 自动配置球员」为该页独有（后备球员页标题虽有
    /// 「后备球员」但无侧栏文字项；排序/筛选弹窗先于本判定）。整体实力 + 战术 做兜底证据——
    /// ★战术必须整词精确匹配：详情页左上角的球员风格标签「基础战术」会让 ContainsJoined
    /// 误命中（实机 2026-10-25：详情页被判站位页后 Y 重按死循环）。
    /// </summary>
    public static bool IsLineup(OcrResult ocr) =>
        ocr.ContainsJoined("替补球员") || ocr.ContainsJoined("自动配置球员") ||
        (ocr.ContainsJoined("整体实力") && ocr.FindAll("战术").Any(w =>
            w.NormText == "战术" || (w.NormText.Length <= 3 && w.NormText.EndsWith("战术"))));

    /// <summary>球员详情页：底部「X 球员动作」为主判据，「惯用脚」（数据栏固定列）兜底。</summary>
    public static bool IsDetail(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") || ocr.ContainsJoined("惯用脚");

    /// <summary>球员动作弹窗：详情页底部也有「球员动作」四字，弹窗必须叠加「取消」同屏才成立。</summary>
    public static bool IsActionPopup(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") && ocr.ContainsJoined("取消");

    /// <summary>能力点分配页：底部「Y 推荐分配」为唯一词（弹窗项只有「能力点分配」）。</summary>
    public static bool IsAlloc(OcrResult ocr) => ocr.ContainsJoined("推荐分配");

    /// <summary>
    /// 后备球员卡格子页：左下「剩余球员数: N」唯一；兜底 标题+排除侧栏词
    /// （首发站位页侧栏也印「后备球员」）。★必须先于本页判站位页：换人后 B 返回的
    /// 「侧栏光标停在 后备球员 上」的站位页右侧仍预览后备名单（含剩余球员数），
    /// 但同屏有侧栏词——见 <see cref="IsLineup"/>（用户 2026-10-25 截图实锤）。
    /// </summary>
    public static bool IsBench(OcrResult ocr) =>
        ocr.ContainsJoined("剩余球员数") || ocr.ContainsJoined("剩余球员") ||
        (ocr.ContainsJoined("后备球员") && !ocr.ContainsJoined("替补球员") &&
         !ocr.ContainsJoined("自动配置球员"));

    /// <summary>排序弹窗：列表项（「整体评价」，OCR 漏读时退回「当前等级」）+ 「取消」。
    /// 先于筛选（弹窗盖在列表上，底层词可能漏读进来）。</summary>
    public static bool IsSort(OcrResult ocr) =>
        (ocr.ContainsJoined("整体评价") || ocr.ContainsJoined("当前等级")) && ocr.ContainsJoined("取消");

    /// <summary>卡片类型子页：末行「不可用的球员」为该页独有，先于筛选页判定。
    /// ★不能再看「状态火热」：筛选页光标停在「卡片类型」行时，右侧已选值
    /// 「状态火热/精选/高光/高光-ShowTime」会被 OCR 整串读进来（2026-10-30 用户实机），
    /// 误判成子页 → 筛选页识别不到 → 巡检 B×3 逃生弃权。</summary>
    public static bool IsCardType(OcrResult ocr) =>
        ocr.ContainsJoined("不可用的球员");

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
        // 详情页先于站位页（双保险：详情页也有球员风格标签/右侧整体实力等站位页要素）
        if (IsDetail(ocr)) return SwapScreen.Detail;
        // ★站位页先于后备页：换人后 B 返回的站位页侧栏光标停在「后备球员」上、
        // 右侧预览后备名单（含剩余球员数），必须按站位页处理（→ 进阵继续巡检）；
        // 卡格子页无侧栏词，仍走后备页分支
        if (IsLineup(ocr)) return SwapScreen.Lineup;
        if (IsBench(ocr)) return SwapScreen.Bench;
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

    // ---------------- 换入生效判据（整体实力 / 剩余球员数）与弹窗词 ----------------

    /// <summary>
    /// 整体实力数值（站位页/后备球员页右上角，值在标签正下方偏右，实机 1280×720：
    /// 标签 (0.855, 0.20)、值 2403 位于 (0.89, 0.26)）。换入生效后阵容变化该值必变，
    /// 被同名卡挡下（交换未生效）则保持不变——用户 2026-10-25 实机。读不出 null。
    /// </summary>
    public static int? ReadOverallRating(OcrResult ocr)
    {
        var label = ocr.FindFirst("整体实力");
        if (label is null) return null;
        // 整体实力 3~4 位（2403），ParseSingleNumber 的 1~3 位上限不适用，单独解析
        static int? ParseRating(string text)
        {
            string d = new string(text.Where(char.IsDigit).ToArray());
            return d.Length is >= 3 and <= 4 && int.TryParse(d, out int v) && v >= 100 ? v : null;
        }
        return ocr.MergedWords
            .Where(w => w.NormText != label.NormText &&
                        w.CenterY > label.CenterY - 0.02 && w.CenterY <= label.CenterY + 0.12 &&
                        w.CenterX >= label.CenterX - 0.05 && w.CenterX <= label.CenterX + 0.15)
            .Select(w => ParseRating(w.Text))
            .FirstOrDefault(v => v is not null);
    }

    /// <summary>
    /// 后备球员页左下「剩余球员数：N」。换入生效后名单少一人该值必减。
    /// 数字通常与标签同词（OCR 连读「剩余球员数828」）；分离时取右侧最近数字词。读不出 null。
    /// </summary>
    public static int? ReadBenchRemaining(OcrResult ocr)
    {
        var label = ocr.FindFirst("剩余球员数") ?? ocr.FindFirst("剩余球员");
        if (label is null) return null;
        string digits = new(label.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > 0 && int.TryParse(digits, out int same)) return same;
        return ocr.MergedWords
            .Where(w => w.NormText != label.NormText &&
                        Math.Abs(w.CenterY - label.CenterY) <= 0.05 &&
                        w.CenterX > label.CenterX - 0.02 && w.CenterX <= label.CenterX + 0.15)
            .OrderBy(w => w.CenterX)
            .Select(w => ParseSingleNumber(w.Text))
            .FirstOrDefault(v => v is not null);
    }

    /// <summary>
    /// 游戏错误弹窗（实机 2026-10-25 02:17：换人流程被同名卡打乱后弹出
    /// 「功能目前无法使用／详情请参阅[最新消息]／不便之处,敬请原谅／代码:C_XIQX_0019／OK」，
    /// B 按不掉只能 A 点 OK）。出现即全局拦截。
    /// </summary>
    public static bool IsGameErrorDialog(OcrResult ocr) =>
        ocr.ContainsJoined("功能目前无法使用") || ocr.ContainsJoined("不便之处");

    /// <summary>
    /// 确认类弹窗盖在站位页/后备页上（底层页面词仍在，画面分类不变）：
    /// 以「确定/确认/是否」为准——原页固定词里没有这三个词（完成/返回/战术…），命中即有弹窗。
    /// </summary>
    public static bool HasDialogConfirmWords(OcrResult ocr) =>
        ocr.ContainsJoined("确定") || ocr.ContainsJoined("确认") || ocr.ContainsJoined("是否");

    // ---------------- 后备列表暗卡探针（同名在首发/替补名单的卡整体压暗，用户 2026-10-25） ----------------

    /// <summary>列几何（兜底）：5 列卡心 x（实机 2026-10-25 截图 1280×720 归一化）。
    /// 网格会随窗口/预览面板变化（实测两帧列距 0.116 vs 0.097），优先用
    /// <see cref="BenchColumnCenters"/> 从名字行动态推导。</summary>
    private static readonly double[] BenchColumnsFixed = { 0.174, 0.290, 0.405, 0.520, 0.635 };

    /// <summary>行几何：4 行卡顶 y（名字行中心上移 0.085，两帧实测一致）。</summary>
    private static readonly double[] BenchRowTops = { 0.228, 0.389, 0.550, 0.710 };

    /// <summary>
    /// 卡格列心：用第一行球员名字的 OCR 词按 x 间隙聚类推导（名字行 y≈0.31 带、
    /// 右边预览卡区域 x&gt;0.68 排除）。网格几何随窗口变化，固定坐标会采错格
    /// （实机 2026-10-25：第二次进列表列距与截图标定不同 → 探针失效按了暗卡）。
    /// 词数不足返回空数组，由调用方退回固定列。
    /// </summary>
    internal static double[] BenchColumnCenters(OcrResult ocr)
    {
        var words = ocr.Words
            .Where(w => w.CenterY is > 0.28 and < 0.345 && w.CenterX is > 0.08 and < 0.68)
            // 单字符碎片（图标/评分边缘误读，如 'F'@0.436）不算名字词，否则劈出假列
            .Where(w => w.Text.Replace(" ", "").Length >= 2)
            .OrderBy(w => w.CenterX)
            .ToList();
        var groups = new List<(double Lo, double Hi)>();
        foreach (var w in words)
        {
            if (groups.Count > 0 && w.CenterX - groups[^1].Hi < 0.030)
                groups[^1] = (groups[^1].Lo, w.CenterX); // 同名字的第二个词（如 Dodi Lukébakio）
            else
                groups.Add((w.CenterX, w.CenterX));
        }
        return groups.Count >= 3 ? groups.Select(g => (g.Lo + g.Hi) / 2).ToArray() : Array.Empty<double>();
    }

    /// <summary>
    /// 卡格高亮像素数（采样窗 x=cx±half、y=卡顶+0.012 起 0.115 高，含评分数字与头像、
    /// 避开名字行与选中蓝框角）。实机 20 格标定：压暗卡 144/147（连白字都暗），
    /// 亮卡最低 259（深色球衣但鲜艳的卡不受影响——压暗是乘性降光，高光最敏感）。
    /// </summary>
    internal static int CardHighlightPixels(FrameData f, int row, int col, double[] columns)
    {
        if (row < 0 || row >= BenchRowTops.Length || col < 0 || col >= columns.Length) return 0;
        if (f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4) return 0;
        double half = 0.045;
        if (columns.Length >= 2)
        {
            double spacing = columns.Length >= 2 ? columns[1] - columns[0] : 0.116;
            if (spacing > 0) half = Math.Min(0.045, spacing * 0.42);
        }
        int x0 = (int)((columns[col] - half) * f.Width), x1 = (int)((columns[col] + half) * f.Width);
        int y0 = (int)((BenchRowTops[row] + 0.012) * f.Height);
        int y1 = (int)((BenchRowTops[row] + 0.012 + 0.115) * f.Height);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(f.Width, x1); y1 = Math.Min(f.Height, y1);
        int hi = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                int lum = (f.Bgra[i] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i + 2] * 114) / 1000;
                if (lum >= 180) hi++;
            }
        return hi;
    }

    /// <summary>
    /// 第一张未压暗卡的下标（行优先、0 起；0 = 第一张就可用）。同名卡头像整体变暗不可换入
    /// （用户 2026-10-25：第一排左一张、第三排右一张都不能选）。亮 = 高亮占比 ≥ 0.021
    /// （暗卡实测 1.5%、亮卡最低 2.7%）。
    /// 返回 -1 = 网格有效但一张亮卡都没探到（多为列表未渲染完的过场帧）→ 上层等一帧重探；
    /// 返回 0 = 帧整体太黑（黑帧/探针不可用）→ 保守不跳。
    /// </summary>
    public static int FirstBrightCardIndex(OcrResult ocr, FrameData f)
    {
        var cols = BenchColumnCenters(ocr);
        if (cols.Length != 5) cols = BenchColumnsFixed; // 少检/多检列会让行优先下标错位，退回固定列
        long area = (long)(0.09 * f.Width) * (long)(0.115 * f.Height);
        if (area <= 0) return 0;
        int threshold = (int)(area * 21 / 1000); // 0.021
        for (int row = 0; row < BenchRowTops.Length; row++)
            for (int col = 0; col < cols.Length; col++)
                if (CardHighlightPixels(f, row, col, cols) >= threshold)
                    return row * cols.Length + col;
        return FrameMeanLuma(f) < 8 ? 0 : -1;
    }

    /// <summary>整帧平均亮度（每 4 像素采样一次）。</summary>
    private static double FrameMeanLuma(FrameData f)
    {
        if (f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4) return 0;
        long sum = 0; int n = 0;
        for (int i = 0; i < f.Bgra.Length; i += 16)
        {
            sum += (f.Bgra[i] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i + 2] * 114) / 1000;
            n++;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    // ---------------- 详情页等级列文本（供整帧+裁切两源合并解析） ----------------

    /// <summary>整帧「等级」表头正下方的值文本（与 <see cref="ReadParts"/> 同几何）。</summary>
    public static string? ReadLevelText(OcrResult ocr)
    {
        var lvHead = FindHead(ocr, "等级", 0.28);
        return lvHead is null ? null : JoinBelow(ocr, lvHead, FindHead(ocr, "能力点", 0.28), 0.10);
    }

    /// <summary>
    /// 多来源等级解析兜底：每个来源先按整串（<see cref="ParseLevelPair"/>），全部失败再
    /// 跨来源拼半截——实机 2026-10-25 落帧 read_fail_split：整帧 OCR 读到「/32」漏了「1」，
    /// 裁切放大帧读到「1」却丢了「/32」（两帧各读半截）。斜杠后段=上限、无斜杠纯数字=当前，
    /// 逐来源提取后合并。
    /// </summary>
    internal static (int Cur, int Max)? ParseLevelPairLoose(params string?[] sources)
    {
        foreach (var s in sources)
            if (ParseLevelPair(s) is { } p) return p;
        int? cur = null, max = null;
        foreach (var s in sources)
        {
            if (string.IsNullOrEmpty(s)) continue;
            int slash = s.IndexOf('/');
            string before = slash >= 0 ? s[..slash] : s;
            string after = slash >= 0 ? s[(slash + 1)..] : "";
            if (cur is null)
            {
                string bd = new(before.Where(char.IsDigit).ToArray());
                if (bd.Length is > 0 and <= 2 && int.TryParse(bd, out int c) && c <= 99) cur = c;
            }
            if (max is null && after.Length > 0)
            {
                string ad = new(after.Where(char.IsDigit).ToArray());
                if (ad.Length is > 0 and <= 2 && int.TryParse(ad, out int m) && m is > 0 and <= 99) max = m;
            }
        }
        return cur is { } cc && max is { } mm && cc <= mm ? (cc, mm) : null;
    }

    // ---------------- 等级多配置投票（大字细笔画「1」单配置不可信，实机 20261025_11 五帧标定） ----------------

    /// <summary>等级列值区词（「等级」表头正下方 ±0.06 横带内，与 <see cref="ReadParts"/> 同几何）。
    /// 投票的直接帧候选必须限定在该区域——全页数字词（年龄/能力点/评分）会污染票仓。</summary>
    public static IReadOnlyList<OcrWord> LevelValueWords(OcrResult ocr)
    {
        var head = FindHead(ocr, "等级", 0.28);
        if (head is null) return Array.Empty<OcrWord>();
        return ocr.Words
            .Where(w => w != head &&
                        w.CenterY > head.CenterY + 0.005 && w.CenterY <= head.CenterY + 0.10 &&
                        Math.Abs(w.CenterX - head.CenterX) <= 0.06)
            .OrderBy(w => w.CenterX)
            .ToList();
    }

    /// <summary>词级等级候选：单字符 L/J/I/l/i 是细笔画「1」的经典误读归一为 1；
    /// 斜杠（或误读成的':'）后段=上限、纯数字=当前（等级不存在 0，cur 必须 &gt;0）；
    /// 无数字词（标签「用脚」「n」）不出候选。</summary>
    internal static IEnumerable<(int? Cur, int? Max)> LevelCandidatesFromWords(IEnumerable<OcrWord> words)
    {
        foreach (var w in words)
        {
            string t = w.Text.Trim();
            if (t.Length == 0) continue;
            if (t.Length == 1 && "LJIli".Contains(t[0]) && !char.IsDigit(t[0]))
            {
                yield return (1, null);
                continue;
            }
            int sep = t.IndexOfAny(new[] { '/', ':' });
            int? before = sep >= 0 ? DigitsAtMost(t[..sep], 2) : null;
            int? after = sep >= 0 ? DigitsAtMost(t[(sep + 1)..], 2) : null;
            if (sep >= 0)
            {
                if (after is not null && after > 0)
                    yield return (before is > 0 ? before : null, after);
                else if (before is > 0)
                    yield return (before, null);
            }
            else
            {
                int? d = DigitsAtMost(t, 2);
                if (d is > 0) yield return (d, null);
            }
        }
    }

    private static int? DigitsAtMost(string s, int maxLen)
    {
        string d = new(s.Where(char.IsDigit).ToArray());
        return d.Length > 0 && d.Length <= maxLen && int.TryParse(d, out int v) ? v : null;
    }

    /// <summary>候选投票：当前等级取众数（平票取较小——细「1」会被读大成 5/7，取小安全；
    /// 低等级下 1 与 5 的决策同为「未满级」），上限取众数（平票取较大——偏小会伪造满级）。
    /// cur&gt;max 视为读坏：上限作废（交给上层按「等级远低于上限」兜底）。</summary>
    internal static (int Cur, int? Max)? VoteLevel(IEnumerable<(int? Cur, int? Max)> candidates)
    {
        var curs = candidates.Where(c => c.Cur is not null).Select(c => c.Cur!.Value).ToList();
        var maxs = candidates.Where(c => c.Max is not null).Select(c => c.Max!.Value).ToList();
        if (curs.Count == 0) return null;
        int cur = curs.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        int? max = maxs.Count == 0
            ? null
            : maxs.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        if (max is int m && cur > m) max = null; // 5/51 之类碎片毒化：上限作废交给上层兜底
        return (cur, max);
    }

    /// <summary>
    /// 能力点徽标（详情页数据栏 能力点 值区）：有剩余能力点 = 橙色圆圈 + 白字，
    /// 0 = 白字「0」无橙圈。实机 2026-10-25 三帧标定：橙圈 1103 像素、0 点 0 像素，
    /// 颜色判据 r&gt;180 / g 90~200 / b&lt;100 / r−b&gt;120，阈值 150。
    /// 纯像素判定，不依赖 OCR 数字（橙色圈内的「2」被读成 21 的实机误读教训）。
    /// </summary>
    public static bool HasAbilityPointBadge(FrameData f)
    {
        if (f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4) return false;
        int x0 = (int)(0.55 * f.Width), x1 = (int)(0.66 * f.Width);
        int y0 = (int)(0.395 * f.Height), y1 = (int)(0.475 * f.Height);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (r > 180 && g is > 90 and < 200 && b < 100 && r - b > 120) n++;
            }
        return n >= 150;
    }

    // ---------------- 排序/筛选「已设置」探针（中断续跑不重复勾选） ----------------

    /// <summary>
    /// 排序弹窗「整体评价」行是否已有蓝勾（勾 = 该排序键已生效）。实机 2026-10-24
    /// （1280×720 用户截图）：勾在行右端 x≈0.94~0.97，亮蓝 ≈RGB(33,155,243)；
    /// 行文字也是蓝色但都在 x&lt;0.7，探针只看右端不误伤。勾在时配合箭头判断即可
    /// 直接 B 返回（配合 <see cref="IsSortArrowUp"/>）。
    /// ★2026-10-27 用户要求：排序键由「当前等级」改为「整体评价」（更贴合选人目标）。
    /// </summary>
    public static bool IsOverallRatingChecked(OcrResult ocr, FrameData f)
    {
        // 排序行标签整词；OCR 偶尔把「整体评价」拆成「整体」「评价」两个词，
        // 退回到含「整体」的词（限弹窗列 x<0.8，避开右侧球员卡的「整体实力」）
        var head = ocr.Words.FirstOrDefault(w => w.NormText.Contains("整体评价"))
                   ?? ocr.Words.FirstOrDefault(w => w.NormText.Contains("整体") && w.CenterX < 0.8);
        if (head is null || f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4)
            return false;
        int x0 = (int)(f.Width * 0.93), x1 = (int)(f.Width * 0.985);
        int yc = (int)(head.CenterY * f.Height);
        int y0 = Math.Max(0, yc - (int)(f.Height * 0.028));
        int y1 = Math.Min(f.Height, yc + (int)(f.Height * 0.028));
        int blue = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (b > 170 && r < 120 && g > 90 && g < 215) blue++;
            }
        }
        return blue >= 15;
    }

    /// <summary>
    /// 筛选页「卡片类型」行是否已设置（右侧值 ≠「无」，如「精选/高光/高光-Show Time/传奇/史诗/…」）。
    /// 用户 2026-10-24：文字判断放宽，非「无」即可。★实机帧（TestData/swap/filter_cardtype_set）
    /// OCR 会漏读「卡片类型」灰字标签但读得到右侧值词——表头定位不可靠，改用固定行带：
    /// 筛选页第 5 行 y≈0.483（实机 1280×720 归一化，行距 0.066），值列 x∈[0.46,0.82]。
    /// 值词整行漏读按未设置处理（进子页重设，代价是几秒）。
    /// </summary>
    public static bool IsCardTypeFilterSet(OcrResult ocr)
    {
        var joined = string.Concat(ocr.Words
            .Where(w => w.CenterY is > 0.455 and < 0.512 && w.CenterX is > 0.46 and < 0.82)
            .OrderBy(w => w.CenterX)
            .Select(w => w.NormText));
        return joined.Replace("无", "").Length > 0;
    }

    // ---------------- 排序弹窗方向箭头（像素探针） ----------------

    /// <summary>
    /// 排序弹窗右上角方向箭头：↑ = 整体评价升序（列表首位为最低评价，换人目标）。
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
