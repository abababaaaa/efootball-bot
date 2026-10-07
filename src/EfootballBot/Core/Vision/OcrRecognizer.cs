using EfootballBot.Core.Capture;
using System.IO;
using RapidOcrNet;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace EfootballBot.Core.Vision;

/// <summary>OCR 词块，坐标已归一化到 0~1。</summary>
public sealed record OcrWord(string Text, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

/// <summary>一条排版行：行内词按 X 排序后的规范化拼接文本 + 整行包围盒。</summary>
public sealed record OcrLine(string Text, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

public sealed record OcrResult(IReadOnlyList<OcrWord> Words, int Width, int Height)
{
    public static readonly OcrResult Empty = new(Array.Empty<OcrWord>(), 0, 0);

    private IReadOnlyList<OcrLine>? _lines;

    /// <summary>
    /// 按排版行聚合（每个词属于唯一一行），行文本为行内词 Norm 拼接。
    /// 页面探针在此之上匹配：关键词在每条行至多命中一次，避免「原始词 + 包含它的合并行」
    /// 双重计分（MergedWords 同时含原始词与合并词，会把同行单词算两遍）。
    /// </summary>
    public IReadOnlyList<OcrLine> Lines => _lines ??= BuildLines();

    private IReadOnlyList<OcrLine> BuildLines()
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in Words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l.Average(x => x.CenterY) - w.CenterY) < Math.Max(w.H, l.Average(x => x.H)) * 0.7);
            if (line is null) lines.Add(new List<OcrWord> { w });
            else line.Add(w);
        }
        return lines.Select(line =>
        {
            var ordered = line.OrderBy(w => w.X).ToList();
            double x0 = ordered.Min(w => w.X), y0 = ordered.Min(w => w.Y);
            double x1 = ordered.Max(w => w.X + w.W), y1 = ordered.Max(w => w.Y + w.H);
            return new OcrLine(string.Concat(ordered.Select(w => OcrText.Norm(w.Text))),
                x0, y0, x1 - x0, y1 - y0);
        }).ToList();
    }

    /// <summary>在排版行中找关键词子串（每条行至多一个命中）。</summary>
    public IEnumerable<OcrLine> FindLines(string normalizedKeyword)
    {
        foreach (var l in Lines)
            if (l.Text.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                yield return l;
    }

    private IReadOnlyList<OcrWord>? _merged;

    /// <summary>同一行的碎词合并成整行（Windows OCR 常把中文逐字拆开）。</summary>
    public IReadOnlyList<OcrWord> MergedWords => _merged ??= MergeLines();

    private IReadOnlyList<OcrWord> MergeLines()
    {
        var result = new List<OcrWord>(Words);
        // 按行分组：中心 Y 接近且高度相近的词归为一行
        var lines = new List<List<OcrWord>>();
        foreach (var w in Words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l.Average(x => x.CenterY) - w.CenterY) < Math.Max(w.H, l.Average(x => x.H)) * 0.7);
            if (line is null) lines.Add(new List<OcrWord> { w });
            else line.Add(w);
        }
        foreach (var line in lines)
        {
            if (line.Count < 2) continue;
            var ordered = line.OrderBy(w => w.X).ToList();
            string text = string.Concat(ordered.Select(w => OcrText.Norm(w.Text)));
            if (text.Length < 2) continue;
            double x0 = ordered.Min(w => w.X), y0 = ordered.Min(w => w.Y);
            double x1 = ordered.Max(w => w.X + w.W), y1 = ordered.Max(w => w.Y + w.H);
            result.Add(new OcrWord(text, x0, y0, x1 - x0, y1 - y0));
        }
        return result;
    }

    public IEnumerable<OcrWord> FindAll(string normalizedKeyword)
    {
        foreach (var w in MergedWords)
            if (OcrText.Norm(w.Text).Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                yield return w;
    }

    public bool Contains(string normalizedKeyword) => FindAll(normalizedKeyword).Any();

    private string? _joined;

    /// <summary>所有原始词按阅读顺序拼接并规范化（无分隔），用于匹配被逐字拆开的短词，
    /// 如「回」「放」实际是「回放」、「比」「赛」「回」「放」是「比赛回放」。
    /// 拼接前先按 Y 聚类成行：避免同一行某字 Y 微抖 1px 就被全局 Y 排序打乱词序。</summary>
    public string JoinedText => _joined ??= BuildJoinedText();

    private string BuildJoinedText()
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in Words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l.Average(x => x.CenterY) - w.CenterY) < Math.Max(w.H, l.Average(x => x.H)) * 0.7);
            if (line is null) lines.Add(new List<OcrWord> { w });
            else line.Add(w);
        }
        return string.Concat(lines
            .OrderBy(l => l.Average(w => w.CenterY))
            .SelectMany(l => l.OrderBy(w => w.X).Select(w => OcrText.Norm(w.Text))));
    }

    /// <summary>在全文拼接里找子串（不要求关键词落在同一个 OCR 词内）。</summary>
    public bool ContainsJoined(string normalizedKeyword)
        => JoinedText.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);

    public bool ContainsAny(IEnumerable<string> normalizedKeywords)
        => normalizedKeywords.Any(Contains);

    public OcrWord? FindFirst(string normalizedKeyword) => FindAll(normalizedKeyword).FirstOrDefault();

    /// <summary>
    /// 按词找按钮：纯拉丁字母短词（ok/skip/yes/no/next 等）要求整词精确相等，
    /// 避免「Opoku」「NGoloKante」含子串「ok」被误当按钮（真机 2026-10-06 故障）；
    /// 中文等关键词仍走子串包含（OCR 常把图标误识字拼到词尾，如「跳过參」）。
    /// </summary>
    public IEnumerable<OcrWord> FindButtonWords(string normalizedKeyword)
    {
        bool exact = normalizedKeyword.Length > 0 && normalizedKeyword.All(char.IsAsciiLetter);
        foreach (var w in MergedWords)
        {
            string t = OcrText.Norm(w.Text);
            bool hit = exact
                ? t.Equals(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
                : t.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
            if (hit) yield return w;
        }
    }

    /// <summary>按钮词匹配的第一个命中（规则见 <see cref="FindButtonWords"/>）。</summary>
    public OcrWord? FindButton(string normalizedKeyword)
        => FindButtonWords(normalizedKeyword).FirstOrDefault();
}

