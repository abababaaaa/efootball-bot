using EfootballBot.Core.Capture;
using System.IO;
using RapidOcrNet;
using SkiaSharp;

namespace EfootballBot.Core.Vision;

/// <summary>OCR 词块，坐标已归一化到 0~1。</summary>
public sealed record OcrWord(string Text, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    private string? _norm;

    /// <summary>
    /// 规范化文本缓存（<see cref="OcrText.Norm"/> 的惰性结果）。
    /// Norm 要走 Unicode FormKC 归一 + 标点剔除 + 替换表，单次约 10µs；一帧 100+ 词、
    /// 每轮判定要跑十几个探针，同一个词会被反复规范化几十次（每帧数千次纯重复计算）。
    /// 对固定 Text 结果恒定，缓存不改变任何行为，仅省掉重复分配与字符串处理。
    /// </summary>
    public string NormText => _norm ??= OcrText.Norm(Text);
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
            return new OcrLine(string.Concat(ordered.Select(w => w.NormText)),
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
            string text = string.Concat(ordered.Select(w => w.NormText));
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
            if (w.NormText.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
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
            .SelectMany(l => l.OrderBy(w => w.X).Select(w => w.NormText)));
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
            string t = w.NormText;
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
    // 2026-10-21 反馈包（GDI 兜底捕获 + 2027 新 UI 美术字）：繁体单字误识回升——
    // 「前往比賽」「賽程表」「锦标賽」「階段」「組別」均现。单字映射逐词生效，
    // 碎词（前|往|比|賽 被拆成 4 个词）也能还原，比整词规则抗拆分。
    internal static readonly (string Bad, string Good)[] Replacements =
    {
        ("前往比賽", "前往比赛"),
        ("賽", "赛"),
        ("階", "阶"),
        ("組", "组"),
        ("別", "别"),
    };
}

/// <summary>OCR 识别后端。</summary>
public enum OcrEngineKind
{
    /// <summary>RapidOCR PP-OCRv6-small（ONNX Runtime CPU）：本地离线深度学习，单模型通吃简繁英日。</summary>
    RapidV6,
    /// <summary>【已删除引擎，仅为旧配置编号兼容保留】3.3.0.1 起 Windows 内置 OCR 已移除，
    /// 读取到该值一律按 <see cref="SimdPaddle"/> 处理（其碎词/误识质量不达挂机判定线）。</summary>
    Windows = 1,
    /// <summary>SimdPaddleOCR（纯托管推理，Apache-2.0，默认引擎）：与 RapidV6 共用 models/v6 模型文件，
    /// 零原生依赖。加载失败自动回退 RapidV6。</summary>
    SimdPaddle = 2,
    /// <summary>Unlimited-OCR（百度 2026 文档解析 VLM，MIT）：官方仅提供 torch/vLLM/SGLang 部署，
    /// 本程序以 HTTP 客户端接入 OpenAI 兼容服务（见 <see cref="UnlimitedOcrBackend"/>）。
    /// 逐帧秒级~数十秒，只作精度参照/离线校准；服务不可达自动回退 SimdPaddle。</summary>
    UnlimitedOcr = 3,
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

    /// <summary>非空 = 选定引擎加载失败已自动回退（内容为原因），日志须播报。</summary>
    public string? FallbackNote { get; }

    /// <summary>旧签名兼容：默认使用当前默认引擎（离线命令行自检 --check-frame/--dump 走此入口）。
    /// preferredTag 曾用于选择 Windows OCR 语言包，该引擎已删除，参数保留仅防调用方破坏。</summary>
    public OcrRecognizer(string? preferredTag = null) : this(OcrEngineKind.SimdPaddle) { }

    /// <param name="downscaleLongSide">OCR 输入降采样上限（帧长边超过该值先等比缩小再识别）。
    /// 0 = 关闭。4K 级画面降采样到 1080p 后 det 像素量约为原来 1/4，UI 文字在 720p 实测帧即可读全；
    /// 词块坐标按归一化输出，降采样不影响 0~1 坐标契约。</param>
    /// <param name="preferInt8">true = small 识别模型优先加载 int8 量化版（缺失自动回退 fp32）。</param>
    public OcrRecognizer(OcrEngineKind engine, string? preferredTag = null,
        int downscaleLongSide = DefaultDownscaleLongSide, bool preferInt8 = true,
        EfootballBot.Core.Config.UnlimitedOcrConfig? unlimitedOcr = null)
    {
        EngineKind = engine;
        _downscaleLongSide = downscaleLongSide;
        switch (engine)
        {
            case OcrEngineKind.UnlimitedOcr:
                try
                {
                    _backend = new UnlimitedOcrBackend(unlimitedOcr ?? new EfootballBot.Core.Config.UnlimitedOcrConfig());
                }
                catch (Exception ex)
                {
                    // 外部服务随时可能没起：绝不因试验引擎断了挂机
                    FallbackNote = $"Unlimited-OCR 加载失败已回退 SimdPaddle：{ex.Message}";
                    try
                    {
                        _backend = new SimdPaddleOcrBackend();
                    }
                    catch (Exception ex2)
                    {
                        FallbackNote += $"；SimdPaddle 亦失败，回退 RapidV6：{ex2.Message}";
                        _backend = new RapidV6OcrBackend(preferInt8);
                    }
                }
                break;
            case OcrEngineKind.SimdPaddle:
            case OcrEngineKind.Windows: // 旧配置迁移：Windows 引擎已删除，一律落 SimdPaddle
                try
                {
                    _backend = new SimdPaddleOcrBackend();
                }
                catch (Exception ex)
                {
                    // 纯托管引擎也可能因模型图形态不兼容失败：绝不因试验引擎断了挂机
                    FallbackNote = $"SimdPaddle 加载失败已回退 RapidV6：{ex.Message}";
                    _backend = new RapidV6OcrBackend(preferInt8);
                }
                break;
            default:
                _backend = new RapidV6OcrBackend(preferInt8);
                break;
        }
    }

    /// <summary>降采样上限默认值（1080p 长边）。</summary>
    public const int DefaultDownscaleLongSide = 1920;

    private readonly int _downscaleLongSide;

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        var scaled = ImageScaling.DownscaleBilinear(frame, _downscaleLongSide);
        return _backend.RecognizeAsync(scaled, ct);
    }

    public void Dispose() => _backend.Dispose();
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

    public string Label { get; }

    /// <param name="preferInt8">true = 优先加载 models/v6/int8 下的 int8 量化模型（CPU 推理约 2-4x）；
    /// int8 文件缺失或加载失败（损坏/ORT 不兼容）时自动回退 fp32，绝不因量化模型问题而启动失败。</param>
    public RapidV6OcrBackend(bool preferInt8 = true)
    {
        // 单文件发布时模型在临时解压目录；普通发布在 exe 同级 models，FindBundledFile 两种都兼容
        var fp32Det = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_det_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_det_small.onnx");
        var clsPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
            ?? throw ModelMissing("models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        // int8 降级阶梯：识别模型动态量化精度无损（实测文本逐字一致）优先启用；
        // 检测模型静态量化对 DBNet 概率图敏感（MinMax 校准实测大面积漏检），
        // det int8 只有在通过全量 RealFrame 回归后才随包发布。逐级尝试，全败落 fp32。
        var fp32Rec = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_rec_small.onnx")
            ?? throw ModelMissing("models/v6/PP-OCRv6_rec_small.onnx");
        var keysPath = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "ppocrv6_small_dict.txt")
            ?? throw ModelMissing("models/v6/ppocrv6_small_dict.txt");
        var recInt8 = preferInt8 ? EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "int8", "PP-OCRv6_rec_small_int8.onnx") : null;
        var detInt8 = preferInt8 ? EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "int8", "PP-OCRv6_det_small_int8.onnx") : null;
        static string Grade(string? d, string? r) =>
            d is null && r is null ? "" : $"（{(d is not null ? "det" : "")}{(d is not null && r is not null ? "+" : "")}{(r is not null ? "rec" : "")} int8）";

        (string det, string rec, string grade)[] ladder =
        [
            (detInt8 ?? fp32Det, recInt8 ?? fp32Rec, Grade(detInt8, recInt8)),
            (fp32Det, recInt8 ?? fp32Rec, Grade(null, recInt8)),
            (fp32Det, fp32Rec, ""),
        ];

        RapidOcrNet.RapidOcr? loaded = null;
        string int8Grade = "";
        foreach (var (det, rec, g) in ladder)
        {
            try
            {
                loaded = CreateOcr(det, clsPath, rec, keysPath);
                int8Grade = g;
                break;
            }
            catch (Exception ex)
            {
                // 量化模型损坏/不兼容：降级到下一级（放弃 int8 只损失速度，不该损失可用性）
                global::System.Diagnostics.Debug.WriteLine($"[OCR] 模型加载失败（det={Path.GetFileName(det)} rec={Path.GetFileName(rec)}），降级重试：{ex.Message}");
            }
        }

        _ocr = loaded ?? CreateOcr(fp32Det, clsPath, fp32Rec, keysPath);
        Label = LabelText + int8Grade;

        // PPOCRv6 预处理（短边自适应 736、不加白边）；游戏界面文字不会倒置，跳过 180° 分类
        _options = RapidOcrOptions.PPOCRv6 with { DoAngle = false };
    }

    private static RapidOcrNet.RapidOcr CreateOcr(string detPath, string clsPath, string recPath, string keysPath)
    {
        // RapidOcrNet 4.2.0 自管 InferenceSession（CPU，不接受外部 SessionOptions）；
        // GPU 加速仅覆盖 YOLO 检测（OnnxSessionFactory），OCR 走包内默认 CPU 会话
        var ocr = new RapidOcrNet.RapidOcr();
        ocr.InitModels(detPath, clsPath, recPath, keysPath);
        return ocr;
    }

    private static InvalidOperationException ModelMissing(string rel) => new(
        "PP-OCRv6 模型文件缺失：" + rel + "。请重新下载完整版程序（models 目录需与 exe 同级）。");

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

