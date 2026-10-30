using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOcrNet;
using Xunit;
using Xunit.Abstractions;
using OcrResult = EfootballBot.Core.Vision.OcrResult;

namespace EfootballBot.Tests;

// 模型档位横评第四批（2026-10-29）：ONNX 生态里唯二「非 Paddle 骨干 + 原生中文」的候选。
//
// 前三批（survey/ocr_model_sweep_20261023.md、ocr_model_sweep_20261028.md）把 PaddleOCR
// v4/v5/v6 各档、int8、Tesseract、漫画域特化全试遍了，结论是「同量级词块 OCR 已到头」。
// 本批换的不是档位而是**家族**：
//
//   1) 度光 OCR（duguang）：ConvNeXt-Tiny 骨干 + PRelu + Gemm，纯 CNN+CTC，无 RNN/无 attention。
//      det 5.5MB + rec 7.8MB = 13.4MB，字典 7643 字。rec 输入固定 [3,3,32,300] → 输出 [3,75,7644]。
//      镜像 Luoaho/duguang-ocr-onnx 未附预处理说明，归一化只能试：本测跑
//      「/255-0.5/0.5」(Paddle 惯例)、「/255」、「ImageNet mean/std」三档，用词级结果判定。
//
//   2) Indra OCR（datalab-to/indra-ocr）：1M 参数级线识别器，ONNX Runtime + CPU，无 PyTorch。
//      rec_cjk 10.2MB，输入是**墨迹图** x[B,1,32,W]（0=纸 1=墨）+ widths[B]，输出 ids/conf/lengths，CTC。
//      预处理与解码按官方 indra_ocr/rec_preproc.py、vocab.py、pipeline.py 原样移植：
//      0.3h/0.2h 外扩 → 32 高按比例缩放 → 深底判定(dark)后决定是否取反 → blank=0、chars[i-1]。
//
// 两条都走「v6 det 出框 + 候选 rec」的隔离口径，与基线（v6 small det+rec）逐词可比；
// 另加一条度光整链（RapidOcrNet 直接吃它的 det+rec，检验 det 是否也能用）。
// 耗时列：基线含 det+rec；候选为 rec-only（det 复用基线框，不计入）。
//
// 模型落 tools/ocr_candidates_20261029/（.gitignore 覆盖 ocr_candidates_*，不随包）；目录缺失整段跳过。
public class OcrModelSweep20261029
{
    private readonly ITestOutputHelper _out;
    public OcrModelSweep20261029(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
    private static readonly string? CandidatesDir = FindDir("ocr_candidates_20261029");

    private const string BaselineName = "Simd|v6 small det+rec（基线，含 det）";

    private const int DuguangOffset = 2;   // Probe 判定：输出 7644 类 = blank + 1 占位 + 7641 字典
    private const int DuguangNorm = 1;     // Probe 判定：原始 /255（RGB 顺序），不做 mean/std

    private static string? FindDir(string leaf)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "tools", leaf);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

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
        "entry_maxlevel.png",       // 大字「1」漏读那帧
        "focus_fail_live.png",
        "difficulty_live_20261020.png",
        "inmatch_popup_tr.png",
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
    public void Sweep_Duguang_Indra_OnRealFrames()
    {
        if (CandidatesDir is null)
        {
            _out.WriteLine("（tools/ocr_candidates_20261029 不存在，跳过第四批横评）");
            return;
        }
        string cd = CandidatesDir;
        string cls = RepoFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        string v6Det = RepoFile("models", "v6", "PP-OCRv6_det_small.onnx");
        string v6Rec = RepoFile("models", "v6", "PP-OCRv6_rec_small.onnx");
        string v6Keys = RepoFile("models", "v6", "ppocrv6_small_dict.txt");

        string dgDet = Path.Combine(cd, "dg_small_det_800.onnx");
        string dgRec = Path.Combine(cd, "dg_small_rec.onnx");
        string dgVocab = Path.Combine(cd, "dg_vocab.txt");
        string indraRec = Path.Combine(cd, "indra_rec_cjk.onnx");
        string indraMeta = Path.Combine(cd, "indra_meta.json");

        foreach (var f in AllFrames) _frames[f] = LoadFrame(f);

        // —— 基线（同时充当候选的取框器，注册在最前以保证先出框）——
        var baseline = new Baseline();
        baseline.Init(v6Det, cls, v6Rec, v6Keys);
        _baseline = baseline;
        Register(BaselineName, "SimdPaddle", baseline.Run, baseline.Dispose);

        // —— 候选 1：度光整链（自带 det，检验 det 能否被 Paddle 后处理吃下）——
        if (File.Exists(dgDet) && File.Exists(dgRec) && File.Exists(dgVocab))
        {
            try
            {
                var ocr = new RapidOcr();
                ocr.InitModels(dgDet, cls, dgRec, dgVocab);
                Register("Rapid|度光 small det+rec（整链）", "RapidOcrNet", f => RapidRun(ocr, f), ocr.Dispose);
                _out.WriteLine("[加载] 度光整链 → RapidOcrNet 成功");
            }
            catch (Exception ex)
            {
                _failed["Rapid|度光 small det+rec（整链）"] = FirstLine(ex.Message);
                _out.WriteLine($"[加载] 度光整链 → 失败：{ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        else _out.WriteLine("（度光 det/rec/字典缺失，跳过整链）");

        // —— 候选 2：度光 rec（隔离）。归一化/偏移由 Probe_Duguang_Preprocessing 网格定死：
        //     /255 原值 + RGB 顺序、类别偏移 2 → 合成图 6/6 原样读回 ——
        if (File.Exists(dgRec) && File.Exists(dgVocab))
        {
            var dict = LoadDuguangDict(dgVocab);
            _out.WriteLine($"[数据] 度光字典 {dict.Count} 字（类别 {dict.Count + 3}）");
            try
            {
                var probe = new DuguangProbe(dgRec, dict, DuguangNorm, DuguangOffset, _out);
                Register("v6 det + 度光 rec（/255、偏移2）", "ORT",
                    f => RunOverBaselineBoxes(f, boxes => probe.RecognizeFrame(f, boxes)), probe.Dispose);
            }
            catch (Exception ex)
            {
                _failed["v6 det + 度光 rec（/255、偏移2）"] = FirstLine(ex.Message);
                _out.WriteLine($"[加载] 度光 rec → 失败：{FirstLine(ex.Message)}");
            }
        }

        // —— 候选 3：Indra rec_cjk → 判定「不可接入」，不参与打分 ——
        //    证据（见 Debug_Indra_Crop）：① 官方 pipeline.py 喂给识别器的不是普通裁剪，而是
        //    line_mask.py 里 CenterLine 掩膜(mask.onnx)处理过的 96px 窗口；② 发布的 ONNX 包里
        //    没有 mask.onnx；③ 按 crop_line 原样移植后，真实帧与合成白底黑字行都只出全 0 的
        //    ids（blank），换 6 种数值约定也无改善。复现需要额外的掩膜模型 + 窗口语义，成本远高于收益。
        if (File.Exists(indraRec) && File.Exists(indraMeta))
        {
            _failed["v6 det + Indra rec_cjk（隔离）"] =
                "不可接入：官方识别器需 mask.onnx 处理的 96px 窗口，发布包缺该文件；按 crop_line 移植后 ids 全 blank";
            _out.WriteLine("[跳过] Indra rec_cjk：缺 mask.onnx（CenterLine 掩膜），按官方 crop_line 移植后 ids 全 blank");
        }
        else _out.WriteLine("（Indra rec_cjk / meta 缺失，跳过）");

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
            _out.WriteLine($"| {name} | {kernel} | {_hits[name].Count}/{KeywordFrames.Length} | {st.Average(x => x.Ms):F0} ms | " +
                           $"{st.Max(x => x.Ms):F0} ms | {_memMb[name]} MB | {string.Join("；", missed)} |");
        }
        _out.WriteLine("");
        _out.WriteLine("（耗时口径：基线含 det+rec；候选为 rec-only——det 复用基线框，不计入。行高均 32。）");

        _out.WriteLine("");
        _out.WriteLine("## 词级差异（vs 基线，空白归一后逐词比较）");
        foreach (var (name, _) in _order)
            if (name != BaselineName && !_failed.ContainsKey(name)) PrintDiff(name);

        string reportPath = Path.Combine(cd, "sweep_20261029_report.txt");
        File.WriteAllText(reportPath, _report.ToString(), new UTF8Encoding(false));
        _out.WriteLine("");
        _out.WriteLine($"（全量词表与差异已写入 {reportPath}）");
    }

    // ================= 基线 =================

    private Baseline? _baseline;

    private sealed class Baseline
    {
        private Sdcb.SimdPaddleOCR.PaddleOcrAll? _ocr;
        private readonly Dictionary<FrameData, List<(float, float, float, float)>> _boxes =
            new(ReferenceEqualityComparer.Instance);

        public void Init(string det, string cls, string rec, string keys)
        {
            _ocr = Sdcb.SimdPaddleOCR.PaddleOcrAll.Load(det, cls, rec, keys,
                new Sdcb.SimdPaddleOCR.PaddleOcrOptions
                {
                    UseDirectionClassification = false,
                    Detector = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Classifier = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                    Recognizer = new() { Backend = Sdcb.SimdPaddleOCR.OcrBackend.Cpu },
                });
        }

        public IReadOnlyList<(float X0, float Y0, float X1, float Y1)> BoxesOf(FrameData f)
            => _boxes.TryGetValue(f, out var b)
                ? b
                : (IReadOnlyList<(float X0, float Y0, float X1, float Y1)>)Array.Empty<(float, float, float, float)>();

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

    // ================= Indra 调试：把模型实际看到的墨迹图 dump 出来 =================

    /// <summary>Indra 吃的是「墨迹图」（0=纸 1=墨）。整帧只读出 -/• 时，先确认预处理后的图长什么样。</summary>
    [Fact]
    public void Debug_Indra_Crop()
    {
        if (CandidatesDir is null) { _out.WriteLine("（候选目录缺失，跳过）"); return; }
        string cd = CandidatesDir;
        string indraRec = Path.Combine(cd, "indra_rec_cjk.onnx");
        string indraMeta = Path.Combine(cd, "indra_meta.json");
        if (!File.Exists(indraRec)) { _out.WriteLine("（Indra 模型缺失，跳过）"); return; }

        const string frameName = "difficulty_new_ui.png";
        var frame = LoadFrame(frameName);

        string cls = RepoFile("models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var baseline = new Baseline();
        baseline.Init(RepoFile("models", "v6", "PP-OCRv6_det_small.onnx"), cls,
            RepoFile("models", "v6", "PP-OCRv6_rec_small.onnx"), RepoFile("models", "v6", "ppocrv6_small_dict.txt"));
        baseline.Run(frame);
        var boxes = baseline.BoxesOf(frame);
        _out.WriteLine($"{frameName}: 基线出 {boxes.Count} 个框");

        var vocab = LoadIndraVocab(indraMeta);
        using var probe = new IndraProbe(indraRec, vocab, _out);

        var top = boxes.OrderByDescending(b => (b.X1 - b.X0) * (b.Y1 - b.Y0)).Take(4).ToArray();
        for (int i = 0; i < top.Length; i++)
        {
            var ink = IndraProbe.CropLineInkPublic(frame, top[i], out int w);
            _out.WriteLine($"[框{i}] ({top[i].X0:F0},{top[i].Y0:F0})-({top[i].X1:F0},{top[i].Y1:F0}) " +
                           $"宽{top[i].X1 - top[i].X0:F0} → 墨迹图 32×{w}，min {ink.Min():F2} max {ink.Max():F2} mean {ink.Average():F2}");
            SaveInkPng(ink, 32, w, Path.Combine(cd, $"indra_crop_{i}.png"));
            // 输入数值约定扫描：官方墨迹图 / ×255 / 居中 / 取反 …
            var inv = ink.Select(v => 1f - v).ToArray();
            var variants = new (string Name, float[] Data)[]
            {
                ("墨迹 0..1", ink),
                ("墨迹 ×255", ink.Select(v => v * 255f).ToArray()),
                ("墨迹 居中", ink.Select(v => (v - 0.5f) / 0.5f).ToArray()),
                ("取反 0..1", inv),
                ("取反 ×255", inv.Select(v => v * 255f).ToArray()),
                ("取反 居中", inv.Select(v => (v - 0.5f) / 0.5f).ToArray()),
            };
            foreach (var (vn, vd) in variants)
                _out.WriteLine($"    {vn,-10}: 「{probe.RunOne(vd, w, out var vi)}」  {vi}");
        }
        // 合成图对照：白底黑字的「标准文档行」，若这个也读不出，就与游戏帧的裁剪无关
        var synth = RenderLine("选择赛事等级");
        var sbox = (X0: 0f, Y0: 0f, X1: 300f, Y1: 32f);
        var sink = IndraProbe.CropLineInkPublic(synth, sbox, out int sw);
        SaveInkPng(sink, 32, sw, Path.Combine(cd, "indra_synth.png"));
        _out.WriteLine($"[合成图] 墨迹 32×{sw} min {sink.Min():F2} max {sink.Max():F2} mean {sink.Average():F2}");
        foreach (var (vn, vd) in new (string, float[])[]
                 {
                     ("墨迹 0..1", sink),
                     ("墨迹 ×255", sink.Select(v => v * 255f).ToArray()),
                     ("取反 0..1", sink.Select(v => 1f - v).ToArray()),
                     ("取反 ×255", sink.Select(v => (1f - v) * 255f).ToArray()),
                 })
            _out.WriteLine($"    {vn,-10}: 「{probe.RunOne(vd, sw, out var si)}」  {si}");
        // 窗口口径（96px，行占 1/3）对照
        for (int i = 0; i < Math.Min(2, top.Length); i++)
        {
            var wink = IndraProbe.CropWindowInk(frame, top[i], out int ww);
            _out.WriteLine($"[窗口口径 框{i}] 墨迹 32×{ww} mean {wink.Average():F2}");
            foreach (var (vn, vd) in new (string, float[])[]
                     {
                         ("墨迹 0..1", wink),
                         ("墨迹 ×255", wink.Select(v => v * 255f).ToArray()),
                     })
                _out.WriteLine($"    {vn,-10}: 「{probe.RunOne(vd, ww, out var wi)}」  {wi}");
        }
        _out.WriteLine($"[整帧] {string.Join(" | ", probe.RecognizeFrame(frame, boxes))}");
    }

    private static void SaveInkPng(float[] ink, int h, int w, string path)
    {
        const int scale = 4;
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)Math.Clamp((int)(ink[y * w + x] * 255), 0, 255);
                bmp.SetPixel(x, y, new SkiaSharp.SKColor(v, v, v));
            }
        using var big = new SkiaSharp.SKBitmap(w * scale, h * scale);
        bmp.ScalePixels(big, SkiaSharp.SKFilterQuality.None);
        using var img = SkiaSharp.SKImage.FromBitmap(big);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    // ================= 度光预处理探针：归一化 × 字典偏移 =================

    /// <summary>
    /// 度光镜像没给预处理与字典对齐规则，用合成图做网格搜索：渲染干净文字 → 跑 rec →
    /// 找出「能原样读回」的（归一化, 类别偏移）组合。输出 7644 类、字典 7641 字，中间差 3，
    /// 所以偏移要试 1/2/3。
    /// </summary>
    [Fact]
    public void Probe_Duguang_Preprocessing()
    {
        if (CandidatesDir is null) { _out.WriteLine("（候选目录缺失，跳过探针）"); return; }
        string cd = CandidatesDir;
        string dgRec = Path.Combine(cd, "dg_small_rec.onnx");
        string dgVocab = Path.Combine(cd, "dg_vocab.txt");
        if (!File.Exists(dgRec) || !File.Exists(dgVocab)) { _out.WriteLine("（度光模型缺失，跳过探针）"); return; }

        var dict = LoadDuguangDict(dgVocab);
        string[] cases = { "选择赛事等级", "下一步", "前往比赛", "我的联赛", "1120", "最近5场比赛" };
        var frames = cases.Select(RenderLine).ToArray();
        var box = (X0: 0f, Y0: 0f, X1: 300f, Y1: 32f);

        _out.WriteLine($"| 归一化 | 偏移 | 读回 |");
        _out.WriteLine($"|---|---|---|");
        foreach (var norm in new[] { 0, 1, 2, 3 })
            foreach (var off in new[] { 1, 2, 3 })
            {
                using var probe = new DuguangProbe(dgRec, dict, norm, off);
                var got = probe.RecognizeFrame(frames[0], new[] { box });
                var all = frames.Select(fr => probe.RecognizeFrame(fr, new[] { box })[0]).ToArray();
                int exact = all.Zip(cases).Count(p => p.First == p.Second);
                _out.WriteLine($"| norm{norm} | {off} | {exact}/{cases.Length} 全对；首帧「{got[0]}」；全量 {string.Join(" / ", all)} |");
            }
    }

    /// <summary>渲染一行 32 高、300 宽的白底黑字图（近似 rec 输入）。</summary>
    private static FrameData RenderLine(string text)
    {
        using var bmp = new SkiaSharp.SKBitmap(300, 32, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.Black,
                TextSize = 24,
                IsAntialias = true,
                Typeface = SkiaSharp.SKTypeface.FromFamilyName("Microsoft YaHei"),
            };
            canvas.DrawText(text, 6, 25, paint);
        }
        var bytes = new byte[300 * 32 * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), bytes, 0, bytes.Length);
        return new FrameData(bytes, 300, 32, 0);
    }

    // ================= 候选实现 =================

    /// <summary>度光 rec：固定 [3,3,32,300]，输出 [3,75,7644]，blank=0、chars[i-1]。</summary>
    private sealed class DuguangProbe : IDisposable
    {
        private const int H = 32, W = 300, Batch = 3;
        private readonly InferenceSession _s;
        private readonly string _inName, _outName;
        private readonly List<string> _dict;
        private readonly int _norm, _offset;

        public DuguangProbe(string modelPath, List<string> dict, int norm, int offset, ITestOutputHelper? o = null)
        {
            _dict = dict; _norm = norm; _offset = offset;
            _s = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
            _inName = _s.InputMetadata.Keys.First();
            _outName = _s.OutputMetadata.Keys.First();
            o?.WriteLine($"[度光] 输入 {_inName} [{string.Join(",", _s.InputMetadata[_inName].Dimensions)}] → " +
                         $"输出 {_outName} [{string.Join(",", _s.OutputMetadata[_outName].Dimensions)}]；字典 {dict.Count}，偏移 {offset}");
        }

        public string[] RecognizeFrame(FrameData f, IReadOnlyList<(float X0, float Y0, float X1, float Y1)> boxes)
        {
            var lines = new float[boxes.Count][];
            for (int i = 0; i < boxes.Count; i++) lines[i] = SampleBoxRgb(f, boxes[i], H, _norm);
            return lines.Length == 0 ? Array.Empty<string>() : RunBatch(lines);
        }

        private string[] RunBatch(IReadOnlyList<float[]> lines)
        {
            var results = new string[lines.Count];
            for (int start = 0; start < lines.Count; start += Batch)
            {
                int n = Math.Min(Batch, lines.Count - start);
                var data = new float[Batch * 3 * H * W];
                for (int b = 0; b < Batch; b++)
                    Array.Copy(lines[start + Math.Min(b, n - 1)], 0, data, b * 3 * H * W, 3 * H * W);
                var t = new DenseTensor<float>(data, new[] { Batch, 3, H, W });
                using var res = _s.Run(new[] { NamedOnnxValue.CreateFromTensor(_inName, t) });
                var pred = res.First(r => r.Name == _outName).AsTensor<float>();
                int T = pred.Dimensions[1], C = pred.Dimensions[2];
                var arr = pred.ToArray();
                for (int b = 0; b < n; b++)
                {
                    var sb = new StringBuilder();
                    int prev = -1;
                    for (int ti = 0; ti < T; ti++)
                    {
                        int off = (b * T + ti) * C, best = 0;
                        float bv = float.MinValue;
                        for (int c = 0; c < C; c++)
                        {
                            float v = arr[off + c];
                            if (v > bv) { bv = v; best = c; }
                        }
                        if (best == 0 || best == prev) { prev = best; continue; }
                        prev = best;
                        int di = best - _offset;   // 类别 = 偏移 + 字典(7641)，输出 7644 → 偏移待定，由探针定
                        if (di >= 0 && di < _dict.Count) sb.Append(_dict[di]);
                    }
                    results[start + b] = sb.ToString();
                }
            }
            return results;
        }

        public void Dispose() => _s.Dispose();
    }

    /// <summary>Indra rec：x[B,1,32,W] 墨迹图 + widths[B] → ids/conf/lengths，CTC blank=0、chars[i-1]。</summary>
    private sealed class IndraProbe : IDisposable
    {
        private const int H = 32;
        private const double PadY = 0.2, PadX = 0.3;
        private readonly InferenceSession _s;
        private readonly List<string> _chars;
        private readonly string _xName, _wName;

        public IndraProbe(string modelPath, List<string> chars, ITestOutputHelper o)
        {
            _chars = chars;
            _s = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
            _xName = _s.InputMetadata.Keys.First();
            _wName = _s.InputMetadata.Keys.Skip(1).First();
            o.WriteLine($"[Indra] 输入 {_xName} [{string.Join(",", _s.InputMetadata[_xName].Dimensions)}] / " +
                        $"{_wName} [{string.Join(",", _s.InputMetadata[_wName].Dimensions)}]；输出 {string.Join("/", _s.OutputMetadata.Keys)}");
        }

        /// <summary>整帧一次批量（宽度右补 0 到 8 的倍数，widths 记真实宽）。</summary>
        public string[] RecognizeFrame(FrameData f, IReadOnlyList<(float X0, float Y0, float X1, float Y1)> boxes)
        {
            int n = boxes.Count;
            var inks = new float[n][];
            var widths = new int[n];
            int maxW = 8;
            for (int i = 0; i < n; i++)
            {
                inks[i] = CropLineInk(f, boxes[i], out int w);
                widths[i] = w;
                maxW = Math.Max(maxW, w);
            }
            maxW = (int)(Math.Ceiling(maxW / 8.0) * 8);
            var data = new float[n * H * maxW];
            for (int i = 0; i < n; i++)
                Array.Copy(inks[i], 0, data, i * H * maxW, inks[i].Length);
            var wt = new long[n];
            for (int i = 0; i < n; i++) wt[i] = widths[i];

            var xt = new DenseTensor<float>(data, new[] { n, 1, H, maxW });
            var wtt = new DenseTensor<long>(wt, new[] { n });
            using var res = _s.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor(_xName, xt),
                NamedOnnxValue.CreateFromTensor(_wName, wtt),
            });
            var ids = AsLongs(res.First(r => r.Name == "ids"));
            var lens = AsLongs(res.First(r => r.Name == "lengths"));
            int step = n > 0 ? ids.Length / n : 0;   // 输出时间步 ≠ 输入宽度（有下采样），按张量实际步数走
            var texts = new string[n];
            for (int b = 0; b < n; b++)
            {
                int len = (int)Math.Min(lens[b], step);
                var sb = new StringBuilder();
                long prev = -1;
                for (int t = 0; t < len; t++)
                {
                    long id = ids[b * step + t];
                    bool keep = id != 0 && (t == 0 || id != prev);
                    prev = id;
                    if (!keep) continue;
                    int ci = (int)id - 1;
                    if (ci >= 0 && ci < _chars.Count) sb.Append(_chars[ci]);
                }
                texts[b] = sb.ToString();
            }
            return texts;
        }

