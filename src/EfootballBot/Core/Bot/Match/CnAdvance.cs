using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

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
/// 国服「下一步 / 确认 / 确定」推进键序（用户 2026-11-21 定的规则）：
/// <list type="number">
/// <item>① 先按页面自己印出来的提示键 <b>RT</b>——国服底栏几乎都写「RT 下一步 / RT 确认」；</item>
/// <item>② RT 按了画面不动（该页 RT 无绑定）→ 按 <b>A</b>；</item>
/// <item>③ A 也不动（国服客户端里的「网页层」页面只吃鼠标，实机 2026-11-07 段位积分页 A×5 不动）
///       → 后台 <b>鼠标点击</b>按钮：OCR 推进词 → 右下 btn_confirm 检测框 → 兜底坐标。</item>
/// </list>
/// 为什么放共享层：国服天梯巅峰赛 / 国服周活动 / 我的联赛（国服）三个模块都会撞上同一批页面
/// （段位积分页、赛后小结、比赛起始页、各种结算页），此前每个模块各写一套「先 A 再 RT 再鼠标」
/// 的阶梯，键序还不一样；现在只在这里写一份，模块只传「同一页第几轮」。
/// <para>
/// <b>仅限国服</b>：国际服客户端不吃后台鼠标（PostMessage 不被处理），所以国际服模块不调用本类，
/// 仍走 A 为主、必要时才 RT 的老路。
/// </para>
/// 线程模型：与场景决策循环同线程调用，无锁。
/// </summary>
public static class CnAdvance
{
    /// <summary>本类的一次推进手段。</summary>
    public enum Step
    {
        /// <summary>按底栏所印的 RT（右扳机）。</summary>
        Rt,
        /// <summary>按 A（手柄确认键）。</summary>
        Confirm,
        /// <summary>后台鼠标点击推进按钮。</summary>
        Mouse,
    }

    /// <summary>第几轮起改鼠标：①RT ②A ③鼠标…（与 <see cref="StepOf"/> 一致，供测试与日志用）。</summary>
    public const int MouseFromAttempt = 3;

    /// <summary>OCR 与检测都定位不到按钮时的国服通用右下落点（1280×720 与 1920×1080 都落在按钮内）。</summary>
    public const double DefaultX = 0.870;

    public const double DefaultY = 0.920;

    /// <summary>国服推进按钮的通用词表（结算页/弹窗共用的固定 UI 词；段位名/积分等可变内容不作锚点）。</summary>
    public static readonly string[] AdvanceWords = { "下一步", "确认", "確定", "确定", "OK" };

    /// <summary>
    /// 键序：同一页面上的第 <paramref name="attempt"/> 轮（1 起）用什么手段。
    /// 1 → RT、2 → A、≥3 → 鼠标。
    /// </summary>
    public static Step StepOf(int attempt) => attempt <= 1 ? Step.Rt : attempt == 2 ? Step.Confirm : Step.Mouse;

    /// <summary>
    /// 在帧上找推进按钮的落点（归一化）：先看 OCR 下半屏（y&gt;0.55）里的推进词，取最靠下、
    /// 同高取最靠右的一枚（结算页的推进键在右下、弹窗的确认键在底部按钮行）；OCR 整帧漏读
    /// 按钮字时（段位积分页常见）退回右下角的 btn_confirm 检测框。都找不到返回 null，
    /// 调用方用兜底坐标。
    /// </summary>
    public static (double X, double Y)? FindButton(OcrResult ocr, IReadOnlyList<YoloDetection>? dets,
        params string[] words)
    {
        IEnumerable<string> keys = words is { Length: > 0 } ? words : AdvanceWords;
        OcrWord? Pick(IEnumerable<OcrWord> pool)
        {
            OcrWord? best = null;
            foreach (var w in pool)
            {
                if (w.CenterY < 0.55 || w.W > 0.35) continue; // 顶栏不参与，且剔除跨半屏整行合并伪框
                if (!MatchesAnyAdvanceKey(w.NormText, keys)) continue;
                if (best is null || w.CenterY > best.CenterY + 0.02 ||
                    (Math.Abs(w.CenterY - best.CenterY) <= 0.02 && w.CenterX > best.CenterX))
                    best = w;
            }
            return best;
        }
        var hit = Pick(ocr.Words) ?? Pick(ocr.MergedWords);
        if (hit is not null) return (hit.CenterX, hit.CenterY);
        if (FindRightBottomConfirm(dets) is { } box) return (box.CenterX, box.CenterY);
        return null;
    }

