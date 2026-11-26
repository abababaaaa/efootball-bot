using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、像素探针、弹窗判定。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 本层不许 using / 引用 Core/Bot/Scenarios.*（共享层绝不能反向依赖模块）
//   · 场次记账（MatchesDone/KeyMatches）与比赛内挂机节奏只在本层实现，模块不许自己抄一份
// 新增共享能力：本层加文件；需要模块配合的用接口（如 IModuleLifecycle）反向交给模块。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 国服赛后 / 结算页左侧那块白色大卡（蓝色右箭头 + 底下一行「下一步」）的落点定位。
/// <para>
/// 为什么要单独一层（用户 2026-11-25 实机，两次都是同一页）：
/// 这页是国服客户端的<strong>网页层</strong>，底栏不印「RT 下一步」，推进键就是那块白卡本身，
/// 卡面在设计上就是按下去整块吃鼠标的按钮（箭头 + 三个字都在卡内）。
/// 但卡的<strong>位置和大小随页面版本变</strong>——实机两版帧：
/// <list type="bullet">
/// <item>版式 A（<c>fulltime_user_1.png</c>）：卡 x 0.028~0.239 / y 0.031~0.444，箭头中心 (0.130,0.215)；</item>
/// <item>版式 B（用户 2026-11-25 新截图）：卡 x 0.026~0.242 / y 0.019~0.881，箭头中心 (0.130,0.428)。</item>
/// </list>
/// 所以<strong>不能写死一个 y</strong>：旧实现两档都退到「国服右下兜底坐标 0.870,0.920」，
/// 这一页右下角是「RS ● ● ●」翻页指示，点什么都没反应，日志里就是「全场结束→下一步：第150轮·鼠标」
/// 一路刷到用户手动停挂机（2026-11-25 23:52 现场：连续 150+ 轮、约 8 分钟原地不动）。
/// </para>
/// <para>
/// 定位顺序（越靠前越准）：
/// <list type="number">
/// <item>OCR 在<strong>左 1/3 区域</strong>读到「下一步 / 继续」的整词（两版帧都读得到）→ 直接点字的中心；</item>
/// <item>整帧漏读按钮字 → <strong>像素探针找白卡</strong>（左 1/3 里近白的竖条 + 右半屏是深色游戏背景）→ 点卡内箭头位置（卡高 46% 处，两版帧实测误差 &lt;0.02）；</item>
/// <item>连帧都拿不到（极少：空帧）→ 仅当本页命中结算页签名词时，按标定落点轮换。</item>
/// </list>
/// </para>
/// 线程模型：纯函数 + 只读帧采样，与场景决策循环同线程调用，无锁、无状态。
/// </summary>
public static class ResultCardProbe
{
    // ---------------- 卡面几何（实机标定，1280×720 内容帧） ----------------

    /// <summary>左 1/3 搜索窗的左边界（卡的最左边实机 0.026，外面留一像素余量）。</summary>
    public const double SearchLeft = 0.005;

    /// <summary>搜索窗右边界：卡片最宽实机 0.242，留到 0.30 消化窗口化缩放。</summary>
    public const double SearchRight = 0.30;

    /// <summary>搜索窗上边界（卡顶实机 0.019~0.031）。</summary>
    public const double SearchTop = 0.010;

    /// <summary>搜索窗下边界（卡底实机 0.444~0.881）。</summary>
    public const double SearchBottom = 0.95;

    /// <summary>白卡像素判据：亮度均值过这条线算卡面（实机白底 235~255，游戏深色 UI 25~70）。</summary>
    public const int WhiteLuma = 200;

    /// <summary>卡面最小尺寸（归一化）。实机两版：0.211×0.413 与 0.216×0.862。</summary>
    public const double MinCardWidth = 0.06;

    public const double MinCardHeight = 0.12;

    /// <summary>卡面最大宽度：再宽就不是这张卡，而是整幅白底文档页（我的联赛说明页那种）。</summary>
    public const double MaxCardWidth = 0.34;

    /// <summary>卡顶必须在画面上部（实机 0.019~0.031）：白横条出现在中下部一律不当这张卡。</summary>
    public const double MaxCardTop = 0.15;

    /// <summary>右半屏亮度上限：结算页背景是深色面板，白底文档页右半屏同样雪白，用它把两者分开。</summary>
    public const int MaxBackdropLuma = 120;

    /// <summary>箭头在卡内的纵向位置（卡高比例）。两版帧实测 0.446 / 0.474，取中。</summary>
    public const double ArrowHeightRatio = 0.46;

