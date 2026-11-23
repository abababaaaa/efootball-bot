using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 比赛页（大活动页）磁贴行的 OCR 动态定位（共享层）：从画面文字认出各磁贴标题并按 x 排序，
/// 返回目标磁贴是第几格（0 起）。
///
/// 背景：磁贴行会随服务器活动增减（用户实机 2026-11-22：活动结束后第一格消失，
/// 「eFootball联赛」从第 3 卡左移到第 2 卡），任何写死"第 N 卡"的导航都会在活动轮换后按错。
/// 周活动/AI天梯都靠这个共享能力实时定位，读不全时返回 null，调用方退回原扫描，绝不瞎猜。
/// </summary>
public static class HubTileLocator
{
    /// <summary>磁贴标题（大字印在卡片下半部；描述句不是整词，不参与）。</summary>
    private static readonly string[] Titles = { "锦标赛", "活动", "我的联赛" };

    /// <summary>
    /// 目标磁贴在磁贴行的序号（0 起，按屏幕从左到右）。读不全/找不到返回 null。
    /// </summary>
    /// <param name="target">目标标题，如「活动」「eFootball联赛」（归一后的形式）。</param>
    public static int? FindTileOffset(OcrResult ocr, string target)
    {
        var tiles = CollectTiles(ocr);
        if (tiles.Count < 2) return null; // 读不全不猜格位
        tiles.Sort((a, b) => a.X.CompareTo(b.X));
        int idx = tiles.FindIndex(t => t.Title == target);
        return idx >= 0 ? idx : null;
    }

    /// <summary>
    /// 这一帧认出几枚磁贴标题。用途：判断"画面真的是比赛页磁贴行"——从别的页（如锦标赛子页）
    /// 按 B 退回时，单看分类可能仍判 MatchHub，但那一页只有一枚「锦标赛」标题，凑不出磁贴行。
    /// </summary>
    public static int VisibleTitleCount(OcrResult ocr) => CollectTiles(ocr).Count;

    /// <summary>磁贴行里能认出的标题（x 升序未排序；同一格的碎词/合并整行只留第一次命中）。</summary>
    private static List<(double X, string Title)> CollectTiles(OcrResult ocr)
    {
        var tiles = new List<(double X, string Title)>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY is < 0.40 or > 0.90 || w.CenterX > 0.99) continue;
            string? title = NormalizeTitle(w.NormText);
            if (title is null) continue;
            // 同一格的碎词/合并整行只保留第一次命中
            if (tiles.Any(t => Math.Abs(t.X - w.CenterX) < 0.02)) continue;
            tiles.Add((w.CenterX, title));
        }
        return tiles;
    }

    /// <summary>磁贴标题归一：「eFootball™联赛」被拆写时也归为「eFootball联赛」。</summary>
    private static string? NormalizeTitle(string norm)
    {
        if (Titles.Contains(norm)) return norm;
        if (norm.Contains("eFootball", StringComparison.OrdinalIgnoreCase) && norm.Contains("联赛"))
            return "eFootball联赛";
        return null;
    }

    // ---------------- 主菜单底部磁贴行定位 ----------------
    /// <summary>主菜单底部卡片行的固定标签词（用户实机 2026-11-23 截图：
    /// 比赛 / 我的球队 / 合约 / 任务 / 每日小游戏，横向排列，文字在图标下方）。</summary>
    private static readonly string[] MainMenuLabels =
        { "比赛", "我的球队", "合约", "任务", "每日小游戏" };

    /// <summary>
    /// 主菜单「比赛」磁贴在底部卡片行的格位（0 起，按屏幕从左到右）。
    /// 只在底部行 y≈0.62~0.92 且认出 ≥3 个不同标签时才可信；否则返回 null 让调用方
    /// 回退原「下×2→左×6」饱和归位——主菜单标签 OCR 偶发读不全，绝不瞎猜格位。
    /// </summary>
    public static int? FindMainMenuMatchOffset(OcrResult ocr)
    {
        var tiles = new List<(double X, string Label)>();
        foreach (var w in ocr.Words)
        {
            // 底部卡片行：标签文字在图标下方，实机 y≈0.75~0.88；放宽到 0.62~0.92
            if (w.CenterY is < 0.62 or > 0.92) continue;
            string norm = w.NormText.Replace(" ", "");
            // 精确匹配固定标签（"比赛"须整词，避免"比赛记录/前往比赛"混入——那些 y 更高）
            string? label = MainMenuLabels.FirstOrDefault(l => norm == l);
            if (label is null) continue;
            if (tiles.Any(t => Math.Abs(t.X - w.CenterX) < 0.02)) continue;
            tiles.Add((w.CenterX, label));
        }
        // 交叉验证：至少认出 3 个不同标签，才认为这帧的底部行读全了
        if (tiles.Select(t => t.Label).Distinct().Count() < 3) return null;
        tiles.Sort((a, b) => a.X.CompareTo(b.X));
        int idx = tiles.FindIndex(t => t.Label == "比赛");
        return idx >= 0 ? idx : null;
    }
}
