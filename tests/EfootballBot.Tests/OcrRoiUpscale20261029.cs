using System.Text;
using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Xunit.Abstractions;
using OcrResult = EfootballBot.Core.Vision.OcrResult;

namespace EfootballBot.Tests;

// ROI 预处理 A/B（2026-10-29）：不换模型，只改「det 框 → rec 输入」这一段。
//
// 前提（必须先说清）：rec 的输入被钉在 48 高，放大后再送进去会被缩回 48，**不会增信息**。
// 所以这里测的不是「放大」，而是**重采样/增强**：放大到 2×/3× 用不同核再缩回 48，等价于换一个
// 上/下采样滤波器；外加对比度拉伸与二值化，看能否把描边/发光的美术字从背景里分离出来。
//
// 口径：det 框来自基线 SimdPaddle（不动），候选只用 ORT 跑 PP-OCRv6-small rec，
// 与基线整链逐词可比。变体 0（原样）是对照组——它应当与基线同分，否则说明取框方式有偏差。
public class OcrRoiUpscale20261029
{
    private readonly ITestOutputHelper _out;
    public OcrRoiUpscale20261029(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
    private const string BaselineName = "Simd|v6 small det+rec（基线，含 det）";

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

    private static readonly string[] HardFrames =
    {
        "entry_maxlevel.png", "focus_fail_live.png", "difficulty_live_20261020.png", "inmatch_popup_tr.png",
    };

    private static string[] AllFrames => KeywordFrames.Select(f => f.File).Concat(HardFrames).ToArray();

    private sealed record FrameStat(double Ms, string[] Words);
    private readonly List<(string Name, string Kernel)> _order = new();
    private readonly List<(string Name, Func<FrameData, OcrResult> Run, Action Dispose)> _backends = new();
    private readonly Dictionary<string, Dictionary<string, FrameStat>> _data = new();
    private readonly Dictionary<string, HashSet<string>> _hits = new();
    private readonly Dictionary<string, string> _failed = new();
    private readonly Dictionary<string, long> _memMb = new();
    private readonly StringBuilder _report = new();
    private readonly Dictionary<string, FrameData> _frames = new();
    private Baseline? _baseline;

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    private static string RepoFile(params string[] parts)
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
    public void Sweep_RoiPreprocess_OnRealFrames()
    {
        string cls = RepoFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        string v6Det = RepoFile("models", "v6", "PP-OCRv6_det_small.onnx");
        string v6Rec = RepoFile("models", "v6", "PP-OCRv6_rec_small.onnx");
        string v6Keys = RepoFile("models", "v6", "ppocrv6_small_dict.txt");

        foreach (var f in AllFrames) _frames[f] = LoadFrame(f);

        _baseline = new Baseline();
        _baseline.Init(v6Det, cls, v6Rec, v6Keys);
        Register(BaselineName, "SimdPaddle", _baseline.Run, _baseline.Dispose);

        var dict = LoadDict(v6Keys);
        _out.WriteLine($"[数据] v6 字典 {dict.Count} 字");
        foreach (var (mode, label) in new[]
                 {
                     (0, "原样（对照）"),
                     (1, "2× 双线性再缩回"),
                     (2, "2× + 锐化"),
                     (3, "3× 双线性再缩回"),
                     (4, "2× 最近邻再缩回"),
                     (5, "对比度拉伸"),
                     (6, "Otsu 二值化"),
                 })
        {
            try
            {
                var probe = new V6RecProbe(v6Rec, dict, mode, _out);
                Register($"v6 det + rec（{label}）", "ORT",
                    f => RunOverBaselineBoxes(f, boxes => probe.RecognizeFrame(f, boxes)), probe.Dispose);
            }
            catch (Exception ex)
            {
                _failed[$"v6 det + rec（{label}）"] = FirstLine(ex.Message);
                _out.WriteLine($"[加载] {label} → 失败：{FirstLine(ex.Message)}");
            }
        }

        ExecuteAll();

        _out.WriteLine("");
        _out.WriteLine("| 配置 | 内核 | 关键词命中 | 平均耗时/帧 | 最慢帧 | 内存增量 | 漏判 |");
        _out.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (name, kernel) in _order)
        {
            if (_failed.TryGetValue(name, out var fail))
            {
                _out.WriteLine($"| {name} | {kernel} | — 失败 | {fail.Replace("|", "/")} | | | |");
                continue;
            }
            if (_data[name].Count == 0) continue;
            var st = _data[name].Values;
            var missed = KeywordFrames.Where(kf => !_hits[name].Contains(kf.File))
                .Select(kf => $"{kf.File}({kf.Keyword})").ToArray();
            _out.WriteLine($"| {name} | {kernel} | {_hits[name].Count}/{KeywordFrames.Length} | {st.Average(x => x.Ms):F0} ms | " +
                           $"{st.Max(x => x.Ms):F0} ms | {_memMb[name]} MB | {string.Join("；", missed)} |");
        }

        _out.WriteLine("");
        _out.WriteLine("## 词级差异（vs 基线）");
        foreach (var (name, _) in _order)
            if (name != BaselineName && !_failed.ContainsKey(name)) PrintDiff(name);

        string reportPath = Path.Combine(AppContext.BaseDirectory, "roi_upscale_20261029_report.txt");
        File.WriteAllText(reportPath, _report.ToString(), new UTF8Encoding(false));
        _out.WriteLine($"（全量词表与差异已写入 {reportPath}）");
    }

