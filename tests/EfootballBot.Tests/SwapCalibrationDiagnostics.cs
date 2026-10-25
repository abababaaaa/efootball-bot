using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 临时诊断（校准用，跑完可删）：打印实机帧 OCR 全部词 / 网格亮度，定位读不出与暗卡阈值。
public class SwapCalibrationDiagnostics
{
    private readonly ITestOutputHelper _out;
    public SwapCalibrationDiagnostics(ITestOutputHelper output) => _out = output;

    private static string SwapDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "swap");

    private static FrameData LoadFrame(string file)
    {
        using var mat = Cv2.ImRead(Path.Combine(SwapDir, file), ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    [Fact]
    public async Task Dump_ReadMaxedDetailFrames()
    {
        // 用户 2026-10-25 午后：Zico 7/7、Salah 22/22 满级未识别——直接对截图跑读取器
        using var ocrEngine = new OcrRecognizer();
        foreach (var file in new[] { "detail_zico_74_7of7.png", "detail_salah_22of22.png", "detail_zico_7of7.png" })
        {
            var frame = LoadFrame(file);
            var res = TestOcr.Run(ocrEngine, frame);
            var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(
                ocrEngine, res, frame, CancellationToken.None);
            var direct = MaxLevelSwapBrain.ReadParts(res);
            _out.WriteLine($"== {file}: class={MaxLevelSwapBrain.Classify(res)} direct=({direct.Pair},{direct.Points}) full={status}");
            var valueWords = MaxLevelSwapBrain.LevelValueWords(res)
                .Select(w => $"'{w.Text}'@({w.CenterX:F3},{w.CenterY:F3})");
            _out.WriteLine($"   valueWords=[{string.Join(",", valueWords)}]");
            foreach (var w in res.Words.Where(w => w.CenterY is > 0.37 and < 0.52 && w.CenterX is > 0.40 and < 0.75)
                .OrderBy(w => w.CenterY).ThenBy(w => w.CenterX))
                _out.WriteLine($"   ({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");
        }
    }

    [Fact]
    public void Dump_OrangeBadge_Calibration()
    {
        // 能力点区域橙色圆圈计数标定：Salah(2 点，橙圈) vs Zico/Orsolini(白字 0)
        foreach (var file in new[] { "detail_salah_22of22.png", "detail_zico_74_7of7.png",
            "detail_zico_7of7.png", "read_fail_110427.png", "read_fail_1227.png" })
        {
            var frame = LoadFrame(file);
            _out.WriteLine($"== {file}: orange={CountOrange(frame, 0.55, 0.66, 0.395, 0.475)} " +
                $"wide={CountOrange(frame, 0.52, 0.72, 0.38, 0.50)}");
        }
    }

    private static int CountOrange(FrameData f, double x, double x1, double y, double y1)
    {
        int n = 0;
        for (int yy = (int)(y * f.Height); yy < (int)(y1 * f.Height); yy++)
            for (int xx = (int)(x * f.Width); xx < (int)(x1 * f.Width); xx++)
            {
                int i = (yy * f.Width + xx) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (r > 180 && g is > 90 and < 200 && b < 100 && r - b > 120) n++;
            }
        return n;
    }

    [Fact]
    public void Dump_BenchColumnClusters()
    {
        foreach (var file in new[] { "bench_dark_cards.png", "bench_second_entry.png" })
        {
            var frame = LoadFrame(file);
            using var ocr = new OcrRecognizer();
            var res = TestOcr.Run(ocr, frame);
            var words = res.Words.Where(w => w.CenterY is > 0.28 and < 0.345 && w.CenterX is > 0.08 and < 0.68)
                .OrderBy(w => w.CenterX).ToList();
            _out.WriteLine($"== {file}: words=[{string.Join(", ", words.Select(w => $"'{w.Text}'@{w.CenterX:F3},{w.CenterY:F3}"))}]");
            var cols = MaxLevelSwapBrain.BenchColumnCenters(res);
            _out.WriteLine($"   cols=[{string.Join(", ", cols.Select(c => c.ToString("F3")))}]");
            DumpCrops(frame, res);
        }
    }

    private void DumpCrops(FrameData frame, OcrResult res)
    {
        var cols = MaxLevelSwapBrain.BenchColumnCenters(res);
        if (cols.Length == 0) cols = new[] { 0.174, 0.290, 0.405, 0.520, 0.635 };
        var vals = new List<string>();
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < cols.Length; col++)
                vals.Add($"{row},{col}={MaxLevelSwapBrain.CardHighlightPixels(frame, row, col, cols)}");
        _out.WriteLine($"   cells: {string.Join(" ", vals)}");
    }

    [Fact]
    public void Dump_ClassifyDetail_Zico()
    {
        // 详情页被误判「画面 Lineup」排查：打印命中关键词
        foreach (var file in new[] { "detail_zico_7of7.png", "read_fail_110427.png" })
        {
            var frame = LoadFrame(file);
            using var ocr = new OcrRecognizer();
            var res = TestOcr.Run(ocr, frame);
            var screen = MaxLevelSwapBrain.Classify(res);
            _out.WriteLine($"== {file}: class={screen} words={res.Words.Count}");
            foreach (var kw in new[] { "替补球员", "自动配置球员", "战术", "整体实力", "球员动作", "惯用脚", "剩余球员数", "后备球员", "当前等级" })
                if (res.ContainsJoined(kw)) _out.WriteLine($"   HIT '{kw}'");
            foreach (var w in res.Words.Where(w => w.Text.Contains('战') || w.Text.Contains('替') || w.Text.Contains('配') || w.Text.Contains('脚')))
                _out.WriteLine($"   word ({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");
        }
    }

    [Fact]
    public async Task Dump_OcrWords_ReadFailSplit()
    {
        foreach (var file in new[] { "read_fail_split_1231.png", "read_fail_110427.png",
            "read_fail_110447.png", "read_fail_110454.png", "read_fail_110500.png" })
        {
            var frame = LoadFrame(file);
            using var ocr = new OcrRecognizer();
            var res = TestOcr.Run(ocr, frame);
            var (pair, pts) = MaxLevelSwapBrain.ReadParts(res);
            var lvText = MaxLevelSwapBrain.ReadLevelText(res);
            var valueWords = MaxLevelSwapBrain.LevelValueWords(res)
                .Select(w => $"'{w.Text}'@({w.CenterX:F3},{w.CenterY:F3})");
            _out.WriteLine($"== {file}: {frame.Width}x{frame.Height}, {res.Words.Count}w, direct=({pair},{pts}) lvText='{lvText}' valueWords=[{string.Join(",", valueWords)}]");

            var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(ocr, res, frame, CancellationToken.None);
            _out.WriteLine($"   full read: {status}");
            foreach (var (x0, x1, y0, y1) in new[] { (0.46, 0.575, 0.425, 0.505), (0.42, 0.585, 0.36, 0.52) })
            {
                foreach (var binarize in new[] { false, true })
                {
                    var lvCrop = ImageScaling.CropUpscale(frame, x0, x1, y0, y1, 8);
                    if (binarize)
                    {
                        var px = (byte[])lvCrop.Bgra.Clone();
                        for (int i = 0; i < px.Length; i += 4)
                        {
                            int lum = (px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) / 1000;
                            byte v = lum >= 150 ? byte.MaxValue : byte.MinValue;
                            px[i] = px[i + 1] = px[i + 2] = v;
                        }
                        lvCrop = new FrameData(px, lvCrop.Width, lvCrop.Height, 0);
                    }
                    var lvRes = await ocr.RecognizeAsync(lvCrop, CancellationToken.None);
                    string tag = binarize ? "bin" : "raw";
                    _out.WriteLine($"   crop[{x0},{tag}]: '{string.Join("' '", lvRes.Words.OrderBy(w => w.CenterX).Select(w => w.Text))}'");
                }
            }
            foreach (var w in res.Words.Where(w => w.CenterX is > 0.40 and < 0.75 && w.CenterY is > 0.36 and < 0.55)
                .OrderBy(w => w.CenterY).ThenBy(w => w.CenterX))
                _out.WriteLine($"   ({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");
        }
    }

    [Fact]
    public void Dump_LumaGrid_BenchDarkCards()
    {
        var frame = LoadFrame("bench_dark_cards.png");
        _out.WriteLine($"frame {frame.Width}x{frame.Height}");
        // 5 列 × 4 行卡格：打印每格中心若干窗口的平均亮度
        double[] colCx = { 0.174, 0.290, 0.405, 0.520, 0.635 };
        double[] rowNameY = { 0.313, 0.474, 0.635, 0.795 };
        foreach (var (name, cy) in new[] { ("cardTop", 0.22), ("portrait", 0.255), ("whole", 0.25) })
        {
            _out.WriteLine($"--- window {name} (cy={cy}) ---");
            for (int r = 0; r < rowNameY.Length; r++)
            {
                var vals = new List<string>();
                for (int c = 0; c < colCx.Length; c++)
                {
                    double cyAbs = rowNameY[r] - 0.058 + (cy - 0.22); // 行内相对窗口
                    double lum = MeanLuma(frame, colCx[c] - 0.035, cyAbs - 0.025, 0.07, 0.05);
                    vals.Add($"{colCx[c]:F3},{cyAbs:F3}={lum:F0}");
                }
                _out.WriteLine($"row{r + 1}: " + string.Join(" | ", vals));
            }
        }

        // 裁出 row1 五格 + row3p5 保存，供人工核对窗口是否对准
        string dir = Path.Combine(SwapDir, "diag");
        Directory.CreateDirectory(dir);
        using (var src = Cv2.ImRead(Path.Combine(SwapDir, "bench_dark_cards.png"), ImreadModes.Color))
        {
            for (int c = 0; c < colCx.Length; c++)
                SaveCrop(src, colCx[c] - 0.06, 0.175, 0.12, 0.16, Path.Combine(dir, $"r1c{c + 1}.png"));
            SaveCrop(src, colCx[4] - 0.06, 0.497, 0.12, 0.16, Path.Combine(dir, "r3c5.png"));

            // 全 20 格统计：mean / p90 亮度 / 饱和度 / 高亮像素数（暗卡=整体压暗无高光）
            _out.WriteLine("cell | mean | p90 | hi180 | hi220");
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 5; c++)
                {
                    double top = rowNameY[r] - 0.085; // 卡内区（含评分/头像，避开名字行与蓝框角）
                    var (mean, p90, h1, h2) = CellStats(src, colCx[c] - 0.045, top + 0.012, 0.09, 0.115);
                    _out.WriteLine($"r{r + 1}c{c + 1} | {mean:F0} | {p90:F0} | {h1} | {h2}");
                }
        }
        _out.WriteLine("crops saved");
    }

    private static (double Mean, double P90, int Hi180, int Hi220) CellStats(Mat bgr, double x, double y, double w, double h)
    {
        int x0 = Math.Max(0, (int)(x * bgr.Width)), y0 = Math.Max(0, (int)(y * bgr.Height));
        int x1 = Math.Min(bgr.Width, (int)((x + w) * bgr.Width)), y1 = Math.Min(bgr.Height, (int)((y + h) * bgr.Height));
        using var roi = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
        var lums = new List<int>();
        int n = 0, h1 = 0, h2 = 0;
        for (int yy = 0; yy < roi.Rows; yy++)
            for (int xx = 0; xx < roi.Cols; xx++)
            {
                var b = roi.At<Vec3b>(yy, xx);
                int lum = (b.Item0 * 299 + b.Item1 * 587 + b.Item2 * 114) / 1000;
                lums.Add(lum);
                if (lum >= 180) h1++;
                if (lum >= 220) h2++;
                n++;
            }
        lums.Sort();
        return (lums.Average(), lums[(int)(lums.Count * 0.9)], h1, h2);
    }

    private static void SaveCrop(Mat src, double x, double y, double w, double h, string path)
    {
        int x0 = Math.Max(0, (int)(x * src.Width)), y0 = Math.Max(0, (int)(y * src.Height));
        int x1 = Math.Min(src.Width, (int)((x + w) * src.Width)), y1 = Math.Min(src.Height, (int)((y + h) * src.Height));
        using var roi = new Mat(src, new Rect(x0, y0, x1 - x0, y1 - y0));
        Cv2.ImWrite(path, roi);
    }

    private static double MeanLuma(FrameData f, double x, double y, double w, double h)
    {
        int x0 = Math.Max(0, (int)(x * f.Width)), y0 = Math.Max(0, (int)(y * f.Height));
        int x1 = Math.Min(f.Width, (int)((x + w) * f.Width)), y1 = Math.Min(f.Height, (int)((y + h) * f.Height));
        long sum = 0; int n = 0;
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int i = (yy * f.Width + xx) * 4;
                sum += (f.Bgra[i] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i + 2] * 114) / 1000;
                n++;
            }
        return n == 0 ? 0 : (double)sum / n;
    }
}