    /// <summary>卡面上的推进词（网页层大卡只印箭头 + 这三个字，没有按键前缀）。</summary>
    public static readonly string[] CardWords = { "下一步", "下一步骤", "继续" };

    /// <summary>结算 / 赛后页的签名词（只在拿不到帧的极端情况下用来决定「敢不敢按标定落点」）。</summary>
    public static readonly string[] PageSigns = { "球员评价", "玩家评价", "全场", "比赛统计" };

    /// <summary>
    /// 拿不到帧时的标定落点（两版实机帧的箭头 / 文字中心）。轮换着点，
    /// 不在同一个坐标上无限连点——旧版「固定右下 0.870,0.920」连点 961 轮就是这么卡死的。
    /// </summary>
    public static readonly (double X, double Y)[] FallbackPoints =
    {
        (0.130, 0.215), // 版式 A：短卡，箭头在卡上部
        (0.130, 0.428), // 版式 B：长卡，箭头在画面中部
        (0.132, 0.500), // 版式 B 的「下一步」文字行（箭头正下方一档）
    };

    /// <summary>一次命中的落点（归一化 + 人话说明，直接进日志）。</summary>
    public readonly record struct CardHit(double X, double Y, string Where);

    /// <summary>
    /// 定位白卡推进按钮。返回 null = 这一帧既读不到卡面文字、也找不到白卡，调用方用自己的兜底。
    /// </summary>
    /// <param name="ocr">当前帧 OCR（可为 <see cref="OcrResult.Empty"/>）。</param>
    /// <param name="frame">当前帧像素（可为 null / 空帧：只走 OCR 与标定落点两条路）。</param>
    /// <param name="attempt">同一页面上的第几轮（≥0；用于标定落点轮换）。</param>
    public static CardHit? Locate(OcrResult? ocr, FrameData? frame, int attempt)
    {
        // ① OCR 读到卡面推进词：字的中心就是按钮中心（最准的一档）
        if (ocr is not null && FindCardWord(ocr) is { } word)
            return new CardHit(word.CenterX, word.CenterY, "白卡「下一步」文字");

        // ② 按钮字整帧漏读：像素探针找白卡，点卡内箭头位置
        if (FindCard(frame) is { } card)
        {
            double y = card.Y0 + (card.Y1 - card.Y0) * ArrowHeightRatio;
            return new CardHit((card.X0 + card.X1) / 2, y, "白卡像素探针");
        }

        // ③ 连帧都没有：只有本页确实是结算页才敢按标定落点轮换（宁可不点也不乱点别处）
        if (ocr is not null && LooksLikeCardPage(ocr))
        {
            var p = FallbackPoints[Math.Abs(attempt) % FallbackPoints.Length];
            return new CardHit(p.X, p.Y, "白卡标定落点");
        }
        return null;
    }

