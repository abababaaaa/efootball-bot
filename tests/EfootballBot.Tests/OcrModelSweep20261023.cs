using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OcrResult = EfootballBot.Core.Vision.OcrResult;
using RapidOcrNet;
using SkiaSharp;
using Tesseract;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 模型档位横评第二批（2026-10-23）：同一套 RapidOcrNet/SimdPaddle 推理内核，
// 换 PP-OCRv5/v4 不同档位的 det/rec 模型 + Tesseract 5 异族基线。
// 模型来自 hf-mirror（bukuroo/PPOCRv5-ONNX、SWHL/RapidOCR）与 gh-proxy（tessdata_best），
// 全部落在 tools/ocr_candidates_20261023/（开发机专用，不进仓库、不随包发布）；
// 目录不存在或模型缺失时整段跳过，不影响 CI。
// 命中口径与 OcrModelComparisonSurvey 一致（ContainsJoined，同 12 帧、同期望词）。
public class OcrModelSweep20261023
{
    private readonly ITestOutputHelper _out;
    public OcrModelSweep20261023(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    /// <summary>候选模型根目录（开发机）。缺失则跳过本横评。</summary>
    private static readonly string? CandidatesDir = FindCandidatesDir();

    private static string? FindCandidatesDir()
    {
        // 测试基目录向上找仓库根（含 tools/ocr_candidates_20261023 的任意一层）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tools", "ocr_candidates_20261023");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    private static readonly (string File, string Keyword)[] Frames =
    {
        ("difficulty_new_ui.png", "选择赛事等级"),
        ("difficulty_2.png", "选择赛事等级"),
        ("keymatch_p1.png", "关键比赛"),
        ("keymatch_p3.png", "关键比赛"),
        ("matchhub_user_1.png", "我的联赛"),
        ("match_start_page.png", "下一步"),
        ("myleague_exp_result_page.png", "查看经验值"),
        ("itempage_inuse_1.png", "使用的物品"),
        ("next_season.png", "下个赛季"),
        ("fulltime_user_1.png", "下一步"),
        ("home_nav_cursor.png", "前往比赛"),
        ("focus_state_2.png", "赛程表"),
    };

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    private sealed record BenchRow(string Name, int Hits, double AvgMs, double PeakMs, long MemMb, string[] Missed);
    private readonly List<BenchRow> _results = new();

    [Fact]
    public void Sweep_ModelTiers_OnRealFrames()
    {
        if (CandidatesDir is null)
        {
            _out.WriteLine("（tools/ocr_candidates_20261023 不存在，跳过模型档位横评）");
            return;
        }
        string cd = CandidatesDir;
        string cls = RepoModel("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        string v6Det = RepoModel("models", "v6", "PP-OCRv6_det_small.onnx");
        string v6Rec = RepoModel("models", "v6", "PP-OCRv6_rec_small.onnx");
        string v6Keys = RepoModel("models", "v6", "ppocrv6_small_dict.txt");

        // —— RapidOcrNet 内核 × 模型档位矩阵 ——
        Run("Rapid|v6 small det+rec（基线）", v6Det, cls, v6Rec, v6Keys);
        Try("Rapid|v5 mobile det+rec", Path.Combine(cd, "ppocrv5-mobile-det.onnx"), cls,
            Path.Combine(cd, "ppocrv5-mobile-rec.onnx"), Path.Combine(cd, "ppocrv5_dict.txt"));
        Try("Rapid|v5 mobile det + v5 server rec", Path.Combine(cd, "ppocrv5-mobile-det.onnx"), cls,
            Path.Combine(cd, "ppocrv5-server-rec.onnx"), Path.Combine(cd, "ppocrv5_dict.txt"));
        Try("Rapid|v5 server det + v5 server rec", Path.Combine(cd, "ppocrv5-server-det.onnx"), cls,
            Path.Combine(cd, "ppocrv5-server-rec.onnx"), Path.Combine(cd, "ppocrv5_dict.txt"));
        Try("Rapid|v6 small det + v4 server rec", v6Det, cls,
            Path.Combine(cd, "ch_PP-OCRv4_rec_server_infer.onnx"), Path.Combine(cd, "ppocr_keys_v1.txt"));

        // —— SimdPaddle 内核 × v5 rec（若其预处理/字典校验兼容） ——
        TrySimd("Simd|v5 mobile det+rec", Path.Combine(cd, "ppocrv5-mobile-det.onnx"),
            Path.Combine(cd, "ppocrv5-mobile-rec.onnx"), Path.Combine(cd, "ppocrv5_dict.txt"));
        TrySimd("Simd|v5 mobile det + server rec", Path.Combine(cd, "ppocrv5-mobile-det.onnx"),
            Path.Combine(cd, "ppocrv5-server-rec.onnx"), Path.Combine(cd, "ppocrv5_dict.txt"));

        // —— Tesseract 5 异族基线 ——
        string tessdata = Path.Combine(cd, "tessdata");
        if (File.Exists(Path.Combine(tessdata, "chi_sim.traineddata")))
            TryTesseract("Tesseract5 chi_sim+eng best", tessdata);
        else
            _out.WriteLine("（tessdata/chi_sim.traineddata 缺失，跳过 Tesseract 基线）");

        _out.WriteLine("");
        _out.WriteLine("| 配置 | 关键词命中 | 平均耗时/帧 | 最慢帧 | 内存增量 | 漏判 |");
        _out.WriteLine("|---|---|---|---|---|---|");
        foreach (var r in _results)
            _out.WriteLine($"| {r.Name} | {r.Hits}/{Frames.Length} | {r.AvgMs:F0} ms | {r.PeakMs:F0} ms | {r.MemMb} MB | {string.Join("；", r.Missed)} |");
    }

    private static string RepoModel(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(new[] { dir.FullName, "src", "EfootballBot" }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("仓库模型缺失: " + string.Join('/', parts));
    }

    // —— RapidOcrNet 直连（绕过 OcrRecognizer 的模型路径约定） ——

    private void Run(string name, string det, string cls, string rec, string keys)
    {
        try
        {
            var ocr = new RapidOcr();
            ocr.InitModels(det, cls, rec, keys);
            RunBackend(name, f => DetectRapid(ocr, f), () => ocr.Dispose());
        }
        catch (Exception ex)
        {
            _out.WriteLine($"（{name} 加载失败：{ex.Message}）");
        }
    }

    private void Try(string name, string det, string cls, string rec, string keys)
    {
        if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
        {
            _out.WriteLine($"（{name} 模型缺失，跳过）");
            return;
        }
        Run(name, det, cls, rec, keys);
    }

    private static OcrResult DetectRapid(RapidOcr ocr, FrameData frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var words = new List<OcrWord>();
        unsafe
        {
            fixed (byte* pixels = frame.Bgra)
            {
                using var bmp = new SKBitmap();
                if (!bmp.InstallPixels(info, (IntPtr)pixels, info.RowBytes))
                    throw new InvalidOperationException("SKBitmap 像素装载失败。");
                var result = ocr.Detect(bmp, RapidOcrOptions.PPOCRv6 with { DoAngle = false });
                foreach (var b in result.TextBlocks)
                {
                    if (string.IsNullOrWhiteSpace(b.Text)) continue;
                    float minX = b.BoxPoints.Min(p => p.X), maxX = b.BoxPoints.Max(p => p.X);
                    float minY = b.BoxPoints.Min(p => p.Y), maxY = b.BoxPoints.Max(p => p.Y);
                    words.Add(Norm(b.Text, minX, minY, maxX, maxY, frame));
                }
            }
        }
        return new OcrResult(words, frame.Width, frame.Height);
    }

    // —— SimdPaddle 直连 ——

    private void TrySimd(string name, string det, string rec, string keys)
    {
        if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
        {
            _out.WriteLine($"（{name} 模型缺失，跳过）");
            return;
        }
        try
        {
            string cls = RepoModel("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
            var ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys,
                new Sdcb.SimdPaddleOCR.PaddleOcrOptions
                {
                    UseDirectionClassification = false,
                    Detector = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                });
            RunBackend(name, f =>
            {
                var result = ocr.Run(f.Bgra, f.Width, f.Height, f.Width * 4,
                    Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
                var words = new List<OcrWord>();
                foreach (var line in result.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text)) continue;
                    var b = line.Box;
                    float minX = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4));
                    float maxX = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
                    float minY = Math.Min(Math.Min(b.Y1, b.Y2), Math.Min(b.Y3, b.Y4));
                    float maxY = Math.Max(Math.Max(b.Y1, b.Y2), Math.Max(b.Y3, b.Y4));
                    words.Add(Norm(line.Text, minX, minY, maxX, maxY, f));
                }
                return new OcrResult(words, f.Width, f.Height);
            }, () => ocr.Dispose());
        }
        catch (Exception ex)
        {
            _out.WriteLine($"（{name} 不兼容 SimdPaddle 内核，跳过：{ex.Message}）");
        }
    }

    // —— Tesseract 5 ——

    private void TryTesseract(string name, string tessdata)
    {
        try
        {
            var engine = new TesseractEngine(tessdata, "chi_sim+eng", EngineMode.Default);
            engine.DefaultPageSegMode = PageSegMode.SparseText;
            RunBackend(name, f =>
            {
                // Tesseract 只吃编码图像：SKImage 编码 PNG 后装载（编码耗时不计入公平性会偏差，报告中注明）
                SKImage img;
                unsafe
                {
                    fixed (byte* p = f.Bgra)
                        img = SKImage.FromPixelCopy(new SKImageInfo(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)p, f.Width * 4);
                }
                using (img)
                using (var png = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var ms = new MemoryStream())
                {
                    png.SaveTo(ms);
                    using var pix = Pix.LoadFromMemory(ms.ToArray());
                    using var page = engine.Process(pix);
                    var words = new List<OcrWord>();
                    using var it = page.GetIterator();
                    do
                    {
                        string? text = it.GetText(PageIteratorLevel.Word);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out var r)) continue;
                        words.Add(Norm(text.Trim(), r.X1, r.Y1, r.X2, r.Y2, f));
                    } while (it.Next(PageIteratorLevel.Word));
                    return new OcrResult(words, f.Width, f.Height);
                }
            }, () => engine.Dispose());
        }
        catch (Exception ex)
        {
            _out.WriteLine($"（{name} 加载失败：{ex.Message}）");
        }
    }

    private static OcrWord Norm(string text, float x1, float y1, float x2, float y2, FrameData f)
        => new(text,
            Math.Clamp(x1 / f.Width, 0, 1),
            Math.Clamp(y1 / f.Height, 0, 1),
            Math.Clamp((x2 - x1) / f.Width, 0, 1),
            Math.Clamp((y2 - y1) / f.Height, 0, 1));

    // —— 公共计时/命中统计（与 OcrModelComparisonSurvey 同口径） ——

    private void RunBackend(string name, Func<FrameData, OcrResult> recognize, Action dispose)
    {
        var times = new Dictionary<string, double>();
        var hits = new HashSet<string>();
        long memBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var (file, keyword) in Frames)
            {
                var frame = LoadFrame(file);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = recognize(frame);
                sw.Stop();
                times[file] = Math.Min(times.GetValueOrDefault(file, double.MaxValue), sw.Elapsed.TotalMilliseconds);
                if (res.ContainsJoined(keyword)) hits.Add(file);
            }
        }
        dispose();
        long memMb = Math.Max(0, (System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 - memBefore) / (1024 * 1024));

        var avg = times.Values.Average();
        var missed = Frames.Where(f => !hits.Contains(f.File)).Select(f => $"{f.File}({f.Keyword})").ToArray();
        _results.Add(new BenchRow(name, hits.Count, avg, times.Values.Max(), memMb, missed));
        _out.WriteLine($"[{name}] 命中 {hits.Count}/{Frames.Length}，平均 {avg:F0} ms/帧，内存 +{memMb} MB");
        foreach (var f in Frames)
            _out.WriteLine($"    {f.File}: {times[f.File]:F0} ms，命中={hits.Contains(f.File)}");
    }
}
