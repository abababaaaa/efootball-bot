using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision;

/// <summary>
/// OCR 难读文本调优（2027 新 UI 美术字 / 描边字 / 碎词化）。
///
/// 默认实例 = 全关闭 = 历史行为逐字节一致：既有 500+ 项 RealFrame 回归都固化在
/// 「整帧一次 det + 每框独立 rec」这条链上，任何默认值变化都必须先过 A/B 基准。
/// 开关分两类：
///   · 检测参数（Det*）：传给 SimdPaddle 的 DBNet 选项，治「碎框」「大字漏框」；
///   · 行级重读（Line*）：对聚成行的碎词做裁切放大重识别，治「碎词化 + 繁体化」。
/// </summary>
public sealed class OcrTuning
{
    /// <summary>生产默认（不调参、不重读）。</summary>
    public static readonly OcrTuning Off = new();

    // —— 检测参数（null = 用 Sdcb 默认值）——

    /// <summary>DBNet 二值化阈值。Sdcb 默认 0.2；调低提召回（细笔画、描边字更易过线）。</summary>
    public float? DetBitmapThreshold { get; init; }

    /// <summary>文本框置信阈值。Sdcb 默认 0.45；调低收进更弱的框。</summary>
    public float? DetBoxThreshold { get; init; }

    /// <summary>框外扩比例。Sdcb 默认 1.4；调大让框包住描边，减少「一个字切成两半」。</summary>
    public float? DetUnclipRatio { get; init; }

    /// <summary>检测阶段长边上限。Sdcb 默认 960——1280/1920 的帧在检测时被压到 960，
    /// 小字与美术字在这里先掉一轮信息。调大即用更多原始分辨率换时间。</summary>
    public int? DetLimitSideLength { get; init; }

    /// <summary>检测前是否做膨胀。Sdcb 默认 false；对细笔画/描边字可提高粘连召回。</summary>
    public bool? DetUseDilation { get; init; }

    // —— 行级重读 ——

    /// <summary>开启行级重读：碎词行 → 整行包围盒裁切 + 整数倍放大 → 重识别 → 整行替换。</summary>
    public bool LineReread { get; init; }

    /// <summary>重读放大倍数（整数最近邻放大，沿用手写重读已验证的 ×8 思路，默认 ×2 控开销）。</summary>
    public int LineRereadScale { get; init; } = 2;

    /// <summary>裁切外扩比例（按行高）：描边字/渐变字紧裁会把笔画切掉，必须留边。</summary>
    public double LineRereadPadRatio { get; init; } = 0.35;

    /// <summary>单帧最多重读多少行（控时间预算）。</summary>
    public int LineRereadMaxLines { get; init; } = 12;

    /// <summary>只重读「碎词行」：词数 ≥ 此值才算碎（1 = 连单词行也试）。</summary>
    public int LineRereadMinWords { get; init; } = 2;

    /// <summary>
    /// 只重读**逐字碎词**行（默认开）：行内平均词长 ≤ 1 才动手。
    /// A/B 实测（survey/ocr_hardtext_ab_20261028.md）说明这条是必须的——放宽到"任何多词行"
    /// 会把相邻 UI 元素（日志行 + 按钮角标）一起合并，耗时翻倍且引入错误合并。
    /// </summary>
    public bool LineRereadRequireFragments { get; init; } = true;

    public bool IsOff =>
        DetBitmapThreshold is null && DetBoxThreshold is null && DetUnclipRatio is null
        && DetLimitSideLength is null && DetUseDilation is null && !LineReread;
}

/// <summary>
/// 行级重读：把「det 切碎的词」合成一行整块重新识别。
///
/// 碎词化是美术字最大的伤害源——det 把「前往比赛」切成 4 个框后，每个框单独送 rec，
/// 没有左右上下文，繁体化/乱码就是这么来的。做法是拿行包围盒（外扩留边）裁切放大后
/// 再喂一次同样的引擎，若整块读出的规范化文本比原来的碎片拼接**更长**，就用整块结果
/// 替换掉该行的碎片词。规则保守（只做加法，不看置信度），保证不会把读对的行改坏。
/// </summary>
public static class LineReread
{
    /// <summary>一行重读的结果统计（诊断/基准用）。</summary>
    public readonly record struct Stats(int LinesConsidered, int LinesReread, int LinesChanged);

