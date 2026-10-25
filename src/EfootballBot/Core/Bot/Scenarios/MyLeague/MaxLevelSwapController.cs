using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using System.IO; // Core.System 命名空间遮蔽：裸 Path/File/Directory 必须靠本 using（同 MyLeagueScenario）

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>状态机对手柄的最小依赖（测试用录制实现替换；生产转发引擎手柄）。</summary>
internal interface ISwapPad
{
    Task Y();
    Task X();
    Task Confirm();
    Task Back();
    Task Dpad(PadDir dir);
    Task DpadRepeat(PadDir dir, int count);
}

/// <summary><see cref="ISwapPad"/> 的生产实现：转发到引擎手柄。批量移光标走快速连按
/// （保持/间隔减半、跳过拟人犹豫——机械导航不是拟人语义），单键仍走完整节奏。</summary>
internal sealed class EngineSwapPad(Pad pad) : ISwapPad
{
    public Task Y() => pad.Y();
    public Task X() => pad.X();
    public Task Confirm() => pad.Confirm();
    public Task Back() => pad.Back();
    // 单次移光标同样走快速连按通道（机械导航不是拟人语义，用户 2026-10-25：方向移动太拖沓）
    public Task Dpad(PadDir dir) => pad.DpadRepeatFast(dir, 1);
    public Task DpadRepeat(PadDir dir, int count) => pad.DpadRepeatFast(dir, count);
}

/// <summary>
/// 满级自动换人状态机（跨帧，「一帧一步、屏幕验证后才按下一键」）：
/// 联赛主页 Y → 比赛计划（首发站位页，光标默认门将）→ 沿
/// ↑ ← ↑ → → → → → ↓ ← 巡检 11 人，每站 Y 进详情 → X 球员动作 →
/// ★满级判定不用 OCR 数字，用弹窗首项：白字「等级训练完成」=满级（灰字「等级训练」=未满级，
/// 用户 2026-10-25 实机对比图）——大字「7」被读成 17、橙色圈「2」被读成 21 等误读全部规避。
/// 满级：↓+A 能力点分配 → Y 推荐分配一次归零 → B → 详情 B → 站位页 A 选中 → ←×8 进侧栏
/// → 上×5 下×3 落「后备球员」→ A → ←+A 开排序 → 上×6 下×5 落「当前等级」→ 右上箭头
/// 为下则 Y 改升序 → A；回列表 ← ↓ A 开筛选 → ↓×4 A 进卡片类型 → 上×9 ↓↓ + 6×(A ↓)
/// 勾高阶卡 → B；↓×4 ↓ A 应用筛选（★排序+筛选本轮挂机只做一次，之后换人直奔选卡）→
/// 在后备列表选第一张未压暗的卡按 A 换入（当前等级最低；用户 2026-10-25：选到亮卡
/// A 一定成功，★不做任何被挡判定）。A 后 B 返回站位页（侧栏光标停在「后备球员」、
/// 右侧预览后备名单——这个页面同屏有侧栏词，按站位页判）+ → 进阵（光标回到刚换人的
/// 球员）→ 继续巡检下一位，不退出比赛计划。11 人完成 B（完成）→ 确认窗 上移+A → 主页。
/// 非满级：B 关弹窗 → B 退出详情，直接下一位。11 人完成交还正常流程。
/// </summary>
public sealed class MaxLevelSwapController
{
    private enum Phase
    {
        Idle,
        /// <summary>已按 Y，等待进入比赛计划（站位页）。</summary>
        Opening,
        /// <summary>站位页：光标移动到当前巡检位 + Y 进详情。</summary>
        Inspect,
        /// <summary>详情页：X 球员动作（弹窗「等级训练完成」判满级，不读任何数字）。</summary>
        DetailRead,
        /// <summary>详情页 B 后：确认回站位页并推进到下一位。</summary>
        InspectAdvance,
        /// <summary>球员动作弹窗：白字「等级训练完成」=满级→↓+A；灰字「等级训练」=未满级→B。</summary>
        ActionPopup,
        /// <summary>分配页：Y 推荐分配（只按一次即归零）；弹窗滞留=无能力点→B 直接换人。</summary>
        AllocWait,
        /// <summary>分配完成：B 关闭分配页（不读数不复按）→ 详情页。</summary>
        AllocDone,
        /// <summary>回详情页确认能力点 = 0 → B。</summary>
        DetailVerify,
        /// <summary>站位页：A 选中该球员。</summary>
        LineupSelect,
        /// <summary>←×8 进侧栏 → 上×5 下×3 落「后备球员」→ A。</summary>
        SidebarNav,
        /// <summary>后备列表：←+A 开排序弹窗。</summary>
        BenchWait,
        /// <summary>排序弹窗：上×6 下×5 落「当前等级」+ 箭头核验 + A。</summary>
        SortWait,
        /// <summary>排序已应用：回列表 ← ↓ A 开筛选。</summary>
        SortApplied,
        /// <summary>筛选页：↓×4 落「卡片类型」→ A。</summary>
        FilterWait,
        /// <summary>卡片类型子页：固定勾选序列 → B。</summary>
        CardTypeWait,
        /// <summary>回筛选页：↓×4 落比赛风格 ↓ 落完成 → A 应用。</summary>
        FilterApply,
        /// <summary>筛选后的列表：跳过头像压暗的卡（同名在名单），A 换入第一张亮卡（一定成功）。</summary>
        BenchSwap,
        /// <summary>A 后直接退出：反复 B 完成（动画吞输入）→ 确认窗上移+A、错误弹窗 A → 主页。</summary>
        SwapExit,
        /// <summary>主页 Y 重进比赛计划（光标回门将）。</summary>
        Reopen,
        /// <summary>盲走光标回刚换人的位置（不带等级识别）。</summary>
        Rewind,
        /// <summary>巡检完成：B 返回主页后收尾。</summary>
        Done,
    }

