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
    public async Task Dump_OcrWords_ReadFailSplit()
    {
        var frame = LoadFrame("read_fail_split_1231.png");
        using var ocr = new OcrRecognizer();
        var res = TestOcr.Run(ocr, frame);
        _out.WriteLine($"frame {frame.Width}x{frame.Height}, {res.Words.Count} words");
        foreach (var w in res.Words.OrderBy(w => w.CenterY).ThenBy(w => w.CenterX))
            _out.WriteLine($"({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");

        var (pair, pts) = MaxLevelSwapBrain.ReadParts(res);
        _out.WriteLine($"direct pair={pair}, pts={pts}");
        var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(ocr, res, frame, CancellationToken.None);
        _out.WriteLine($"full read: {status}");

        // 裁切重读逐字打印：定位「1」为何救不回
        var lvCrop = ImageScaling.CropUpscale(frame, 0.42, 0.585, 0.36, 0.52, 8);
        var lvRes = await ocr.RecognizeAsync(lvCrop, CancellationToken.None);
        foreach (var w in lvRes.Words.OrderBy(w => w.CenterX))
            _out.WriteLine($"lvCrop ({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");
        var ptCrop = ImageScaling.CropUpscale(frame, 0.575, 0.72, 0.36, 0.52, 8);
        var ptRes = await ocr.RecognizeAsync(ptCrop, CancellationToken.None);
        foreach (var w in ptRes.Words.OrderBy(w => w.CenterX))
            _out.WriteLine($"ptCrop ({w.CenterX:F3},{w.CenterY:F3}) '{w.Text}'");
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