    // ================= 基线 =================

    private sealed class Baseline
    {
        private Sdcb.SimdPaddleOCR.PaddleOcrAll? _ocr;
        private readonly Dictionary<FrameData, List<(float, float, float, float)>> _boxes =
            new(ReferenceEqualityComparer.Instance);

        public void Init(string det, string cls, string rec, string keys)
            => _ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys, new Sdcb.SimdPaddleOCR.PaddleOcrOptions
            {
                UseDirectionClassification = false,
                Detector = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
            });

        public IReadOnlyList<(float X0, float Y0, float X1, float Y1)> BoxesOf(FrameData f)
            => _boxes.TryGetValue(f, out var b) ? b : Array.Empty<(float, float, float, float)>();

        public OcrResult Run(FrameData f)
        {
            var result = _ocr!.Run(f.Bgra, f.Width, f.Height, f.Width * 4, Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
            var words = new List<OcrWord>();
            var boxes = new List<(float, float, float, float)>();
            foreach (var line in result.Lines)
            {
                var b = line.Box;
                float minX = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4));
                float maxX = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
                float minY = Math.Min(Math.Min(b.Y1, b.Y2), Math.Min(b.Y3, b.Y4));
                float maxY = Math.Max(Math.Max(b.Y1, b.Y2), Math.Max(b.Y3, b.Y4));
                if (!string.IsNullOrWhiteSpace(line.Text)) words.Add(Norm(line.Text, minX, minY, maxX, maxY, f));
                boxes.Add((minX, minY, maxX, maxY));
            }
            if (!_boxes.ContainsKey(f)) _boxes[f] = boxes;
            return new OcrResult(words, f.Width, f.Height);
        }

        public void Dispose() => _ocr?.Dispose();
    }

    private OcrResult RunOverBaselineBoxes(FrameData f,
        Func<IReadOnlyList<(float X0, float Y0, float X1, float Y1)>, string[]> recognize)
    {
        var boxes = _baseline?.BoxesOf(f) ?? Array.Empty<(float, float, float, float)>();
        if (boxes.Count == 0) return new OcrResult(Array.Empty<OcrWord>(), f.Width, f.Height);
        var texts = recognize(boxes);
        var words = new List<OcrWord>();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i])) continue;
            words.Add(Norm(texts[i], boxes[i].X0, boxes[i].Y0, boxes[i].X1, boxes[i].Y1, f));
        }
        return new OcrResult(words, f.Width, f.Height);
    }

    // ================= rec 探针 =================

    /// <summary>PP-OCRv6 small rec：输入 [1,3,48,W]、(x/255-0.5)/0.5、CTC blank=0、chars[i-1]。</summary>
    private sealed class V6RecProbe : IDisposable
    {
        private const int RecH = 48;
        private readonly InferenceSession _s;
        private readonly string _inName, _outName;
        private readonly List<string> _dict;
        private readonly int _mode;

        public V6RecProbe(string modelPath, List<string> dict, int mode, ITestOutputHelper o)
        {
            _dict = dict; _mode = mode;
            _s = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
            _inName = _s.InputMetadata.Keys.First();
            _outName = _s.OutputMetadata.Keys.First();
            if (mode == 0)
                o.WriteLine($"[rec] 输入 {_inName} [{string.Join(",", _s.InputMetadata[_inName].Dimensions)}] → " +
                            $"输出 {_outName} [{string.Join(",", _s.OutputMetadata[_outName].Dimensions)}]；字典 {dict.Count}");
        }

        public string[] RecognizeFrame(FrameData f, IReadOnlyList<(float X0, float Y0, float X1, float Y1)> boxes)
        {
            var texts = new string[boxes.Count];
            for (int i = 0; i < boxes.Count; i++) texts[i] = Recognize(f, boxes[i]);
            return texts;
        }

        private string Recognize(FrameData f, (float X0, float Y0, float X1, float Y1) box)
        {
            // 1) 原生分辨率裁出 RGB 平面
            double bw = Math.Max(1, box.X1 - box.X0), bh = Math.Max(1, box.Y1 - box.Y0);
            int cw = Math.Max(1, (int)Math.Round(bw)), ch = Math.Max(1, (int)Math.Round(bh));
            var rgb = SampleCropRgb(f, box, cw, ch);

            // 2) 变体增强（在原生裁切上做，再交给 rec 自己缩到 48 高）
            rgb = _mode switch
            {
                1 => ResizeRgb(rgb, ch, cw, ch * 2, cw * 2, false),
                2 => Unsharp(ResizeRgb(rgb, ch, cw, ch * 2, cw * 2, false), ch * 2, cw * 2, 1.2, 1.6),
                3 => ResizeRgb(rgb, ch, cw, ch * 3, cw * 3, false),
                4 => ResizeRgb(rgb, ch, cw, ch * 2, cw * 2, true),
                5 => ContrastStretch(rgb, ch, cw),
                6 => OtsuBinarize(rgb, ch, cw),
                _ => rgb,
            };
            if (_mode == 1 || _mode == 2 || _mode == 3 || _mode == 4) { ch *= (_mode == 3 ? 3 : 2); cw *= (_mode == 3 ? 3 : 2); }

            // 3) 缩到 48 高、等比宽（REC 自身那一步）
            int outW = Math.Max(4, (int)Math.Round(cw * (double)RecH / ch));
            var rec = ResizeRgb(rgb, ch, cw, RecH, outW, false);

            // 4) 归一化 → CHW
            var data = new float[3 * RecH * outW];
            int plane = RecH * outW;
            for (int i = 0; i < plane; i++)
            {
                data[i] = (float)((rec[i] / 255.0 - 0.5) / 0.5);
                data[plane + i] = (float)((rec[plane + i] / 255.0 - 0.5) / 0.5);
                data[2 * plane + i] = (float)((rec[2 * plane + i] / 255.0 - 0.5) / 0.5);
            }
            var t = new DenseTensor<float>(data, new[] { 1, 3, RecH, outW });
            using var res = _s.Run(new[] { NamedOnnxValue.CreateFromTensor(_inName, t) });
            var pred = res.First(r => r.Name == _outName).AsTensor<float>();
            int T = pred.Dimensions[1], C = pred.Dimensions[2];
            var arr = pred.ToArray();
            var sb = new StringBuilder();
            int prev = -1;
            for (int ti = 0; ti < T; ti++)
            {
                int off = ti * C, best = 0;
                float bv = float.MinValue;
                for (int c = 0; c < C; c++) { float v = arr[off + c]; if (v > bv) { bv = v; best = c; } }
                if (best == 0 || best == prev) { prev = best; continue; }
                prev = best;
                int di = best - 1;
                if (di >= 0 && di < _dict.Count) sb.Append(_dict[di]);
            }
            return sb.ToString();
        }

        public void Dispose() => _s.Dispose();
    }

    // ================= 图像 =================

    private static float[] SampleCropRgb(FrameData f, (float X0, float Y0, float X1, float Y1) box, int cw, int ch)
    {
        var dst = new float[3 * ch * cw];
        int plane = ch * cw;
        double sx = (box.X1 - box.X0) / cw, sy = (box.Y1 - box.Y0) / ch;
        for (int y = 0; y < ch; y++)
        {
            double fy = box.Y0 + (y + 0.5) * sy - 0.5;
            for (int x = 0; x < cw; x++)
            {
                double fx = box.X0 + (x + 0.5) * sx - 0.5;
                int px = Math.Clamp((int)Math.Round(fx), 0, f.Width - 1);
                int py = Math.Clamp((int)Math.Round(fy), 0, f.Height - 1);
                int i = (py * f.Width + px) * 4;
                int o = y * cw + x;
                dst[o] = f.Bgra[i + 2];
                dst[plane + o] = f.Bgra[i + 1];
                dst[2 * plane + o] = f.Bgra[i];
            }
        }
        return dst;
    }

    /// <summary>平面 RGB 双线性缩放（nearest=true 时用最近邻，用于对照锐利插值的效果）。</summary>
    private static float[] ResizeRgb(float[] src, int sh, int sw, int dh, int dw, bool nearest)
    {
        var dst = new float[3 * dh * dw];
        int sp = sh * sw, dp = dh * dw;
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < dh; y++)
            {
                double fy = (y + 0.5) * sh / dh - 0.5;
                for (int x = 0; x < dw; x++)
                {
                    double fx = (x + 0.5) * sw / dw - 0.5;
                    double v;
                    if (nearest)
                    {
                        int px = Math.Clamp((int)Math.Round(fx), 0, sw - 1), py = Math.Clamp((int)Math.Round(fy), 0, sh - 1);
                        v = src[c * sp + py * sw + px];
                    }
                    else
                    {
                        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                        double dx = fx - x0, dy = fy - y0;
                        int x1 = Math.Clamp(x0 + 1, 0, sw - 1), y1 = Math.Clamp(y0 + 1, 0, sh - 1);
                        x0 = Math.Clamp(x0, 0, sw - 1); y0 = Math.Clamp(y0, 0, sh - 1);
                        double v00 = src[c * sp + y0 * sw + x0], v10 = src[c * sp + y0 * sw + x1];
                        double v01 = src[c * sp + y1 * sw + x0], v11 = src[c * sp + y1 * sw + x1];
                        v = (v00 * (1 - dx) + v10 * dx) * (1 - dy) + (v01 * (1 - dx) + v11 * dx) * dy;
                    }
                    dst[c * dp + y * dw + x] = (float)Math.Clamp(v, 0, 255);
                }
            }
        return dst;
    }

    private static float[] Unsharp(float[] src, int h, int w, double sigma, double amount)
    {
        var dst = new float[src.Length];
        int plane = h * w;
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int yy = Math.Clamp(y + dy, 0, h - 1), xx = Math.Clamp(x + dx, 0, w - 1);
                            sum += src[c * plane + yy * w + xx]; n++;
                        }
                    double blur = sum / n;
                    double v = src[c * plane + y * w + x];
                    dst[c * plane + y * w + x] = (float)Math.Clamp(v + amount * (v - blur), 0, 255);
                }
        return dst;
    }

    private static float[] ContrastStretch(float[] src, int h, int w)
    {
        var dst = (float[])src.Clone();
        var luma = new int[256];
        int plane = h * w;
        for (int i = 0; i < plane; i++)
            luma[(int)Math.Clamp(0.299 * src[i] + 0.587 * src[plane + i] + 0.114 * src[2 * plane + i], 0, 255)]++;
        long n = plane;
        int Lo(long q) { long cum = 0; for (int v = 0; v < 256; v++) { cum += luma[v]; if (cum > q) return v; } return 255; }
        int lo = Lo((long)(0.02 * (n - 1))), hi = Lo((long)(0.98 * (n - 1)));
        if (hi - lo < 16) return dst;
        double scale = 255.0 / (hi - lo);
        for (int i = 0; i < dst.Length; i++) dst[i] = (float)Math.Clamp((src[i] - lo) * scale, 0, 255);
        return dst;
    }

    private static float[] OtsuBinarize(float[] src, int h, int w)
    {
        int plane = h * w;
        var luma = new int[256];
        for (int i = 0; i < plane; i++)
            luma[(int)Math.Clamp(0.299 * src[i] + 0.587 * src[plane + i] + 0.114 * src[2 * plane + i], 0, 255)]++;
        long total = plane, sumAll = 0;
        for (int v = 0; v < 256; v++) sumAll += (long)v * luma[v];
        double sumB = 0, maxVar = -1; int th = 128; long wB = 0;
        for (int v = 0; v < 256; v++)
        {
            wB += luma[v];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += (double)v * luma[v];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar) { maxVar = between; th = v; }
        }
        var dst = new float[src.Length];
        for (int i = 0; i < plane; i++)
        {
            double l = 0.299 * src[i] + 0.587 * src[plane + i] + 0.114 * src[2 * plane + i];
            byte b = (byte)(l > th ? 255 : 0);
            dst[i] = b; dst[plane + i] = b; dst[2 * plane + i] = b;
        }
        return dst;
    }

    // ================= 通用 =================

    private static OcrWord Norm(string text, float x1, float y1, float x2, float y2, FrameData f)
        => new(text, Math.Clamp(x1 / f.Width, 0, 1), Math.Clamp(y1 / f.Height, 0, 1),
            Math.Clamp((x2 - x1) / f.Width, 0, 1), Math.Clamp((y2 - y1) / f.Height, 0, 1));

    private static List<string> LoadDict(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        string line = i < 0 ? s : s[..i];
        return line.Length > 160 ? line[..160] + "…" : line.Trim();
    }

    private static string Loose(string s) => Regex.Replace(s, @"\s+", "");

    private void Register(string name, string kernel, Func<FrameData, OcrResult> run, Action dispose)
    {
        _order.Add((name, kernel));
        _backends.Add((name, run, dispose));
        _data[name] = new();
        _hits[name] = new();
    }

    private void ExecuteAll()
    {
        foreach (var (name, run, dispose) in _backends)
        {
            var times = new Dictionary<string, double>();
            string? failure = null;
            long memBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            for (int pass = 0; pass < 3 && failure is null; pass++)
            {
                bool last = pass == 2;
                foreach (var file in AllFrames)
                {
                    var frame = _frames[file];
                    OcrResult res;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { res = run(frame); }
                    catch (Exception ex) { failure = $"{ex.GetType().Name}: {FirstLine(ex.Message)}"; break; }
                    sw.Stop();
                    times[file] = Math.Min(times.GetValueOrDefault(file, double.MaxValue), sw.Elapsed.TotalMilliseconds);
                    if (!last) continue;
                    _data[name][file] = new FrameStat(times[file], res.Words.Select(w => w.Text.Trim()).ToArray());
                    foreach (var kf in KeywordFrames)
                        if (kf.File == file && res.ContainsJoined(kf.Keyword)) _hits[name].Add(file);
                }
            }
            try { dispose(); } catch { }
            if (failure is not null)
            {
                _failed[name] = failure;
                _report.AppendLine($"[{name}] 运行期失败：{failure}");
                _out.WriteLine($"[{name}] 运行期失败：{failure}");
                continue;
            }
            _memMb[name] = Math.Max(0, (System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 - memBefore) / (1024 * 1024));
            _report.AppendLine($"[{name}] 命中 {_hits[name].Count}/{KeywordFrames.Length}，平均 {times.Values.Average():F0} ms/帧，内存 +{_memMb[name]} MB");
            foreach (var file in AllFrames) _report.AppendLine($"    {file}: {times[file]:F0} ms");
            _out.WriteLine($"[{name}] 命中 {_hits[name].Count}/{KeywordFrames.Length}，平均 {times.Values.Average():F0} ms/帧");
        }
    }

    private void PrintDiff(string name)
    {
        if (!_data.TryGetValue(name, out var cand) || cand.Count == 0) return;
        if (!_data.TryGetValue(BaselineName, out var baseline) || baseline.Count == 0) return;
        int sameTotal = 0, onlyBase = 0, onlyCand = 0;
        var lines = new List<string>();
        foreach (var file in AllFrames)
        {
            if (!cand.TryGetValue(file, out var cs) || !baseline.TryGetValue(file, out var bs)) continue;
            var b = bs.Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var c = cs.Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var ob = b.Except(c).ToArray();
            var oc = c.Except(b).ToArray();
            int same = b.Intersect(c).Count();
            sameTotal += same; onlyBase += ob.Length; onlyCand += oc.Length;
            if (ob.Length == 0 && oc.Length == 0) continue;
            string d = $"  {file}（同 {same}）";
            if (ob.Length > 0) d += $"\n      仅基线: {Clip(ob)}";
            if (oc.Length > 0) d += $"\n      仅候选: {Clip(oc)}";
            lines.Add(d);
        }
        int total = sameTotal + onlyBase + onlyCand;
        _out.WriteLine($"[{name}] 共 {total} 词，相同 {sameTotal}（{(total == 0 ? "n/a" : $"{sameTotal * 100.0 / total:F1}%")}），仅基线 {onlyBase}，仅候选 {onlyCand}");
        foreach (var l in lines) _out.WriteLine(l);
        _report.AppendLine();
        _report.AppendLine($"## {name} 词级差异");
        _report.AppendLine($"共 {total} 词，相同 {sameTotal}（{(total == 0 ? "n/a" : $"{sameTotal * 100.0 / total:F1}%")}），仅基线 {onlyBase}，仅候选 {onlyCand}");
        foreach (var l in lines) _report.AppendLine(l);
        _report.AppendLine();
        _report.AppendLine($"## {name} 全量词表");
        foreach (var file in AllFrames)
            if (cand.TryGetValue(file, out var st)) _report.AppendLine($"  {file}: {string.Join(" | ", st.Words)}");
    }

    private static string Clip(string[] words)
    {
        var shown = words.Take(12).Select(w => "「" + (w.Length > 24 ? w[..24] + "…" : w) + "」");
        return string.Join(" ", shown) + (words.Length > 12 ? $" …共{words.Length}个" : "");
    }
}
