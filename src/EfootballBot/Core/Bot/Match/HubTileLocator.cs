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
        if (tiles.Count < 2) return null; // 读不全不猜格位
        tiles.Sort((a, b) => a.X.CompareTo(b.X));
        int idx = tiles.FindIndex(t => t.Title == target);
        return idx >= 0 ? idx : null;
    }

    /// <summary>磁贴标题归一：「eFootball™联赛」被拆写时也归为「eFootball联赛」。</summary>
    private static string? NormalizeTitle(string norm)
    {
        if (Titles.Contains(norm)) return norm;
        if (norm.Contains("eFootball", StringComparison.OrdinalIgnoreCase) && norm.Contains("联赛"))
            return "eFootball联赛";
        return null;
    }
}