/// <summary>文字规范化，对抗 OCR 的空格 / 全半角 / 常见误识。</summary>
public static class OcrText
{
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new global::System.Text.StringBuilder(s.Length);
        foreach (char ch in s.Normalize(global::System.Text.NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            sb.Append(ch);
        }
        var r = sb.ToString();
        foreach (var (bad, good) in Replacements)
            r = r.Replace(bad, good);
        return r;
    }

    // 仅保留 PP-OCRv6 实测仍会出现的误识（82 帧真机 dump 中 45 帧把「前往比赛」识成
    // 「前往比賽」）。以下旧 Windows OCR 时代的规则在 v6 下 0 命中，已于本次删除：
    // 我的聏赛/我的聏、联賽/聯賽、关键比賽/關鍵比賽、兌换/兌換、傑出、教練、經驗、
    // 設置、賽程、積分、森破/皋破/皋分/森分、惠品/愚品、道具(自映射)、確認、返囘、
    // 下ー步、新(自映射)、前往比(自映射)。真机日志出现新误识时再按数据补规则。
    internal static readonly (string Bad, string Good)[] Replacements =
    {
        ("前往比賽", "前往比赛"),
    };
}

/// <summary>OCR 识别后端。</summary>
public enum OcrEngineKind
{
    /// <summary>RapidOCR PP-OCRv6-small：本地离线深度学习模型，单模型通吃简繁英日，游戏美术字体识别准（默认）。</summary>
    RapidV6,
    /// <summary>Windows 内置 OCR：速度快、零体积，但中文与小字误识多，作为回退方案。</summary>
    Windows,
}

/// <summary>OCR 后端统一契约：输入一帧 BGRA，输出归一化坐标词块。</summary>
internal interface IOcrBackend : IDisposable
{
    /// <summary>引擎/语言标签，用于日志与环境检测展示。</summary>
    string Label { get; }

    Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default);
}

/// <summary>
/// OCR 识别器门面：按配置选择后端，对上保持 <see cref="OcrResult"/> 契约不变。
/// 默认 PP-OCRv6（<see cref="OcrEngineKind.RapidV6"/>），可回退 Windows 内置 OCR。
/// </summary>
public sealed class OcrRecognizer : IDisposable
{
    private readonly IOcrBackend _backend;

    /// <summary>当前引擎类型。</summary>
    public OcrEngineKind EngineKind { get; }

    /// <summary>引擎/语言标签（日志展示用）。</summary>
    public string LanguageTag => _backend.Label;

    public static IReadOnlyList<string> AvailableLanguages()
        => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>旧签名兼容：默认使用 PP-OCRv6（离线命令行自检 --check-frame/--dump 走此入口）。</summary>
    public OcrRecognizer(string? preferredTag = null) : this(OcrEngineKind.RapidV6, preferredTag) { }

    public OcrRecognizer(OcrEngineKind engine, string? preferredTag = null)
    {
        EngineKind = engine;
        _backend = engine switch
        {
            OcrEngineKind.Windows => new WindowsOcrBackend(preferredTag),
            _ => new RapidV6OcrBackend(),
        };
    }

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
        => _backend.RecognizeAsync(frame, ct);

    public void Dispose() => _backend.Dispose();
}

/// <summary>Windows 内置离线 OCR（优先简体中文，回退英文 / 系统首选）。</summary>
internal sealed class WindowsOcrBackend : IOcrBackend
{
    private readonly OcrEngine _engine;

