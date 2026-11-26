using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· CnWeeklyBrain 判定
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `CnWeeklyBrain.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

/// <summary>赛后「活动内升级奖励」的三块面板（UI 从左到右固定顺序，也是同分时的优先级：
/// 前锋 &gt; 中场 &gt; 后场）。</summary>
public enum CwRewardSlot
{
    Forward = 0,
    Midfield = 1,
    Backfield = 2,
}

/// <summary>一块面板的定位结果：标题词（「前锋捆绑包」/「前锋球员等级提升」）中心 + 是否来自 OCR。</summary>
public readonly record struct CwRewardPanel(double X, double Y, bool FromOcr);

/// <summary>选择结论（纯数据，供场景层点击与验证）。</summary>
/// <param name="Slot">选中的面板。</param>
/// <param name="PanelX">该面板中心的归一化 X（验证「已选」蓝底用）。</param>
/// <param name="ClickX">鼠标点击点（面板标题词中心）。</param>
/// <param name="ClickY">鼠标点击点（面板标题词中心）。</param>
/// <param name="Value">用于比较的数值：阵容奖励=两卡评级之和，特殊奖励=面板上的等级数字。</param>
/// <param name="Detail">六卡/三卡明细（写日志用，现场据此核对读得对不对）。</param>
/// <param name="Complete">是否所有参与比较的字段都读全了（false = 有字读不出，结论仍可用但需留意）。</param>
public sealed record CwRewardChoice(CwRewardSlot Slot, double PanelX, double ClickX, double ClickY,
    int Value, string Detail, bool Complete);

public static partial class CnWeeklyBrain
{
    // 本文件小活动：赛后「活动内升级奖励」两页（2026-11-25 国服实机）。
    //   ① 阵容奖励：三块面板（前锋/中场/后场捆绑包）+ 每块两块球员卡，卡底印评级字母 A/B/C/D。
    //      规则（用户 2026-11-25 定）：六卡评级两两求和，选分最高的那一组；同分 前锋 > 中场 > 后场。
    //   ② 特殊奖励：三块面板（球员等级提升）+ 卡底「球员等级提升至 N 级」。
    //      规则：N>40 的一律不选，剩下选 N 最小的；同数 前锋 > 中场 > 后场。
    // 两页都靠「鼠标点面板标题」选中（手柄方向键在这两页不通），选中态 = 整块卡面变深蓝 + 打勾。

    /// <summary>评级字母白名单（卡底右下角那枚大字；S 目前未在实机出现，预留）。</summary>
    private const string GradeLetters = "ABCDS";

    /// <summary>面板中心 X 的兜底几何（1280×720 实机帧标定：三块面板 120..447 / 495..805 / 828..1158）。</summary>
    private static readonly double[] RewardPanelX = { 0.2215, 0.508, 0.776 };

    /// <summary>面板标题行的 Y 兜底（「前锋捆绑包」/「前锋球员等级提升」中心 y≈0.271）。</summary>
    private const double RewardTitleY = 0.271;

    /// <summary>评级字母所在的行（y≈0.710）与左右两卡的横向偏移（相对面板中心）。</summary>
    private const double GradeY = 0.710;

    private const double GradeLeftDx = -0.059;

    private const double GradeRightDx = 0.060;

    /// <summary>特殊奖励里「不选」的等级上限（用户 2026-11-25：大于 40 的一律不选）。</summary>
    public const int LevelRewardMaxSelectable = 40;

    /// <summary>「已选」蓝底判定阈值（蓝底帧该区域约 1700+ 命中，白底约 0；见 RegionProbe.CountBlue）。</summary>
    private const int ChosenBlueRef = 300;

    // ---------------- 两页的界面判定 ----------------

    /// <summary>
    /// 「阵容奖励」选择页：固定词 阵容奖励 / 阵容强化奖励 + 三块面板标题「xx 捆绑包」。
    /// 不用卡名（奥利·沃特金斯…）、不用球员评分数字——那些每周轮换（可变内容禁令）。
    /// </summary>
    public static bool IsSquadRewardPick(OcrResult ocr)
    {
        int bundles = ocr.MergedWords.Count(w => w.NormText.Contains("捆绑包"));
        if (bundles >= 2) return true;
        bool title = ocr.ContainsJoined("阵容奖励") || ocr.ContainsJoined("阵容强化奖励");
        return title && bundles >= 1;
    }

    /// <summary>
    /// 「特殊奖励」选择页：固定词 特殊奖励 / 等级强化奖励 + 卡面「球员等级提升（至 N 级）」。
    /// 与「阵容奖励」页互斥（那页没有「等级提升」，这页没有「捆绑包」），不会互相误判。
    /// <para>
    /// ★2026-11-25 实机补充第二种形态（单卡版，同一页的另一套奖励）：标题仍是「特殊奖励」，
    /// 三块面板标题写成「前场球员 / 中场球员 / 后场球员」（不是「xx 球员等级提升」），每块只印
    /// 一张球员卡（卡面左上角球员总评 84/80/81、卡底评级字母），底栏「选择一个阵容强化奖励」。
    /// 旧判据只认「等级提升」→ 这页判 None → 被共享层当 LeagueInfoPage，鼠标在下一步上空点
    /// 三千多次、整场卡死（用户 2026-11-25 实机）。
    /// </para>
    /// </summary>
    public static bool IsLevelRewardPick(OcrResult ocr)
    {
        bool title = ocr.ContainsJoined("特殊奖励") || ocr.ContainsJoined("等级强化奖励");
        if (!title) return false;
        if (ocr.MergedWords.Any(w => w.NormText.Contains("等级提升"))) return true;
        // 单卡版：≥2 块「xx球员」面板标题 + 底栏「选择一个阵容强化奖励」，且没有「捆绑包」
        // （有「捆绑包」是六卡版的阵容奖励页，归 IsSquadRewardPick）
        bool hasBundle = ocr.MergedWords.Any(w => w.NormText.Contains("捆绑包"));
        int playerPanels = ocr.MergedWords.Count(w =>
            w.CenterY is > 0.18 and < 0.35 && w.NormText.Contains("球员"));
        return !hasBundle && playerPanels >= 2 && ocr.ContainsJoined("阵容强化奖励");
    }

    /// <summary>
    /// 选完两页奖励后连弹的两个领取结果弹窗：①「新球员加入！/二位球员已经加入你的挑战阵容」
    /// ②「奖励已发放至收件箱」+ 金币。两者都是单键「确定」，背景是活动主页（前往比赛/通关奖励
    /// 都能读到）——不先认出来会被主页分支按 A 前往比赛，弹窗永远关不掉。
    /// </summary>
    public static bool IsRewardResultPopup(OcrResult ocr)
    {
        bool title = ocr.ContainsJoined("新球员加入") || ocr.ContainsJoined("加入你的挑战阵容") ||
                     ocr.ContainsJoined("已发放至收件箱") || ocr.ContainsJoined("奖励已发放");
        if (!title) return false;
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY < 0.55) continue;  // 弹窗按钮在下半屏；上方的页眉/说明不算
            string t = w.NormText;
            if (t.Length is >= 2 and <= 4 && (t.StartsWith("确定", StringComparison.Ordinal) ||
                                              t.StartsWith("确认", StringComparison.Ordinal) ||
                                              t.StartsWith("確定", StringComparison.Ordinal)))
                return true;
            if (t.Equals("OK", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------------- 面板定位 ----------------
    /// <summary>
    /// 三块面板的标题位置。优先用 OCR 读到的标题词（含「前锋/中场/后场」前缀即按名归位），
    /// 读不到的那块退回固定几何——两页布局一致，固定几何与 OCR 值只差几个千分点。
    /// </summary>
    /// <param name="ocr">当前帧 OCR。</param>
    /// <param name="titleKeyword">标题关键词：阵容奖励页用「捆绑包」，特殊奖励页用「等级提升」。</param>
    public static CwRewardPanel[] Panels(OcrResult ocr, string titleKeyword)
    {
        var found = new (double X, double Y)?[3];
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY < 0.18 || w.CenterY > 0.35) continue; // 标题行（卡底「提升至 N 级」在 0.65，不吃）
            string t = w.NormText;
            if (!t.Contains(titleKeyword, StringComparison.Ordinal)) continue;
            int slot = (t.Contains("前锋") || t.Contains("前场")) ? 0
                     : t.Contains("中场") ? 1
                     : (t.Contains("后场") || t.Contains("后卫")) ? 2 : -1;
            if (slot < 0) continue;
            found[slot] ??= (w.CenterX, w.CenterY);
        }
        var result = new CwRewardPanel[3];
        for (int s = 0; s < 3; s++)
            result[s] = found[s] is { } p
                ? new CwRewardPanel(p.X, p.Y, true)
                : new CwRewardPanel(RewardPanelX[s], RewardTitleY, false);
        return result;
    }

    // ---------------- 评级字母（阵容奖励） ----------------

    /// <summary>评级字母 → 分数（求和用：A=4 B=3 C=2 D=1 S=5）。未知字母记 0。</summary>
    public static int GradeScore(char? grade) => grade switch
    {
        'S' => 5,
        'A' => 4,
        'B' => 3,
        'C' => 2,
        'D' => 1,
        _ => 0,
    };

    /// <summary>
    /// 读一块面板里第 index 张卡（0=左 1=右）的评级字母。
    /// <b>颜色优先</b>：实机帧标定 A=青(16,229,247)/B=绿(14,230,35)/C=黄绿(222,231,15)/D=橙(254,194,21)，
    /// 四者色相相隔很远，比字形识别稳（OCR 常把单字母读丢或读成符号）；颜色读不出时才用 OCR 单字母。
    /// </summary>
    public static char? ReadGrade(OcrResult ocr, FrameData? frame, double panelX, int index)
    {
        double cx = panelX + (index == 0 ? GradeLeftDx : GradeRightDx);
        char? byColor = frame is null ? null : GradeByColor(frame, cx, GradeY);
        if (byColor is not null) return byColor;
        return ReadGradeWord(ocr, cx);
    }

    /// <summary>区域主色 → 评级字母（只在字母窗口内找高饱和像素，背景白/深蓝都被饱和度筛掉）。</summary>
    public static char? GradeByColor(FrameData f, double cx, double cy)
    {
        if (f.Width < 100 || f.Height < 100 || f.Bgra.Length < f.Width * f.Height * 4) return null;
        int x0 = Math.Max(0, (int)(f.Width * (cx - 0.030)));
        int x1 = Math.Min(f.Width, (int)(f.Width * (cx + 0.030)));
        int y0 = Math.Max(0, (int)(f.Height * (cy - 0.022)));
        int y1 = Math.Min(f.Height, (int)(f.Height * (cy + 0.022)));
        Span<int> counts = stackalloc int[5]; // 下标：0=D 1=C 2=B 3=A 4=S(未用)
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                int mx = Math.Max(r, Math.Max(g, b));
                if (mx < 120) continue;                       // 太暗：黑描边/暗背景
                int mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn < mx * 0.55) continue;            // 低饱和：白底、灰影、深蓝底都不算
                switch (HueClass(r, g, b))
                {
                    case 'D': counts[0]++; break;
                    case 'C': counts[1]++; break;
                    case 'B': counts[2]++; break;
                    case 'A': counts[3]++; break;
                }
            }
        char[] chars = { 'D', 'C', 'B', 'A', '\0' };
        int best = -1, bestCount = 0;
        for (int k = 0; k < 4; k++)
            if (counts[k] > bestCount) { best = k; bestCount = counts[k]; }
        if (best < 0) return null;
        // 字母笔画在 1280×720 上约 300~460 像素；换算到基准分辨率后低于 60 视为噪声
        return RegionProbe.ToReference(bestCount, f) >= 60 ? chars[best] : null;
    }

    /// <summary>色相分段（实机帧标定，段间留空档拒噪声）：D 橙 28~52 / C 黄绿 52~92 / B 绿 108~156 / A 青 168~208。</summary>
    private static char HueClass(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b));
        int mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn;
        if (d <= 0) return '\0';
        double h;
        if (mx == r) h = 60 * (((g - b) / d) % 6);
        else if (mx == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        if (h >= 28 && h < 52) return 'D';
        if (h >= 52 && h < 92) return 'C';
        if (h >= 108 && h < 156) return 'B';
        if (h >= 168 && h < 208) return 'A';
        return '\0';
    }

    /// <summary>
    /// OCR 兜底：字母行（y 0.64~0.78）里离期望 X 最近的单字母词。
    /// </summary>
    public static char? ReadGradeWord(OcrResult ocr, double cx)
    {
        char? best = null;
        double bestDx = 0.035;
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < 0.64 || w.CenterY > 0.78) continue;
            string t = w.NormText.Trim().ToUpperInvariant();
            if (t.Length != 1 || !GradeLetters.Contains(t[0])) continue;
            double dx = Math.Abs(w.CenterX - cx);
            if (dx >= bestDx) continue;
            bestDx = dx;
            best = t[0];
        }
        return best;
    }

    /// <summary>
    /// 整行兜底：字母行里读到 6 枚单字母时，按 X 从左到右两两分组（不依赖面板几何，
    /// 窗口比例/分辨率变化导致期望 X 偏移时仍能读全六卡）。
    /// </summary>
    public static char?[]? ReadGradeRow(OcrResult ocr)
    {
        var row = ocr.Words
            .Where(w => w.CenterY >= 0.64 && w.CenterY <= 0.78)
            .Select(w => (w.CenterX, Text: w.NormText.Trim().ToUpperInvariant()))
            .Where(x => x.Text.Length == 1 && GradeLetters.Contains(x.Text[0]))
            .OrderBy(x => x.CenterX)
            .ToList();
        if (row.Count != 6) return null;  // 多/少都不猜：数量不对说明读到别的单字母了
        var result = new char?[6];
        for (int i = 0; i < 6; i++) result[i] = row[i].Text[0];
        return result;
    }

    /// <summary>
    /// 阵容奖励决策：六卡评级两两求和 → 选分最高的一组；同分按 前锋 &gt; 中场 &gt; 后场。
    /// 只有一块面板两张卡都读全才参与「完整」比较；都不完整时退一步用已读出的评级相加（Complete=false），
    /// 全读不出返回 null（调用方按重试处理，不盲点）。
    /// </summary>
    public static CwRewardChoice? PlanSquadReward(OcrResult ocr, FrameData? frame)
    {
        var panels = Panels(ocr, "捆绑包");
        var row = ReadGradeRow(ocr); // 六枚单字母可读时按 X 两两分组，作为期望位置读不出时的兜底
        var scores = new int[3];
        var complete = new bool[3];
        var desc = new string[3];
        for (int s = 0; s < 3; s++)
        {
            char? l = ReadGrade(ocr, frame, panels[s].X, 0) ?? row?[s * 2];
            char? r = ReadGrade(ocr, frame, panels[s].X, 1) ?? row?[s * 2 + 1];
            scores[s] = GradeScore(l) + GradeScore(r);
            complete[s] = l is not null && r is not null;
            desc[s] = $"{SlotName((CwRewardSlot)s)} {l?.ToString() ?? "?"}+{r?.ToString() ?? "?"}={scores[s]}";
        }
        int pick = -1;
        bool allComplete = false;
        for (int pass = 0; pass < 2 && pick < 0; pass++)
        {
            bool needComplete = pass == 0;
            for (int s = 0; s < 3; s++)
            {
                if (needComplete ? !complete[s] : scores[s] <= 0) continue;
                // 严格大于才换：同分保留下标小的 = 前锋 &gt; 中场 &gt; 后场
                if (pick < 0 || scores[s] > scores[pick]) pick = s;
            }
            if (pick >= 0) allComplete = needComplete;
        }
        if (pick < 0) return null;
        var p = panels[pick];
        return new CwRewardChoice((CwRewardSlot)pick, p.X, p.X, p.Y, scores[pick],
            string.Join(" | ", desc), allComplete);
    }

    // ---------------- 等级数字（特殊奖励） ----------------

    /// <summary>
    /// 读一块面板卡底的「球员等级提升至 N 级」里的 N。
    /// 只在该面板 x±0.13、y 0.60~0.70 的带内找数字词（面板间距 0.286，不会串门）；
    /// 词可能被 OCR 拆成「球员等级提升至」+「26级」，也可能并成一整行，两种都能取到。
    /// </summary>
    public static int? ReadLevelNumber(OcrResult ocr, double panelX)
    {
        int? best = null;
        double bestDx = double.MaxValue;
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < 0.60 || w.CenterY > 0.70) continue;
            double dx = Math.Abs(w.CenterX - panelX);
            if (dx > 0.13) continue;
            var m = Regex.Match(w.NormText, @"(\d{1,3})");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int v) || v <= 0) continue;
            if (dx >= bestDx) continue;
            bestDx = dx;
            best = v;
        }
        return best;
    }

    /// <summary>
    /// 读一块面板里那张卡的**球员总评**（单卡版「特殊奖励」：卡面左上角那枚 2 位数，实机 84/80/81）。
    /// 窗口取 panelX±0.09 / y 0.35~0.46（实机帧：84 @(0.190,0.397)、80 @(0.468,0.397)、81 @(0.746,0.397)，
    /// 面板中心 0.221/0.499/0.777，横向偏移恒为 -0.031，不会串门）。只认 40~110 的整数，别把评级/星级当总评。
    /// </summary>
    public static int? ReadCardRating(OcrResult ocr, double panelX)
    {
        int? best = null;
        double bestDx = double.MaxValue;
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < 0.35 || w.CenterY > 0.46) continue;
            double dx = Math.Abs(w.CenterX - panelX);
            if (dx > 0.09) continue;
            var m = Regex.Match(w.NormText.Trim(), @"^(\d{2,3})$");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int v) || v < 40 || v > 110) continue;
            if (dx >= bestDx) continue;
            bestDx = dx;
            best = v;
        }
        return best;
    }

    /// <summary>
    /// 特殊奖励决策，两种形态：
    /// <list type="number">
    /// <item>卡底有「球员等级提升至 N 级」→ N &gt; 40 的一律不选，剩下的选 N 最小的；同数 前锋 &gt; 中场 &gt; 后场。</item>
    /// <item>单卡版（2026-11-25 实机，卡底没印等级、只印球员总评）→ **选总评最高的**；同分仍按
    ///   前锋 &gt; 中场 &gt; 后场（用户 2026-11-25 定：单个球员也先看评分，同评分优先级不变）。</item>
    /// </list>
    /// 两套都读不出返回 null——调用方重试一拍，仍读不出再按「前锋」兜底，绝不空点乱选。
    /// </summary>
    public static CwRewardChoice? PlanLevelReward(OcrResult ocr, int maxSelectable = LevelRewardMaxSelectable)
    {
        // 两形态的面板标题都含「球员」（「前锋球员等级提升」/「前场球员」），用同一个关键词定位
        var panels = Panels(ocr, "球员");
        var nums = new int?[3];
        for (int s = 0; s < 3; s++) nums[s] = ReadLevelNumber(ocr, panels[s].X);

        // ---- 形态 A：等级数字 ----
        if (nums.Any(n => n is not null))
        {
            var descA = new string[3];
            for (int s = 0; s < 3; s++)
                descA[s] = $"{SlotName((CwRewardSlot)s)} {(nums[s]?.ToString() ?? "?")}级";
            int pickA = -1;
            for (int s = 0; s < 3; s++)
            {
                if (nums[s] is not { } v || v > maxSelectable) continue;
                // 严格小于才换：同数保留下标小的 = 前锋 > 中场 > 后场
                if (pickA < 0 || v < nums[pickA]!.Value) pickA = s;
            }
            if (pickA < 0) return null;
            var pA = panels[pickA];
            return new CwRewardChoice((CwRewardSlot)pickA, pA.X, pA.X, pA.Y, nums[pickA]!.Value,
                string.Join(" | ", descA), nums.All(n => n is not null));
        }

        // ---- 形态 B：单卡版按球员总评 ----
        var ratings = new int?[3];
        for (int s = 0; s < 3; s++) ratings[s] = ReadCardRating(ocr, panels[s].X);
        var descB = new string[3];
        for (int s = 0; s < 3; s++)
            descB[s] = $"{SlotName((CwRewardSlot)s)} 总评{ratings[s]?.ToString() ?? "?"}";
        int pickB = -1;
        for (int s = 0; s < 3; s++)
        {
            if (ratings[s] is not { } v) continue;
            // 严格大于才换：同分保留下标小的 = 前锋 > 中场 > 后场
            if (pickB < 0 || v > ratings[pickB]!.Value) pickB = s;
        }
        if (pickB < 0) return null;
        var pB = panels[pickB];
        return new CwRewardChoice((CwRewardSlot)pickB, pB.X, pB.X, pB.Y, ratings[pickB]!.Value,
            string.Join(" | ", descB) + "（卡面总评）", ratings.All(r => r is not null));
    }

    /// <summary>
    /// 该面板是否已选中：卡面中上部由白 (255,255,255) 变成深蓝 (0,0,151)（勾/黄边是附加特征）。
    /// 区域取标题行下方的纯底带（0.28~0.33），避开图形与评级字母。
    /// </summary>
    public static bool IsRewardChosen(FrameData? frame, double panelX)
    {
        if (frame is null || frame.Width < 100 || frame.Height < 100) return false;
        int blue = RegionProbe.CountBlueRef(frame, panelX - 0.10, panelX + 0.10, 0.280, 0.330);
        return blue >= ChosenBlueRef;
    }

    /// <summary>面板中文名（日志用）。</summary>
    public static string SlotName(CwRewardSlot slot) => slot switch
    {
        CwRewardSlot.Forward => "前锋",
        CwRewardSlot.Midfield => "中场",
        _ => "后场",
    };

    /// <summary>
    /// 兜底结论：三拍都读不出评级/等级时按优先级取前锋（同分优先级里的第一位），
    /// 保证流程不停在原地——宁可少拿一点也绝不能卡死在奖励页。
    /// </summary>
    public static CwRewardChoice FallbackChoice()
        => new(CwRewardSlot.Forward, RewardPanelX[0], RewardPanelX[0], RewardTitleY, 0,
            "评级/等级未读出（按优先级兜底选前锋）", false);
}
