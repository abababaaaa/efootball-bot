using System.Text.RegularExpressions;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· CnWeeklyBrain 判定
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `CnWeeklyBrain.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

public static partial class CnWeeklyBrain
{
    // 本文件小活动：赛事等级/难度 —— 难度卡片行与选中态判定。

    // ---------------- 选择比赛难度页（2026-10-17 实机 cn_tour_difficulty.png） ----------------
    /// <summary>难度卡名前缀 → 规范难度名（MatchBrain.Difficulties 同表）。国服卡名与国际服
    /// 用词可能有别（如 顶尖球[星/球员]），按前缀归一到规范名供配置比对。</summary>
    private static readonly (string Prefix, string Name)[] DifficultyPrefixes =
    {
        ("新手", "新手"), ("业余", "业余"), ("标准", "标准"), ("职业", "职业"),
        ("顶尖球", "顶尖球员"), ("超级球", "超级球星"), ("传奇", "传奇"),
    };

    /// <summary>OCR 词 → 规范难度名（卡头行难度名词；匹配不到返回 null）。</summary>
    public static string? MatchDifficultyWord(OcrWord w)
    {
        string t = w.Text.Replace(" ", "");
        foreach (var (prefix, name) in DifficultyPrefixes)
            if (t.Contains(prefix) && t.Length <= prefix.Length + 2) return name;
        return null;
    }

    /// <summary>难度卡行兜底特征：同一行（y 0.25~0.45，实机卡头行）≥3 个不同难度名 +
    /// 底栏「确定」。仅单难度词（球员评价等偶然出现）不构成卡行。</summary>
    private static bool HasCnDifficultyCardRow(OcrResult ocr)
    {
        var names = new HashSet<string>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < 0.25 || w.CenterY > 0.45) continue;
            var name = MatchDifficultyWord(w);
            if (name is not null) names.Add(name);
        }
        return names.Count >= 3 && ocr.ContainsJoined("确定");
    }

    /// <summary>
    /// 检测当前选中的难度卡（用户 2026-10-17 实机 cn_tour_difficulty）：光标所在卡
    /// 白底高亮、其余卡深灰底——对每张卡的难度名词采样其下方卡体区平均亮度，
    /// 相对阈值（中位数+22，下限 112）取最亮过线者；全不过线 = 未检出（调用方按
    /// 游戏当前值直接确认，绝不盲导航）。
    /// </summary>
    public static string? DetectSelectedCnDifficulty(FrameData frame, OcrResult ocr)
    {
        var bgra = frame.Bgra;
        int w = frame.Width, h = frame.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        double RegionAvg(double x0, double x1, double y0, double y1)
        {
            long sum = 0; int n = 0;
            for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
                for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
                {
                    int i = (y * w + x) * 4;
                    sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? 0 : (double)sum / n;
        }
        var scores = new List<(string Name, double Avg)>();
        foreach (var word in ocr.Words)
        {
            var name = MatchDifficultyWord(word);
            if (name is null || scores.Any(s => s.Name == name)) continue;
            // 卡体采样：难度名下方（y +0.16~+0.26，避开描述文字行），x 取名词周围卡宽
            double avg = RegionAvg(word.CenterX - 0.06, word.CenterX + 0.06,
                                   word.CenterY + 0.16, word.CenterY + 0.26);
            scores.Add((name, avg));
        }
        if (scores.Count < 2) return null;
        var sorted = scores.Select(s => s.Avg).OrderBy(v => v).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double threshold = Math.Max(112, median + 22);
        var best = scores.OrderByDescending(s => s.Avg).First();
        return best.Avg >= threshold ? best.Name : null;
    }
}
