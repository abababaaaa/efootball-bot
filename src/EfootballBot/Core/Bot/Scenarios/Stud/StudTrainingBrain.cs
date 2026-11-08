using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.Stud;

/// <summary>种马球员培训（购买）涉及的界面。全部用固定词判定，可变球员名/数量不作判据。</summary>
public enum StudScreen
{
    Unknown,
    /// <summary>游戏主菜单（顶部 游戏主菜单/商店/学院/额外选项 + 底部 比赛/我的球队/合约…）。</summary>
    MainMenu,
    /// <summary>合约首页（特殊球员名单/组合包/可自定义物品/普通球员 横向卡片）。</summary>
    ContractHome,
    /// <summary>合约 → 普通球员列表（右上货币，底部 排序/筛选）。</summary>
    ContractPlayerList,
    /// <summary>普通球员列表的「排序」弹窗（位置/整体评价/球员评价/价格 + 取消/顺序 + 右上箭头）。</summary>
    SortPopup,
    /// <summary>普通球员商品详情（底部 X 商品详情 / Y 切换显示 / 签下 A）。</summary>
    ContractDetail,
    /// <summary>付款确认弹窗（使用GP签下这名球员 + 取消）。</summary>
    PaymentConfirm,
    /// <summary>获得球员弹窗（获得:xxx / OK）。</summary>
    AcquiredPopup,
    /// <summary>签下完成后的商品详情（底部 下一步 A，无「签下」「商品详情」）。</summary>
    PostAcquireDetail,
    /// <summary>我的球队首页（比赛计划/球员/球衣/自定义球场）。</summary>
    MyTeamHome,
    /// <summary>全部球员列表（标题 球员 + 右上 储备球员 + 数量）。</summary>
    SquadList,
    /// <summary>全部球员的「动作」侧栏（选择视图/排序/筛选 + 取消）。</summary>
    ActionMenu,
    /// <summary>筛选页（俱乐部球队/类别/…/培育/球员评价 + 完成）。</summary>
    Filter,
    /// <summary>最佳位置子页（搜索将基于任一选定的筛选条件 + GK/CB/…）。</summary>
    PositionFilter,
    /// <summary>培育子页（未完成培育/已完成培育）。</summary>
    TrainingFilter,
    /// <summary>球员评价子页（一颗星…，OCR 可能只读不到星）。</summary>
    RatingFilter,
    /// <summary>培训球员详情（底部 球员动作/切换显示/返回，无「下一步」）。</summary>
    TrainingPlayerDetail,
    /// <summary>球员动作侧栏（等级训练/能力点分配/技巧训练/… + 取消）。</summary>
    PlayerActions,
    /// <summary>技巧训练页（球员技巧/附加技巧/技巧列表 + 关闭）。</summary>
    SkillTraining,
    /// <summary>选择要使用的物品弹窗（选择/随机 + 取消）。</summary>
    SkillItemSelect,
    /// <summary>执行训练课确认弹窗（将使用随机技巧代币 + 取消）。</summary>
    SkillConfirm,
    /// <summary>训练结果弹窗（绿底大字技巧名 + OK）。</summary>
    SkillResult,
}