    public string Label { get; }

    public WindowsOcrBackend(string? preferredTag = null)
    {
        var avail = OcrEngine.AvailableRecognizerLanguages.ToList();
        Language? lang = null;
        if (preferredTag is not null)
            lang = avail.FirstOrDefault(l => string.Equals(l.LanguageTag, preferredTag, StringComparison.OrdinalIgnoreCase));
        lang ??= avail.FirstOrDefault(l => l.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
                                       || l.LanguageTag.Equals("zh", StringComparison.OrdinalIgnoreCase)
                                       || l.LanguageTag.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase));
        lang ??= avail.FirstOrDefault(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        lang ??= avail.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        lang ??= avail.FirstOrDefault();
        if (lang is null)
            throw new InvalidOperationException("系统没有可用的 OCR 语言包。请在 Windows 设置 → 时间和语言 → 语言中添加简体中文。");
        _engine = OcrEngine.TryCreateFromLanguage(lang) ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("无法创建 OCR 引擎。");
        Label = lang.LanguageTag;
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        IBuffer buffer = CryptographicBuffer.CreateFromByteArray(frame.Bgra);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);

        var result = await _engine.RecognizeAsync(bmp).AsTask(ct);
        var words = new List<OcrWord>(result.Lines.Sum(l => l.Words.Count));
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                string text = word.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var r = word.BoundingRect;
                words.Add(new OcrWord(
                    text,
                    Math.Clamp(r.X / frame.Width, 0, 1),
                    Math.Clamp(r.Y / frame.Height, 0, 1),
                    Math.Clamp(r.Width / frame.Width, 0, 1),
                    Math.Clamp(r.Height / frame.Height, 0, 1)));
            }
        }
        return new OcrResult(words, frame.Width, frame.Height);
    }

    public void Dispose() { }
}

/// <summary>
/// RapidOCR PP-OCRv6-small 多语言后端（ONNX Runtime 本地推理，简繁英日一个模型）。
/// 模型随程序输出到 models/v6，文本行方向分类器复用包自带 v5 cls；游戏文字均正立，关闭方向分类省一次推理。
/// </summary>
internal sealed class RapidV6OcrBackend : IOcrBackend
{
    public const string LabelText = "PP-OCRv6-small";

    private readonly RapidOcrNet.RapidOcr _ocr;
    private readonly RapidOcrOptions _options;

    public string Label => LabelText;

    public RapidV6OcrBackend()
    {
        // 单文件发布时模型在临时解压目录；普通发布在 exe 同级 models，FindBundledFile 两种都兼容
        string detPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_det_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_det_small.onnx");
        string recPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_rec_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_rec_small.onnx");
        string keysPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "ppocrv6_small_dict.txt")
            ?? throw ModelMissing("models/v6/ppocrv6_small_dict.txt");
        string clsPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
            ?? throw ModelMissing("models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        _ocr = new RapidOcrNet.RapidOcr();
        _ocr.InitModels(detPath, clsPath, recPath, keysPath);
        // PPOCRv6 预处理（短边自适应 736、不加白边）；游戏界面文字不会倒置，跳过 180° 分类
        _options = RapidOcrOptions.PPOCRv6 with { DoAngle = false };
    }

    private static InvalidOperationException ModelMissing(string rel) => new(
        "PP-OCRv6 模型文件缺失：" + rel +
        "。请重新下载完整版程序（models 目录需与 exe 同级），或在设置中把 OCR 引擎切回「Windows 内置 OCR」。");

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var words = new List<OcrWord>();
            unsafe
            {
                fixed (byte* pixels = frame.Bgra)
                {
                    // InstallPixels 零拷贝借用托管数组（fixed 固定），Detect 结束前不释放
                    using var bmp = new SKBitmap();
                    if (!bmp.InstallPixels(info, (IntPtr)pixels, info.RowBytes))
                        throw new InvalidOperationException("SKBitmap 像素装载失败。");

                    var result = _ocr.Detect(bmp, _options);
                    foreach (var block in result.TextBlocks)
                    {
                        string text = block.Text;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        var pts = block.BoxPoints;
                        float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
                        for (int i = 1; i < pts.Length; i++)
                        {
                            minX = Math.Min(minX, pts[i].X);
                            maxX = Math.Max(maxX, pts[i].X);
                            minY = Math.Min(minY, pts[i].Y);
                            maxY = Math.Max(maxY, pts[i].Y);
                        }
                        words.Add(new OcrWord(
                            text,
                            Math.Clamp(minX / frame.Width, 0, 1),
                            Math.Clamp(minY / frame.Height, 0, 1),
                            Math.Clamp((maxX - minX) / frame.Width, 0, 1),
                            Math.Clamp((maxY - minY) / frame.Height, 0, 1)));
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            return new OcrResult(words, frame.Width, frame.Height);
        }, ct);

    public void Dispose() => _ocr.Dispose();
}
