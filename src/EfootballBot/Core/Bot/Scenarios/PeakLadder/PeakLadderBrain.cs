using System.Text.RegularExpressions;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.PeakLadder;

/// <summary>国服天梯巅峰赛界面（PeakLadderBrain 判定）。None = 非本模块界面，交给 MyLeagueBrain 分类。</summary>
public enum PlScreen
{
    /// <summary>非天梯巅峰赛界面（比赛中/终场/共用弹窗/未知过场等）。</summary>
    None,
    /// <summary>主页·比赛页签（顶部 推荐|比赛|签约|商店 + 比赛活动/天梯巅峰赛/我的联盟/好友对战 卡片）。</summary>
    HomeMatch,
    /// <summary>主页·推荐页签（活动中心/我的球队 等磁贴；进比赛页签需先右移一次）。</summary>
    HomeRecommended,
    /// <summary>模式选择页（页头 剩余时间 + 天梯赛/天梯巅峰赛 × 手动/教练 四卡；本页无手柄导航，仅鼠标可点、B 返回）。</summary>
    ModeSelect,
    /// <summary>教练天梯巅峰赛活动主页（前往比赛 + 本周可参赛场次 + 比赛记录/排名）。</summary>
    ActivityHome,
    /// <summary>「请选择参赛模式」弹窗（单场比赛/连续比赛/取消，手柄光标默认在取消）。</summary>
    ModeDialog,
}

/// <summary>
/// 国服天梯巅峰赛决策大脑（菜单侧）：界面分类 + 场次解析 + 鼠标点击锚定。纯函数，可单测。
/// 判定只用固定 UI 词（天梯巅峰赛/剩余时间/本周可参赛场次/请选择参赛模式/单场比赛…）；
/// 段位名（新手联赛/大师联赛）、积分、赛季数、剩余天数每周变动，一律不作判定点（可变内容禁令）。
/// 比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用 MyLeagueBrain。
/// </summary>
public static class PeakLadderBrain
{
    // ---------------- 兜底点击坐标（实机 1280×720 校准，2026-10-14） ----------------

    /// <summary>模式选择页右下「天梯巅峰赛-教练」卡中心（鼠标点入教练模式活动）。</summary>
    public const double CoachCardFallbackX = 0.720;
    public const double CoachCardFallbackY = 0.703;

    /// <summary>活动主页左上「前往比赛」卡中心。</summary>
    public const double GoMatchFallbackX = 0.143;
    public const double GoMatchFallbackY = 0.244;

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    // ---------------- 界面分类 ----------------

    /// <summary>
    /// 界面分类。判定顺序：参赛模式弹窗 → 活动主页 → 模式选择页 → 主页（比赛页签/推荐页签）。
    /// 活动主页标题「教练天梯巅峰赛」与模式选择页四卡都含「天梯巅峰赛」、主页头部也含
    /// 「活动剩余时间」，因此必须按此顺序互斥（弹窗的「当前剩余参赛场次」无「本周」前缀不误判主页）。
    /// </summary>
    public static PlScreen Classify(OcrResult ocr)
    {
        // 参赛模式弹窗：标题独有词；或 单场+连续 两选项同屏
        if (ocr.ContainsJoined("请选择参赛模式") ||
            (ocr.ContainsJoined("单场比赛") && ocr.ContainsJoined("连续比赛")))
            return PlScreen.ModeDialog;

        // 活动主页：「本周可参赛场次」独有；前往比赛+比赛记录 兜底（OCR 漏读数字行时）
        if (ocr.ContainsJoined("本周可参赛场次") ||
            (ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("比赛记录")))
            return PlScreen.ActivityHome;

        // 模式选择页：页头「剩余时间:18天」+ 天梯巅峰赛（活动主页已先行排除）
        if (ocr.ContainsJoined("剩余时间") && ocr.ContainsJoined("天梯巅峰赛"))
            return PlScreen.ModeSelect;

        // 主页：顶部页签行（同一行 y<0.15 含 推荐/签约/商店），按内容词分页签
        if (HasTopTabs(ocr))
        {
            if (ocr.ContainsJoined("好友对战") || ocr.ContainsJoined("我的联盟") ||
                ocr.ContainsJoined("我的联赛") || ocr.ContainsJoined("比赛活动"))
                return PlScreen.HomeMatch;
            if (ocr.ContainsJoined("活动中心") || ocr.ContainsJoined("我的球队"))
                return PlScreen.HomeRecommended;
        }

        return PlScreen.None;
    }

    /// <summary>顶部页签行通用特征（共享实现见 <see cref="RegionProbe.HasTopTabs"/>）。</summary>
    public static bool HasTopTabs(OcrResult ocr) => RegionProbe.HasTopTabs(ocr);

    // ---------------- 场次解析 ----------------

    /// <summary>
    /// 解析本周可参赛场次：活动主页「本周可参赛场次: 40」行，或弹窗「当前剩余参赛场次: 40」行
    /// （点开前往比赛后的弹窗二次确认用）。取行内最后一串数字；无行/无数字返回 null（绝不猜）。
    /// </summary>
    public static int? ParsePlayableCount(OcrResult ocr)
    {
        foreach (var line in ocr.Lines)
        {
            if (!line.Text.Contains("本周可参赛场次") && !line.Text.Contains("当前剩余参赛场次"))
                continue;
            MatchCollection ms = Digits.Matches(line.Text);
            if (ms.Count > 0 && int.TryParse(ms[^1].Value, out int n))
                return n;
        }
        return null;
    }

    // ---------------- 鼠标点击锚定 ----------------

    /// <summary>
    /// 在模式选择页 OCR 中找右下「天梯巅峰赛-教练」卡标题词：取含「天梯巅峰赛」且
    /// CenterX&gt;0.5（排除左侧「-手动」卡）、y 在卡区（0.45~0.88）的词。整行合并词会横跨
    /// 左右两卡（CenterX≈0.42），天然被排除。找不到返回 null（场景层用兜底坐标）。
    /// </summary>
    public static OcrWord? FindCoachCardWord(OcrResult ocr)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY < 0.45 || w.CenterY > 0.88) continue;
            if (w.CenterX <= 0.5) continue;
            if (w.NormText.Contains("天梯巅峰赛")) return w;
        }
        return null;
    }

    /// <summary>
    /// 在活动主页 OCR 中找「前往比赛」卡标题词（y&lt;0.45 左栏；同分取最左——活动主页左列
    /// 只有前往比赛卡含此词）。找不到返回 null（场景层用兜底坐标）。
    /// </summary>
    public static OcrWord? FindGoMatchWord(OcrResult ocr)
    {
        OcrWord? best = null;
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY >= 0.45) continue;
            if (!w.NormText.Contains("前往比赛")) continue;
            if (best is null || w.CenterX < best.CenterX) best = w;
        }
        return best;
    }
}