/// <summary>
/// 种马购买/培训的纯判定：界面分类 + 子页当前高亮行读取。
/// 依据用户 2026-11-08 实机截图（1280×720）：普通球员列表排序弹窗价格行 y≈0.385，
/// 右侧箭头与我的联赛排序弹窗同位（x≈0.92~0.98 / y≈0.04~0.20）；筛选/最佳位置子页
/// 的选中行为纯白底，未选中行为深灰底（x≈0.5~0.8 采样即可区分）。
/// </summary>
public static class StudTrainingBrain
{
    public static StudScreen Classify(OcrResult ocr)
    {
        // 盖层/弹窗优先，避免背景词把当前页判错。
        if (IsPayment(ocr)) return StudScreen.PaymentConfirm;
        if (IsAcquired(ocr)) return StudScreen.AcquiredPopup;
        if (IsSkillResult(ocr)) return StudScreen.SkillResult;
        if (IsSkillItemSelect(ocr)) return StudScreen.SkillItemSelect;
        if (IsSkillConfirm(ocr)) return StudScreen.SkillConfirm;
        if (IsSkillTraining(ocr)) return StudScreen.SkillTraining;
        if (IsPlayerActions(ocr)) return StudScreen.PlayerActions;
        if (IsTrainingPlayerDetail(ocr)) return StudScreen.TrainingPlayerDetail;
        if (IsSortPopup(ocr)) return StudScreen.SortPopup;
        // 子页标题优先于背景行：球员评价子页背景也印着「最佳位置」，
        // 若先判最佳位置会把评价页误判成位置页（2026-11-08 CI 实机帧回归）。
        if (IsTrainingFilter(ocr)) return StudScreen.TrainingFilter;
        if (IsRatingFilter(ocr)) return StudScreen.RatingFilter;
        if (IsPositionFilter(ocr)) return StudScreen.PositionFilter;
        if (IsActionMenu(ocr)) return StudScreen.ActionMenu;
        if (IsPostAcquireDetail(ocr)) return StudScreen.PostAcquireDetail;
        if (IsContractDetail(ocr)) return StudScreen.ContractDetail;
        if (IsSquadList(ocr)) return StudScreen.SquadList;
        if (IsFilter(ocr)) return StudScreen.Filter;
        if (IsContractPlayerList(ocr)) return StudScreen.ContractPlayerList;
        if (IsMyTeamHome(ocr)) return StudScreen.MyTeamHome;
        if (IsContractHome(ocr)) return StudScreen.ContractHome;
        if (IsMainMenu(ocr)) return StudScreen.MainMenu;
        return StudScreen.Unknown;
    }

    public static bool IsMainMenu(OcrResult ocr) =>
        ocr.ContainsJoined("游戏主菜单") && ocr.ContainsJoined("合约") && ocr.ContainsJoined("我的球队");

    public static bool IsContractHome(OcrResult ocr) =>
        ocr.ContainsJoined("合约") &&
        (ocr.ContainsJoined("特殊球员名单") || ocr.ContainsJoined("可自定义物品"));

    public static bool IsContractPlayerList(OcrResult ocr) =>
        ocr.ContainsJoined("普通球员") && ocr.ContainsJoined("排序") && ocr.ContainsJoined("筛选");

    public static bool IsSortPopup(OcrResult ocr) =>
        ocr.ContainsJoined("排序") && ocr.ContainsJoined("价格") && ocr.ContainsJoined("取消");

    public static bool IsContractDetail(OcrResult ocr) =>
        ocr.ContainsJoined("商品详情") && ocr.ContainsJoined("签下");

    public static bool IsPayment(OcrResult ocr) => ocr.ContainsJoined("付款确认");

    public static bool IsAcquired(OcrResult ocr) =>
        ocr.ContainsJoined("获得") && ocr.ContainsJoined("OK");

    public static bool IsPostAcquireDetail(OcrResult ocr) =>
        ocr.ContainsJoined("下一步") && ocr.ContainsJoined("球员动作") &&
        ocr.ContainsJoined("切换显示") && !ocr.ContainsJoined("商品详情");

    public static bool IsMyTeamHome(OcrResult ocr) =>
        ocr.ContainsJoined("我的球队") && ocr.ContainsJoined("比赛计划");

    public static bool IsSquadList(OcrResult ocr) =>
        ocr.ContainsJoined("储备球员") && ocr.ContainsJoined("数量");

    public static bool IsActionMenu(OcrResult ocr) =>
        ocr.ContainsJoined("选择视图") && ocr.ContainsJoined("筛选") && ocr.ContainsJoined("取消");

    public static bool IsFilter(OcrResult ocr) =>
        HasFilterDoneButton(ocr) &&
        (ocr.ContainsJoined("球员筛选") || ocr.ContainsJoined("俱乐部球队"));