/// <summary>
/// SimdPaddleOCR 后端（sdcb 纯托管 PP-OCR 推理，Apache-2.0）：与 RapidV6 共用 models/v6 模型文件，
/// 零原生依赖的备选引擎。固定 CPU（不与游戏抢 GPU）；游戏界面文字均正立，关闭方向分类。
/// </summary>
internal sealed class SimdPaddleOcrBackend : IOcrBackend
{
    private readonly Sdcb.SimdPaddleOCR.PaddleOcrAll _ocr;

    public string Label { get; }

    public SimdPaddleOcrBackend()
    {
        string det = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_det_small.onnx")
            ?? throw ModelFileMissing("models/v6/PP-OCRv6_det_small.onnx");
        string rec = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "PP-OCRv6_rec_small.onnx")
            ?? throw ModelFileMissing("models/v6/PP-OCRv6_rec_small.onnx");
        string keys = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v6", "ppocrv6_small_dict.txt")
            ?? throw ModelFileMissing("models/v6/ppocrv6_small_dict.txt");
        string cls = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
            ?? throw ModelFileMissing("models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        _ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys, new Sdcb.SimdPaddleOCR.PaddleOcrOptions
        {
            UseDirectionClassification = false,
            Detector = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
            Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
            Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
        });
        Label = "PP-OCRv6-small（SimdPaddle）";
    }

    private static InvalidOperationException ModelFileMissing(string rel) => new(
        "SimdPaddleOCR 模型文件缺失：" + rel + "。请重新下载完整版程序（models 目录需与 exe 同级）。");

    public Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // BGRA 原生支持，零转换直喂
            var result = _ocr.Run(frame.Bgra, frame.Width, frame.Height, frame.Width * 4,
                Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
            var words = new List<OcrWord>(result.Lines.Length);
            foreach (var line in result.Lines)
            {
                string text = line.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var b = line.Box;
                float minX = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4));
                float maxX = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
                float minY = Math.Min(Math.Min(b.Y1, b.Y2), Math.Min(b.Y3, b.Y4));
                float maxY = Math.Max(Math.Max(b.Y1, b.Y2), Math.Max(b.Y3, b.Y4));
                words.Add(new OcrWord(
                    text,
                    Math.Clamp(minX / frame.Width, 0, 1),
                    Math.Clamp(minY / frame.Height, 0, 1),
                    Math.Clamp((maxX - minX) / frame.Width, 0, 1),
                    Math.Clamp((maxY - minY) / frame.Height, 0, 1)));
            }
            return new OcrResult(words, frame.Width, frame.Height);
        }, ct);

    public void Dispose() => _ocr.Dispose();
}
