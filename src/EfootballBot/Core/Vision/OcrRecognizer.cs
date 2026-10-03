using EfootballBot.Core.Capture;
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

public sealed record OcrResult(IReadOnlyList<OcrWord> Words, int Width, int Height)
{
    public static readonly OcrResult Empty = new(Array.Empty<OcrWord>(), 0, 0);

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

    private static readonly (string, string)[] Replacements =
    {
        ("我的聏赛", "我的联赛"), ("我的聏", "我的联"),
        ("联賽", "联赛"), ("聯賽", "联赛"),
        ("前往比賽", "前往比赛"), ("前往比", "前往比"),
        ("关键比賽", "关键比赛"), ("關鍵比賽", "关键比赛"),
        ("兌换", "兑换"), ("兌換", "兑换"),
        ("傑出", "杰出"),
        ("教練", "教练"),
        ("經驗", "经验"),
        ("設置", "设置"),
        ("賽程", "赛程"),
        ("積分", "积分"),
        ("森破", "积分"), ("皋破", "积分"), ("皋分", "积分"), ("森分", "积分"),
        ("惠品", "物品"), ("愚品", "物品"),
        ("道具", "道具"),
        ("確認", "确认"),
        ("返囘", "返回"),
        ("下ー步", "下一步"),
        ("新", "新"),
    };
}

/// <summary>Windows 内置离线 OCR 封装（优先简体中文，回退英文 / 系统首选）。</summary>
public sealed class OcrRecognizer
{
    private readonly OcrEngine _engine;
    public string LanguageTag { get; }

    public static IReadOnlyList<string> AvailableLanguages()
        => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public OcrRecognizer(string? preferredTag = null)
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
        LanguageTag = lang.LanguageTag;
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
}