    /// <summary>
    /// 筛选页底部「B 返回 / 完成」按钮。不能直接用 ContainsJoined("完成")：
    /// 培育子页的「未完成培育」也含「完成」二字，会把子页误判成筛选页。
    /// </summary>
    public static bool HasFilterDoneButton(OcrResult ocr) =>
        ocr.Words.Any(w => w.CenterY > 0.85 && w.NormText.EndsWith("完成", StringComparison.Ordinal));

    public static bool IsPositionFilter(OcrResult ocr) =>
        ocr.ContainsJoined("最佳位置") && ocr.ContainsJoined("搜索将基于任一选定的筛选条件");

    public static bool IsTrainingFilter(OcrResult ocr) =>
        !HasFilterDoneButton(ocr) &&
        (ocr.ContainsJoined("未完成培育") || ocr.ContainsJoined("已完成培育"));

    public static bool IsSkillResult(OcrResult ocr) =>
        ocr.ContainsJoined("OK") && !ocr.ContainsJoined("获得") &&
        (ocr.ContainsJoined("技巧列表") || ocr.ContainsJoined("技巧训练") || ocr.ContainsJoined("使")) &&
        !ocr.ContainsJoined("选择要使用的物品") && !ocr.ContainsJoined("执行训练课") &&
        !ocr.ContainsJoined("付款确认");

    public static bool IsSkillItemSelect(OcrResult ocr) =>
        ocr.ContainsJoined("选择要使用的物品");

    public static bool IsSkillConfirm(OcrResult ocr) =>
        ocr.ContainsJoined("执行训练课");

    public static bool IsSkillTraining(OcrResult ocr) =>
        ocr.ContainsJoined("技巧训练") && ocr.ContainsJoined("附加技巧") &&
        (ocr.ContainsJoined("技巧列表") || ocr.ContainsJoined("关闭"));

    public static bool IsPlayerActions(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") && ocr.ContainsJoined("技巧训练") &&
        ocr.ContainsJoined("取消");

    public static bool IsTrainingPlayerDetail(OcrResult ocr) =>
        ocr.ContainsJoined("球员动作") && ocr.ContainsJoined("切换显示") &&
        !ocr.ContainsJoined("下一步") && !ocr.ContainsJoined("商品详情") &&
        !ocr.ContainsJoined("技巧训练") && !ocr.ContainsJoined("取消");

    /// <summary>
    /// 技巧训练页第一个「附加技巧」槽是否为空（蓝色加号）。实机 2026-11-08：
    /// 第一格中心 x≈0.557 / y≈0.324，加号为高蓝低红；选中边框在四角，不进入中心探针区。
    /// </summary>
    public static bool HasEmptyFirstSkillSlot(FrameData f) => SkillSlotBlueCounts(f)[0] >= 25;

    /// <summary>
    /// 任意一个「附加技巧」槽是否显示蓝色加号。实机 2026-11-08 反馈：第一格已有技巧时，
    /// 游戏会把光标自动移到下一个空槽（第二格变蓝框 + 蓝色加号）。只探第一格会误判
    /// 「第一格不是加号」并把所有球员跳过，因此改为扫描 5 个槽位。
    /// </summary>
    public static bool HasEmptySkillSlot(FrameData f) => SkillSlotBlueCounts(f).Any(c => c >= 25);

    /// <summary>5 个附加技巧槽中心区域的蓝色像素数（排障日志用；顺序=左上/右上/左中/右中/左下）。</summary>
    public static int[] SkillSlotBlueCounts(FrameData f) => new[]
    {
        CountBlue(f, 0.574, 0.300), CountBlue(f, 0.790, 0.300),
        CountBlue(f, 0.574, 0.535), CountBlue(f, 0.790, 0.535),
        CountBlue(f, 0.574, 0.765),
    };

    private static int CountBlue(FrameData f, double cx, double cy)
    {
        if (f.Width < 200 || f.Height < 120 || f.Bgra.Length < f.Width * f.Height * 4) return 0;
        int x0 = (int)(f.Width * (cx - 0.056)), x1 = (int)(f.Width * (cx + 0.056));
        int y0 = (int)(f.Height * (cy - 0.065)), y1 = (int)(f.Height * (cy + 0.065));
        int blue = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(f.Height, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(f.Width, x1); x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (b > 170 && g > 110 && r < 125 && b - r > 80) blue++;
            }
        }
        return blue;
    }