        /// <summary>调试用：直接喂一张墨迹图，返回解码文本与原始张量形状/前几个 id。</summary>
        public string RunOne(float[] ink, int w, out string info)
        {
            int wm = (int)(Math.Ceiling(w / 8.0) * 8);
            var data = new float[H * wm];
            Array.Copy(ink, data, Math.Min(ink.Length, data.Length));
            var xt = new DenseTensor<float>(data, new[] { 1, 1, H, wm });
            var wt = new DenseTensor<long>(new long[] { w }, new[] { 1 });
            using var res = _s.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor(_xName, xt),
                NamedOnnxValue.CreateFromTensor(_wName, wt),
            });
            var ids = AsLongs(res.First(r => r.Name == "ids"));
            var lens = AsLongs(res.First(r => r.Name == "lengths"));
            info = string.Join(" | ", res.Select(r => r.Name + "=" + Describe(r))) +
                   " ; lens0=" + (lens.Length > 0 ? lens[0].ToString() : "-") + " ; nz=" + ids.Count(x => x != 0);
            var sb = new StringBuilder();
            long prev = -1;
            for (int t = 0; t < ids.Length; t++)
            {
                long id = ids[t];
                bool keep = id != 0 && (t == 0 || id != prev);
                prev = id;
                if (!keep) continue;
                int ci = (int)id - 1;
                if (ci >= 0 && ci < _chars.Count) sb.Append(_chars[ci]);
            }
            return sb.ToString();
        }

        private static string Describe(NamedOnnxValue v)
        {
            try
            {
                var f = v.AsTensor<float>();
                return $"[{string.Join(",", f.Dimensions.ToArray())}]f";
            }
            catch { /* 非 float */ }
            try
            {
                var l = v.AsTensor<long>();
                var a = l.ToArray();
                return $"[{string.Join(",", l.Dimensions.ToArray())}]i:{(a.Length > 0 ? string.Join(",", a.Take(16)) : "")}";
            }
            catch { return "?"; }
        }

        private static long[] AsLongs(NamedOnnxValue v)
        {
            try { return v.AsTensor<long>().ToArray(); }
            catch (Exception)
            {
                try { return v.AsTensor<int>().Select(x => (long)x).ToArray(); }
                catch (Exception) { return v.AsTensor<short>().Select(x => (long)x).ToArray(); }
            }
        }

        /// <summary>官方 rec_preproc.crop_line：外扩 → 裁（越界补纸色）→ 32 高按比例 → 深底判定决定是否取反。</summary>
        /// <summary>官方口径：先取 1.0h 上下、0.5h 左右的窗口（96px 高），行约占 1/3，再缩到 32 高。</summary>
        public static float[] CropWindowInk(FrameData f, (float X0, float Y0, float X1, float Y1) box, out int outW)
        {
            double h = Math.Max(1.0, box.Y1 - box.Y0);
            double X0 = box.X0 - 0.5 * h, X1 = box.X1 + 0.5 * h;
            double Y0 = box.Y0 - 1.0 * h, Y1 = box.Y1 + 1.0 * h;
            double cw = Math.Max(1, Math.Ceiling(X1) - Math.Floor(X0));
            double ch = Math.Max(1, Math.Ceiling(Y1) - Math.Floor(Y0));
            bool dark = IsDarkLine(f, box);
            outW = Math.Max(8, Math.Min(4096, (int)Math.Round(cw * H / ch)));
            var resized = SampleRegionGray(f, X0, Y0, X1, Y1, outW, H, dark ? 0 : 255);
            for (int i = 0; i < resized.Length; i++)
            {
                float v = resized[i] / 255f;
                resized[i] = dark ? v : 1f - v;
            }
            return resized;
        }

        public static float[] CropLineInkPublic(FrameData f, (float X0, float Y0, float X1, float Y1) box, out int outW) => CropLineInk(f, box, out outW);

        private static float[] CropLineInk(FrameData f, (float X0, float Y0, float X1, float Y1) box, out int outW)
        {
            double x0 = box.X0, y0 = box.Y0, x1 = box.X1, y1 = box.Y1;
            double h = Math.Max(1.0, y1 - y0);
            double X0 = x0 - PadX * h, X1 = x1 + PadX * h;
            double Y0 = y0 - PadY * h, Y1 = y1 + PadY * h;
            double cw = Math.Max(1, Math.Ceiling(X1) - Math.Floor(X0));
            double ch = Math.Max(1, Math.Ceiling(Y1) - Math.Floor(Y0));
            bool dark = IsDarkLine(f, box);
            double fill = dark ? 0 : 255;
            outW = Math.Max(8, Math.Min(4096, (int)Math.Round(cw * H / ch)));
            var resized = SampleRegionGray(f, X0, Y0, X1, Y1, outW, H, fill);
            for (int i = 0; i < resized.Length; i++)
            {
                float v = resized[i] / 255f;
                resized[i] = dark ? v : 1f - v;   // 深底保留、浅底取反：结果恒为「亮墨」
            }
            return resized;
        }

        /// <summary>官方 is_dark_line：亮尾(p98-p50) 明显长于暗尾(p50-p2) → 深底。</summary>
        private static bool IsDarkLine(FrameData f, (float X0, float Y0, float X1, float Y1) box)
        {
            int x0 = Math.Max(0, (int)box.X0), x1 = Math.Min(f.Width - 1, (int)box.X1);
            int y0 = Math.Max(0, (int)box.Y0), y1 = Math.Min(f.Height - 1, (int)box.Y1);
            if (x1 < x0 || y1 < y0) return false;
            var hist = new int[256];
            long n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    hist[Luma(f.Bgra, (y * f.Width + x) * 4)]++;
                    n++;
                }
            if (n == 0) return false;
            double p2 = Percentile(hist, n, 0.02), p50 = Percentile(hist, n, 0.50), p98 = Percentile(hist, n, 0.98);
            return p98 - p50 > 2 * (p50 - p2) + 8;
        }

        private static double Percentile(int[] hist, long n, double q)
        {
            double pos = q * (n - 1);
            long cum = 0;
            for (int v = 0; v < 256; v++)
            {
                cum += hist[v];
                if (cum > pos) return v;
            }
            return 255;
        }

        public void Dispose() => _s.Dispose();
    }

    // ================= 采样 =================

    private static int Luma(byte[] bgra, int i)
        => (int)(0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2]);   // BGRA

    private static float[] SampleRegionGray(FrameData f, double x0, double y0, double x1, double y1,
        int outW, int outH, double fill)
    {
        var dst = new float[outW * outH];
        double sx = (x1 - x0) / outW, sy = (y1 - y0) / outH;
        for (int oy = 0; oy < outH; oy++)
        {
            double fy = y0 + (oy + 0.5) * sy - 0.5;
            for (int ox = 0; ox < outW; ox++)
            {
                double fx = x0 + (ox + 0.5) * sx - 0.5;
                dst[oy * outW + ox] = (float)SampleGray(f, fx, fy, fill);
            }
        }
        return dst;
    }

    private static double SampleGray(FrameData f, double fx, double fy, double fill)
    {
        if (fx < -0.5 || fy < -0.5 || fx > f.Width - 0.5 || fy > f.Height - 0.5) return fill;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double dx = fx - x0, dy = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, f.Width - 1), y1 = Math.Clamp(y0 + 1, 0, f.Height - 1);
        x0 = Math.Clamp(x0, 0, f.Width - 1); y0 = Math.Clamp(y0, 0, f.Height - 1);
        double v00 = Luma(f.Bgra, (y0 * f.Width + x0) * 4), v10 = Luma(f.Bgra, (y0 * f.Width + x1) * 4);
        double v01 = Luma(f.Bgra, (y1 * f.Width + x0) * 4), v11 = Luma(f.Bgra, (y1 * f.Width + x1) * 4);
        return (v00 * (1 - dx) + v10 * dx) * (1 - dy) + (v01 * (1 - dx) + v11 * dx) * dy;
    }

    /// <summary>度光 rec 预处理：按框裁 RGB、保持比例缩到高 32、右侧补 0 到 300，再按变体归一化 → CHW。</summary>
    private static float[] SampleBoxRgb(FrameData f, (float X0, float Y0, float X1, float Y1) box, int outH, int norm)
    {
        const int W = 300;
        double bw = Math.Max(1, box.X1 - box.X0), bh = Math.Max(1, box.Y1 - box.Y0);
        int drawn = Math.Clamp((int)Math.Round(bw * outH / bh), 1, W);
        var chw = new float[3 * outH * W];
        double sx = bw / drawn, sy = bh / outH;
        int plane = outH * W;
        for (int oy = 0; oy < outH; oy++)
        {
            double fy = box.Y0 + (oy + 0.5) * sy - 0.5;
            for (int ox = 0; ox < drawn; ox++)
            {
                double fx = box.X0 + (ox + 0.5) * sx - 0.5;
                int x = Math.Clamp((int)Math.Round(fx), 0, f.Width - 1);
                int y = Math.Clamp((int)Math.Round(fy), 0, f.Height - 1);
                int i = (y * f.Width + x) * 4;
                double r = f.Bgra[i + 2] / 255.0, g = f.Bgra[i + 1] / 255.0, b = f.Bgra[i] / 255.0;
                double rr, gg, bb;
                switch (norm)
                {
                    case 0: rr = (r - 0.5) / 0.5; gg = (g - 0.5) / 0.5; bb = (b - 0.5) / 0.5; break;
                    case 1: rr = r; gg = g; bb = b; break;
                    case 2:
                        rr = (r - 0.485) / 0.229; gg = (g - 0.456) / 0.224; bb = (b - 0.406) / 0.225; break;
                    default:   // BGR 顺序 + /255-0.5/0.5
                        rr = (b - 0.5) / 0.5; gg = (g - 0.5) / 0.5; bb = (r - 0.5) / 0.5; break;
                }
                int idx = oy * W + ox;
                chw[idx] = (float)rr;
                chw[plane + idx] = (float)gg;
                chw[2 * plane + idx] = (float)bb;
            }
        }
        return chw;
    }

    // ================= 字典/词表 =================

    private static List<string> LoadDuguangDict(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static List<string> LoadIndraVocab(string metaPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
        // meta.json 里 vocab_cjk 是一段被二次编码的 JSON 字符串
        string raw = doc.RootElement.GetProperty("vocab_cjk").GetString()!;
        using var inner = JsonDocument.Parse(raw);
        var chars = new List<string>();
        foreach (var c in inner.RootElement.GetProperty("chars").EnumerateArray())
            chars.Add(c.GetString() ?? "");
        return chars;
    }

    // ================= 执行/报告 =================

    private void Register(string name, string kernel, Func<FrameData, OcrResult> run, Action dispose)
    {
        _order.Add((name, kernel));
        _backends.Add((name, run, dispose));
        _data[name] = new();
        _hits[name] = new();
    }

    private static OcrResult RapidRun(RapidOcr ocr, FrameData f)
    {
        var info = new SkiaSharp.SKImageInfo(f.Width, f.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        var words = new List<OcrWord>();
        unsafe
        {
            fixed (byte* pixels = f.Bgra)
            {
                using var bmp = new SkiaSharp.SKBitmap();
                if (!bmp.InstallPixels(info, (IntPtr)pixels, info.RowBytes))
                    throw new InvalidOperationException("SKBitmap 像素装载失败。");
                var result = ocr.Detect(bmp, RapidOcrOptions.PPOCRv6 with { DoAngle = false });
                foreach (var b in result.TextBlocks)
                {
                    if (string.IsNullOrWhiteSpace(b.Text)) continue;
                    float minX = b.BoxPoints.Min(p => p.X), maxX = b.BoxPoints.Max(p => p.X);
                    float minY = b.BoxPoints.Min(p => p.Y), maxY = b.BoxPoints.Max(p => p.Y);
                    words.Add(Norm(b.Text, minX, minY, maxX, maxY, f));
                }
            }
        }
        return new OcrResult(words, f.Width, f.Height);
    }

    private static OcrWord Norm(string text, float x1, float y1, float x2, float y2, FrameData f)
        => new(text,
            Math.Clamp(x1 / f.Width, 0, 1),
            Math.Clamp(y1 / f.Height, 0, 1),
            Math.Clamp((x2 - x1) / f.Width, 0, 1),
            Math.Clamp((y2 - y1) / f.Height, 0, 1));

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
                    catch (Exception ex)
                    {
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

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        string line = i < 0 ? s : s[..i];
        return line.Length > 160 ? line[..160] + "…" : line.Trim();
    }

    private static string Loose(string s) => Regex.Replace(s, @"\s+", "");

    private void PrintDiff(string name)
    {
        if (!_data.TryGetValue(name, out var cand) || cand.Count == 0) return;
        if (!_data.TryGetValue(BaselineName, out var baseline) || baseline.Count == 0) return;

        int sameTotal = 0, onlyBaseTotal = 0, onlyCandTotal = 0;
        var lines = new List<string>();
        foreach (var file in AllFrames)
        {
            if (!cand.TryGetValue(file, out var cs) || !baseline.TryGetValue(file, out var bs)) continue;
            var b = bs.Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var c = cs.Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
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
            if (cand.TryGetValue(file, out var st))
                _report.AppendLine($"  {file}: {string.Join(" | ", st.Words)}");
    }

    private static string Clip(string[] words)
    {
        const int max = 14;
        var shown = words.Take(max).Select(w => "「" + (w.Length > 24 ? w[..24] + "…" : w) + "」");
        return string.Join(" ", shown) + (words.Length > max ? $" …共{words.Length}个" : "");
    }
}
