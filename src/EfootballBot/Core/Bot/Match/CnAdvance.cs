using EfootballBot.Core.Capture;
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
/// 国服「下一步 / 确认 / 确定」推进键序（用户 2026-11-21 定的规则；2026-11-25 客户改版后
/// 底栏「下一步」一律印 RT、A 退成兜底，键序不变但 RT 地位更硬）：
/// <list type="number">
/// <item>① 先按页面自己印出来的提示键 <b>RT</b>——国服底栏几乎都写「RT 下一步 / RT 确认」；</item>
/// <item>② RT 按了画面不动（该页 RT 无绑定）→ 按 <b>A</b>；</item>
/// <item>③ A 也不动（国服客户端里的「网页层」页面只吃鼠标，实机 2026-11-07 段位积分页 A×5 不动）
///       → 后台 <b>鼠标点击</b>按钮：OCR 推进词 → 右下 btn_confirm 检测框 → 页面兜底坐标 →
///       <b>白卡推进按钮</b>（<see cref="ResultCardProbe"/>：赛后「球员评价」页那块箭头大卡）。</item>
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

    /// <summary>第几轮起按 A（用户 2026-11-25 实机定稿：RT 不动就补 A）。</summary>
    public const int ConfirmAtAttempt = 2;

    /// <summary>第几轮起改鼠标兜底：① RT ② A ③起鼠标。</summary>
    public const int MouseFromAttempt = 3;

    /// <summary>OCR 与检测都定位不到按钮时的国服通用右下落点（1280×720 与 1920×1080 都落在按钮内）。</summary>
    public const double DefaultX = 0.870;

    public const double DefaultY = 0.920;

    /// <summary>国服推进按钮的通用词表（结算页/弹窗共用的固定 UI 词；段位名/积分等可变内容不作锚点）。</summary>
    public static readonly string[] AdvanceWords = { "下一步", "确认", "確定", "确定", "OK" };

    /// <summary>
    /// 国服「进入」类按钮词（活动列表/介绍页/卡面/阵容菜单/弹窗）：用户 2026-11-25 定，
    /// 国服先按 RT、再按 A，最后才点鼠标，所以这些词也进按钮定位词表。
    /// </summary>
    public static readonly string[] EnterWords =
    {
        "进入", "加入", "参加", "对阵电脑", "前往比赛", "比赛活动", "球队阵容", "活动加成",
        "单场比赛", "开始", "保存", "购买", "签下", "确定", "确认", "下一步",
    };

    /// <summary>
    /// 赛外键序（用户 2026-11-25 定稿，实测「全场结束 / 球员评价」页 RT 不动、鼠标被 UIPI 拦）：
    /// <b>① 1 轮 RT → ② 2 轮 A → ③ ≥3 轮后台鼠标点按钮</b>。
    /// 三步都不动才继续退避重试，不会再出现「只 RT 和鼠标、RT 无绑定就原地空点十几轮」。
    /// </summary>
    public static Step StepOf(int attempt) => attempt switch
    {
        <= 1 => Step.Rt,
        ConfirmAtAttempt => Step.Confirm,
        _ => Step.Mouse,
    };

    /// <summary>
    /// 比赛中弹窗的键序：<b>每轮都按 RT</b>。2026-11-25 国服改版后弹窗的「确认 / 下一步」
    /// 也印 RT；比赛里 A 是传球/逼抢、鼠标点击会落到球场上，所以这条链只发 RT
    /// （赛外那条 ①RT→②A→③鼠标 的阶梯不适用于比赛内）。
    /// <para>
    /// <b>仅限国服</b>：国际服比赛内仍按 A（<see cref="MatchActionExecutor"/> 的默认 PadConfirm
    /// 分支 / <c>MyLeagueScenario.AdvanceConfirmAsync</c> 基类实现），国际服的 RT 是冲刺键。
    /// 边界由 <c>ArchitectureTests.国服键序只许国服模块引用_国际服比赛内仍按A</c> 锁死。
    /// </para>
    /// </summary>
    public static Step InMatchStepOf(int attempt) => Step.Rt;

    /// <summary>
    /// 比赛中弹窗的节拍：0.9s 一拍「连点」——用户 2026-11-25 反馈「右下角有下一步弹窗
    /// 反倒不一直点了」：比赛内这层覆盖弹窗必须连着点，弹窗一消失就自然回到比赛挂机节奏；
    /// 赛外那套 2.8s 的慢节拍只适合菜单页。
    /// </summary>
    public const int InMatchWaitMs = 900;

    /// <summary>
    /// 比赛中弹窗确认一次（RT 优先，A 交替兜底），返回本轮建议等待毫秒。
    /// </summary>
    /// <param name="engine">引擎（取 Pad）。</param>
    /// <param name="attempt">同一弹窗上的第几轮（1 起，通常传 streak+1）。</param>
    /// <param name="what">日志里的弹窗名（通常是 action.Target）。</param>
    /// <param name="module">日志前缀里的模块名。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<int> ConfirmInMatchAsync(BotEngine engine, int attempt, string what,
        string module, Action<string, LogLevel> log, CancellationToken ct, int waitMs = 0)
    {
        ct.ThrowIfCancellationRequested();
        // 比赛内只发 RT：用户 2026-11-25 定「国服不用 A」，比赛里也不点鼠标（会落到球场）
        // waitMs>0 = 上层给的节拍（「看见右下角下一步弹窗立马点」会传 200ms）
        log($"[{module}] {what}：弹窗{attempt}·RT", LogLevel.Info);
        await engine.Pad.RT();
        return waitMs > 0 ? waitMs : InMatchWaitMs;
    }

    /// <summary>
    /// 单键确认页（难度页 / 教练资料页 / 签下确认 / 进入类按钮）用的阶梯，与 <see cref="StepOf"/> 同一条：
    /// ① RT → ② A → ③ 后台鼠标点按钮。
    /// </summary>
    public static Step ConfirmStepOf(int attempt) => StepOf(attempt);

    /// <summary>
    /// 按「RT → A → 鼠标」确认一次（单键确认页用），返回本轮建议等待毫秒。
    /// 鼠标那一轮的落点：OCR 下半屏的推进词 → 右下 btn_confirm 检测框 → 兜底坐标，
    /// 找不到按钮字也不会点到别处（默认右下落点）。
    /// </summary>
    /// <param name="engine">引擎（取窗口句柄与 Pad）。</param>
    /// <param name="mouse">后台鼠标（模块持有的实例，保证轨迹连续）。</param>
    /// <param name="attempt">同一页面上的第几轮（1 起，通常传 streak+1）。</param>
    /// <param name="what">日志里的页面名（如「难度页确认」）。</param>
    /// <param name="module">日志前缀里的模块名（如「国服周活动」）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ocr">当前帧 OCR（找确认按钮锚点用）。</param>
    /// <param name="dets">当前帧 YOLO 检测框（OCR 漏读时找右下按钮框）。</param>
    /// <param name="fallback">OCR/检测都定位不到按钮时的兜底落点；null = 国服通用右下落点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="words">该页的确认词（空 = <see cref="AdvanceWords"/>）。</param>
    public static async Task<int> ConfirmAsync(BotEngine engine, BackgroundMouse mouse, int attempt,
        string what, string module, Action<string, LogLevel> log, OcrResult ocr,
        IReadOnlyList<YoloDetection>? dets, (double X, double Y)? fallback, CancellationToken ct,
        params string[] words)
    {
        switch (ConfirmStepOf(attempt))
        {
            case Step.Rt:
                log($"[{module}] {what}：第{attempt}轮·RT", LogLevel.Info);
                await engine.Pad.RT();
                return 2800;
            case Step.Confirm:
                log($"[{module}] {what}：第{attempt}轮·A（RT 没推进，补手柄 A）", LogLevel.Info);
                await engine.Pad.Confirm();
                return 2500;
        }
        string[] keys = words is { Length: > 0 } ? words : AdvanceWords.Concat(EnterWords).Distinct().ToArray();
        var hit = FindButton(ocr, dets, keys);
        (double nx, double ny) = hit ?? fallback ?? (DefaultX, DefaultY);
        string where = hit is not null ? "按钮位置" : fallback is not null ? "兜底坐标" : "国服右下兜底坐标";
        log($"[{module}] {what}：第{attempt}轮·鼠标({where} {nx:F3},{ny:F3})", LogLevel.Warn);
        await ClickAsync(engine, mouse, nx, ny, module, log, ct);
        return 3000;
    }

    /// <summary>
    /// 在帧上找推进按钮的落点（归一化）：先看 OCR 下半屏（y&gt;0.55）里的推进词，取最靠下、
    /// 同高取最靠右的一枚（结算页的推进键在右下、弹窗的确认键在底部按钮行）；OCR 整帧漏读
    /// 按钮字时（段位积分页常见）退回右下角的 btn_confirm 检测框。都找不到返回 null，
    /// 调用方用兜底坐标。
    /// <para>
    /// ★2026-11-25 实机事故（重要）：<see cref="OcrResult.MergedWords"/> 里除了原始词，还有
    /// 「同一排版行合并成的整行词」。国服「奖励已发放至收件箱」弹窗上，弹窗自己的「确定」按钮
    /// 与右侧页面上的「奖励累计领取2/10次」被合成一条 <c>确定奖励累计领取210次</c>，它的中心
    /// x=0.716 —— 已经在蓝色按钮（x 0.33~0.68）右边外面。旧实现按「同高取最靠右」选中这条合并行，
    /// 后台鼠标每一轮都点在按钮右侧的空白面板上，弹窗 961 轮没关掉（挂机白跑 56 分钟）。
    /// 现在按词框可信度分两档：<b>整词/短行（长度 ≤ 关键词+2，如「确定」「RT下一步」）优先</b>，
    /// 只有整帧都找不到短行时才退回合并长行的中心（长行中心不可信，但总比没有落点强）。
    /// </para>
    /// </summary>
    public static (double X, double Y)? FindButton(OcrResult ocr, IReadOnlyList<YoloDetection>? dets,
        params string[] words)
    {
        IReadOnlyList<string> keys = words is { Length: > 0 } ? words : AdvanceWords;
        OcrWord? compactBest = null; // 整词 / 「RT下一步」这类带按键前缀的短行：中心点落在按钮上
        OcrWord? rowBest = null;     // 合并长行：中心点会漂到整行中部，只能当最后手段
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY < 0.55) continue; // 顶栏页签/标题行不参与（推进键只在下半屏）
            string? key = keys.FirstOrDefault(k => w.NormText.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (key is null) continue;
            bool compact = w.NormText.Length <= key.Length + 2;
            if (compact)
            {
                if (compactBest is null || Better(w, compactBest)) compactBest = w;
            }
            else if (rowBest is null || Better(w, rowBest)) rowBest = w;
        }
        if (compactBest is not null) return (compactBest.CenterX, compactBest.CenterY);
        if (rowBest is not null) return (rowBest.CenterX, rowBest.CenterY);
        if (FindRightBottomConfirm(dets) is { } box) return (box.CenterX, box.CenterY);
        return null;

        // 同一档内的先后：更靠下（推进键在底部）优先；同一行（±0.02）再取更靠右（结算页在右下）
        static bool Better(OcrWord a, OcrWord b)
            => a.CenterY > b.CenterY || (Math.Abs(a.CenterY - b.CenterY) < 0.02 && a.CenterX > b.CenterX);
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

    // ---------------- 奖励/结算结果弹窗（单键「确定」）的落点档位 ----------------

    /// <summary>
    /// 单键「确定」弹窗上第 3 轮起轮转的落点档位。用户 2026-11-25 实机：
    /// 这类弹窗 RT / A 都没有绑定，只有后台鼠标点得动；但落点必须一次比一次更"贴身"，
    /// 否则像旧版那样固定一个坐标连点，坐标一旦偏出按钮就永远不生效（961 轮 = 56 分钟）。
    /// </summary>
    public enum PopupVariant
    {
        /// <summary>按 OCR / 检测定位到的按钮（最准的一档，优先上）。</summary>
        OcrHit,
        /// <summary>弹窗底部按钮行（按弹窗版本标定的 y）。</summary>
        ButtonRow,
        /// <summary>另一版弹窗的按钮行（两个结果弹窗的按钮 y 差 0.08，互为保险）。</summary>
        AltButtonRow,
        /// <summary>同一落点双击（个别网页层弹窗第一下只当"取焦点"）。</summary>
        DoubleClick,
        /// <summary>再按一次 RT（手柄通道偶发被吞，隔几轮重发一次）。</summary>
        RetryRt,
        /// <summary>按 B 试关弹窗。</summary>
        Back,
    }

    /// <summary>一轮轮转覆盖的档位数（每 <see cref="PopupVariantCount"/> 轮把所有手段试一遍）。</summary>
    public const int PopupVariantCount = 6;

    /// <summary>
    /// 弹窗第 <paramref name="attempt"/> 轮该用哪一档：前两轮留给 ① RT ② A（<see cref="StepOf"/>），
    /// 第 3 轮起按 <see cref="PopupVariantCount"/> 档循环，每轮都换一个手段。
    /// </summary>
    public static PopupVariant PopupVariantOf(int attempt)
    {
        int i = Math.Max(0, attempt - MouseFromAttempt);
        return (PopupVariant)(i % PopupVariantCount);
    }

    /// <summary>一次鼠标落点：归一化坐标 + 人话说明（直接进日志，现场一眼看出点的是哪一档）。</summary>
    public readonly record struct ClickPoint(double X, double Y, string Where);

    /// <summary>
    /// 第几轮起允许升级为「前台真实点击」（SendInput 真鼠标）。后台 PostMessage 被游戏吞掉时
    /// （网页层某些页只处理真实输入）这是最后一档——只在已经卡住 ≥<see cref="ForegroundClickEvery"/>×3 秒
    /// 之后才用，且会短暂抢一次前台再还回去，正常页面上永远走不到。
    /// </summary>
    public const int ForegroundClickFromAttempt = 8;

    /// <summary>升级到前台真实点击之后，每隔这么多轮做一次（其余轮仍走后台，不每次都抢前台）。</summary>
    public const int ForegroundClickEvery = 6;

    /// <summary>这一轮该走后台 PostMessage 还是前台真实点击。</summary>
    public static bool UseForegroundClick(int attempt)
        => attempt >= ForegroundClickFromAttempt &&
           (attempt - ForegroundClickFromAttempt) % ForegroundClickEvery == 0;

    /// <summary>
    /// 判定这一轮鼠标该点哪里（纯函数，便于单测）。档位从准到糙：
    /// <list type="number">
    /// <item><see cref="FindButton"/> 的按钮锚点（OCR 下半屏推进词 / 右下 btn_confirm 检测框）；</item>
    /// <item>页面自己给的兜底坐标（起始页、难度页这类已知版面）；</item>
    /// <item><b>白卡推进按钮</b>（<see cref="ResultCardProbe"/>：赛后「球员评价」页那块箭头大卡，OCR 字 → 像素探针 → 标定落点）；</item>
    /// <item>国服通用右下坐标（最后手段）。</item>
    /// </list>
    /// 为什么白卡排在页面兜底之后：起始页/难度页的兜底点是按版面标定的、比通用规则更准；
    /// 而赛后那一页的调用方给的是 null，正好落到白卡这一档。旧版没有这一档，
    /// 于是白卡页每一轮都点在右下角的「RS ● ● ●」翻页指示上，150+ 轮原地不动
    /// （用户 2026-11-25 23:52 实机，日志「全场结束→下一步：第150轮·鼠标」）。
    /// </summary>
    /// <param name="ocr">当前帧 OCR。</param>
    /// <param name="dets">当前帧 YOLO 检测框（可为 null）。</param>
    /// <param name="fallback">调用方给的版面兜底落点（可为 null）。</param>
    /// <param name="attempt">同一页面上的第几轮（1 起；白卡标定落点按它轮换）。</param>
    /// <param name="frame">当前帧像素（可为 null：只走 OCR / 标定落点）。</param>
    /// <param name="words">该页的推进词（null/空 = <see cref="AdvanceWords"/> ∪ <see cref="EnterWords"/>）。</param>
    public static ClickPoint LocateClickPoint(OcrResult ocr, IReadOnlyList<YoloDetection>? dets,
        (double X, double Y)? fallback, int attempt, FrameData? frame, IReadOnlyList<string>? words = null)
    {
        string[] keys = words is { Count: > 0 }
            ? words.ToArray()
            : AdvanceWords.Concat(EnterWords).Distinct().ToArray();
        if (FindButton(ocr, dets, keys) is { } hit) return new(hit.X, hit.Y, "按钮位置");
        if (fallback is { } fb) return new(fb.X, fb.Y, "兜底坐标");
        if (ResultCardProbe.Locate(ocr, frame, attempt) is { } card) return new(card.X, card.Y, card.Where);
        return new(DefaultX, DefaultY, "国服右下兜底坐标");
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
    /// <param name="fallback">OCR/检测都定位不到按钮时的兜底落点；null = 走白卡探针 / 国服通用右下落点。</param>
    /// <param name="frame">当前帧像素（白卡像素探针用；null = 只靠 OCR 与标定落点）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="words">该页的推进词（空 = <see cref="AdvanceWords"/>）。</param>
    public static async Task<int> AdvanceAsync(BotEngine engine, BackgroundMouse mouse, int attempt,
        string what, string module, Action<string, LogLevel> log, OcrResult ocr,
        IReadOnlyList<YoloDetection>? dets, (double X, double Y)? fallback, FrameData? frame,
        CancellationToken ct, params string[] words)
    {
        switch (StepOf(attempt))
        {
            case Step.Rt:
                log($"[{module}] {what}：第{attempt}轮·RT", LogLevel.Info);
                await engine.Pad.RT();
                return 2800;
            case Step.Confirm:
                // 用户 2026-11-25 实机：终场「球员评价」等页 RT 按了不动，必须补一次手柄 A
                log($"[{module}] {what}：第{attempt}轮·A（RT 没推进，补手柄 A）", LogLevel.Info);
                await engine.Pad.Confirm();
                return 2500;
        }
        var point = LocateClickPoint(ocr, dets, fallback, attempt, frame, words);
        bool foreground = UseForegroundClick(attempt);
        log($"[{module}] {what}：第{attempt}轮·鼠标({point.Where} {point.X:F3},{point.Y:F3})" +
            (foreground ? "——后台点不动，改前台真实点击（会短暂切到游戏窗口）" : "——需以管理员运行"),
            LogLevel.Warn);
        await ClickAsync(engine, mouse, point.X, point.Y, module, log, ct,
            foreground ? MouseMode.Foreground : MouseMode.Background);
        return 3000;
    }

    /// <summary>鼠标点击方式：后台 PostMessage（默认、不抢前台）/ 前台 SendInput 真鼠标（后台被吞时的最后一档）。</summary>
    public enum MouseMode
    {
        Background,
        Foreground,
    }

    /// <summary>鼠标点击一次（归一化坐标）。窗口句柄缺失时退回按 A，绝不让模块卡在这条路径上。</summary>
    /// <param name="engine">引擎（取窗口句柄与 Pad）。</param>
    /// <param name="mouse">后台鼠标实例（模块持有，保证轨迹连续）。</param>
    /// <param name="nx">归一化 x（0~1）。</param>
    /// <param name="ny">归一化 y（0~1）。</param>
    /// <param name="module">日志前缀里的模块名。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="mode">后台消息 / 前台真实点击。</param>
    public static async Task ClickAsync(BotEngine engine, BackgroundMouse mouse, double nx, double ny,
        string module, Action<string, LogLevel> log, CancellationToken ct,
        MouseMode mode = MouseMode.Background)
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
        if (mode == MouseMode.Background)
        {
            await mouse.ClickNormalizedAsync(win.Handle, nx, ny, w, h, downHoldMs: 70, ct: ct);
            return;
        }
        // 前台真实点击：置前 → 真实光标 + SendInput → 等游戏处理完 → 把前台还给用户原来那个窗口。
        // 为什么需要（用户 2026-11-25 第二次卡在同一页）：网页层个别页只认真实输入，
        // PostMessage 投进去没人处理；此时后台连点一万轮也是原地不动。
        int px = Math.Clamp((int)Math.Round(nx * w), 0, Math.Max(0, w - 1));
        int py = Math.Clamp((int)Math.Round(ny * h), 0, Math.Max(0, h - 1));
        var fg = new ForegroundMouse();
        IntPtr previous = fg.BringToFront(win.Handle);
        try
        {
            await Task.Delay(180, ct); // 等窗口真的拿到前台再点，否则点击落到别的窗口上
            await fg.ClickClientAsync(win.Handle, px, py, downHoldMs: 70, ct: ct);
            await Task.Delay(320, ct); // 等游戏把这次点击处理完再交还前台
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != win.Handle) fg.RestoreFront(previous);
        }
        log($"[{module}] 前台真实点击已完成：({nx:F3},{ny:F3})，前台已交还", LogLevel.Warn);
    }
}