    /// <summary>光标巡检路径：位置 0 = 门将（进页默认位）；moves[i] = 从位置 i-1 到位置 i 的移动（共 11 位）。</summary>
    internal static readonly PadDir[] CursorMoves =
        { PadDir.Up, PadDir.Left, PadDir.Up, PadDir.Right, PadDir.Right,
          PadDir.Right, PadDir.Right, PadDir.Right, PadDir.Down, PadDir.Left };

    /// <summary>整轮巡检轮数保险丝（含等待重试；约 700 轮 × ~1.2s ≈ 14 分钟上限）。</summary>
    internal const int MaxRounds = 700;

    private Phase _phase = Phase.Idle;
    /// <summary>当前巡检位（0=门将 … 10=最后一位）。</summary>
    private int _pos;
    /// <summary>刚完成换人的位置（重进后盲走回这里）。</summary>
    private int _swapPos;
    private int _stall;
    private int _readTries;
    private int _escapes;
    private int _rounds;
    private int _swapped;
    private int _maxed;
    /// <summary>本次排序弹窗已按过一次 Y 翻方向（至多一次，防探针误判来回翻死循环）。</summary>
    private bool _sortFlipDone;
    /// <summary>★本轮挂机已完成过一次排序+筛选（用户 2026-10-25：之后换人不再重复进出
    /// 排序/筛选页，直奔选卡）。挂机重启（场景重建）自然回归 false；不随 <see cref="Reset"/> 清零。</summary>
    private bool _sortFilterDone;
    /// <summary>当前位详情页读到的「有剩余能力点」（橙圈徽标像素判定）。</summary>
    private bool _hasPoints;
    /// <summary>SwapExit 内的观察轮计数（进相位置 0）。</summary>
    private int _verifyRounds;
    /// <summary>Rewind 是否需要从门将盲走到换人位（意外退出重进时 true；换人后 B→→ 进阵时为 false）。</summary>
    private bool _rewindWalk;
    /// <summary>Rewind 已把光标走到本巡检位，Inspect 不再补移动（只按 Y）。</summary>
    private bool _skipInspectMove;
    /// <summary>退出驱动的上一次逃生键是否为 A（A/B 交替，防止卡死在确认窗上）。</summary>
    private bool _exitUsedA;
    private bool _errorDumped;

    /// <summary>巡检是否空闲（空闲时场景层才允许触发新的巡检）。</summary>
    public bool Idle => _phase == Phase.Idle;

    public void Reset()
    {
        _phase = Phase.Idle;
        _pos = _swapPos = _stall = _readTries = _escapes = _rounds = _swapped = _maxed = 0;
        _sortFlipDone = false;
        _verifyRounds = 0;
        _rewindWalk = false;
        _skipInspectMove = false;
        _hasPoints = false;
        _exitUsedA = false;
        _errorDumped = false;
        // _sortFilterDone 有意保留：挂机会话级状态
    }

    /// <summary>在联赛主页（无弹窗）触发：按 Y 打开比赛计划。返回下一轮延时。</summary>
    public Task<int> StartAsync(BotEngine engine, Action<string, LogLevel> log, CancellationToken ct = default)
        => StartCore(new EngineSwapPad(engine.Pad), log);

    /// <summary>核心入口（手柄参数化，可单测）。</summary>
    internal async Task<int> StartCore(ISwapPad pad, Action<string, LogLevel> log)
    {
        Reset();
        _phase = Phase.Opening;
        log("[我的联赛] 满级巡检开始：按 Y 打开比赛计划（光标默认门将）", LogLevel.Info);
        await pad.Y();
        return 1300;
    }

    /// <summary>已在比赛计划（首发站位页）触发：跳过按 Y，直接从门将开始巡检。</summary>
    public Task<int> StartOnPlanAsync(BotEngine engine, Action<string, LogLevel> log,
        CancellationToken ct = default)
        => StartOnPlanCore(new EngineSwapPad(engine.Pad), log);

    /// <summary>核心入口（手柄参数化，可单测）。</summary>
    internal Task<int> StartOnPlanCore(ISwapPad pad, Action<string, LogLevel> log)
    {
        Reset();
        _phase = Phase.Inspect;
        _pos = 0;
        log("[我的联赛] 满级巡检开始：已在比赛计划内，直接从门将巡检", LogLevel.Info);
        return Task.FromResult(1000);
    }