    /// <param name="baseResult">整帧识别的结果（坐标归一化到 <paramref name="frame"/>）。</param>
    /// <param name="frame">整帧识别时用的帧（与 baseResult 同尺寸口径）。</param>
    /// <param name="recognize">同一个引擎的识别入口（对裁切图再跑一次）。</param>
    public static async Task<(OcrResult Result, Stats Stats)> ApplyAsync(
        OcrResult baseResult, FrameData frame, Func<FrameData, CancellationToken, Task<OcrResult>> recognize,
        OcrTuning tuning, CancellationToken ct)
    {
        var words = baseResult.Words;
        if (words.Count < tuning.LineRereadMinWords || frame.Width <= 0 || frame.Height <= 0)
            return (baseResult, default);

        var lines = GroupLines(words);
        var replaced = new HashSet<OcrWord>();
        var added = new List<OcrWord>();
        int considered = 0, reread = 0, changed = 0;

        foreach (var line in lines)
        {
            if (line.Count < tuning.LineRereadMinWords) continue;
            if (reread >= tuning.LineRereadMaxLines) break;
            // 只治逐字碎词：行内平均规范化词长 ≤ 1 说明 det 是按字切开的
            if (tuning.LineRereadRequireFragments)
            {
                double avgLen = line.Average(w => w.NormText.Length);
                if (avgLen > 1.0) continue;
            }
            considered++;

            double x0 = line.Min(w => w.X), x1 = line.Max(w => w.X + w.W);
            double y0 = line.Min(w => w.Y), y1 = line.Max(w => w.Y + w.H);
            double h = y1 - y0;
            if (h <= 0 || x1 <= x0) continue;

            // 外扩留边：描边字/渐变字的笔画会跑到框外，紧裁等于自己切自己
            double padY = h * tuning.LineRereadPadRatio;
            double padX = h * tuning.LineRereadPadRatio;
            double cx0 = Math.Clamp(x0 - padX, 0, 1), cx1 = Math.Clamp(x1 + padX, 0, 1);
            double cy0 = Math.Clamp(y0 - padY, 0, 1), cy1 = Math.Clamp(y1 + padY, 0, 1);

            OcrResult sub;
            try
            {
                reread++;
                var crop = ImageScaling.CropUpscale(frame, cx0, cx1, cy0, cy1, tuning.LineRereadScale,
                    out int offX, out int offY, out int cropW, out int cropH);
                ct.ThrowIfCancellationRequested();
                sub = await recognize(crop, ct).ConfigureAwait(false);

                string oldText = string.Concat(line.OrderBy(w => w.X).Select(w => w.NormText));
                string newText = string.Concat(sub.Words.OrderBy(w => w.X).Select(w => w.NormText));
                // 只接受「读出了更多字」的重读：碎词合并的最直接证据就是规范化文本变长。
                // 相等或更短说明整块重认没有带来信息，保留原碎片（避免用更差的整块覆盖好词）。
                if (newText.Length <= oldText.Length || sub.Words.Count == 0) continue;

                foreach (var w in line) replaced.Add(w);
                added.AddRange(sub.Words.Select(w => MapBack(w, offX, offY, cropW, cropH, frame)));
                changed++;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 单行重读失败绝不能影响整帧结果：保留原碎片继续
            }
        }

        if (changed == 0) return (baseResult, new Stats(considered, reread, 0));

        var merged = new List<OcrWord>(words.Count + added.Count);
        foreach (var w in words)
            if (!replaced.Contains(w)) merged.Add(w);
        merged.AddRange(added);
        return (new OcrResult(merged, baseResult.Width, baseResult.Height),
            new Stats(considered, reread, changed));
    }

    /// <summary>裁切图上的归一化词坐标 → 整帧归一化坐标。</summary>
    private static OcrWord MapBack(OcrWord w, int offX, int offY, int cropW, int cropH, FrameData frame)
    {
        double px = offX + w.X * cropW;
        double py = offY + w.Y * cropH;
        double pw = w.W * cropW;
        double ph = w.H * cropH;
        return new OcrWord(w.Text,
            Math.Clamp(px / frame.Width, 0, 1),
            Math.Clamp(py / frame.Height, 0, 1),
            Math.Clamp(pw / frame.Width, 0, 1),
            Math.Clamp(ph / frame.Height, 0, 1));
    }

    /// <summary>与 <see cref="OcrResult.Lines"/> 完全一致的行聚类（中心 Y 邻近 + 高度容差）。</summary>
    internal static List<List<OcrWord>> GroupLines(IReadOnlyList<OcrWord> words)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l.Average(x => x.CenterY) - w.CenterY) < Math.Max(w.H, l.Average(x => x.H)) * 0.7);
            if (line is null) lines.Add(new List<OcrWord> { w });
            else line.Add(w);
        }
        return lines;
    }
}