    private static bool MatchesAnyAdvanceKey(string normText, IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            bool ascii = k.Length > 0 && k.All(c => c < 128);
            if (ascii ? normText.Equals(k, StringComparison.OrdinalIgnoreCase)
                      : normText.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 右下角的 btn_confirm 检测框（国服结算页的「RT 下一步」大蓝条，实机置信度 0.92）。
    /// 限定右下四分之一（x&gt;0.55 / y&gt;0.72），避开比赛中回放横幅、列表行等误检；
    /// 同分区取置信度最高的一枚。dets 为空返回 null。
    /// </summary>
    public static YoloDetection? FindRightBottomConfirm(IReadOnlyList<YoloDetection>? dets, float minScore = 0.45f)
    {
        if (dets is null) return null;
        YoloDetection? best = null;
        foreach (var d in dets)
        {
            if (d.ClassName != "btn_confirm" || d.Score < minScore) continue;
            if (d.CenterX < 0.55 || d.CenterY < 0.72) continue;
            if (best is null || d.Score > best.Score) best = d;
        }
        return best;
    }

    /// <summary>
    /// 按国服键序推进一步，返回本轮建议等待毫秒。
    /// </summary>
    /// <param name="engine">引擎（取窗口句柄与 Pad）。</param>
    /// <param name="mouse">后台鼠标（模块持有的实例，保证轨迹连续）。</param>
    /// <param name="attempt">同一页面上的第几轮（1 起，通常传 streak+1）。</param>
    /// <param name="what">日志里的页面名（如「段位积分页」）。</param>
    /// <param name="module">日志前缀里的模块名（如「天梯巅峰赛」）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ocr">当前帧 OCR（找按钮锚点用）。</param>
    /// <param name="dets">当前帧 YOLO 检测框（OCR 漏读时找右下按钮框）。</param>
    /// <param name="fallback">OCR/检测都定位不到按钮时的兜底落点；null = 用国服通用右下落点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="words">该页的推进词（空 = <see cref="AdvanceWords"/>）。</param>
    public static async Task<int> AdvanceAsync(BotEngine engine, BackgroundMouse mouse, int attempt,
        string what, string module, Action<string, LogLevel> log, OcrResult ocr,
        IReadOnlyList<YoloDetection>? dets, (double X, double Y)? fallback, CancellationToken ct,
        params string[] words)
    {
        switch (StepOf(attempt))
        {
            case Step.Rt:
                log($"[{module}] {what}：第 {attempt} 轮先按底栏印的 RT（国服下一步优先提示键）", LogLevel.Info);
                await engine.Pad.RT();
                return 2800;
            case Step.Confirm:
                log($"[{module}] {what}：RT 未推进，第 {attempt} 轮改按 A", LogLevel.Info);
                await engine.Pad.Confirm();
                return 2800;
        }
        var hit = FindButton(ocr, dets, words);
        (double nx, double ny) = hit ?? fallback ?? (DefaultX, DefaultY);
        string where = hit is not null ? "按钮位置" : fallback is not null ? "兜底坐标" : "国服右下兜底坐标";
        log($"[{module}] {what}：RT/A 均未推进，第 {attempt} 轮改鼠标点击（{where} {nx:F3},{ny:F3}）"
            + "——国服网页层页面只吃鼠标，需本程序以管理员运行", LogLevel.Warn);
        await ClickAsync(engine, mouse, nx, ny, module, log, ct);
        return 3000;
    }

    /// <summary>后台鼠标点击一次（归一化坐标）。窗口句柄缺失时退回按 A，绝不让模块卡在这条路径上。</summary>
    public static async Task ClickAsync(BotEngine engine, BackgroundMouse mouse, double nx, double ny,
        string module, Action<string, LogLevel> log, CancellationToken ct)
    {
        var win = engine.Window;
        if (win is null)
        {
            log($"[{module}] 游戏窗口句柄缺失，鼠标点击无法执行：改按 A", LogLevel.Error);
            await engine.Pad.Confirm();
            return;
        }
        // 全限定：GameWindow 在 EfootballBot.Core.System 命名空间（本文件已有 using 时也无冲突）
        var (w, h) = EfootballBot.Core.System.GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        await engine.Pad.HumanWait(150, 320, ct); // 拟人预停顿
        await mouse.ClickNormalizedAsync(win.Handle, nx, ny, w, h, downHoldMs: 70, ct: ct);
    }

    /// <summary>国服主页只吃鼠标的提示弹窗（「来试试技巧提升吧」等）：先点确定/确认/知道了，防止吃光 D-pad。</summary>
    public static async Task<bool> TryDismissHomePopupAsync(BotEngine engine, BackgroundMouse mouse, OcrResult ocr,
        string module, Action<string, LogLevel> log, CancellationToken ct)
    {
        var hit = FindButton(ocr, null, "确定", "确认", "知道了");
        if (hit is not { } pos) return false;
        log($"[{module}] 国服主页有鼠标提示弹窗（确定/知道了），鼠标点击关闭后再继续导航", LogLevel.Info);
        await ClickAsync(engine, mouse, pos.X, pos.Y, module, log, ct);
        return true;
    }
}
