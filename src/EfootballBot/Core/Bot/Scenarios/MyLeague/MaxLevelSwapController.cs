using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

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

/// <summary><see cref="ISwapPad"/> 的生产实现：转发到引擎手柄。</summary>
internal sealed class EngineSwapPad(Pad pad) : ISwapPad
{
    public Task Y() => pad.Y();
    public Task X() => pad.X();
    public Task Confirm() => pad.Confirm();
    public Task Back() => pad.Back();
    public Task Dpad(PadDir dir) => pad.Dpad(dir);
    public Task DpadRepeat(PadDir dir, int count) => pad.DpadRepeat(dir, count);
}

/// <summary>
/// 满级自动换人状态机（跨帧，「一帧一步、屏幕验证后才按下一键」）：
/// 联赛主页 Y → 比赛计划（首发站位页，光标默认门将）→ 沿
/// ↑ ← ↑ → → → → → ↓ ← 巡检 11 人，每站 Y 进详情读「等级 x/x + 能力点」；
/// 满级且能力点 ≠ 0：X 球员动作（核验「等级训练完成」）→ ↓+A 能力点分配 →
/// Y 推荐分配归零 → B×2 回站位页 → A 选中 → ←×8 进侧栏 → 上×5 下×3 落「后备球员」
/// → A → ←+A 开排序 → 上×6 下×5 落「当前等级」→ 右上箭头为下则 Y 改升序 → A；
/// 回列表 ← ↓ A 开筛选 → ↓×4 A 进卡片类型 → 上×9 ↓↓ + 6×(A ↓) 勾高阶卡 → B；
/// ↓×4 ↓ A 应用筛选 → A 换入首位（当前等级最低）→ B 回站位页 → B 完成 →
/// 主页 Y 重进（光标回门将）→ 盲走光标回刚换人的位置继续下一位。
/// 非满级：详情页 B 退出，直接下一位。11 人完成 B 返回主页交还正常流程。
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
        /// <summary>详情页：读等级/能力点并决策。</summary>
        DetailRead,
        /// <summary>详情页 B 后：确认回站位页并推进到下一位。</summary>
        InspectAdvance,
        /// <summary>球员动作弹窗：核验等级训练完成 → ↓+A 进能力点分配。</summary>
        ActionPopup,
        /// <summary>分配页：Y 推荐分配。</summary>
        AllocWait,
        /// <summary>分配页：等能力点归零 → B 关闭。</summary>
        AllocCheck,
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
        /// <summary>筛选后的列表：A 换入首位后备球员。</summary>
        BenchSwap,
        /// <summary>交换后 B 回站位页。</summary>
        SwapReturn,
        /// <summary>站位页 B 完成 → 回联赛主页。</summary>
        ExitPlan,
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

    /// <summary>整轮巡检轮数保险丝（含等待重试；约 400 轮 × ~2s ≈ 13 分钟上限）。</summary>
    internal const int MaxRounds = 400;

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

    /// <summary>巡检是否空闲（空闲时场景层才允许触发新的巡检）。</summary>
    public bool Idle => _phase == Phase.Idle;

    public void Reset()
    {
        _phase = Phase.Idle;
        _pos = _swapPos = _stall = _readTries = _escapes = _rounds = _swapped = _maxed = 0;
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
        return 2400;
    }

    /// <summary>每轮观察后调用；巡检未激活返回 null（交还场景正常流程）。</summary>
    public Task<int?> StepAsync(BotEngine engine, Observation obs, Action<string, LogLevel> log,
        CancellationToken ct = default)
        => StepCore(obs.Ocr, obs.Frame, new EngineSwapPad(engine.Pad),
            (o, f) => ReadStatusAsync(engine, o, f, ct), log, ct);

    /// <summary>等级/能力点读取：全帧 OCR 直读，失败裁切数据栏放大重 OCR（约数百毫秒，仅读不出时）。</summary>
    internal static async Task<PlayerStatus?> ReadStatusAsync(BotEngine engine, OcrResult ocr,
        FrameData frame, CancellationToken ct)
    {
        var direct = MaxLevelSwapBrain.ReadPlayerStatus(ocr);
        if (direct is not null) return direct;
        try
        {
            var crop = ImageScaling.CropUpscale(frame, 0.44, 0.70, 0.32, 0.50, 6);
            var res = await engine.Ocr.RecognizeAsync(crop, ct);
            return MaxLevelSwapBrain.ReadPlayerStatus(res, headYMin: 0.15, valueMaxDy: 0.35);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>核心状态机（OCR/帧/手柄/读取器全参数化，可单测）。返回本轮延时；null = 巡检结束。</summary>
    internal async Task<int?> StepCore(OcrResult ocr, FrameData frame, ISwapPad pad,
        Func<OcrResult, FrameData, Task<PlayerStatus?>> readStatus,
        Action<string, LogLevel> log, CancellationToken ct = default)
    {
        if (_phase == Phase.Idle) return null;
        if (++_rounds > MaxRounds)
            return await AbortAsync("巡检轮数超限", pad, log);

        var screen = MaxLevelSwapBrain.Classify(ocr);
        bool home = ocr.ContainsJoined("前往比赛");

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
                    return 1200;
                }
                // Y 未生效（弹窗/加载）：重按，最多 3 次
                if (++_stall <= 3)
                {
                    await pad.Y();
                    return 2200;
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
                if (_pos > 0) await pad.Dpad(CursorMoves[_pos - 1]);
                await pad.Y();
                log($"[我的联赛] 巡检第 {_pos + 1}/11 位：打开球员详情", LogLevel.Debug);
                _phase = Phase.DetailRead;
                return 2400;
            }

            case Phase.DetailRead:
            {
                if (screen == SwapScreen.Detail)
                {
                    _stall = 0;
                    var status = await readStatus(ocr, frame);
                    if (status is null)
                    {
                        if (++_readTries <= 2) return 1200; // 页面静止，下一轮重读
                        log("[我的联赛] 等级/能力点读不出，跳过该位（B 退出）", LogLevel.Warn);
                        _readTries = 0;
                        await pad.Back();
                        _phase = Phase.InspectAdvance;
                        return 2000;
                    }
                    _readTries = 0;
                    if (status.NeedsSwap)
                    {
                        _maxed++;
                        _swapPos = _pos;
                        log($"[我的联赛] 第 {_pos + 1}/11 位球员满级（{status.Level}/{status.LevelMax}，能力点 {status.Points}）：分配能力点并换人", LogLevel.Success);
                        await pad.X();
                        _phase = Phase.ActionPopup;
                        return 2200;
                    }
                    if (status.Level == status.LevelMax)
                        log($"[我的联赛] 第 {_pos + 1}/11 位球员已满级但能力点已用完（{status.Level}/{status.LevelMax}），跳过", LogLevel.Info);
                    await pad.Back();
                    _phase = Phase.InspectAdvance;
                    return 2000;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Y(); // Y 未生效，重按
                    return 2000;
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
                        return 2200;
                    }
                    _pos++;
                    _phase = Phase.Inspect;
                    return 1200;
                }
                if (screen == SwapScreen.Detail)
                {
                    await pad.Back(); // B 未生效再按
                    return 1800;
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
                        // 等级 x/x 但弹窗首项仍是「等级训练」：证据冲突，取消换人保安全
                        log($"[我的联赛] 第 {_pos + 1} 位弹窗无「等级训练完成」，取消换人（B）", LogLevel.Warn);
                        await pad.Back();
                        _phase = Phase.InspectAdvance;
                        return 2000;
                    }
                    await pad.Dpad(PadDir.Down);  // 等级训练完成 → 能力点分配
                    await pad.Confirm();
                    _phase = Phase.AllocWait;
                    return 2600;
                }
                if (screen == SwapScreen.Detail && ++_stall <= 2)
                {
                    await pad.X(); // X 未生效
                    return 2000;
                }
                return await StallAsync("球员动作弹窗未出现", screen, pad, log);
            }

            case Phase.AllocWait:
            {
                if (screen == SwapScreen.Alloc)
                {
                    _stall = 0;
                    await pad.Y(); // 推荐分配：能力点一次性归零
                    _phase = Phase.AllocCheck;
                    return 2400;
                }
                if (screen == SwapScreen.ActionPopup && ++_stall <= 2)
                {
                    await pad.Confirm(); // A 未生效
                    return 2000;
                }
                return await StallAsync("能力点分配页未打开", screen, pad, log);
            }

            case Phase.AllocCheck:
            {
                if (screen == SwapScreen.Alloc)
                {
                    var pts = MaxLevelSwapBrain.ReadRemainingPoints(ocr);
                    if (pts is null && ++_stall <= 2) return 1200; // 再读一轮
                    _stall = 0;
                    if (pts is > 0)
                    {
                        await pad.Y(); // 还有剩余，再按一次推荐分配
                        return 2200;
                    }
                    await pad.Back(); // B 关闭分配页
                    _phase = Phase.DetailVerify;
                    return 2000;
                }
                if (screen == SwapScreen.Detail)
                {
                    _phase = Phase.DetailVerify; // 分配页没进去/已关闭
                    return 300;
                }
                return await StallAsync("分配页异常", screen, pad, log);
            }

            case Phase.DetailVerify:
            {
                if (screen == SwapScreen.Detail)
                {
                    _stall = 0;
                    var status = await readStatus(ocr, frame);
                    if (status is { } s && s.Points != 0)
                        log($"[我的联赛] 注意：能力点分配后仍为 {s.Points}（推荐分配未生效？），继续换人", LogLevel.Warn);
                    await pad.Back();
                    _phase = Phase.LineupSelect;
                    return 2000;
                }
                if (screen == SwapScreen.Alloc)
                {
                    await pad.Back();
                    return 1800;
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
                    return 1800;
                }
                if (screen == SwapScreen.Detail)
                {
                    await pad.Back();
                    return 1800;
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
                return 3500;
            }

            case Phase.BenchWait:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    await pad.Dpad(PadDir.Left);  // 列表 → 侧栏排序图标
                    await pad.Confirm();
                    _phase = Phase.SortWait;
                    return 2200;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Confirm(); // 侧栏 A 未生效
                    return 2000;
                }
                return await StallAsync("后备球员页未打开", screen, pad, log);
            }

            case Phase.SortWait:
            {
                if (screen == SwapScreen.Sort)
                {
                    _stall = 0;
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
                    return 2600;
                }
                if (screen == SwapScreen.Bench && ++_stall <= 2)
                {
                    await pad.Dpad(PadDir.Left);
                    await pad.Confirm(); // 排序弹窗没开，重试
                    return 2200;
                }
                return await StallAsync("排序弹窗未出现", screen, pad, log);
            }

            case Phase.SortApplied:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    await pad.Dpad(PadDir.Left);  // 列表 → 排序图标
                    await pad.Dpad(PadDir.Down);  // → 筛选图标
                    await pad.Confirm();
                    _phase = Phase.FilterWait;
                    return 2400;
                }
                if (screen == SwapScreen.Sort && ++_stall <= 2)
                {
                    await pad.Confirm(); // A 未生效
                    return 2000;
                }
                return await StallAsync("排序应用异常", screen, pad, log);
            }

            case Phase.FilterWait:
            {
                if (screen == SwapScreen.Filter)
                {
                    _stall = 0;
                    await pad.DpadRepeat(PadDir.Up, 9);   // 饱和到「俱乐部球队」
                    await pad.DpadRepeat(PadDir.Down, 4); // → 卡片类型
                    await pad.Confirm();
                    _phase = Phase.CardTypeWait;
                    return 2400;
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
                        return 2000;
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
                return 4500;
            }

            case Phase.FilterApply:
            {
                if (screen == SwapScreen.Filter)
                {
                    _stall = 0;
                    await pad.DpadRepeat(PadDir.Down, 4); // 卡片类型 → 比赛风格
                    await pad.Dpad(PadDir.Down);          // 比赛风格 → 完成
                    await pad.Confirm();                  // 应用筛选
                    _phase = Phase.BenchSwap;
                    return 2600;
                }
                if (screen == SwapScreen.CardType)
                {
                    await pad.Back(); // B 未生效
                    return 1800;
                }
                return await StallAsync("筛选应用异常", screen, pad, log);
            }

            case Phase.BenchSwap:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    _swapped++;
                    log($"[我的联赛] 换人：首位后备球员（当前等级最低）换入第 {_swapPos + 1} 位", LogLevel.Success);
                    await pad.Confirm(); // A 交换
                    _phase = Phase.SwapReturn;
                    return 2400;
                }
                return await StallAsync("筛选后列表异常", screen, pad, log);
            }

            case Phase.SwapReturn:
            {
                if (screen == SwapScreen.Bench)
                {
                    _stall = 0;
                    await pad.Back(); // B 回站位页
                    _phase = Phase.ExitPlan;
                    return 2200;
                }
                if (screen == SwapScreen.Lineup)
                {
                    _phase = Phase.ExitPlan; // 已在站位页
                    return 300;
                }
                if (++_stall > 3)
                {
                    await pad.Back();
                    _phase = Phase.ExitPlan;
                    return 1800;
                }
                return 1500; // 交换动画/意外弹窗，先观察
            }

            case Phase.ExitPlan:
            {
                if (screen == SwapScreen.Lineup)
                {
                    _stall = 0;
                    await pad.Back(); // B 完成：退出比赛计划
                    _phase = Phase.Reopen;
                    return 2200;
                }
                if (home)
                {
                    _phase = Phase.Reopen;
                    return 500;
                }
                return await StallAsync("退出比赛计划异常", screen, pad, log);
            }

            case Phase.Reopen:
            {
                if (home)
                {
                    _stall = 0;
                    await pad.Y(); // 重进比赛计划，光标回门将
                    _phase = Phase.Rewind;
                    return 2400;
                }
                if (screen == SwapScreen.Lineup && ++_stall <= 2)
                {
                    await pad.Back(); // B 未生效
                    return 2000;
                }
                return await StallAsync("回主页异常", screen, pad, log);
            }

            case Phase.Rewind:
            {
                if (screen != SwapScreen.Lineup)
                    return await StallAsync("重进比赛计划异常", screen, pad, log);
                _stall = 0;
                for (int i = 0; i < _swapPos; i++)
                    await pad.Dpad(CursorMoves[i]); // 盲走回刚换人的位置（不带等级识别）
                _pos = _swapPos + 1;                // 换入的新球员不再识别，直接下一位
                _phase = _pos >= CursorMoves.Length ? Phase.Done : Phase.Inspect;
                if (_phase == Phase.Done)
                {
                    log($"[我的联赛] 首发巡检完成：满级 {_maxed} 名，换人 {_swapped} 名", LogLevel.Success);
                    await pad.Back();
                    return 2200;
                }
                log($"[我的联赛] 已回到第 {_pos + 1}/11 位，继续巡检", LogLevel.Info);
                return 1500 + 400 * _swapPos;
            }

            case Phase.Done:
            {
                if (home)
                {
                    log("[我的联赛] 满级巡检结束，交还正常流程", LogLevel.Info);
                    Reset();
                    return null;
                }
                if (++_stall > 5)
                {
                    log("[我的联赛] 满级巡检收尾超时，放弃等待", LogLevel.Warn);
                    Reset();
                    return null;
                }
                await pad.Back(); // 逐级 B 退出到主页
                return 2000;
            }

            default:
                Reset();
                return null;
        }
    }

    /// <summary>阶段内画面不符合预期：连续 3 轮按 B 逃生一次；逃生 3 次整场放弃。</summary>
    private async Task<int?> StallAsync(string what, SwapScreen screen, ISwapPad pad, Action<string, LogLevel> log)
    {
        if (++_stall <= 3) return 1500; // 先观察（页面切换/OCR 抖动）
        _stall = 0;
        if (++_escapes <= 3)
        {
            log($"[我的联赛] 满级巡检卡住（{what}，画面 {screen}）：按 B 逃生（第 {_escapes} 次）", LogLevel.Warn);
            await pad.Back();
            return 2000;
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
