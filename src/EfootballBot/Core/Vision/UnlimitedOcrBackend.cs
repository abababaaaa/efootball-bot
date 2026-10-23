using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using SkiaSharp;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.Vision;

/// <summary>
/// Unlimited-OCR（百度 2026 文档解析 VLM，arXiv 2606.23050，MIT）输出解析。
/// 模型只能以 Python/torch 或 vLLM/SGLang 服务形态运行（官方无 ONNX 导出），
/// 因此客户端只负责「帧 → OpenAI 兼容 chat/completions → 词块」的协议转换。
/// <para>
/// 输出格式：<c>&lt;|ref|&gt;文本&lt;|/ref|&gt;&lt;|det|&gt;[[x1,y1,x2,y2]]&lt;|/det|&gt;</c>，
/// 坐标是 0~999 的归一化值（官方 visualization 即按 <c>x/999*width</c> 还原）。
/// 纯文本无标记时按行降级：给出整行宽度、按行号均分高度（无真实框，位置仅供日志/点选兜底）。
/// </para>
/// </summary>
public static class UnlimitedOcrMarkup
{
    /// <summary>ref + det 成对标记（允许中间有换行）。</summary>
    private static readonly Regex RefDetPattern = new(
        @"<\|ref\|>(.*?)<\|/ref\|><\|det\|>(.*?)<\|/det\|>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// det 前置标记：实测模型输出是 <c>&lt;|det|&gt;类型 [x1,y1,x2,y2]&lt;|/det|&gt;正文</c>
    /// （类型=title/text/table…，正文跟在闭合标记后）；官方 re_match 的另一种
    /// <c>&lt;|ref|&gt;正文&lt;|/ref|&gt;&lt;|det|&gt;[[…]]&lt;|/det|&gt;</c> 也兼容。
    /// </summary>
    private static readonly Regex DetLabelPattern = new(
        @"<\|det\|>\s*([A-Za-z_][\w-]*)\s*(\[[^\]\[]*\])\s*<\|/det\|>([^\n]*)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>det 里的坐标串：支持 [[x1,y1,x2,y2]] 与多点 [[x,y],...] 两种写法。</summary>
    private static readonly Regex BoxPattern = new(
        @"\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]",
        RegexOptions.Compiled);

    /// <summary>模型偶尔把 figure 占位写进正文（图片块），不是文字，直接丢弃。</summary>
    private static readonly Regex ImageRefPattern = new(
        @"!\[[^\]]*\]\([^)]*\)|<\|ref\|>image<\|/ref\|>",
        RegexOptions.Compiled);

    /// <summary>把模型输出文本解析成归一化词块。</summary>
    public static List<OcrWord> Parse(string text)
    {
        var words = new List<OcrWord>();
        if (string.IsNullOrWhiteSpace(text)) return words;

        // 1) 实测主格式（det 前置）：<|det|>title [x1,y1,x2,y2]<|/det|>正文（正文在闭合标记后同行）
        foreach (Match m in DetLabelPattern.Matches(text))
        {
            if (m.Groups[1].Value.Trim().Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            string content = Clean(m.Groups[3].Value);
            if (content.Length == 0) continue;
            AddBoxes(words, m.Groups[2].Value, content);
        }
        if (words.Count > 0) return words;

        // 2) 官方 re_match 格式（ref 正文前置）：<|ref|>正文<|/ref|><|det|>[[x1,y1,x2,y2]]<|/det|>
        foreach (Match m in RefDetPattern.Matches(text))
        {
            if (m.Groups[1].Value.Trim().Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            string content = Clean(m.Groups[1].Value);
            if (content.Length == 0) continue;
            AddBoxes(words, m.Groups[2].Value, content);
        }
        if (words.Count > 0) return words;

        // 3) 无标记（纯文本回答）：按行给均分伪框，至少让 Contains/ContainsJoined 可用
        var lines = text.Replace("\r", "").Split('\n')
            .Select(Clean)
            .Where(l => l.Length > 0)
            .ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            double h = 1.0 / Math.Max(lines.Count, 1);
            words.Add(new OcrWord(lines[i], 0, i * h, 1, h));
        }
        return words;
    }

    /// <summary>框串支持 <c>[x1,y1,x2,y2]</c> 与多点 <c>[[..],[..]]</c>（坐标 0~999）。</summary>
    private static void AddBoxes(List<OcrWord> words, string boxText, string content)
    {
        var boxes = BoxPattern.Matches(boxText);
        if (boxes.Count == 0)
        {
            words.Add(new OcrWord(content, 0, 0, 1, 0.03));
            return;
        }
        foreach (Match b in boxes)
        {
            double x1 = Num(b.Groups[1].Value), y1 = Num(b.Groups[2].Value);
            double x2 = Num(b.Groups[3].Value), y2 = Num(b.Groups[4].Value);
            double x = Math.Clamp(Math.Min(x1, x2) / 999.0, 0, 1);
            double y = Math.Clamp(Math.Min(y1, y2) / 999.0, 0, 1);
            double w = Math.Clamp(Math.Abs(x2 - x1) / 999.0, 0, 1);
            double h = Math.Clamp(Math.Abs(y2 - y1) / 999.0, 0, 1);
            words.Add(new OcrWord(content, x, y, w, h));
        }
    }

    private static string Clean(string s)
    {
        string t = ImageRefPattern.Replace(s, "");
        t = t.Replace("<|ref|>", "").Replace("<|/ref|>", "")
             .Replace("<|det|>", "").Replace("<|/det|>", "")
             .Replace("<|end▁of▁sentence|>", "")
             .Trim();
        // 同一块内的换行属于换行排版，合并成一个词（与 PP-OCR 的「块」语义一致）
        return t.Replace("\r", "").Replace("\n", "");
    }

    private static double Num(string s) =>
        double.TryParse(s, global::System.Globalization.NumberStyles.Float,
            global::System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
}

/// <summary>
/// Unlimited-OCR HTTP 后端：向本机/局域网 OpenAI 兼容服务（vLLM / SGLang 官方部署方式）
/// 发送「一帧 + document parsing.」，取回结构化文本并转成 <see cref="OcrWord"/>。
/// <para>
/// 逐帧推理成本是 VLM 级别（A100 级 GPU 约 1~3s/帧，CPU 数十秒/帧），
/// 与挂机 2s 的 OCR 节流不在同一量级——定位为「离线/校准可选的精度参照」，
/// 不作为默认引擎；服务不可达时构造期抛错，由 <see cref="OcrRecognizer"/> 回退 SimdPaddle。
/// </para>
/// </summary>
internal sealed class UnlimitedOcrBackend : IOcrBackend
{
    private readonly HttpClient _http;
    private readonly UnlimitedOcrConfig _cfg;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Label { get; }

    public UnlimitedOcrBackend(UnlimitedOcrConfig cfg)
    {
        _cfg = cfg;
        if (string.IsNullOrWhiteSpace(cfg.Endpoint))
            throw new InvalidOperationException("Unlimited-OCR 服务地址为空");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(cfg.TimeoutSeconds, 10)) };

        // 构造期健康探测：2 秒内不可达即抛错（由门面回退），避免挂机跑到一半才发现引擎不可用
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = _http.GetAsync(ModelsUrl(cfg.Endpoint), cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unlimited-OCR 服务不可达（{cfg.Endpoint}）：{ex.Message}");
        }

        Label = $"Unlimited-OCR（{cfg.Model}）";
    }

    /// <summary>从 chat/completions 地址推导 /models（OpenAI 兼容服务的健康探针）。</summary>
    private static string ModelsUrl(string endpoint)
    {
        var u = endpoint.TrimEnd('/');
        int idx = u.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? u[..idx] + "/v1/models" : u + "/v1/models";
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string dataUri = EncodeJpegDataUri(frame);

        var payload = new
        {
            model = _cfg.Model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = string.IsNullOrWhiteSpace(_cfg.Prompt) ? "document parsing." : _cfg.Prompt },
                        new { type = "image_url", image_url = new { url = dataUri } },
                    },
                },
            },
            temperature = 0,
            max_tokens = 4096,
            stream = false,
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_cfg.Endpoint, content, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Unlimited-OCR HTTP {(int)resp.StatusCode}：{Truncate(body)}");

        string text = ExtractContent(body);
        var words = UnlimitedOcrMarkup.Parse(text);
        return new OcrResult(words, frame.Width, frame.Height);
    }

    private static string ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException($"Unlimited-OCR 响应无 choices：{Truncate(body)}");
        var msg = choices[0].GetProperty("message");
        return msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static string EncodeJpegDataUri(FrameData frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap();
        unsafe
        {
            fixed (byte* pixels = frame.Bgra)
            {
                if (!bmp.InstallPixels(info, (IntPtr)pixels, info.RowBytes))
                    throw new InvalidOperationException("SKBitmap 像素装载失败。");
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
                return "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
