using System.Text;
using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OcrResult = EfootballBot.Core.Vision.OcrResult;
using RapidOcrNet;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 模型档位横评第三批（2026-10-28）：PP-OCRv6 的另外两档（tiny / medium）+ v6 漫画特化微调。
// 前两批已排除 v4/v5 各档与 Tesseract（见 survey/ocr_model_sweep_20261023.md），
// 本批只测「v6 族内从未跑过的档位」与「域特化 rec」。重点不是命中数（前两批已坐实两帧漏判属标注问题），
// 而是**词级差异**：美术字/描边字/窄条退化帧上逐词认出了什么、认错了什么。
// 模型来源 hf-mirror，落 tools/ocr_candidates_20261028/（开发机专用，.gitignore 已覆盖 ocr_candidates_*）；
// 目录缺失整段跳过，不影响 CI。
public class OcrModelSweep20261028
{
    private readonly ITestOutputHelper _out;
    public OcrModelSweep20261028(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static readonly string? CandidatesDir = FindCandidatesDir();

    private const string BaselineName = "Simd|v6 small det+rec（基线）";

    // 每个后端：名字、实际内核、逐帧耗时与词表（最后一次 pass）、关键词命中
    private readonly List<(string Name, string Kernel)> _order = new();
    private readonly List<(string Name, Func<FrameData, OcrResult> Run, Action Dispose)> _backends = new();
    private readonly Dictionary<string, Dictionary<string, FrameStat>> _data = new();
    private readonly Dictionary<string, HashSet<string>> _hits = new();
    private readonly Dictionary<string, string> _failed = new();
    private readonly StringBuilder _report = new();

    private static string? FindCandidatesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tools", "ocr_candidates_20261028");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>与前两批完全一致的 12 帧 + 期望词，用于跨批次可比。</summary>
    private static readonly (string File, string Keyword)[] KeywordFrames =
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

    /// <summary>硬帧：不设期望词，只看词级 diff。entry_maxlevel 是大字「1」漏读那帧。</summary>
    private static readonly string[] HardFrames =
    {
        "entry_maxlevel.png",
        "focus_fail_live.png",
        "difficulty_live_20261020.png",
        "inmatch_popup_tr.png",
    };

    private static string[] AllFrames => KeywordFrames.Select(f => f.File).Concat(HardFrames).ToArray();

    private sealed record FrameStat(double Ms, string[] Words);

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
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

    [Fact]
    public void Sweep_Tiny_Medium_Manga_OnRealFrames()
    {
        if (CandidatesDir is null)
        {
            _out.WriteLine("（tools/ocr_candidates_20261028 不存在，跳过第三批横评）");
            return;
        }
        string cd = CandidatesDir;
        string cls = RepoModel("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        string v6Det = RepoModel("models", "v6", "PP-OCRv6_det_small.onnx");
        string v6Rec = RepoModel("models", "v6", "PP-OCRv6_rec_small.onnx");
        string v6Keys = RepoModel("models", "v6", "ppocrv6_small_dict.txt");

        string mangaDet = Path.Combine(cd, "manga_det_v0.2.onnx");
        string mangaRec = Path.Combine(cd, "manga_rec_v0.2.onnx");
        string tinyDet = Path.Combine(cd, "v6_tiny_det.onnx");
        string tinyRec = Path.Combine(cd, "v6_tiny_rec.onnx");
        string medDet = Path.Combine(cd, "v6_medium_det.onnx");
        string medRec = Path.Combine(cd, "v6_medium_rec.onnx");

        AddSimd(BaselineName, v6Det, cls, v6Rec, v6Keys);
        // 候选 2：v6 tiny（反向下探体积）；tiny rec 字符集仅 6904 条，与 small 的 18708 不同，须配自己的字典
        string tinyKeys = Path.Combine(cd, "ppocrv6_tiny_dict.txt");
        AddSimd("Simd|v6 tiny det+rec", tinyDet, cls, tinyRec, File.Exists(tinyKeys) ? tinyKeys : v6Keys);
        // 候选 3：v6 medium（v6 精度上限）
        AddSimd("Simd|v6 medium det+rec", medDet, cls, medRec, v6Keys);
        // 候选 1：v6 漫画特化微调（tiny_det + small_rec 在 Manga109-s/AnimeText 上微调，日+中）
        //   只换 rec 是主实验；manga 自带 det 先试 SimdPaddle，运行期若不支持再用 RapidOcrNet 定性
        AddSimd("Simd|v6 det small + manga rec（只换 rec）", v6Det, cls, mangaRec, v6Keys);
        AddSimd("Simd|manga det+rec", mangaDet, cls, mangaRec, v6Keys);
        // 隔离实验：崩点定位。v6 det 配 manga rec 也崩 → 问题在 rec；反配若能跑 → det 本身可单独用
        AddSimd("Simd|manga det + v6 rec small（隔离 det）", mangaDet, cls, v6Rec, v6Keys);
        AddRapid("Rapid|manga det+rec（内核能力对照）", mangaDet, cls, mangaRec, v6Keys);

        ExecuteAll();

        _out.WriteLine("");
        _out.WriteLine("| 配置 | 内核 | 关键词命中 | 平均耗时/帧 | 最慢帧 | 内存增量 | 漏判 |");
        _out.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (name, kernel) in _order)
        {
            if (_failed.TryGetValue(name, out var fail))
            {
                _out.WriteLine($"| {name} | {kernel} | — 运行期失败 | {fail.Replace("|", "/")} | | | |");
                continue;
            }
            if (_data[name].Count == 0) continue;
            var st = _data[name].Values;
            var missed = KeywordFrames.Where(kf => !_hits[name].Contains(kf.File))
                .Select(kf => $"{kf.File}({kf.Keyword})").ToArray();
            _out.WriteLine($"| {name} | {kernel} | {_hits[name].Count}/{KeywordFrames.Length} | {st.Average(x => x.Ms):F0} ms | {st.Max(x => x.Ms):F0} ms | {_memMb[name]} MB | {string.Join("；", missed)} |");
        }

        _out.WriteLine("");
        _out.WriteLine("## 词级差异（vs 基线，空白归一后逐词比较）");
        foreach (var (name, _) in _order)
            if (name != BaselineName && !_failed.ContainsKey(name)) PrintDiff(name);

        string reportPath = Path.Combine(cd, "sweep_20261028_report.txt");
        File.WriteAllText(reportPath, _report.ToString(), new UTF8Encoding(false));
        _out.WriteLine("");
        _out.WriteLine($"（全量词表与差异已写入 {reportPath}）");
    }

    private readonly Dictionary<string, long> _memMb = new();

    // —— 后端登记：先试 SimdPaddle（快内核），加载失败回落 RapidOcrNet ——

    private void AddSimd(string name, string det, string cls, string rec, string keys)
    {
        if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
        {
            _out.WriteLine($"（{name} 模型缺失，跳过）");
            return;
        }
        try
        {
            var ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys,
                new Sdcb.SimdPaddleOCR.PaddleOcrOptions
                {
                    UseDirectionClassification = false,
                    Detector = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                });
            Register(name, "SimdPaddle", f =>
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
            }, ocr.Dispose);
            _out.WriteLine($"[加载] {name} → SimdPaddle 成功");
            return;
        }
        catch (Exception ex)
        {
            _out.WriteLine($"[加载] {name} → SimdPaddle 不支持（{ex.GetType().Name}: {ex.Message}），回落 RapidOcrNet");
        }

