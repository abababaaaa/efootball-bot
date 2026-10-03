using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 挑战清单卡片状态读取（纯函数，支持 --check-frame 离线自检）。
/// 核心思路：先定位焦点卡（轮播中焦点卡比两侧高出一截，实测顶边高 16px），
/// 再把绿勾徽章（右上角外）与挂锁图标（右下角内）的 ROI 换算到该卡，
/// 彻底避免固定 ROI 在焦点移到中间/右侧槽位时误采邻卡。
/// 判定：Done=焦点卡带绿勾；Unlocked=焦点卡右下角无挂锁图标（暗色参赛卡也是可打的）。
/// 移动验证用「顶边+底边两条横带」的画面签名：焦点换卡会换高/换内容，滚动会整体平移。
/// </summary>
public static class ChallengeCardReader
{
    /// <summary>卡体亮度阈值：卡身深灰 lum≈41，缝隙/背景 lum≤17（实测）。</summary>
    private const int CardLumMin = 30;

    /// <summary>分段扫描行（客户区归一化 Y），位于所有卡的中部。</summary>
    private const double ScanRowY = 0.625;

    /// <summary>焦点卡顶边搜索起点（客户区归一化 Y）。</summary>
    private const double TopScanFromY = 0.10;

    /// <summary>卡段最小宽度（客户区归一化）：卡约 0.27，露头的邻卡约 0.06 被滤除。</summary>
    private const double MinCardWidth = 0.10;

    public const double LockMin = 0.05;
    public const double GreenMin = 0.10;

    /// <summary>签名横带（客户区归一化）：顶带跨过焦点卡顶边，底带跨过底边。</summary>
    public static readonly NormRect SigBandTop = new(0.03, 0.150, 0.94, 0.115);
    public static readonly NormRect SigBandBot = new(0.03, 0.795, 0.94, 0.125);

    /// <summary>签名下采样尺寸与移动判定阈值。</summary>
    public const int SigCols = 24;
    public const int SigRows = 4;
    public const double MoveMadMin = 8.0;

    /// <summary>单张焦点卡的读取结果。</summary>
    /// <param name="Number">OCR 实测 1 基卡号（仅辅助日志，判定不依赖）。</param>
    /// <param name="BgLum">焦点卡身上部平均亮度（0~255，诊断用）。</param>
    /// <param name="LockRatio">焦点卡右下角挂锁灰像素占比。</param>
    /// <param name="GreenRatio">焦点卡右上角绿勾徽章绿色占比。</param>
    /// <param name="Located">是否成功定位到焦点卡。</param>
    /// <param name="Unlocked">是否未锁（无挂锁图标）。</param>
    /// <param name="Done">是否已完成（带绿勾徽章）。</param>
    public sealed record Info(int? Number, double BgLum, double LockRatio, double GreenRatio,
        bool Located, bool Unlocked, bool Done);