    /// <summary>每轮观察后调用；巡检未激活返回 null（交还场景正常流程）。</summary>
    public Task<int?> StepAsync(BotEngine engine, Observation obs, Action<string, LogLevel> log,
        CancellationToken ct = default)
        => StepCore(obs.Ocr, obs.Frame, new EngineSwapPad(engine.Pad),
            (o, f) => ReadStatusAsync(engine, o, f, ct), log,
            onMaxed: () => engine.Stats.PlayersMaxed++,
            dumpFrame: (f, tag) => DumpFrame(f, tag, log), ct);

    /// <summary>读不出的详情页帧落 data\dump\（远程排障：OCR 几何与截图差异只有真帧能定位）。</summary>
    private static void DumpFrame(Capture.FrameData f, string tag, Action<string, LogLevel> log)
    {
        try
        {
            var pixels = (byte[])f.Bgra.Clone();
            string dir = Path.Combine(EfootballBot.Core.System.AppPaths.DataDir, "dump");
            string file = Path.Combine(dir, $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var info = new SkiaSharp.SKImageInfo(f.Width, f.Height,
                        SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                    unsafe
                    {
                        fixed (byte* p = pixels)
                        {
                            using var bmp = new SkiaSharp.SKBitmap();
                            if (!bmp.InstallPixels(info, (nint)p, info.RowBytes)) return;
                            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                            using var fs = File.OpenWrite(file);
                            data.SaveTo(fs);
                        }
                    }
                    log($"[我的联赛] 已存实机帧：{file}", LogLevel.Info);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>
    /// 等级/能力点读取：全帧 OCR 分要素直读；等级缺时走「多配置裁切 × 词级候选投票」
    /// （实机 20261025_11 四连读不出 + split 帧标定：大字细笔画「1」没有任何单一配置稳定——
    /// 紧窗原始读 L/J、紧窗二值读 1 偶尔 L、宽窗原始读 1 但混入标签碎片 5、还有帧连 /29 都丢；
    /// 跨配置投票必然收敛：L/J/I 归一为 1、':' 当 '/'、平票 cur 取小 max 取大）。
    /// 全配置 max 仍读不出但 cur ≤ 23（等级上限实证 24~37）→ 必未满级，返回
    /// <see cref="PlayerStatus.LevelMax"/>=0 哨兵由状态机按未满级放行。能力点列 x 0.575~0.72。
    /// </summary>
    internal static Task<PlayerStatus?> ReadStatusAsync(BotEngine engine, OcrResult ocr,
        FrameData frame, CancellationToken ct)
        => ReadPartsFromFrameAsync(engine.Ocr, ocr, frame, ct);

    /// <summary>核心读取（OCR 引擎参数化，可单测；实机落帧 swap_read_fail 做回归）。</summary>
    internal static async Task<PlayerStatus?> ReadPartsFromFrameAsync(OcrRecognizer ocrEngine,
        OcrResult ocr, FrameData frame, CancellationToken ct)
    {
        var direct = MaxLevelSwapBrain.ReadParts(ocr);
        int? cur = direct.Pair?.Cur, max = direct.Pair?.Max, pts = direct.Points;
        try
        {
            if (cur is null || max is null)
            {
                // 直接帧候选只取等级列值区词（全页数字会污染票仓），裁切窗口小、全词可入
                var candidates = MaxLevelSwapBrain
                    .LevelCandidatesFromWords(MaxLevelSwapBrain.LevelValueWords(ocr)).ToList();
                foreach (var (x0, x1, y0, y1, binarize) in new[]
                     { (0.46, 0.575, 0.425, 0.505, false), (0.46, 0.575, 0.425, 0.505, true),
                       (0.42, 0.585, 0.36, 0.52, false), (0.42, 0.585, 0.36, 0.52, true) })
                {
                    var lvCrop = ImageScaling.CropUpscale(frame, x0, x1, y0, y1, 8);
                    if (binarize) lvCrop = Binarize(lvCrop);
                    var lvRes = await ocrEngine.RecognizeAsync(lvCrop, ct);
                    candidates.AddRange(MaxLevelSwapBrain.LevelCandidatesFromWords(lvRes.Words));
                }
                var voted = MaxLevelSwapBrain.VoteLevel(candidates);
                cur ??= voted?.Cur;
                max ??= voted?.Max;
            }
            if (pts is null)
            {
                var ptCrop = ImageScaling.CropUpscale(frame, 0.575, 0.72, 0.36, 0.52, 8);
                var ptRes = await ocrEngine.RecognizeAsync(ptCrop, ct);
                var ptText = string.Concat(ptRes.Words.OrderBy(w => w.CenterX).Select(w => w.Text));
                pts = MaxLevelSwapBrain.ParseSingleNumber(ptText.Replace(" ", ""));
            }
        }
        catch
        {
            // 裁切失败按读不出处理（上层重试/落帧）
        }
        if (cur is null || pts is null) return null;
        // max 读不出：低等级必未满级（等级上限实证 24~37），哨兵 0 放行；高等级保守 null 走重试
        if (max is null && cur.Value > 23) return null;
        return new PlayerStatus(cur.Value, max ?? 0, pts.Value);
    }

    /// <summary>裁切帧二值化（白字暗底 luma≥150 → 纯白，其余纯黑）：细笔画数字在
    /// 灰阶过渡里会被读成 L/J/7，二值化后轮廓干净（实机 20261025_11 标定）。</summary>
    private static FrameData Binarize(FrameData f)
    {
        var px = (byte[])f.Bgra.Clone();
        for (int i = 0; i < px.Length; i += 4)
        {
            int lum = (px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) / 1000;
            byte v = lum >= 150 ? byte.MaxValue : byte.MinValue;
            px[i] = px[i + 1] = px[i + 2] = v;
        }
        return new FrameData(px, f.Width, f.Height, 0);
    }

    /// <summary>核心状态机（OCR/帧/手柄/读取器全参数化，可单测）。返回本轮延时；null = 巡检结束。
    /// onMaxed：每检出一个满级球员回调（GUI 统计录入）；dumpFrame：读不出时落实机帧。</summary>
    internal async Task<int?> StepCore(OcrResult ocr, FrameData frame, ISwapPad pad,
        Func<OcrResult, FrameData, Task<PlayerStatus?>> readStatus,
        Action<string, LogLevel> log,
        Action? onMaxed = null, Action<FrameData, string>? dumpFrame = null,
        CancellationToken ct = default)
    {
        if (_phase == Phase.Idle) return null;
        if (++_rounds > MaxRounds)
            return await AbortAsync("巡检轮数超限", pad, log);

        var screen = MaxLevelSwapBrain.Classify(ocr);
        bool home = ocr.ContainsJoined("前往比赛");

        // 游戏错误弹窗（功能目前无法使用 代码:C_XIQX_0019，实机 2026-10-25 换人流程
        // 被同名卡打乱后出现）：B 按不掉，只能 A 点 OK——任何阶段出现都先关掉再继续。
        if (MaxLevelSwapBrain.IsGameErrorDialog(ocr))
        {
            if (!_errorDumped)
            {
                _errorDumped = true;
                dumpFrame?.Invoke(frame, "swap_error_dialog");
            }
            log("[我的联赛] 游戏错误弹窗（功能暂时不可用）：按 A 关闭后继续", LogLevel.Error);
            await pad.Confirm();
            return 1200;
        }

        switch (_phase)
        {
            case Phase.Opening:
            {
                if (screen == SwapScreen.Lineup)
                {
                    _stall = 0;
                    _pos = 0;
                    _phase = Phase.Inspect;
                    log("[我的联赛] 已进入比赛计划，从门将开始巡检", LogLevel.Info);
                    return 600;
                }
                // Y 未生效（弹窗/加载）：重按，最多 3 次
                if (++_stall <= 3)
                {
                    await pad.Y();
                    return 1200;
                }
                return await AbortAsync("打开比赛计划失败", pad, log);
            }

            case Phase.Inspect:
            {
                if (screen == SwapScreen.Detail)
                {
                    _stall = 0;
                    _phase = Phase.DetailRead; // 上一轮 Y 已生效
                    return 300;
                }
                if (screen != SwapScreen.Lineup)
                    return await StallAsync($"站位页异常（{screen}）", screen, pad, log);
                _stall = 0;
                if (_skipInspectMove)
                    _skipInspectMove = false; // Rewind 已把光标走到本巡检位
                else if (_pos > 0)
                    await pad.Dpad(CursorMoves[_pos - 1]);
                await pad.Y();
                log($"[我的联赛] 巡检第 {_pos + 1}/11 位：打开球员详情", LogLevel.Info);
                _phase = Phase.DetailRead;
                return 1300;
            }

            case Phase.DetailRead:
            {
                if (screen == SwapScreen.Detail)
                {
                    _stall = 0;
                    // ★先判能力点（用户 2026-10-25 四路矩阵）：橙圈徽标=有剩余能力点需加点，
                    // 白字 0=无需加点。再 X 球员动作，弹窗首项「等级训练完成/等级训练」判满级——
                    // 不 OCR 任何等级数字（大字「7」被读成 17、橙圈「2」被读成 21 的实机误读）
                    _hasPoints = MaxLevelSwapBrain.HasAbilityPointBadge(frame);
                    await pad.X();
                    _phase = Phase.ActionPopup;
                    return 1100;
                }
                if (screen == SwapScreen.ActionPopup)
                {
                    _phase = Phase.ActionPopup; // X 已生效
                    return 300;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Y(); // Y 未生效，重按
                    return 1100;
                }
                return await StallAsync("详情页未打开", screen, pad, log);
            }

            case Phase.InspectAdvance:
            {
                if (screen == SwapScreen.Lineup)
                {
                    _stall = 0;
                    if (_pos >= CursorMoves.Length)
                    {
                        log($"[我的联赛] 首发巡检完成：满级 {_maxed} 名，换人 {_swapped} 名", LogLevel.Success);
                        _phase = Phase.Done;
                        await pad.Back();
                        return 1200;
                    }
                    _pos++;
                    _phase = Phase.Inspect;
                    return 500;
                }
                if (screen == SwapScreen.Detail)
                {
                    await pad.Back(); // B 未生效再按
                    return 1000;
                }
                return await StallAsync("详情退出异常", screen, pad, log);
            }

            case Phase.ActionPopup:
            {
                if (screen == SwapScreen.ActionPopup)
                {
                    _stall = 0;
                    if (!ocr.ContainsJoined("等级训练完成"))
                    {
                        // 未满级（弹窗首项灰字「等级训练」）→ 不用加点，下一位（用户 2026-10-25 矩阵 1/3 行）
                        log($"[我的联赛] 第 {_pos + 1}/11 位未满级（弹窗「等级训练」无「完成」）：下一位", LogLevel.Info);
                        await pad.Back();
                        _phase = Phase.InspectAdvance;
                        return 1100;
                    }
                    _maxed++;
                    onMaxed?.Invoke();
                    _swapPos = _pos;
                    if (!_hasPoints)
                    {
                        // 满级 + 能力点 0 → 不用加点，B 关弹窗直接走选人换人（矩阵 2 行）
                        log($"[我的联赛] 第 {_pos + 1}/11 位球员满级（弹窗「等级训练完成」，能力点 0）：直接换人", LogLevel.Success);
                        await pad.Back();
                        _phase = Phase.LineupSelect;
                        return 1100;
                    }
                    // 满级 + 有剩余能力点 → 先 ↓+A 分配，完成后换人（矩阵 4 行）
                    log($"[我的联赛] 第 {_pos + 1}/11 位球员满级（弹窗「等级训练完成」，有剩余能力点）：分配能力点并换人", LogLevel.Success);
                    await pad.Dpad(PadDir.Down);  // 等级训练完成 → 能力点分配
                    await pad.Confirm();
                    _phase = Phase.AllocWait;
                    return 1400;
                }
                if (screen == SwapScreen.Detail && ++_stall <= 2)
                {
                    await pad.X(); // X 未生效
                    return 1100;
                }
                return await StallAsync("球员动作弹窗未出现", screen, pad, log);
            }

            case Phase.AllocWait:
            {
                if (screen == SwapScreen.Alloc)
                {
                    _stall = 0;
                    // 推荐分配一次即归零（用户 2026-10-24 实机：按 Y 后「已自动分配能力点」，
                    // 下一步就是 B 返回——不读数不复按，OCR 把 0/86 丢斜杠读成 086 会误判
                    // 「还有剩余」无限按 Y，2026-10-24 实机死循环）
                    await pad.Y();
                    _phase = Phase.AllocDone;
                    return 1300;
                }
                if (screen == SwapScreen.ActionPopup && ++_stall <= 2)
                {
                    await pad.Confirm(); // A 未生效
                    return 1100;
                }
                if (screen == SwapScreen.ActionPopup)
                {
                    // ↓+A 后弹窗仍在：满级但无剩余能力点，「能力点分配」不可用——
                    // B 关弹窗直接走换人（LineupSelect 会补一次 B 退详情）
                    _stall = 0;
                    log("[我的联赛] 能力点分配项不可用（无剩余能力点）：B 关弹窗直接换人", LogLevel.Info);
                    await pad.Back();
                    _phase = Phase.LineupSelect;
                    return 1100;
                }
                return await StallAsync("能力点分配页未打开", screen, pad, log);
            }

            case Phase.AllocDone:
            {
                if (screen == SwapScreen.Alloc)
                {
                    _stall = 0;
                    await pad.Back(); // B 关闭分配页 → 详情页
                    _phase = Phase.DetailVerify;
                    return 1100;
                }
                if (screen == SwapScreen.Detail)
                {
                    _phase = Phase.DetailVerify; // 已自动关闭
                    return 300;
                }
                return await StallAsync("分配页关闭异常", screen, pad, log);
            }

            case Phase.DetailVerify:
            {
                if (screen == SwapScreen.Detail)
                {
                    _stall = 0;
                    await pad.Back(); // B 退出详情 → 站位页选中该球员
                    _phase = Phase.LineupSelect;
                    return 1100;
                }
                if (screen == SwapScreen.Alloc)
                {
                    await pad.Back();
                    return 1000;
                }
                return await StallAsync("分配后详情页异常", screen, pad, log);
            }

            case Phase.LineupSelect:
            {
                if (screen == SwapScreen.Lineup)
                {
                    _stall = 0;
                    await pad.Confirm(); // A 选中该球员
                    _phase = Phase.SidebarNav;
                    return 1300;
                }
                if (screen == SwapScreen.Detail)
                {
                    await pad.Back();
                    return 1000;
                }
                return await StallAsync("选中球员异常", screen, pad, log);
            }

            case Phase.SidebarNav:
            {
                if (screen != SwapScreen.Lineup)
                    return await StallAsync("侧栏导航异常", screen, pad, log);
                _stall = 0;
                await pad.DpadRepeat(PadDir.Left, 8);  // 饱和进侧栏
                await pad.DpadRepeat(PadDir.Up, 5);    // 饱和到「战术」
                await pad.DpadRepeat(PadDir.Down, 3);  // 战术→球队→替补球员→后备球员
                await pad.Confirm();
                _phase = Phase.BenchWait;
                return 1600;
            }

            case Phase.BenchWait:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    if (_sortFilterDone)
                    {
                        // 本轮挂机已设置过排序+筛选（设置会持久）：不再重复进出两个页面，直奔选卡
                        log("[我的联赛] 排序/筛选本轮已设置：直接选卡换人", LogLevel.Info);
                        _phase = Phase.BenchSwap;
                        return 300;
                    }
                    await pad.Dpad(PadDir.Left);  // 列表 → 侧栏排序图标
                    await pad.Confirm();
                    _phase = Phase.SortWait;
                    return 1100;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Confirm(); // 侧栏 A 未生效
                    return 1100;
                }
                return await StallAsync("后备球员页未打开", screen, pad, log);
            }

            case Phase.SortWait:
            {
                if (screen == SwapScreen.Sort)
                {
                    _stall = 0;
                    // 先识别是否已设置（中断续跑/重复进入）：当前等级已勾 + 箭头为上 = 正确，
                    // 直接 B 返回走筛选。★按 Y 翻方向至多一次：实机探针在运行帧上可能把 ↑ 误判
                    // 为 ↓（用户 2026-10-24：明明是 ↑ 还来回翻）——翻过后下一轮无条件 B，
                    // 方向真错了大不了换上高等级球员，绝不来回翻死循环。
                    if (MaxLevelSwapBrain.IsCurrentLevelChecked(ocr, frame))
                    {
                        if (!_sortFlipDone && MaxLevelSwapBrain.IsSortArrowUp(frame) is false)
                        {
                            _sortFlipDone = true;
                            await pad.Y();
                            log("[我的联赛] 当前等级已勾选、箭头读数为下：按一次 Y 翻方向（下轮直接返回）", LogLevel.Info);
                            return 1100;
                        }
                        log("[我的联赛] 排序键已是「当前等级」：B 返回，继续筛选", LogLevel.Info);
                        await pad.Back();
                        _phase = Phase.SortApplied;
                        return 1100;
                    }
                    await pad.DpadRepeat(PadDir.Up, 6);    // 饱和到「球员姓名」
                    await pad.DpadRepeat(PadDir.Down, 5);  // → 当前等级
                    var up = MaxLevelSwapBrain.IsSortArrowUp(frame);
                    if (up is false)
                    {
                        await pad.Y(); // 降序 → 改升序（列表首位=当前等级最低）
                        log("[我的联赛] 排序箭头为下，按 Y 切为升序", LogLevel.Info);
                    }
                    else if (up is null)
                    {
                        log("[我的联赛] 排序箭头方向读不出，按升序继续", LogLevel.Warn);
                    }
                    await pad.Confirm(); // 应用「当前等级」排序
                    _phase = Phase.SortApplied;
                    return 1500;
                }
                if (screen == SwapScreen.Bench && ++_stall <= 2)
                {
                    await pad.Dpad(PadDir.Left);
                    await pad.Confirm(); // 排序弹窗没开，重试
                    return 1600;
                }
                return await StallAsync("排序弹窗未出现", screen, pad, log);
            }

            case Phase.SortApplied:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    _sortFlipDone = false; // 排序弹窗已关闭，下次换人重新识别
                    await pad.Dpad(PadDir.Left);  // 列表 → 排序图标
                    await pad.Dpad(PadDir.Down);  // → 筛选图标
                    await pad.Confirm();
                    _phase = Phase.FilterWait;
                    return 1000;
                }
                if (screen == SwapScreen.Sort && ++_stall <= 2)
                {
                    // 又见排序弹窗（B 未生效或 A 又开了排序）：按 B 关闭，绝不再 A
                    await pad.Back();
                    return 1100;
                }
                return await StallAsync("排序应用异常", screen, pad, log);
            }

            case Phase.FilterWait:
            {
                if (screen == SwapScreen.Filter)
                {
                    _stall = 0;
                    await pad.DpadRepeat(PadDir.Up, 9);   // 饱和到「俱乐部球队」
                    if (MaxLevelSwapBrain.IsCardTypeFilterSet(ocr))
                    {
                        // 已设置过（中断续跑/游戏本来就设置好）：归顶 ↓×9 直达「完成」应用，
                        // 不进子页重复勾选。★同样置会话标志——否则快速路径下每次换人都
                        // 重新开排序/筛选页（用户 2026-10-25 实机反馈）
                        log("[我的联赛] 卡片类型筛选已设置：直接完成应用", LogLevel.Info);
                        await pad.DpadRepeat(PadDir.Down, 9);
                        await pad.Confirm();
                        _sortFilterDone = true;
                        _phase = Phase.BenchSwap;
                        return 1500;
                    }
                    await pad.DpadRepeat(PadDir.Down, 4); // → 卡片类型
                    await pad.Confirm();
                    _phase = Phase.CardTypeWait;
                    return 1300;
                }
                return await StallAsync("筛选页未打开", screen, pad, log);
            }

            case Phase.CardTypeWait:
            {
                if (screen != SwapScreen.CardType)
                {
                    if (screen == SwapScreen.Filter && ++_stall <= 2)
                    {
                        await pad.Confirm(); // A 未生效
                        return 1100;
                    }
                    return await StallAsync("卡片类型页未打开", screen, pad, log);
                }
                _stall = 0;
                // 固定勾选（进页光标在「普通」）：↓↓ 落「精选」，之后 高光/ShowTime/传奇/史诗/BigTime
                // 逐个 ↓+A 勾选，末尾多余的 ↓ 落「不可用的球员」（不勾）→ B 回筛选页
                await pad.DpadRepeat(PadDir.Up, 9);
                await pad.DpadRepeat(PadDir.Down, 2);
                for (int i = 0; i < 6; i++)
                {
                    await pad.Confirm();
                    await pad.Dpad(PadDir.Down);
                }
                await pad.Back();
                _phase = Phase.FilterApply;
                return 2300;
            }

            case Phase.FilterApply:
            {
                if (screen == SwapScreen.Filter)
                {
                    _stall = 0;
                    // 卡牌子页 B 返回后光标在「卡片类型」：归顶后 ↓×9 直达「完成」（到头多按无害）
                    await pad.DpadRepeat(PadDir.Up, 9);
                    await pad.DpadRepeat(PadDir.Down, 9);
                    await pad.Confirm();                  // 应用筛选
                    _sortFilterDone = true;               // 本轮挂机不再重复排序/筛选
                    _phase = Phase.BenchSwap;
                    return 1100;
                }
                if (screen == SwapScreen.CardType)
                {
                    await pad.Back(); // B 未生效
                    return 1000;
                }
                return await StallAsync("筛选应用异常", screen, pad, log);
            }

            case Phase.BenchSwap:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    // 跳过头像压暗的卡（同名卡在首发/替补名单内，游戏禁止重复登场，用户 2026-10-25：
                    // 第一排左一张、第三排右一张都不可选）。全暗/探针失效时从第一张按 A。
                    // ★选到亮卡按 A 一定成功（用户 2026-10-25 实机结论）：不做任何被挡判定，
                    // A 完直接进退出流程（换人动画期间输入被吞，SwapExit 反复 B 直到「完成」生效）
                    int darkSkip = MaxLevelSwapBrain.FirstBrightCardIndex(frame);
                    if (darkSkip > 0)
                    {
                        log($"[我的联赛] 前 {darkSkip} 张后备卡头像压暗（同名卡在名单内）：→×{darkSkip} 跳到第 {darkSkip + 1} 张", LogLevel.Info);
                        await pad.DpadRepeat(PadDir.Right, darkSkip);
                    }
                    _swapped++;
                    log($"[我的联赛] 换人：第 {darkSkip + 1} 张后备球员（当前等级最低）换入第 {_swapPos + 1} 位，B 完成退出", LogLevel.Success);
                    await pad.Confirm(); // A 交换
                    _phase = Phase.SwapExit;
                    _verifyRounds = 0;
                    _exitUsedA = false;
                    return 1300;
                }
                return await StallAsync("筛选后列表异常", screen, pad, log);
            }

