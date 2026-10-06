using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision;

/// <summary>检测到的焦点（高光）卡。</summary>
/// <param name="Name">卡名：兑换积分/前往比赛/赛季纪录/建议/赛事信息/赛程表/i/活动/eFootball联赛/我的联赛/自定义锦标赛/快速；无法识别为 unknown</param>
/// <param name="CX">中心 X（归一化）</param>
/// <param name="CY">中心 Y（归一化）</param>
/// <param name="Confidence">置信度（白格占卡片区比例，0~1）</param>
public sealed record FocusCard(string Name, double CX, double CY, double X, double Y, double W, double H, double Confidence)
{
    public bool IsExchange => Name == "兑换积分";
}

/// <summary>
/// 焦点卡检测器：焦点卡是全屏唯一的大面积纯白圆角块，
/// 用粗网格白块连通域定位，再读白框内 OCR 标题命名；深色照片卡用亮描边兜底。
/// </summary>
public static class FocusCardDetector
{
    // 卡片区：MatchHub 卡片占满全屏，扩大范围覆盖全部卡片区域
    private const double Rx0 = 0.00, Ry0 = 0.10, Rx1 = 1.00, Ry1 = 0.90;
    private const int GridW = 60, GridH = 32;
    private const double CellWhiteRatio = 0.25;   // 单格白像素占比阈值（MatchHub 高亮卡边框较细，降低阈值）
    private const int MinComponentCells = 8;      // 降低最小连通域，适配 MatchHub 小卡片

    private static readonly (string Name, string[] Keys)[] Titles =
    {
        ("兑换积分", new[] { "兑换积分", "兑换稹分", "兑换" }),
        ("前往比赛", new[] { "前往比赛", "前往比賽" }),
        ("赛季纪录", new[] { "赛季纪录", "賽季紀錄", "赛季记录", "賽季記錄" }),
        ("建议",     new[] { "建议", "建議" }),
        ("赛事信息", new[] { "赛事信息", "賽事信息", "赛事", "賽事" }),
        // MatchHub 卡片
        ("活动",     new[] { "活动", "活動" }),
        ("eFootball联赛", new[] { "eFootball", "efootball", "联赛", "聯賽" }),
        ("我的联赛", new[] { "我的联赛", "我的聯賽" }),
        ("自定义锦标赛", new[] { "自定义锦标赛", "自定義錦標賽", "锦标赛", "錦標賽" }),
        ("快速",     new[] { "快速" }),
    };

    public static FocusCard? Detect(FrameData f, OcrResult ocr)
    {
        if (f.Bgra.Length == 0) return null;

        var white = BuildWhiteGrid(f);
        var comp = LargestComponent(white);
        if (comp is not null)
        {
            var (gx0, gy0, gx1, gy1, cells) = comp.Value;
            double x = Rx0 + gx0 * ((Rx1 - Rx0) / GridW);
            double y = Ry0 + gy0 * ((Ry1 - Ry0) / GridH);
            double w = (gx1 - gx0 + 1) * ((Rx1 - Rx0) / GridW);
            double h = (gy1 - gy0 + 1) * ((Ry1 - Ry0) / GridH);
            double cx = x + w / 2, cy = y + h / 2;
            double conf = cells / (double)(GridW * GridH);

            // 置信度过低：可能是噪声（如 i 图标、小光点），直接忽略
            if (conf < 0.05) return null;

            string name = ReadCardName(ocr, cx, cy, Math.Max(w, 0.06), Math.Max(h, 0.05));
            // 小白方块（i 图标）：面积小且无标题，且置信度低——直接忽略
            if (name == "unknown" && w < 0.12 && h < 0.20) return null;
            return new FocusCard(name, cx, cy, x, y, w, h, conf);
        }

        // 深色照片卡 + 白色亮描边兜底
        return DetectPhotoCard(f, ocr);
    }

    // ---------------- 白块网格 ----------------

    private static bool[,] BuildWhiteGrid(FrameData f)
    {
        var grid = new bool[GridW, GridH];
        int px0 = (int)(Rx0 * f.Width), px1 = (int)(Rx1 * f.Width);
        int py0 = (int)(Ry0 * f.Height), py1 = (int)(Ry1 * f.Height);
        double cwPx = (px1 - px0) / (double)GridW;
        double chPx = (py1 - py0) / (double)GridH;
        var bgra = f.Bgra;
        int stride = f.Width * 4;

        for (int gy = 0; gy < GridH; gy++)
        {
            int sy = py0 + (int)(gy * chPx);
            int sye = Math.Max(sy + 1, py0 + (int)((gy + 1) * chPx));
            for (int gx = 0; gx < GridW; gx++)
            {
                int sx = px0 + (int)(gx * cwPx);
                int sxe = Math.Max(sx + 1, px0 + (int)((gx + 1) * cwPx));
                long hit = 0, n = 0;
                for (int yy = sy; yy < sye; yy += 3)
                {
                    int row = yy * stride;
                    for (int xx = sx; xx < sxe; xx += 3)
                    {
                        int i = row + xx * 4;
                        byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                        // 格内抽 4×4 子点
                        if (r >= 160 && g >= 160 && b >= 160 &&
                            Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 40)
                            hit++;
                        n++;
                    }
                }
                grid[gx, gy] = n > 0 && hit / (double)n >= CellWhiteRatio;
            }
        }
        return grid;
    }

