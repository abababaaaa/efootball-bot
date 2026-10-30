using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// CRAFT 字符级检测探针（2026-10-29）：回答「描边/发光的美术字能不能被拆成单字」。
//
// 动机：现有链路的失败在识别（整行 CTC 一起错），字符级定位能把识别单元变小，
// 也能让「逐字模板匹配」这种零模型的路线成立。先只评定位，不接识别。
//
// CRAFT 官方后处理是 numpy 实现、没有 ONNX 版本，这里是精简移植：
// score map（逐字符概率）阈值 0.7 → 连通域 → 每个连通域一个字符框（不做 affinity 合并成词框）。
// 输入按官方口径：RGB、ImageNet mean/std、长边缩到 1280、边长对齐 32。
public class CraftCharProbe20261029
{
    private readonly ITestOutputHelper _out;
    public CraftCharProbe20261029(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
    private static readonly string? CandidatesDir = FindDir("ocr_candidates_20261029");

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

    [Fact]
    public void Probe_Craft_CharacterBoxes()
    {
        if (CandidatesDir is null) { _out.WriteLine("（候选目录缺失，跳过）"); return; }
        string model = Path.Combine(CandidatesDir, "craft_mlt25k.onnx");
        if (!File.Exists(model)) { _out.WriteLine("（craft_mlt25k.onnx 缺失，跳过）"); return; }

        using var sess = new InferenceSession(model, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
        foreach (var kv in sess.InputMetadata)
            _out.WriteLine($"[CRAFT 输入] {kv.Key} [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementType}");
        foreach (var kv in sess.OutputMetadata)
            _out.WriteLine($"[CRAFT 输出] {kv.Key} [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementType}");

        string[] frames = { "difficulty_new_ui.png", "entry_maxlevel.png", "itempage_inuse_1.png", "focus_fail_live.png" };
        foreach (var name in frames)
        {
            var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
            var sw = Stopwatch.StartNew();
            var boxes = DetectChars(sess, data, w, h, out int charsTotal);
            sw.Stop();
            _out.WriteLine($"\n[{name}] {w}×{h}：字符连通域 {charsTotal}（过滤后 {boxes.Count} 个），" +
                           $"耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
            var hist = boxes.GroupBy(b => (int)Math.Round(b.H)).OrderByDescending(g => g.Count()).Take(4)
                .Select(g => $"高≈{g.Key}px×{g.Count()}").ToArray();
            _out.WriteLine($"  尺寸分布: {string.Join("  ", hist)}");
            SaveAnnotated(data, w, h, boxes, Path.Combine(CandidatesDir, $"craft_{Path.GetFileNameWithoutExtension(name)}.png"));
        }
    }

    /// <summary>只对美术字标题区做 CRAFT：把 ROI 放大后单独送检，看能否把 6 个字拆开。</summary>
    [Fact]
    public void Probe_Craft_TitleRoi()
    {
        if (CandidatesDir is null) { _out.WriteLine("（候选目录缺失，跳过）"); return; }
        string model = Path.Combine(CandidatesDir, "craft_mlt25k.onnx");
        if (!File.Exists(model)) { _out.WriteLine("（craft 缺失，跳过）"); return; }

        using var sess = new InferenceSession(model, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
        // 美术字标题：(269,57)-(531,103)「选择赛事等级」；窄条页的名词区也测一条
        var cases = new[]
        {
            ("difficulty_new_ui.png", 260f, 50f, 540f, 112f, "选择赛事等级（描边美术字）"),
            ("difficulty_new_ui.png", 460f, 140f, 720f, 168f, "选择符合你技术水平的难度级别（小字）"),
            ("home_nav_cursor.png", 0f, 0f, 0f, 0f, "占位"),
        };
        foreach (var (file, x0, y0, x1, y1, label) in cases)
        {
            if (x1 <= x0) continue;
            var src = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, file), out int w, out int h);
            int cw = (int)(x1 - x0), ch = (int)(y1 - y0);
            // 裁 ROI → 放大 3 倍（模拟「ROI 放大后再检测」）
            const int S = 3;
            var crop = new byte[cw * S * ch * S * 4];
            for (int y = 0; y < ch * S; y++)
                for (int x = 0; x < cw * S; x++)
                {
                    int sx = Math.Clamp((int)(x0 + x / (double)S), 0, w - 1);
                    int sy = Math.Clamp((int)(y0 + y / (double)S), 0, h - 1);
                    int si = (sy * w + sx) * 4;
                    int di = (y * cw * S + x) * 4;
                    crop[di] = src[si]; crop[di + 1] = src[si + 1]; crop[di + 2] = src[si + 2]; crop[di + 3] = 255;
                }
            var sw = Stopwatch.StartNew();
            var boxes = DetectChars(sess, crop, cw * S, ch * S, out int total);
            sw.Stop();
            _out.WriteLine($"[{label}] ROI {cw}×{ch} → 放大 {cw * S}×{ch * S}：连通域 {total}，过滤后 {boxes.Count} 个，耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
            _out.WriteLine($"  框: {string.Join("  ", boxes.OrderBy(b => b.X).Take(10).Select(b => $"({b.X:F0},{b.Y:F0},{b.W:F0}×{b.H:F0})"))}");
            SaveAnnotated(crop, cw * S, ch * S, boxes, Path.Combine(CandidatesDir, $"craft_roi_{Path.GetFileNameWithoutExtension(file)}_{ch}.png"));
        }
    }

    /// <summary>
    /// 决定性验证：CRAFT 拆出的单字框逐个喂现役 PP-OCRv6 rec，能不能认对。
    /// 对照 = 同一行整行喂 rec（现役做法）。若逐字反而更好 → 字符级路线成立；若更差 → 否决。
    /// </summary>
    [Fact]
    public void Probe_Craft_PerCharRec()
    {
        if (CandidatesDir is null) { _out.WriteLine("（候选目录缺失，跳过）"); return; }
        string craftModel = Path.Combine(CandidatesDir, "craft_mlt25k.onnx");
        if (!File.Exists(craftModel)) { _out.WriteLine("（craft 缺失，跳过）"); return; }

        string recPath = RepoFile("models", "v6", "PP-OCRv6_rec_small.onnx");
        string keysPath = RepoFile("models", "v6", "ppocrv6_small_dict.txt");
        var dict = File.ReadAllLines(keysPath, Encoding.UTF8).ToList();
        while (dict.Count > 0 && string.IsNullOrEmpty(dict[^1])) dict.RemoveAt(dict.Count - 1);

        using var craft = new InferenceSession(craftModel, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
        using var rec = new InferenceSession(recPath, new SessionOptions { IntraOpNumThreads = Environment.ProcessorCount });
        string recIn = rec.InputMetadata.Keys.First(), recOut = rec.OutputMetadata.Keys.First();

        // 期望：ROI 里到底是什么字（人工标注）
        var cases = new (string File, float X0, float Y0, float X1, float Y1, string Truth, string Label)[]
        {
            ("difficulty_new_ui.png", 260f, 50f, 540f, 112f, "选择赛事等级", "描边美术字标题"),
            ("difficulty_new_ui.png", 450f, 138f, 720f, 170f, "选择符合你技术水平的难度级别", "小字说明"),
            ("itempage_inuse_1.png", 118f, 45f, 200f, 75f, "经验值:4x", "窄条小字"),
            ("home_nav_cursor.png", 830f, 118f, 1000f, 146f, "最近5场比赛", "常规UI词"),
        };

        foreach (var (file, x0, y0, x1, y1, truth, label) in cases)
        {
            var src = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, file), out int w, out int h);
            int cw = (int)(x1 - x0), ch = (int)(y1 - y0);

            // 整行对照（现役做法：整行裁出来给 rec）
            string whole = Rec(rec, recIn, recOut, src, w, h, (int)x0, (int)y0, cw, ch, dict);

            // 逐字：ROI 放大 3 倍 → CRAFT → 每个字框再喂 rec
            const int S = 3;
            var crop = new byte[cw * S * ch * S * 4];
            for (int y = 0; y < ch * S; y++)
                for (int x = 0; x < cw * S; x++)
                {
                    int sx = Math.Clamp((int)(x0 + x / (double)S), 0, w - 1);
                    int sy = Math.Clamp((int)(y0 + y / (double)S), 0, h - 1);
                    int si = (sy * w + sx) * 4, di = (y * cw * S + x) * 4;
                    crop[di] = src[si]; crop[di + 1] = src[si + 1]; crop[di + 2] = src[si + 2]; crop[di + 3] = 255;
                }
            var boxes = DetectChars(craft, crop, cw * S, ch * S, out _).OrderBy(b => b.X).ToList();

            var perChar = new List<string>();
            foreach (var b in boxes)
            {
                int bx = Math.Clamp((int)b.X, 0, cw * S - 1), by = Math.Clamp((int)b.Y, 0, ch * S - 1);
                int bw = Math.Max(1, (int)b.W), bh = Math.Max(1, (int)b.H);
                perChar.Add(Rec(rec, recIn, recOut, crop, cw * S, ch * S, bx, by, bw, bh, dict));
            }
            string joined = string.Concat(perChar);
            _out.WriteLine($"[{label}] 期望「{truth}」");
            _out.WriteLine($"   整行: 「{whole}」{(Eq(whole, truth) ? " ✓" : "")}");
            _out.WriteLine($"   逐字 {boxes.Count} 个: {string.Join("|", perChar)}");
            _out.WriteLine($"   拼合: 「{joined}」{(Eq(joined, truth) ? " ✓" : "")}");
        }
    }

    private static bool Eq(string a, string b) => string.Equals(Regex.Replace(a, @"\s+", ""), b, StringComparison.Ordinal);

    /// <summary>裁一块 → 缩到 48 高 → 归一化 → PP-OCRv6 rec CTC 解码。</summary>
    private static string Rec(InferenceSession rec, string inName, string outName,
        byte[] bgra, int imgW, int imgH, int x0, int y0, int cw, int ch, List<string> dict)
    {
        const int H = 48;
        int outW = Math.Max(4, (int)Math.Round(cw * (double)H / ch));
        outW = Math.Min(outW, 2048);
        var data = new float[3 * H * outW];
        int plane = H * outW;
        for (int y = 0; y < H; y++)
        {
            double fy = y0 + (y + 0.5) * ch / H - 0.5;
            int py = Math.Clamp((int)Math.Round(fy), 0, imgH - 1);
            for (int x = 0; x < outW; x++)
            {
                double fx = x0 + (x + 0.5) * cw / outW - 0.5;
                int px = Math.Clamp((int)Math.Round(fx), 0, imgW - 1);
                int i = (py * imgW + px) * 4, o = y * outW + x;
                data[o] = (float)((bgra[i + 2] / 255.0 - 0.5) / 0.5);
                data[plane + o] = (float)((bgra[i + 1] / 255.0 - 0.5) / 0.5);
                data[2 * plane + o] = (float)((bgra[i] / 255.0 - 0.5) / 0.5);
            }
        }
        var t = new DenseTensor<float>(data, new[] { 1, 3, H, outW });
        using var res = rec.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, t) });
        var pred = res.First(r => r.Name == outName).AsTensor<float>();
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
            if (di >= 0 && di < dict.Count) sb.Append(dict[di]);
        }
        return sb.ToString();
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

    private sealed record Box(float X, float Y, float W, float H);

    private List<Box> DetectChars(InferenceSession sess, byte[] bgra, int w, int h, out int total)
    {
        // 长边 → 1280，边长对齐 32
        double scale = 1280.0 / Math.Max(w, h);
        int nw = Math.Max(32, (int)(Math.Round(w * scale / 32) * 32));
        int nh = Math.Max(32, (int)(Math.Round(h * scale / 32) * 32));

        var data = new float[3 * nh * nw];
        int plane = nh * nw;
        double sx = (double)w / nw, sy = (double)h / nh;
        double[] mean = { 0.485, 0.456, 0.406 }, std = { 0.229, 0.224, 0.225 };
        for (int y = 0; y < nh; y++)
        {
            int py = Math.Clamp((int)(y * sy), 0, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int px = Math.Clamp((int)(x * sx), 0, w - 1);
                int i = (py * w + px) * 4;
                int o = y * nw + x;
                data[o] = (float)((bgra[i + 2] / 255.0 - mean[0]) / std[0]);
                data[plane + o] = (float)((bgra[i + 1] / 255.0 - mean[1]) / std[1]);
                data[2 * plane + o] = (float)((bgra[i] / 255.0 - mean[2]) / std[2]);
            }
        }
        var t = new DenseTensor<float>(data, new[] { 1, 3, nh, nw });
        using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(sess.InputMetadata.Keys.First(), t) });

        // 取 score map：找形状里含 [1,1,H,W] 或 [1,H,W] 的那个
        float[] score = Array.Empty<float>();
        int sh = 0, sw2 = 0, stride = 1;
        foreach (var r in res)
        {
            var ten = r.AsTensor<float>();
            var dims = ten.Dimensions.ToArray();
            var raw = ten.ToArray();
            // 这份导出是 NHWC [1,H,W,C]（C=2：0=文字分 1=亲和度），也兼容 [1,1,H,W] / [1,H,W]
            if (dims.Length == 4 && dims[3] <= 4 && dims[2] > 8)
            {
                score = raw; sh = dims[1]; sw2 = dims[2]; stride = dims[3]; break;
            }
            if (dims.Length == 4 && dims[1] == 1)
            {
                score = raw; sh = dims[2]; sw2 = dims[3]; stride = 1; break;
            }
            if (dims.Length == 3)
            {
                score = raw; sh = dims[1]; sw2 = dims[2]; stride = 1; break;
            }
        }
        if (score.Length == 0) { total = 0; return new List<Box>(); }
        float Score(int idx) => stride == 1 ? score[idx] : score[idx * stride];

        // 连通域（score > 0.7）
        const float Thr = 0.7f;
        var visited = new bool[sh * sw2];
        var stack = new Stack<int>();
        var list = new List<Box>();
        total = 0;
        double toFrameX = (double)w / sw2, toFrameY = (double)h / sh;
        for (int i = 0; i < sh * sw2; i++)
        {
            if (visited[i] || Score(i) <= Thr) continue;
            total++;
            stack.Clear();
            stack.Push(i);
            visited[i] = true;
            int minX = sw2, maxX = -1, minY = sh, maxY = -1, count = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int py = p / sw2, px = p % sw2;
                count++;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ny = py + dy, nx = px + dx;
                        if (ny < 0 || ny >= sh || nx < 0 || nx >= sw2) continue;
                        int ni = ny * sw2 + nx;
                        if (visited[ni] || Score(ni) <= Thr) continue;
                        visited[ni] = true;
                        stack.Push(ni);
                    }
            }
            // 过滤噪声小点
            if (count < 12) continue;
            double bw = (maxX - minX + 1) * toFrameX, bh = (maxY - minY + 1) * toFrameY;
            if (bh < 4 || bw < 2) continue;
            list.Add(new Box((float)(minX * toFrameX), (float)(minY * toFrameY), (float)bw, (float)bh));
        }
        return list;
    }

    private static void SaveAnnotated(byte[] bgra, int w, int h, List<Box> boxes, string path)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { Color = new SKColor(255, 40, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
        {
            foreach (var b in boxes) canvas.DrawRect(b.X, b.Y, b.W, b.H, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 85);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
