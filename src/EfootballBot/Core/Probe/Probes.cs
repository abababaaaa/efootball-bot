using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Probe;

/// <summary>探针基类。输出原始分 rawScore（阈值只基于原始值）。</summary>
public abstract class Probe
{
    public abstract double Score(EfootballBot.Core.Bot.Observation obs);
}

/// <summary>OCR 关键词探针。按排版行计分：关键词在每条行至多累加一次 weight；
/// 命中区域内额外加 0.5。不直接在 MergedWords 上计分（原始词与合并行会双重计数）。</summary>
public sealed class OcrProbe : Probe
{
    public string Text { get; init; } = "";
    public double Weight { get; init; } = 1.0;
    public NormRect? Region { get; init; }
    public bool Exact { get; init; }

    /// <summary>
    /// 按原始词逐个计数（用于按钮/标签，如同排并列的多个「进入」按钮）；
    /// 默认 false 按排版行计数（同一行内关键词只计一次，防止行内重复累计）。
    /// </summary>
    public bool CountWords { get; init; }

    public override double Score(EfootballBot.Core.Bot.Observation obs)
    {
        double score = 0;
        if (CountWords)
        {
            foreach (var w in obs.Ocr.Words)
            {
                if (!OcrText.Norm(w.Text).Contains(Text, StringComparison.OrdinalIgnoreCase)) continue;
                double gain = Weight;
                if (Region is NormRect r && r.Contains(w.CenterX, w.CenterY)) gain += 0.5;
                score += gain;
            }
            return score;
        }
        foreach (var line in obs.Ocr.FindLines(Text))
        {
            double gain = Weight;
            if (Region is NormRect r && r.Contains(line.CenterX, line.CenterY)) gain += 0.5;
            score += gain;
        }
        return score;
    }

    public override string ToString() => $"ocr:{Text}";
}

/// <summary>
/// 视觉颜色占比探针。输出 ROI 内通过 HSV 过滤的像素占比（0~1）。
/// 可选护栏（为菜单白字误触机器人 ROI 而设，沿用旧分类器校准值）：
/// MaxOcrWords——本帧 OCR 词数超过即归零；MaxRatio——颜色占比超过即归零。
/// </summary>
public sealed class VisualProbe : Probe
{
    public HsvFilter Filter { get; init; }
    public NormRect Roi { get; init; }
    public double MinRatio { get; init; }
    public double Weight { get; init; } = 1.0;
    public int? MaxOcrWords { get; init; }
    public double? MaxRatio { get; init; }

    public override double Score(EfootballBot.Core.Bot.Observation obs)
    {
        if (obs.Frame is null) return 0;
        if (MaxOcrWords is int maxWords && obs.Ocr.Words.Count > maxWords) return 0;

        double ratio = FrameAnalyzer.ColorRatio(obs.Frame, Filter, Roi);
        if (ratio < MinRatio) return 0;
        if (MaxRatio is double maxRatio && ratio > maxRatio) return 0;
        return ratio * Weight;
    }

    public override string ToString() => $"visual:{Filter}";
}

/// <summary>
/// 顶部记分牌比赛时钟探针（21:34 / 45:00 / 90:34）。冒号常被 OCR 拆成独立词或漏识，
/// 先把顶部词按 Y 聚类成行再按 X 拼接：读法 1 行内拼出 \d{1,2}[:：]\d{2}；
/// 读法 2 两个相邻 2 位纯数字词、x 间隙 ≤0.012（比分是 1 位数不会误中）。
/// 移植自旧分类器（Screens.cs），命中给 3 分。
/// </summary>
public sealed class ClockHudProbe : Probe
{
    private static readonly Regex Clock = new(@"\d{1,2}[:：]\d{2}", RegexOptions.Compiled);

    public override double Score(EfootballBot.Core.Bot.Observation obs)
    {
        var topWords = obs.Ocr.Words.Where(w => w.Y < 0.18).OrderBy(w => w.CenterY).ToList();
        var rows = new List<List<OcrWord>>();
        foreach (var w in topWords)
        {
            List<OcrWord>? hit = null;
            foreach (var r in rows)
            {
                double rowH = r.Average(x => x.H);
                if (Math.Abs(r.Average(x => x.CenterY) - w.CenterY) <= Math.Max(rowH, w.H) * 0.55)
                { hit = r; break; }
            }
            if (hit is null) rows.Add(new List<OcrWord> { w });
            else hit.Add(w);
        }
        bool clock = rows.Any(r =>
        {
            var byX = r.OrderBy(x => x.X).ToList();
            string joined = string.Concat(byX.Select(x => x.Text));
            if (Clock.IsMatch(joined)) return true;
            for (int k = 0; k + 1 < byX.Count; k++)
            {
                var a = byX[k]; var b = byX[k + 1];
                if (a.Text.Length == 2 && b.Text.Length == 2
                    && a.Text.All(char.IsDigit) && b.Text.All(char.IsDigit)
                    && b.X - (a.X + a.W) <= 0.012)
                    return true;
            }
            return false;
        });
        return clock ? 3 : 0;
    }

    public override string ToString() => "clock-hud";
}