        try
        {
            var ocr = new RapidOcr();
            ocr.InitModels(det, cls, rec, keys);
            Register(name, "RapidOcrNet(回落)", f => DetectRapid(ocr, f), ocr.Dispose);
            _out.WriteLine($"[加载] {name} → RapidOcrNet 成功");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"[加载] {name} → 两个内核都失败，跳过：{ex.Message}");
        }
    }

    private void Register(string name, string kernel, Func<FrameData, OcrResult> run, Action dispose)
    {
        _order.Add((name, kernel));
        _backends.Add((name, run, dispose));
        _data[name] = new();
        _hits[name] = new();
    }

    /// <summary>强制走 RapidOcrNet，用于给 SimdPaddle 运行期不支持的模型做内核能力对照。</summary>
    private void AddRapid(string name, string det, string cls, string rec, string keys)
    {
        if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
        {
            _out.WriteLine($"（{name} 模型缺失，跳过）");
            return;
        }
        try
        {
            var ocr = new RapidOcr();
            ocr.InitModels(det, cls, rec, keys);
            Register(name, "RapidOcrNet", f => DetectRapid(ocr, f), ocr.Dispose);
            _out.WriteLine($"[加载] {name} → RapidOcrNet 成功");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"[加载] {name} → RapidOcrNet 也失败，跳过：{ex.Message}");
        }
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

    private static OcrWord Norm(string text, float x1, float y1, float x2, float y2, FrameData f)
        => new(text,
            Math.Clamp(x1 / f.Width, 0, 1),
            Math.Clamp(y1 / f.Height, 0, 1),
            Math.Clamp((x2 - x1) / f.Width, 0, 1),
            Math.Clamp((y2 - y1) / f.Height, 0, 1));

    // —— 执行：3 pass 取最小耗时，命中与词表取最后一次 pass ——

    private void ExecuteAll()
    {
        var frames = AllFrames;
        foreach (var (name, run, dispose) in _backends)
        {
            var times = new Dictionary<string, double>();
            string? failure = null;
            long memBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            for (int pass = 0; pass < 3 && failure is null; pass++)
            {
                bool last = pass == 2;
                foreach (var file in frames)
                {
                    var frame = LoadFrame(file);
                    OcrResult res;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        res = run(frame);
                    }
                    catch (Exception ex)
                    {
                        // 运行期不支持（如 SimdPaddle 的动态形状解析缺陷）只废这一档，不断整轮横评
                        failure = $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
                        break;
                    }
                    sw.Stop();
                    times[file] = Math.Min(times.GetValueOrDefault(file, double.MaxValue), sw.Elapsed.TotalMilliseconds);
                    if (!last) continue;
                    _data[name][file] = new FrameStat(times[file], res.Words.Select(w => w.Text.Trim()).ToArray());
                    foreach (var kf in KeywordFrames)
                        if (kf.File == file && res.ContainsJoined(kf.Keyword))
                            _hits[name].Add(file);
                }
            }
            try { dispose(); } catch { /* 释放失败不影响结论 */ }

            if (failure is not null)
            {
                _failed[name] = failure;
                _report.AppendLine($"[{name}] 运行期失败：{failure}");
                _out.WriteLine($"[{name}] 运行期失败：{failure}");
                continue;
            }

            _memMb[name] = Math.Max(0, (System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 - memBefore) / (1024 * 1024));
            _report.AppendLine($"[{name}] 命中 {_hits[name].Count}/{KeywordFrames.Length}，平均 {times.Values.Average():F0} ms/帧，内存 +{_memMb[name]} MB");
            foreach (var file in frames)
                _report.AppendLine($"    {file}: {times[file]:F0} ms");
            _out.WriteLine($"[{name}] 命中 {_hits[name].Count}/{KeywordFrames.Length}，平均 {times.Values.Average():F0} ms/帧");
        }
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        string line = i < 0 ? s : s[..i];
        return line.Length > 160 ? line[..160] + "…" : line.Trim();
    }

    // —— 词级 diff ——

    private static string Loose(string s) => Regex.Replace(s, @"\s+", "");

    private void PrintDiff(string name)
    {
        if (!_data.TryGetValue(name, out var cand) || cand.Count == 0) return;
        if (!_data.TryGetValue(BaselineName, out var baseline) || baseline.Count == 0) return;

        int sameTotal = 0, onlyBaseTotal = 0, onlyCandTotal = 0;
        var lines = new List<string>();
        foreach (var file in AllFrames)
        {
            var b = baseline[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var c = cand[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var onlyB = b.Except(c).ToArray();
            var onlyC = c.Except(b).ToArray();
            int same = b.Intersect(c).Count();
            sameTotal += same; onlyBaseTotal += onlyB.Length; onlyCandTotal += onlyC.Length;

            if (onlyB.Length == 0 && onlyC.Length == 0) continue;
            string detail = $"  {file}（同 {same}）";
            if (onlyB.Length > 0) detail += $"\n      仅基线有: {Clip(onlyB)}";
            if (onlyC.Length > 0) detail += $"\n      仅候选有: {Clip(onlyC)}";
            lines.Add(detail);
        }

        int total = sameTotal + onlyBaseTotal + onlyCandTotal;
        string pct = total == 0 ? "n/a" : $"{sameTotal * 100.0 / total:F1}%";
        _out.WriteLine($"[{name}]");
        _out.WriteLine($"  共 {total} 词，与基线相同 {sameTotal}（{pct}），仅基线 {onlyBaseTotal}，仅候选 {onlyCandTotal}");
        foreach (var l in lines) _out.WriteLine(l);

        _report.AppendLine();
        _report.AppendLine($"## {name} 词级差异");
        _report.AppendLine($"共 {total} 词，相同 {sameTotal}（{pct}），仅基线 {onlyBaseTotal}，仅候选 {onlyCandTotal}");
        foreach (var l in lines) _report.AppendLine(l);

        _report.AppendLine();
        _report.AppendLine($"## {name} 全量词表");
        foreach (var file in AllFrames)
            _report.AppendLine($"  {file}: {string.Join(" | ", cand[file].Words)}");
    }

    private static string Clip(string[] words)
    {
        const int max = 14;
        var shown = words.Take(max).Select(w => "「" + (w.Length > 24 ? w[..24] + "…" : w) + "」");
        return string.Join(" ", shown) + (words.Length > max ? $" …共{words.Length}个" : "");
    }
}
