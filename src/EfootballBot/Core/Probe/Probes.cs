using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Probe;

/// <summary>探针基类。输出原始分 rawScore（阈值只基于原始值）。</summary>
public abstract class Probe
{
    public abstract double Score(EfootballBot.Core.Bot.Observation obs);
}

/// <summary>OCR 关键词探针。命中词累加 weight；命中区域内额外加 0.5。</summary>
public sealed class OcrProbe : Probe
{
    public string Text { get; init; } = "";
    public double Weight { get; init; } = 1.0;
    public NormRect? Region { get; init; }
    public bool Exact { get; init; }

    public override double Score(EfootballBot.Core.Bot.Observation obs)
    {
        double score = 0;
        foreach (var w in obs.Ocr.FindAll(Text))
        {
            double gain = Weight;
            if (Region is NormRect r && r.Contains(w.CenterX, w.CenterY)) gain += 0.5;
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