    /// <summary>
    /// 训练结果弹窗里「较大的文字」= 技巧名。取 y 0.46~0.70 内字高最大的一行；
    /// 描述文字（使…踢出…）字高明显更小，背景球员名已被 y 下限排除。
    /// </summary>
    public static string? ReadSkillResultName(OcrResult ocr)
    {
        var line = ocr.Lines
            .Where(l => l.CenterY is > 0.46 and < 0.70 && l.H >= 0.030)
            .Where(l => !l.Text.Contains("OK", StringComparison.OrdinalIgnoreCase) &&
                        !l.Text.Contains("技巧列表", StringComparison.Ordinal))
            .OrderByDescending(l => l.H)
            .ThenByDescending(l => l.W)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line?.Text) ? null : line!.Text;
    }

    /// <summary>
    /// 全部球员列表右上「数量:9/900」。OCR 常把冒号和斜杠吞掉（如「球员数量9900储备球员」），
    /// 统一取数字串：末 3 位为容量 900，前面的数字为当前数量。
    /// </summary>
    public static int? ReadVisiblePlayerCount(OcrResult ocr)
    {
        var head = ocr.Words.FirstOrDefault(w => w.NormText.Contains("数量"))
                   ?? ocr.Words.FirstOrDefault(w => w.NormText.Contains("储备球员"));
        if (head is null) return null;
        string digits = new(head.NormText.Where(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        string countPart = digits[..^3];
        if (!int.TryParse(countPart, out int count)) return null;
        return Math.Clamp(count, 0, 900);
    }

    public static bool IsRatingFilter(OcrResult ocr) =>
        ocr.ContainsJoined("球员评价") && !HasFilterDoneButton(ocr) && !ocr.ContainsJoined("价格");

    /// <summary>
    /// 读取右侧子页当前高亮（白底）行的文字。最佳位置/培育/球员评价子页的选中行为纯白，
    /// 其余行为深灰；采样行中心上下若干点，平均亮度 &gt; 170 判为高亮。读不出返回 null。
    /// </summary>
    public static string? SelectedRightRowText(OcrResult ocr, FrameData frame)
    {
        if (frame.Width < 200 || frame.Height < 120 ||
            frame.Bgra.Length < frame.Width * frame.Height * 4) return null;

        foreach (var line in ocr.Lines.Where(l => l.CenterY is > 0.12 and < 0.90)
                                      .OrderBy(l => l.CenterY))
        {
            if (!IsBrightBand(frame, line.CenterY, line.H)) continue;
            var words = ocr.Words
                .Where(w => w.CenterX > 0.30 && Math.Abs(w.CenterY - line.CenterY) < 0.025)
                .OrderBy(w => w.CenterX)
                .Select(w => w.NormText)
                .ToArray();
            string text = string.Concat(words);
            if (text.Length > 0) return text;
        }
        return null;
    }

    private static bool IsBrightBand(FrameData f, double cy, double h)
    {
        int y0 = Math.Clamp((int)((cy - Math.Max(0.012, h * 0.45)) * f.Height), 0, f.Height - 1);
        int y1 = Math.Clamp((int)((cy + Math.Max(0.012, h * 0.45)) * f.Height), y0 + 1, f.Height);
        int lit = 0, total = 0;
        foreach (double fx in new[] { 0.52, 0.66, 0.80 })
        {
            int x = Math.Clamp((int)(fx * f.Width), 0, f.Width - 1);
            for (int y = y0; y < y1; y++)
            {
                int i = (y * f.Width + x) * 4;
                int lum = (f.Bgra[i] * 29 + f.Bgra[i + 1] * 150 + f.Bgra[i + 2] * 77) >> 8;
                if (lum >= 170) lit++;
                total++;
            }
        }
        return total > 0 && lit * 100 / total >= 55;
    }
}