            case Phase.SwapExit:
            {
                _verifyRounds++;
                if (home)
                {
                    // 意外已退出到主页：Y 重进后盲走光标回换人位（Reopen 置 _rewindWalk）
                    _stall = 0;
                    _phase = Phase.Reopen;
                    return 500;
                }
                if (screen == SwapScreen.Lineup)
                {
                    _stall = 0;
                    if (MaxLevelSwapBrain.HasDialogConfirmWords(ocr))
                    {
                        // 确认窗盖在站位页上（兜底）：取消/关闭在下方时先上移再 A
                        if (ocr.ContainsJoined("取消") || ocr.ContainsJoined("关闭"))
                            await pad.Dpad(PadDir.Up);
                        log("[我的联赛] 退出确认弹窗：按 A 确认", LogLevel.Info);
                        await pad.Confirm();
                        _exitUsedA = true;
                        return 1300;
                    }
                    // ★换人后 B 回到的就是这个站位页（侧栏光标停在「后备球员」、右侧预览
                    // 后备名单）：不退出比赛计划，→ 进阵（光标固定在**第 3 位**，用户
                    // 2026-10-25 实机）→ Rewind 按阵型步走到 换人位+1 继续巡检
                    if (_verifyRounds < 2) return 1100; // 换人动画收尾
                    log($"[我的联赛] 已返回站位页：→ 进阵（第 3 位）→ 走到第 {_swapPos + 2}/11 位继续巡检", LogLevel.Info);
                    await pad.Dpad(PadDir.Right);
                    _rewindWalk = false;
                    _phase = Phase.Rewind;
                    _verifyRounds = 0;
                    return 1000;
                }
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    if (_verifyRounds >= 2)
                        await pad.Back(); // 卡格子页：B 返回站位页（动画期间输入被吞，反复按）
                    return 1200;
                }
                if (screen == SwapScreen.None)
                {
                    if (_verifyRounds <= 3) return 1100; // 换人过场/加载
                    _stall = 0;
                    if (_exitUsedA)
                    {
                        await pad.Back();
                        _exitUsedA = false;
                    }
                    else
                    {
                        await pad.Confirm(); // 确认窗（无取消词的单按钮弹窗）直接 A
                        _exitUsedA = true;
                    }
                    return 1300;
                }
                return await StallAsync("换人后返回异常", screen, pad, log);
            }

            case Phase.Reopen:
            {
                if (home)
                {
                    _stall = 0;
                    await pad.Y(); // 重进比赛计划，光标回门将
                    _rewindWalk = true; // 从主页重进：光标在门将，Rewind 需盲走回换人位
                    _phase = Phase.Rewind;
                    return 1300;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Back(); // B 未生效
                    return 1100;
                }
                return await StallAsync("回主页异常", screen, pad, log);
            }

            case Phase.Rewind:
            {
                if (screen != SwapScreen.Lineup)
                    return await StallAsync("重进比赛计划异常", screen, pad, log);
                _stall = 0;
                // 目标 = 换人位 + 1（换入的新球员不再识别）。
                // 正常路径（换人后 B→→ 进阵）：光标固定落在第 3 位（用户 2026-10-25 实机），
                //   按阵型步走到目标位——第 5 个换人→第 6 位 = 从 3 起 ↑、→、→（用户示例）；
                //   换的是第 1 个→第 2 位 = 从 3 起 →（用户示例）。
                // 兜底路径（意外退回主页→Y 重进）：光标回门将，盲走 CursorMoves[0..换人位-1]，
                //   Inspect 补最后一步到目标位。
                int target = _swapPos + 1;
                if (_rewindWalk)
                {
                    for (int i = 0; i < _swapPos; i++)
                        await pad.Dpad(CursorMoves[i]);
                    _skipInspectMove = false;
                }
                else
                {
                    if (target == 1)
                        await pad.Dpad(PadDir.Right);              // 2→1（边界反走）
                    else
                        for (int d = 2; d < target; d++)
                            await pad.Dpad(CursorMoves[d]);        // 2→target
                    _skipInspectMove = true;
                }
                _pos = target;
                _phase = _pos >= CursorMoves.Length ? Phase.Done : Phase.Inspect;
                if (_phase == Phase.Done)
                {
                    log($"[我的联赛] 首发巡检完成：满级 {_maxed} 名，换人 {_swapped} 名", LogLevel.Success);
                    await pad.Back();
                    return 1200;
                }
                log($"[我的联赛] 已走到第 {_pos + 1}/11 位，继续巡检", LogLevel.Info);
                return _rewindWalk ? 700 + 110 * _swapPos : 600;
            }

            case Phase.Done:
            {
                if (home)
                {
                    log("[我的联赛] 满级巡检结束，交还正常流程", LogLevel.Info);
                    Reset();
                    return null;
                }
                if (screen is SwapScreen.Lineup or SwapScreen.Bench)
                {
                    _stall = 0;
                    if (MaxLevelSwapBrain.HasDialogConfirmWords(ocr))
                    {
                        // 巡检结束时阵容已变更，B 完成会弹确认窗：取消/关闭在下方时先上移再 A
                        if (ocr.ContainsJoined("取消") || ocr.ContainsJoined("关闭"))
                            await pad.Dpad(PadDir.Up);
                        log("[我的联赛] 退出确认弹窗：按 A 确认", LogLevel.Info);
                        await pad.Confirm();
                        return 1300;
                    }
                    await pad.Back(); // 逐级 B 退出到主页
                    return 1100;
                }
                if (++_stall > 6)
                {
                    log("[我的联赛] 满级巡检收尾超时，放弃等待", LogLevel.Warn);
                    Reset();
                    return null;
                }
                if (screen == SwapScreen.None && _stall <= 2)
                    return 1300; // 过场/确认窗未渲染出页面词
                if (screen == SwapScreen.None)
                {
                    // 确认窗（无页面词可判）：取消/关闭在下方时先上移再 A
                    if (ocr.ContainsJoined("取消") || ocr.ContainsJoined("关闭"))
                        await pad.Dpad(PadDir.Up);
                    await pad.Confirm();
                    return 1200;
                }
                await pad.Back(); // 其他意外页面：B 退出
                return 1100;
            }

            default:
                Reset();
                return null;
        }
    }

    /// <summary>阶段内画面不符合预期：连续 3 轮按 B 逃生一次；逃生 3 次整场放弃。</summary>
    private async Task<int?> StallAsync(string what, SwapScreen screen, ISwapPad pad, Action<string, LogLevel> log)
    {
        if (++_stall <= 3) return 1100; // 先观察（页面切换/OCR 抖动）
        _stall = 0;
        if (++_escapes <= 3)
        {
            log($"[我的联赛] 满级巡检卡住（{what}，画面 {screen}）：按 B 逃生（第 {_escapes} 次）", LogLevel.Warn);
            await pad.Back();
            return 1100;
        }
        return await AbortAsync($"连续卡住（{what}）", pad, log);
    }

    private Task<int?> AbortAsync(string reason, ISwapPad pad, Action<string, LogLevel> log)
    {
        log($"[我的联赛] 满级巡检放弃：{reason}。按 B 尝试退出，下次比赛结束后重试", LogLevel.Error);
        _phase = Phase.Done;
        _ = pad.Back();
        return Task.FromResult<int?>(2000);
    }
}
