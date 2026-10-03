using System.Linq;
using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision;

/// <summary>归一化矩形（0~1）。</summary>
public readonly record struct NormRect(double X, double Y, double W, double H)
{
    public static NormRect Full => new(0, 0, 1, 1);
    public bool Contains(double px, double py)
        => px >= X && px <= X + W && py >= Y && py <= Y + H;
}

[Flags]
public enum HsvFilter
{
    None = 0,
    White = 1,
    Yellow = 2,
    Cyan = 4,
    Red = 8,
    Green = 16,
    Blue = 32,
    Bright = 64,
}

/// <summary>帧像素分析工具：颜色采样 / 亮度 / 裁剪 / 灰度模板匹配。</summary>
public static class FrameAnalyzer
{
    public static (int X, int Y, int W, int H) PixelRect(FrameData f, NormRect r)
    {
        double cx = Math.Clamp(r.X, 0, 1);
        double cy = Math.Clamp(r.Y, 0, 1);
        double cw = Math.Clamp(r.W, 0, Math.Max(0, 1 - cx));
        double ch = Math.Clamp(r.H, 0, Math.Max(0, 1 - cy));
        // 边界加固：Math.Round 进位可能使 x+w / y+h 超出帧尺寸，clamp 回合法范围
        int x = Math.Clamp((int)Math.Round(cx * f.Width), 0, f.Width - 1);
        int y = Math.Clamp((int)Math.Round(cy * f.Height), 0, f.Height - 1);
        int w = Math.Max(1, Math.Min((int)Math.Round(cw * f.Width), f.Width - x));
        int h = Math.Max(1, Math.Min((int)Math.Round(ch * f.Height), f.Height - y));
        return (x, y, w, h);
    }

