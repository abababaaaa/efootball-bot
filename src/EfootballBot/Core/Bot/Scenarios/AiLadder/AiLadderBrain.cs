using System.Text.RegularExpressions;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.AiLadder;

/// <summary>国际服 AI 天梯联赛界面（AiLadderBrain 判定）。None = 非本模块界面，交给周活动/我的联赛链分类。</summary>
public enum AlScreen
{
    /// <summary>非本模块界面（比赛中/终场/共用弹窗/比赛页/主菜单等）。</summary>
    None,
    /// <summary>eFootball™联赛 类别选择页（对阵AI类别 / 对阵玩家类别 两卡；卡片右下角组别角标数字/颜色会变，不作判定点）。</summary>
    CategorySelect,
    /// <summary>组别主页（左上 组别X + 前往比赛 + 比赛场次 + 组别分布）。</summary>
    DivisionHome,
}

/// <summary>
/// AI 天梯联赛决策大脑（菜单侧）：界面分类 + 组别数字解析。纯函数，可单测。
/// 判定只用固定 UI 词（对阵AI类别/组别分布/比赛场次/前往比赛…）；卡片图片、角标数字、
/// 阶段日期、积分、组别分布百分比全部可变，一律不作判定点（可变内容禁令）。
/// 比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用 MyLeagueBrain。
/// </summary>
public static class AiLadderBrain
{
    /// <summary>「组别+数字」相邻出现（如 组别8）。OCR 偶发繁体「組别」，一并兼容。</summary>
    private static readonly Regex DivisionWord = new("[组組]别(\\d+)", RegexOptions.Compiled);

    // ---------------- 界面分类 ----------------

    /// <summary>
    /// 界面分类。判定顺序：组别主页 → 类别选择页。
    /// 组别主页先判：其左上角 logo 与奖励行都可能出现「eFootball™联赛」字样；
    /// 类别选择页的兜底（顶部标题行 eFootball™联赛）必须排除主页词透出误判。
    /// </summary>
    public static AlScreen Classify(OcrResult ocr)
    {
        // 组别主页：「前往比赛」+ 组别特征词组任一（组别分布/比赛场次/当前积分/左上组别X）。
        // 我的联赛主页有前往比赛但无这组词，与 LeaguePage 区分；比赛页卡片描述
        // 「争取升级至更高组别!」无前往比赛不会命中。
        if (ocr.ContainsJoined("前往比赛") &&
            (ocr.ContainsJoined("组别分布") || ocr.ContainsJoined("比赛场次") ||
             ocr.ContainsJoined("当前积分") || ParseDivision(ocr) is not null))
            return AlScreen.DivisionHome;

        // 类别选择页：两卡标题带「类别」后缀。周活动列表页的 对阵AI/对阵玩家 页签无此后缀，
        // 以此互斥（两页共用 对阵AI/对阵玩家 词，裸词绝不能作判定点）。
        if (ocr.ContainsJoined("对阵AI类别") || ocr.ContainsJoined("对阵玩家类别") ||
            ocr.ContainsJoined("AI类别") || ocr.ContainsJoined("玩家类别"))
            return AlScreen.CategorySelect;

        // 兜底：页面标题「eFootball™联赛」在顶部（y<0.2）。比赛页 hub 的同名词是卡片标签
        // （y≈0.7）不会命中；组别主页顶部无中文「联赛」（logo 是英文 EFOOTBALL LEAGUE）。
        bool topTitle = ocr.Lines.Any(l => l.CenterY < 0.2 && l.Text.Contains("联赛"));
        bool topBrand = ocr.MergedWords.Any(w => w.CenterY < 0.2 &&
            OcrText.Norm(w.Text).Contains("eFootball", StringComparison.OrdinalIgnoreCase));
        if (topTitle && topBrand)
            return AlScreen.CategorySelect;

        return AlScreen.None;
    }

    // ---------------- 组别解析 ----------------

    /// <summary>
    /// 解析组别主页左上角的当前组别（组别8 → 8；10 最低、1 最高）。
    /// 只认「组别+数字」相邻且位置在左上（y&lt;0.22 / x&lt;0.45）的词：右栏「升级至组别7」
    /// （y≈0.29、x≈0.83）、组别分布列表徽章（y&gt;0.3）都被位置过滤排除。
    /// 找不到/越界返回 null（绝不猜——组别是停止判据，误读会误停）。
    /// </summary>
    public static int? ParseDivision(OcrResult ocr)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY >= 0.22 || w.CenterX >= 0.45) continue;
            var m = DivisionWord.Match(OcrText.Norm(w.Text));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int d) && d is >= 1 and <= 10)
                return d;
        }
        return null;
    }
}