    private static (int X0, int Y0, int X1, int Y1, int Cells)? LargestComponent(bool[,] grid)
    {
        var seen = new bool[GridW, GridH];
        (int, int, int, int, int)? best = null;
        for (int sy = 0; sy < GridH; sy++)
            for (int sx = 0; sx < GridW; sx++)
            {
                if (!grid[sx, sy] || seen[sx, sy]) continue;
                int x0 = sx, x1 = sx, y0 = sy, y1 = sy, cells = 0;
                var stack = new Stack<(int X, int Y)>();
                stack.Push((sx, sy));
                seen[sx, sy] = true;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    cells++;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                    TryPush(grid, seen, stack, x + 1, y);
                    TryPush(grid, seen, stack, x - 1, y);
                    TryPush(grid, seen, stack, x, y + 1);
                    TryPush(grid, seen, stack, x, y - 1);
                }
                if (cells >= MinComponentCells && (best is null || cells > best.Value.Item5))
                    best = (x0, y0, x1, y1, cells);
            }
        return best;
    }

    private static void TryPush(bool[,] grid, bool[,] seen, Stack<(int, int)> stack, int x, int y)
    {
        if (x < 0 || x >= GridW || y < 0 || y >= GridH) return;
        if (!grid[x, y] || seen[x, y]) return;
        seen[x, y] = true;
        stack.Push((x, y));
    }

    // ---------------- 卡名识别 ----------------

    private static string ReadCardName(OcrResult ocr, double cx, double cy, double halfW, double halfH)
    {
        foreach (var w in ocr.MergedWords)
        {
            // 词中心落在白框内
            if (Math.Abs(w.CenterX - cx) > halfW || Math.Abs(w.CenterY - cy) > halfH) continue;
            string t = OcrText.Norm(w.Text);
            foreach (var (name, keys) in Titles)
                foreach (var k in keys)
                    if (t.Contains(k, StringComparison.OrdinalIgnoreCase))
                        return name;
        }
        return "unknown";
    }

    // ---------------- 照片卡兜底 ----------------

    private static FocusCard? DetectPhotoCard(FrameData f, OcrResult ocr)
    {
        // 左中卡：x[0.03,0.34] y[0.345,0.685]，四条边采样亮像素
        var strips = new NormRect[]
        {
            new(0.03, 0.345, 0.31, 0.014),  // 上
            new(0.03, 0.672, 0.31, 0.014),  // 下
            new(0.03, 0.345, 0.012, 0.34),  // 左
            new(0.328, 0.345, 0.012, 0.34), // 右
        };
        int hitStrips = 0;
        foreach (var r in strips)
            if (BrightEdgeRatio(f, r) >= 0.15) hitStrips++;
        bool edgeHit = hitStrips >= 3;

        // OCR 槽位佐证：亮边不足时，「赛程表」标题词中心落在左中槽位也算焦点照片卡
        bool ocrHit = false;
        if (!edgeHit)
        {
            foreach (var w in ocr.FindAll("赛程"))
            {
                if (w.CenterX >= 0.03 && w.CenterX <= 0.35 && w.CenterY >= 0.32 && w.CenterY <= 0.72)
                { ocrHit = true; break; }
            }
        }
        if (!edgeHit && !ocrHit) return null;
        return new FocusCard("赛程表", 0.185, 0.515, 0.03, 0.345, 0.31, 0.34, edgeHit ? 0.3 : 0.15);
    }

    private static double BrightEdgeRatio(FrameData f, NormRect r)
    {
        var (x, y, w, h) = FrameAnalyzer.PixelRect(f, r);
        long hit = 0, n = 0;
        var bgra = f.Bgra;
        int stride = f.Width * 4;
        for (int yy = y; yy < y + h; yy += 2)
        {
            int row = yy * stride;
            for (int xx = x; xx < x + w; xx += 2)
            {
                int i = row + xx * 4;
                byte b = bgra[i], g = bgra[i + 1], rr = bgra[i + 2];
                if (rr >= 180 && g >= 180 && b >= 180 &&
                    Math.Max(rr, Math.Max(g, b)) - Math.Min(rr, Math.Min(g, b)) <= 40)
                    hit++;
                n++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }
}