    public static double MeanLuminance(FrameData f, NormRect? region = null)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region ?? NormRect.Full);
        long sum = 0; long n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy += 4)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx += 4)
            {
                int i = row + xx * 4;
                sum += (long)(b[i + 2] * 0.299 + b[i + 1] * 0.587 + b[i] * 0.114);
                n++;
            }
        }
        return n == 0 ? 0 : sum / (double)n;
    }

    /// <summary>区域内通过 HSV 过滤的像素占比（0~1）。</summary>
    public static double ColorRatio(FrameData f, HsvFilter filter, NormRect? region = null)
        => ColorRatioStep(f, filter, region, 2);

    /// <summary>逐像素版颜色占比（小图标/小区域用，避免隔像素采样漏掉 2~3px 的小点）。</summary>
    public static double ColorRatioExact(FrameData f, HsvFilter filter, NormRect? region = null)
        => ColorRatioStep(f, filter, region, 1);

    private static double ColorRatioStep(FrameData f, HsvFilter filter, NormRect? region, int step)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region ?? NormRect.Full);
        long hit = 0, n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy += step)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx += step)
            {
                int i = row + xx * 4;
                byte bb = b[i], gg = b[i + 1], rr = b[i + 2];
                if (MatchHsv(rr, gg, bb, filter)) hit++;
                n++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    /// <summary>
    /// 草皮占比：不依赖 OCR 的比赛画面检测。真机捕获的草皮色相偏黄绿（约77~90），
    /// 通用绿色滤镜（h≥85）会漏检，故单独定义 hue 68~160。
    /// </summary>
    public static double PitchRatio(FrameData f, NormRect? region = null)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region ?? NormRect.Full);
        long hit = 0, n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy += 2)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx += 2)
            {
                int i = row + xx * 4;
                ColorEx.RgbToHsv(b[i + 2], b[i + 1], b[i], out double hh, out double ss, out double vv);
                n++;
                if (hh >= 68 && hh <= 160 && ss >= 0.28 && vv >= 0.32) hit++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    /// <summary>
    /// 白色「下一步」箭头按钮占比：R/G/B ≥195 且通道差 ≤25（排除纯白面板上的文字区域用，
    /// 判定时与固定 ROI 配合）。
    /// </summary>
    public static double ButtonWhiteRatio(FrameData f, NormRect region)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region);
        long hit = 0, n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy += 2)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx += 2)
            {
                int i = row + xx * 4;
                int r = b[i + 2], g = b[i + 1], bl = b[i];
                n++;
                if (r >= 195 && g >= 195 && bl >= 195
                    && Math.Abs(r - g) <= 25 && Math.Abs(g - bl) <= 25) hit++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    /// <summary>
    /// 蓝色「下一步」长条按钮占比：B 比 R 高 ≥45、比 G 高 ≥25，且 B≥90。
    /// </summary>
    public static double ButtonBlueRatio(FrameData f, NormRect region)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region);
        long hit = 0, n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy += 2)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx += 2)
            {
                int i = row + xx * 4;
                int r = b[i + 2], g = b[i + 1], bl = b[i];
                n++;
                if (bl - r >= 45 && bl - g >= 25 && bl >= 90) hit++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    /// <summary>
    /// 检测右下的 RT 提示横幅（「RT 前往比赛」「RT 跳过」，真机实测填充 RGB(0,0,150)、
    /// 位置约 x0.78~0.97、y0.90~0.96，整宽仅约 0.19 屏宽，且被黑色 RT 徽章/白文字/箭头切断）。
    /// 故：①搜索区限定右下 x 0.60~1、y 0.84~1（天然排除左下蓝色返回按钮）；
    /// ②用行内横幅蓝像素占比（≥0.25）而非连续跑，容忍徽章文字打断；
    /// ③支撑行纵向跨度 ≥0.05 屏高。命中时 cy 为支撑行中部（帧归一化）。
    /// </summary>
    public static bool FindRtBanner(FrameData f, out double cy)
    {
        if (f.Bgra.Length == 0) { cy = 0; return false; } // 帧已释放兜底
        int xFrom = (int)(0.60 * f.Width);
        int yFrom = (int)(0.84 * f.Height);
        int spanNeeded = (int)(0.05 * f.Height);
        var b = f.Bgra;

        int top = -1, bottom = -1;
        for (int y = yFrom; y < f.Height; y += 2)
        {
            int blue = 0, n = 0;
            int row = y * f.Width * 4;
            for (int x = xFrom; x < f.Width; x += 2)
            {
                int i = row + x * 4;
                byte rr = b[i + 2], gg = b[i + 1], bbv = b[i];
                n++;
                if (rr <= 12 && gg <= 12 && bbv >= 135 && bbv <= 175) blue++;
            }
            if (n > 0 && blue / (double)n >= 0.25)
            {
                if (top < 0) top = y;
                bottom = y;
            }
        }
        if (top >= 0 && bottom - top >= spanNeeded)
        {
            cy = (top + bottom) / 2.0 / f.Height;
            return true;
        }
        cy = 0;
        return false;
    }

    /// <summary>
    /// 统计区域内「亮绿」像素数（逐像素）：G≥150 且 G−R≥80 且 G−B≥60。
    /// 用于右上 HUD「跳过比赛」绿色圆勾检测——真机实测圆勾 RGB(50,200,90)，
    /// 草皮 (72,108,52) 暗、黄色广告牌 R 高，均被排除；绿"1"数字仅约 30 个稀疏散点。
    /// </summary>
    public static int CountBrightGreen(FrameData f, NormRect region)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = PixelRect(f, region);
        int hit = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx++)
            {
                int i = row + xx * 4;
                byte bb = b[i], gg = b[i + 1], rr = b[i + 2];
                if (gg >= 150 && gg - rr >= 80 && gg - bb >= 60) hit++;
            }
        }
        return hit;
    }

    public static bool MatchHsv(byte r, byte g, byte b, HsvFilter filter)
    {
        if (filter == HsvFilter.None) return true;
        ColorEx.RgbToHsv(r, g, b, out double h, out double s, out double v);

        if (filter.HasFlag(HsvFilter.Bright) && v >= 0.72 && s <= 0.35) return true;
        if (filter.HasFlag(HsvFilter.White) && v >= 0.75 && s <= 0.18) return true;
        if (filter.HasFlag(HsvFilter.Yellow) && h >= 40 && h <= 70 && s >= 0.45 && v >= 0.55) return true;
        if (filter.HasFlag(HsvFilter.Cyan) && h >= 160 && h <= 195 && s >= 0.35 && v >= 0.45) return true;
        if (filter.HasFlag(HsvFilter.Red) && ((h <= 12 || h >= 345)) && s >= 0.45 && v >= 0.4) return true;
        if (filter.HasFlag(HsvFilter.Green) && h >= 85 && h <= 160 && s >= 0.3 && v >= 0.35) return true;
        if (filter.HasFlag(HsvFilter.Blue) && h >= 195 && h <= 260 && s >= 0.35 && v >= 0.35) return true;
        return false;
    }

    /// <summary>裁剪出区域 BGRA 数据（用于模板保存 / 局部识别）。</summary>
    public static FrameData Crop(FrameData f, NormRect r)
    {
        var (x, y, w, h) = PixelRect(f, r);
        var buf = new byte[w * h * 4];
        for (int yy = 0; yy < h; yy++)
            Buffer.BlockCopy(f.Bgra, ((y + yy) * f.Width + x) * 4, buf, yy * w * 4, w * 4);
        return new FrameData(buf, w, h, f.TimestampMs);
    }

    /// <summary>
    /// 灰度归一化互相关模板匹配。
    /// </summary>
    /// <returns>最大相关系数 0~1 及其中心点（归一化坐标）。</returns>
    public static (double Score, double CX, double CY) MatchTemplate(
        FrameData frame, byte[] templateBgra, int tw, int th, NormRect? searchRegion = null)
    {
        var (sx, sy, sw, sh) = PixelRect(frame, searchRegion ?? NormRect.Full);
        if (tw > sw || th > sh || tw < 4 || th < 4) return (0, 0, 0);

        var tpl = ToGray(templateBgra, tw, th, tw);
        var img = frame.Bgra;

        long tSum = 0;
        foreach (var g in tpl) tSum += g;
        double tMean = (double)tSum / tpl.Length;
        double tVar = 0;
        foreach (var g in tpl) tVar += (g - tMean) * (g - tMean);
        if (tVar < 1) return (0, 0, 0);
        double tStd = Math.Sqrt(tVar);

        int step = Math.Max(1, Math.Min(sw, sh) / 320);
        double best = -1; int bx = sx, by = sy;

        for (int yy = sy; yy <= sy + sh - th; yy += step)
        {
            for (int xx = sx; xx <= sx + sw - tw; xx += step)
            {
                double mean = 0, var2 = 0;
                // 两遍：先均值
                for (int j = 0; j < th; j += 2)
                {
                    int fr = (yy + j) * frame.Width * 4;
                    int tr = (j) * tw;
                    for (int i = 0; i < tw; i += 2)
                    {
                        int fi = fr + (xx + i) * 4;
                        mean += img[fi + 2] * 0.299 + img[fi + 1] * 0.587 + img[fi] * 0.114;
                        var2 += 1;
                    }
                }
                mean /= var2;
                double cov = 0, iVar = 0;
                for (int j = 0; j < th; j += 2)
                {
                    int fr = (yy + j) * frame.Width * 4;
                    for (int i = 0; i < tw; i += 2)
                    {
                        int fi = fr + (xx + i) * 4;
                        double gv = img[fi + 2] * 0.299 + img[fi + 1] * 0.587 + img[fi] * 0.114;
                        double tv = tpl[j * tw + i];
                        cov += (gv - mean) * (tv - tMean);
                        iVar += (gv - mean) * (gv - mean);
                    }
                }
                double denom = Math.Sqrt(iVar) * tStd;
                double score = denom < 1e-6 ? 0 : Math.Clamp(cov / denom, -1, 1);
                if (score > best) { best = score; bx = xx; by = yy; }
            }
        }

        return (Math.Max(0, best),
            Math.Clamp((bx + tw / 2.0) / frame.Width, 0, 1),
            Math.Clamp((by + th / 2.0) / frame.Height, 0, 1));
    }

    private static byte[] ToGray(byte[] bgra, int w, int h, int stride)
    {
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride * 4 + x * 4;
                gray[y * w + x] = (byte)(bgra[i + 2] * 0.299 + bgra[i + 1] * 0.587 + bgra[i] * 0.114);
            }
        return gray;
    }
}

internal static class ColorEx
{
    public static void RgbToHsv(byte r8, byte g8, byte b8, out double h, out double s, out double v)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        double hue;
        if (max == r) hue = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) hue = (b - r) / d + 2;
        else hue = (r - g) / d + 4;
        h = hue * 60;
    }
}