    /// <summary>
    /// 在 OCR 里找<strong>左 1/3 区域</strong>的卡面推进词：只要整词/短行（长度 ≤ 关键词+2），
    /// 合并长行的中心不可信（同 <see cref="CnAdvance.FindButton"/> 的 2026-11-25 事故），一律不作锚点。
    /// 同一档里取最靠左的一枚（白卡贴着左边缘，卡外的长文本都在右边）。
    /// </summary>
    public static OcrWord? FindCardWord(OcrResult ocr)
    {
        OcrWord? best = null;
        foreach (var w in ocr.Words)
        {
            if (w.CenterX > SearchRight) continue;                       // 只看左 1/3
            if (w.CenterY < 0.03 || w.CenterY > 0.95) continue;           // 标题栏/底边噪声
            string? key = CardWords.FirstOrDefault(k => w.NormText.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (key is null) continue;
            if (w.NormText.Length > key.Length + 2) continue;             // 合并长行不作锚点
            if (best is null || w.CenterX < best.CenterX) best = w;
        }
        return best;
    }

    /// <summary>
    /// 本页是不是「结算页」家族（球员评价 / 玩家评价 / 全场 / 比赛统计）。
    /// 只在拿不到帧时用于决定敢不敢按标定落点，不参与正常定位。
    /// </summary>
    public static bool LooksLikeCardPage(OcrResult ocr)
        => PageSigns.Any(s => ocr.ContainsJoined(s));

    /// <summary>
    /// 像素探针：在左 1/3 里找那块近白白卡，返回归一化包围盒；找不到返回 null。
    /// 判据三重（缺一不可）：
    /// <list type="number">
    /// <item>该列/该行有足够比例的近白像素（亮度 ≥ <see cref="WhiteLuma"/>）；</item>
    /// <item>卡宽 0.06~0.34、卡高 ≥ 0.12、卡顶在 0.15 以上；</item>
    /// <item>右半屏是深色（亮度 ≤ <see cref="MaxBackdropLuma"/>）——白底文档页的右半屏同样是白的，靠这条排除。</item>
    /// </list>
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1)? FindCard(FrameData? frame)
    {
        if (frame is null) return null;
        int w = frame.Width, h = frame.Height;
        if (w < 200 || h < 200 || frame.Bgra.Length < w * h * 4) return null;
        var bgra = frame.Bgra;

        const int step = 4;
        int x0 = Math.Max(0, (int)(w * SearchLeft)), x1 = Math.Min(w - 1, (int)(w * SearchRight));
        int y0 = Math.Max(0, (int)(h * SearchTop)), y1 = Math.Min(h - 1, (int)(h * SearchBottom));
        if (x1 - x0 < step * 8 || y1 - y0 < step * 8) return null;
        int rows = (y1 - y0) / step + 1;
        int rowsNeed = (int)(rows * 0.25); // 版式 A 的卡只占左列 ~44% 高度，阈值必须放低

        // ---- 第一遍：逐列找近白的连续列段（取最宽的一段）----
        int runStart = -1, bestL = -1, bestR = -1, bestW = 0;
        for (int x = x0; x <= x1; x += step)
        {
            int cnt = 0;
            for (int y = y0; y <= y1; y += step)
                if (IsWhite(bgra, (y * w + x) * 4)) cnt++;
            if (cnt >= rowsNeed)
            {
                if (runStart < 0) runStart = x;
            }
            else if (runStart >= 0)
            {
                if (x - step - runStart > bestW) { bestW = x - step - runStart; bestL = runStart; bestR = x - step; }
                runStart = -1;
            }
        }
        if (runStart >= 0 && x1 - runStart > bestW) { bestW = x1 - runStart; bestL = runStart; bestR = x1; }
        if (bestL < 0 || bestR - bestL < (int)(w * MinCardWidth)) return null;

        // ---- 第二遍：在这几列里逐行找近白的连续行段（取最高的一段 = 卡本体）----
        int cols = (bestR - bestL) / step + 1;
        int colsNeed = Math.Max(1, (int)(cols * 0.6));
        int top = -1, bot = -1, bestH = 0, rStart = -1;
        for (int y = y0; y <= y1; y += step)
        {
            int cnt = 0;
            for (int x = bestL; x <= bestR; x += step)
                if (IsWhite(bgra, (y * w + x) * 4)) cnt++;
            if (cnt >= colsNeed)
            {
                if (rStart < 0) rStart = y;
            }
            else if (rStart >= 0)
            {
                if (y - step - rStart > bestH) { bestH = y - step - rStart; top = rStart; bot = y - step; }
                rStart = -1;
            }
        }
        if (rStart >= 0 && y1 - rStart > bestH) { bestH = y1 - rStart; top = rStart; bot = y1; }
        if (top < 0) return null;

        double cardW = bestW / (double)w, cardH = bestH / (double)h;
        if (cardW > MaxCardWidth) return null;              // 整幅白底文档页
        if (cardH < MinCardHeight) return null;             // 白横条（顶栏）不当卡
        if (top / (double)h > MaxCardTop) return null;      // 白卡只贴在画面上部
        if (BackdropLuma(bgra, w, h) > MaxBackdropLuma) return null; // 右半屏也白 → 不是结算页

        return ((double)bestL / w, (double)top / h, (double)(bestL + bestW) / w, (double)bot / h);
    }

    private static bool IsWhite(byte[] bgra, int i)
        => (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 >= WhiteLuma;

    /// <summary>右半屏平均亮度（x 0.35~0.95 / y 0.10~0.90，8px 步进）：区分「深色结算页」与「白底文档页」。</summary>
    private static int BackdropLuma(byte[] bgra, int w, int h)
    {
        const int step = 8;
        int sum = 0, n = 0;
        for (int y = (int)(h * 0.10); y < (int)(h * 0.90); y += step)
            for (int x = (int)(w * 0.35); x < (int)(w * 0.95); x += step)
            {
                int i = (y * w + x) * 4;
                sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                n++;
            }
        return n == 0 ? 255 : sum / n;
    }
}
