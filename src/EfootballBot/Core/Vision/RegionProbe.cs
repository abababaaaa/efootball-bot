using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision;

/// <summary>
/// 区域像素探针（共享实现）：颜色计数与亮度统计。
/// 此前 CountGray/CountGreen/MedianLuma 在 WeeklyBrain / CnWeeklyBrain / MyLeagueBrain
/// 各有一份逐字相同的副本，HasTopTabs 在 PeakLadderBrain / CnWeeklyBrain 各一份——
/// 改一处漏两处是历次改判定的常见事故源，统一到此处。
/// 各调用点的**阈值仍留在调用点**（不同界面的绿勾/灰锁实测算出来的计数不同），
/// 本类只提供计数，不夹带任何判定语义，保证替换后行为逐像素不变。
/// </summary>
public static class RegionProbe
{
    /// <summary>
    /// 计数基准分辨率。所有探针阈值（≥25 灰、≥40 绿、≥50 勾…）都是在 1280×720 实机帧上
    /// 标定出来的。捕获链会把画面高度压到 720，但**宽度随宽高比变**（16:9=1280、
    /// 16:10=1152、4:3=960、21:9=1720），而 2px 步进采样的命中点数正比于像素面积——
    /// 同一个绿勾在 21:9 上只能数出 74%、在 4:3 上会数出 133%，刚好卡在阈值附近就会
    /// 漏判完成态 / 误判未解锁。基准取 1280×720 时，**720p 场景的换算系数恰为 1.0
    /// （逐像素等价，现有行为一个字都不变）**，只修正其它几何。
    /// </summary>
    public const int RefWidth = 1280;
    public const int RefHeight = 720;

    /// <summary>当前帧相对基准分辨率的面积比（= 基准采样点数 / 当前采样点数）。</summary>
    public static double AreaFactor(FrameData f)
    {
        if (f.Width <= 0 || f.Height <= 0) return 1.0;
        return (double)RefWidth * RefHeight / ((double)f.Width * f.Height);
    }

    /// <summary>把当前帧的采样计数换算到 1280×720 基准（调用点阈值保持不变）。</summary>
    public static int ToReference(int count, FrameData f)
        => count == 0 ? 0 : (int)Math.Round(count * AreaFactor(f));

    /// <summary>区域尺寸合理性下限：过小的帧（启动瞬间的空帧）直接返回 0/false。</summary>
    private static bool Valid(FrameData f, out int w, out int h)
    {
        w = f.Width;
        h = f.Height;
        return w >= 100 && h >= 100 && f.Bgra.Length >= w * h * 4;
    }

    /// <summary>
    /// 区域内灰色像素计数：R≈G≈B（最大最小通道差 &lt; 30）且亮度 90~220。
    /// 用于「未解锁/灰态」列表行判定。2px 步进采样。
    /// </summary>
    public static int CountGray(FrameData f, double x0, double x1, double y0, double y1)
    {
        if (!Valid(f, out int w, out int h)) return 0;
        var bgra = f.Bgra;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int lum = (r + g + b) / 3;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn < 30 && lum >= 90 && lum <= 220) count++;
            }
        return count;
    }

    /// <summary>
    /// 区域内绿色像素计数（绿底白勾的完成徽章）：G&gt;100 且比 R、B 均高出 40 以上。
    /// 2px 步进采样。
    /// </summary>
    public static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
    {
        if (!Valid(f, out int w, out int h)) return 0;
        var bgra = f.Bgra;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 1] > 100 && bgra[i + 1] > bgra[i + 2] + 40 && bgra[i + 1] > bgra[i] + 40) count++;
            }
        return count;
    }

    /// <summary>
    /// 区域内亮度中位数（3px 步进采样）。用中位数抗文字/图标干扰，
    /// 用于「聚焦白卡 vs 未聚焦深灰卡」这类整体明暗判别。
    /// </summary>
    public static int MedianLuma(FrameData f, double x0, double x1, double y0, double y1)
    {
        if (!Valid(f, out int w, out int h)) return 0;
        var bgra = f.Bgra;
        var lumas = new List<int>();
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 3)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 3)
            {
                int i = (y * w + x) * 4;
                lumas.Add((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3);
            }
        if (lumas.Count == 0) return 0;
        lumas.Sort();
        return lumas[lumas.Count / 2];
    }

    /// <summary>
    /// 顶部页签行（y&lt;0.15 的词拼接）是否同时含 推荐/签约/商店。
    /// 国服主页（推荐|比赛|签约|商店…）的通用特征，天梯/周活动共用。
    /// </summary>
    public static bool HasTopTabs(OcrResult ocr)
    {
        string top = string.Join("|", ocr.Words
            .Where(w => w.CenterY < 0.15)
            .Select(w => w.Text.Replace(" ", "")));
        return top.Contains("推荐") && top.Contains("签约") && top.Contains("商店");
    }

    // ---------------- 基准分辨率版本（阈值不变，只修几何差异） ----------------

    /// <summary>灰色像素计数（换算到 1280×720 基准；720p 下与原值完全一致）。</summary>
    public static int CountGrayRef(FrameData f, double x0, double x1, double y0, double y1)
        => ToReference(CountGray(f, x0, x1, y0, y1), f);

    /// <summary>绿色像素计数（换算到 1280×720 基准；720p 下与原值完全一致）。</summary>
    public static int CountGreenRef(FrameData f, double x0, double x1, double y0, double y1)
        => ToReference(CountGreen(f, x0, x1, y0, y1), f);
}