    /// <summary>帧像素矩形 → 帧归一化坐标（自动裁剪到帧内）。</summary>
    private static NormRect Px(FrameData f, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(0, f.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, f.Height - 1));
        w = Math.Clamp(w, 1, f.Width - x);
        h = Math.Clamp(h, 1, f.Height - y);
        return new NormRect(x / (double)f.Width, y / (double)f.Height,
            w / (double)f.Width, h / (double)f.Height);
    }

    public static Info Read(FrameData f, OcrResult ocr,
        double offX, double offY, double cliW, double cliH)
    {
        var card = LocateFocusedCard(f, offX, offY, cliW, cliH);
        if (card is null)
            return new Info(null, 0, 0, 0, false, false, false);

        var (left, top, right, bot) = card.Value;
        int w = right - left;

        // 绿勾徽章：卡在右上角外（实测徽标 37×37，右-46..-9、顶-3..+34）
        var badge = Px(f, right - 56, top - 8, 54, 46);
        // 挂锁图标：卡在右下角内（实测锁体 14×16，右-43..-29、底-52..-36，取 40×40 余量）
        var lockZone = Px(f, right - 60, bot - 62, 40, 40);
        // 背景诊断区：卡身上部中段（避开卡号与正文）
        var bg = Px(f, left + (int)(w * 0.35), top + 30, (int)(w * 0.30), 20);
        // 卡号区：左上段（仅日志）
        var num = Px(f, left + 16, top + 36, 134, 94);

        double greenRatio = FrameAnalyzer.ColorRatioExact(f, HsvFilter.Green, badge);
        double lockRatio = LockGrayRatio(f, lockZone);
        double bgLum = FrameAnalyzer.MeanLuminance(f, bg);
        int? number = ReadNumber(ocr, num);

        bool done = greenRatio >= GreenMin;
        bool unlocked = lockRatio < LockMin;
        return new Info(number, bgLum, lockRatio, greenRatio, true, unlocked, done);
    }

    /// <summary>
    /// 定位焦点卡：在 ScanRowY 行把亮度≥CardLumMin 的连续段当作卡身，
    /// 再在每段中轴竖扫顶边，顶边最高者即焦点卡（实测焦点卡高 16px 且更宽）。
    /// 返回帧像素 (left, top, right, bot)；找不到返回 null。
    /// </summary>
    public static (int left, int top, int right, int bot)? LocateFocusedCard(
        FrameData f, double offX, double offY, double cliW, double cliH)
        => LocateFocusedCardEx(f, offX, offY, cliW, cliH, ScanRowY, TopScanFromY, CardLumMin, MinCardWidth);

    /// <summary>
    /// 定位焦点卡的通用实现（分辨率无关：所有几何量均为客户区归一化比例）。
    /// 在 scanRowY 行把亮度≥lumMin 的连续段当作卡身，再在每段中轴竖扫顶边，
    /// 顶边最高（y 最小）者即焦点卡（焦点卡因放大而更高）。返回帧像素 (left, top, right, bot)。
    /// 活动卡（网页上半屏）用 scanRowY≈0.40、topFromY≈0.05；挑战清单用 0.625 / 0.10。
    /// </summary>
    public static (int left, int top, int right, int bot)? LocateFocusedCardEx(
        FrameData f, double offX, double offY, double cliW, double cliH,
        double scanRowY, double topFromY, int lumMin, double minCardWidth)
    {
        int scanY = (int)Math.Round(offY + scanRowY * cliH);
        if (scanY < 0 || scanY >= f.Height) return null;
        int minW = Math.Max(40, (int)(minCardWidth * cliW));

        // 横向分段（步进 2px）
        var segs = new List<(int x0, int x1)>();
        int runStart = -1;
        for (int x = 0; x < f.Width; x += 2)
        {
            bool on = Lum(f, x, scanY) >= lumMin;
            if (on && runStart < 0) runStart = x;
            else if (!on && runStart >= 0)
            {
                if (x - runStart >= minW) segs.Add((runStart, x));
                runStart = -1;
            }
        }
        if (runStart >= 0 && f.Width - runStart >= minW) segs.Add((runStart, f.Width));
        if (segs.Count == 0) return null;

        int topFrom = Math.Max(0, (int)Math.Round(offY + topFromY * cliH));
        int bestIdx = -1, bestTop = int.MaxValue;
        var tops = new int[segs.Count];
        var bots = new int[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            int cx = (segs[i].x0 + segs[i].x1) / 2;
            int top = -1;
            for (int y = topFrom; y < scanY; y++)
            {
                if (Lum(f, cx, y) >= lumMin) { top = y; break; }
            }
            if (top < 0) { tops[i] = int.MaxValue; bots[i] = scanY; continue; }
            int bot = top;
            for (int y = top + 1; y < f.Height; y++)
            {
                if (Lum(f, cx, y) >= lumMin) bot = y;
                else if (y > scanY + 20) break; // 过扫描行 20px 后出卡身即停
            }
            tops[i] = top;
            bots[i] = bot;
            if (top < bestTop) { bestTop = top; bestIdx = i; }
        }
        if (bestIdx < 0 || bestTop == int.MaxValue) return null;
        return (segs[bestIdx].x0, bestTop, segs[bestIdx].x1, bots[bestIdx]);
    }

    /// <summary>
    /// 移动签名：顶/底两条横带各自灰度下采样 SigCols×SigRows 后拼接。
    /// 焦点换卡（卡高变化/换内容）或轮播滚动都会显著改变；静止时逐帧一致。
    /// </summary>
    public static byte[] Signature(FrameData f, double offX, double offY, double cliW, double cliH)
    {
        var sig = new byte[SigCols * SigRows * 2];
        FillBand(f, Map(f, SigBandTop, offX, offY, cliW, cliH), sig, 0);
        FillBand(f, Map(f, SigBandBot, offX, offY, cliW, cliH), sig, SigCols * SigRows);
        return sig;
    }

    /// <summary>两个签名的平均绝对差：≥MoveMadMin 即轮播确实发生了变化。</summary>
    public static double SignatureMad(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.MaxValue;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    /// <summary>
    /// 活动列表移动签名：覆盖卡片内容主区（客户区归一化 y 0.20~0.50，整幅宽度）的灰度下采样。
    /// 活动列表是「焦点卡居中、内容横滚」的轮播——焦点卡屏幕位置(中心)几乎不变，
    /// 但内容会随滚动整体平移，因此用内容签名判「是否真的换了卡」，而不是用焦点卡中心位移。
    /// </summary>
    public static readonly NormRect ActSigBand = new(0.03, 0.20, 0.94, 0.30);
    public const int ActSigCols = 32;
    public const int ActSigRows = 6;

    public static byte[] ActivitySignature(FrameData f, double offX, double offY, double cliW, double cliH)
    {
        var sig = new byte[ActSigCols * ActSigRows];
        FillBandN(f, Map(f, ActSigBand, offX, offY, cliW, cliH), sig, 0, ActSigCols, ActSigRows);
        return sig;
    }

    /// <summary>活动列表「是否真的换卡」判定阈值：内容签名 MAD ≥ 此值即视为换卡。</summary>
    public const double ActivityMoveMad = 10.0;

    /// <summary>客户区归一化坐标 → 帧归一化坐标（与 ScenarioBase.ToFrameRoi 同公式）。</summary>
    public static NormRect Map(FrameData f, NormRect cr,
        double offX, double offY, double cliW, double cliH)
    {
        if (cliW <= 0 || cliH <= 0 || f.Width <= 0 || f.Height <= 0) return cr;
        return new NormRect(
            (offX + cr.X * cliW) / f.Width,
            (offY + cr.Y * cliH) / f.Height,
            cr.W * cliW / f.Width,
            cr.H * cliH / f.Height);
    }

    /// <summary>读焦点卡大卡号；兼容 Windows OCR 把「01」拆成「0」「1」两个词。仅辅助用途。</summary>
    private static int? ReadNumber(OcrResult ocr, NormRect frameRoi)
    {
        var digits = ocr.Words
            .Where(w => frameRoi.Contains(w.CenterX, w.CenterY))
            .Select(w => (Text: OcrText.Norm(w.Text), w.CenterX))
            .Where(t => t.Text.Length is >= 1 and <= 2 && t.Text.All(char.IsDigit))
            .OrderBy(t => t.CenterX)
            .ToList();
        if (digits.Count == 0) return null;

        if (digits[0].Text.Length == 2
            && int.TryParse(digits[0].Text, out int v) && v is >= 1 and <= 99)
            return v;

        string compact = string.Concat(digits.Take(2).Select(t => t.Text));
        if (compact.Length >= 1 && int.TryParse(compact, out int n) && n is >= 1 and <= 99)
            return n;
        return null;
    }

    /// <summary>
    /// 挂锁灰像素占比：低饱和（max-min≤24）且中亮度（100~200）。
    /// 锁体约 RGB(140,145,150)，暗卡身 (40,40,45)、白卡 255、彩色文字均被排除。
    /// </summary>
    public static double LockGrayRatio(FrameData f, NormRect r)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        var (x, y, w, h) = FrameAnalyzer.PixelRect(f, r);
        int hit = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx++)
            {
                int i = row + xx * 4;
                int rr = b[i + 2], gg = b[i + 1], bb = b[i];
                int max = Math.Max(rr, Math.Max(gg, bb));
                int min = Math.Min(rr, Math.Min(gg, bb));
                if (max - min <= 24 && max >= 100 && max <= 200) hit++;
            }
        }
        return hit / (double)(w * h);
    }

    private static void FillBand(FrameData f, NormRect fr, byte[] sig, int sigOffset)
        => FillBandN(f, fr, sig, sigOffset, SigCols, SigRows);

    private static void FillBandN(FrameData f, NormRect fr, byte[] sig, int sigOffset, int cols, int rows)
    {
        if (f.Bgra.Length == 0) return; // 帧已释放兜底
        var (px, py, pw, ph) = FrameAnalyzer.PixelRect(f, fr);
        for (int sy = 0; sy < rows; sy++)
        {
            int y0 = py + sy * ph / rows;
            int y1 = Math.Max(y0 + 1, py + (sy + 1) * ph / rows);
            for (int sx = 0; sx < cols; sx++)
            {
                int x0 = px + sx * pw / cols;
                int x1 = Math.Max(x0 + 1, px + (sx + 1) * pw / cols);
                long acc = 0; int cnt = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * f.Width * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = row + xx * 4;
                        acc += (long)(b0(f, i) * 299 + b1(f, i) * 587 + b2(f, i) * 114) / 1000;
                        cnt++;
                    }
                }
                sig[sigOffset + sy * cols + sx] = (byte)(cnt == 0 ? 0 : acc / cnt);
            }
        }
        static byte b0(FrameData f, int i) => f.Bgra[i];
        static byte b1(FrameData f, int i) => f.Bgra[i + 1];
        static byte b2(FrameData f, int i) => f.Bgra[i + 2];
    }

    private static int Lum(FrameData f, int x, int y)
    {
        if (f.Bgra.Length == 0) return 0; // 帧已释放兜底
        int i = (y * f.Width + x) * 4;
        return (f.Bgra[i + 2] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i] * 114) / 1000;
    }

    /// <summary>
    /// 活动列表焦点卡定位：焦点卡由底部「继续」按钮的白色高亮唯一标识（新版本活动卡不再放大，
    /// 顶边最高判据失效）。真机实测按钮客户区 y 0.784~0.836（1282×752 帧像素 y 596~633），
    /// 仅焦点卡该按钮为白色、其余卡深灰。在按钮横带内取「白像素连通域」外接框，
    /// 其中心 X 即焦点卡中心 cx（绿勾/手柄/AI 图标均按 cx 定位，分辨率无关）。
    /// 返回帧像素 (left, top, right, bot)（=白按钮外接框）；找不到返回 null。
    /// </summary>
    public static (int left, int top, int right, int bot)? LocateActivityFocusedCard(
        FrameData f, double offX, double offY, double cliW, double cliH)
    {
        if (f.Bgra.Length == 0) return null; // 帧已释放兜底
        // 只圈「继续」白按钮本体（客户区 y 0.784~0.836），避开卡片底部白色描边（≈0.875）与奖励区文字，
        // 防止外框被边框撑大污染 cx。
        int by0 = Math.Clamp((int)Math.Round(offY + 0.78 * cliH), 0, f.Height - 1);
        int by1 = Math.Clamp((int)Math.Round(offY + 0.85 * cliH), by0 + 1, f.Height);
        if (by1 - by0 < 2) return null;

        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = by0; y < by1; y++)
        {
            int row = y * f.Width * 4;
            for (int x = 0; x < f.Width; x++)
            {
                int i = row + x * 4;
                int rr = f.Bgra[i + 2], gg = f.Bgra[i + 1], bb = f.Bgra[i];
                int mx = Math.Max(rr, Math.Max(gg, bb));
                int mn = Math.Min(rr, Math.Min(gg, bb));
                if (mn >= 200 && mx - mn <= 60)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0) return null;

        // 过滤小噪声/左上角残留（白按钮实测宽≈320 帧px≈0.25 客户区宽、高≈37 帧px）
        if (maxX - minX < Math.Max(60, (int)(0.10 * cliW))
            || maxY - minY < Math.Max(8, (int)(0.02 * cliH)))
            return null;

        return (minX, minY, maxX, maxY);
    }

    /// <summary>统计帧像素矩形内「近白」像素数（R/G/B 均 ≥200 且色差 ≤60）。</summary>
    public static int CountWhite(FrameData f, int x0, int x1, int y0, int y1)
    {
        if (f.Bgra.Length == 0) return 0;
        int hit = 0;
        int ya = Math.Max(0, y0), yb = Math.Min(f.Height, y1);
        int xa = Math.Max(0, x0), xb = Math.Min(f.Width, x1);
        for (int y = ya; y < yb; y++)
        {
            int row = y * f.Width * 4;
            for (int x = xa; x < xb; x++)
            {
                int i = row + x * 4;
                int rr = f.Bgra[i + 2], gg = f.Bgra[i + 1], bb = f.Bgra[i];
                int mx = Math.Max(rr, Math.Max(gg, bb));
                int mn = Math.Min(rr, Math.Min(gg, bb));
                if (mn >= 200 && mx - mn <= 60) hit++;
            }
        }
        return hit;
    }
}
