using EfootballBot.Core.Action;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
// Observation 类型与 EfootballBot.Core.Observation 命名空间同名，用 Bot. 前缀消歧
using Bot = EfootballBot.Core.Bot;

namespace EfootballBot.Core.Flow;

/// <summary>比赛流程状态机阶段。</summary>
public enum MatchStage { PreMatch, Kickoff, Playing, Halftime, FullTime, Result, Rewards, Done }

/// <summary>单场比赛的执行结果。</summary>
public enum MatchResult { Completed, Forfeited, Timeout, Error }

/// <summary>单场比赛选项（从 AppConfig 提取，便于 MatchFlow 脱离 ScenarioBase 独立运行）。</summary>
public sealed class MatchOptions
{
    /// <summary>是否在普通比赛中执行弃权（只踢关键模式）。</summary>
    public bool ForfeitNormal;
    /// <summary>是否关键比赛（仅用于日志）。</summary>
    public bool IsKeyMatch;
    /// <summary>赛前画面期望的难度文字。</summary>
    public string Difficulty = "职业";
    /// <summary>单场比赛滑动超时（分钟）。每次确认 InMatch 顺延。</summary>
    public int MatchTimeoutMin = 30;
    /// <summary>比赛中挂机轻按 A 的最小间隔（毫秒）。</summary>
    public int IdleTapMinMs = 1000;
    /// <summary>比赛中挂机轻按 A 的最大间隔（毫秒）。</summary>
    public int IdleTapMaxMs = 4000;
}

/// <summary>
/// 单场比赛 7 阶段状态机：赛前 → 开球 → 比赛中 → 中场 → 全场 → 结果 → 奖励。
/// 从 ScenarioBase.PlayOneMatchAsync 拆分而来，每个阶段一个方法，行为与原版一致。
/// </summary>
public sealed class MatchFlow
{
    private readonly BotEngine _engine;
    private readonly ActionExecutor _actions;
    private readonly Pad _pad;
    private readonly DifficultySelector _difficulty;
    private readonly Func<Bot.Observation, Task<bool>>? _specialInMatchAction;

    /// <summary>阶段切换事件：当前阶段 + 阶段死线（ISO 字符串，无死线时 null）。</summary>
    public event Action<MatchStage, string?>? StageChanged;

    /// <summary>阶段间共享的最新观察。</summary>
    private Bot.Observation _obs = null!;
    /// <summary>本场比赛最终结果。</summary>
    private MatchResult _result = MatchResult.Completed;

    public MatchFlow(BotEngine engine, ActionExecutor actions, Pad pad,
        Func<Bot.Observation, Task<bool>>? specialInMatchAction = null)
    {
        _engine = engine;
        _actions = actions;
        _pad = pad;
        _difficulty = new DifficultySelector(engine, pad);
        _specialInMatchAction = specialInMatchAction;
    }

    /// <summary>执行完整比赛流程。entry 为调用方已观察并 PassThrough 后的初始画面。</summary>
    public async Task<MatchResult> RunAsync(Bot.Observation entry, MatchOptions opts, CancellationToken ct)
    {
        _obs = entry;
        _result = MatchResult.Completed;
        var stage = MatchStage.PreMatch;
        EmitStage(stage, null);
        while (stage != MatchStage.Done)
        {
            ct.ThrowIfCancellationRequested();
            var prev = stage;
            stage = stage switch
            {
                MatchStage.PreMatch => await RunPreMatchAsync(opts, ct),
                MatchStage.Kickoff => await RunKickoffAsync(ct),
                MatchStage.Playing => await RunPlayingAsync(opts, ct),
                MatchStage.Halftime => await RunHalftimeAsync(ct),
                MatchStage.FullTime => await RunFullTimeAsync(ct),
                MatchStage.Result => await RunResultAsync(ct),
                MatchStage.Rewards => await RunRewardsAsync(ct),
                _ => MatchStage.Done
            };
            if (stage != prev)
            {
                // 比赛中阶段携带超时死线；其余阶段无死线
                string? deadline = stage == MatchStage.Playing
                    ? DateTime.Now.AddMinutes(opts.MatchTimeoutMin).ToString("o")
                    : null;
                EmitStage(stage, deadline);
            }
        }
        return _result;
    }

    /// <summary>派发阶段切换事件（非阻塞）。</summary>
    private void EmitStage(MatchStage stage, string? deadline)
        => StageChanged?.Invoke(stage, deadline);

    private void Log(string msg, LogLevel lv = LogLevel.Info) => _engine.LogInternal(msg, lv);

    /// <summary>观察并经过 Overlay PassThrough（赛前/比赛中使用）。</summary>
    private async Task<Bot.Observation> ObserveAsync(CancellationToken ct)
    {
        var o = await _engine.ObserveAsync(ct);
        return await _engine.Overlay.PassThroughAsync(o, ct);
    }

    /// <summary>是否为菜单页（比赛结束后落地的页面）。</summary>
    private static bool IsMenuPage(GameScreen s) => s is
        GameScreen.MyLeagueHome or GameScreen.Home
        or GameScreen.EventHub or GameScreen.MatchHub or GameScreen.ActivityDetail
        or GameScreen.ChallengeList or GameScreen.ChallengeHome or GameScreen.EventInfo;

    // ==================== 阶段 1：赛前 ====================

    /// <summary>
    /// 赛前阶段：
    /// ① 若先弹「选择赛事等级」模态框 → DifficultySelector 智能核对蓝勾并按需改选后确认；
    /// ② 赛前设置页：确认难度显示后按「开始比赛/下一步/前往比赛」。
    /// 退出：检测到 InMatch 或 Loading；超时 12 轮后进入开球等待。
    /// </summary>
    private async Task<MatchStage> RunPreMatchAsync(MatchOptions opts, CancellationToken ct)
    {
        int preGuard = 0;
        while (preGuard++ < 12)
        {
            ct.ThrowIfCancellationRequested();

            // 「选择赛事等级」弹窗：核对/改选 → 确认，确认后落到赛前设置页继续本循环
            if (_obs.Is(GameScreen.DifficultySelect))
            {
                Log($"进入赛事等级选择面板，目标难度：{opts.Difficulty}", LogLevel.Info);
                var after = await _difficulty.HandleAsync(_obs, opts.Difficulty, ct);
                if (after is not null) _obs = await _engine.Overlay.PassThroughAsync(after, ct);
                else _obs = await ObserveAsync(ct);
                continue;
            }

            if (!_obs.Is(GameScreen.PreMatch))
            {
                // 已进入比赛/加载等后续画面：交给开球阶段；其他画面也不再停留赛前
                if (_obs.Is(GameScreen.InMatch) || _obs.Is(GameScreen.Loading)) break;
                // 未知/菜单画面：按 A 尝试推进一次（过场特写等），随后重新观察
                await _pad.Confirm();
                await _pad.Wait(1500, ct);
                _obs = await ObserveAsync(ct);
                continue;
            }

            string diff = opts.Difficulty;
            bool matched = _obs.Ocr.Contains(diff);
            string levelText = matched ? "是" : $"未识别，如非{diff}请手动设为{diff}";
            Log($"{(opts.IsKeyMatch ? "关键" : "普通")}比赛赛前画面，难度{diff}：{levelText}",
                matched ? LogLevel.Debug : LogLevel.Warn);

            // 底部"开始比赛 / 下一步 / 前往比赛"大按钮直接 A
            var start = _obs.Ocr.FindFirst("开始比赛") ?? _obs.Ocr.FindFirst("下一步")
                        ?? _obs.Ocr.FindFirst("前往比赛") ?? _obs.Ocr.FindFirst("kickoff");
            if (start is null || start.CenterY > 0.62)
            {
                await _pad.Confirm();
                await _pad.Wait(2500, ct);
            }
            else if (await _actions.SelectMenuByTextAsync(new[] { "开始比赛", "下一步", "前往比赛", "kickoff" }, ct))
            {
                await _pad.Wait(2500, ct);
            }
            _obs = await ObserveAsync(ct);
            if (_obs.Is(GameScreen.InMatch) || _obs.Is(GameScreen.Loading)) break;
        }
        return MatchStage.Kickoff;
    }

    // ==================== 阶段 2：等待开球 ====================

    /// <summary>
    /// 等待开球：连续 2 次确认 InMatch 才认定比赛真正开始。
    /// 入场动画/球场特写时球场绿占比也高，但缺少 AI 图标和时钟，
    /// 单信号误判会导致比赛计时提前。连续 2 次确认（间隔 1.5s）过滤瞬时误判。
    /// 超时 ~18s 后按当前画面继续处理。
    /// </summary>
    private async Task<MatchStage> RunKickoffAsync(CancellationToken ct)
    {
        Log("等待比赛开始…", LogLevel.Info);
        const int confirmNeeded = 2;
        int confirmed = 0;
        for (int i = 0; i < 12 && confirmed < confirmNeeded; i++)
        {
            ct.ThrowIfCancellationRequested();
            _obs = await ObserveAsync(ct);
            if (_obs.Is(GameScreen.InMatch))
            {
                confirmed++;
                if (confirmed >= confirmNeeded)
                    Log($"比赛开始确认（连续{confirmNeeded}次 InMatch）", LogLevel.Info);
                else
                    await _pad.Wait(1500, ct);
            }
            else
            {
                confirmed = 0;
                await _pad.Wait(1500, ct);
            }
        }
        if (confirmed < confirmNeeded)
            Log("等待比赛开始超时，按当前画面继续处理", LogLevel.Warn);
        return MatchStage.Playing;
    }

    // ==================== 阶段 3：比赛中 ====================

    /// <summary>
    /// 比赛中挂机：跳过回放/暂停按钮、随机轻按 A 防检测、检测弃权条件。
    /// 退出：Result/Rewards/FullTime（转对应阶段）或稳定菜单页（复测 2.5s，转 Done 跳过赛后）。
    /// 滑动超时：每次确认 InMatch 顺延 deadline（MatchTimeoutMin）。
    /// </summary>
    private async Task<MatchStage> RunPlayingAsync(MatchOptions opts, CancellationToken ct)
    {
        // 弃权（仅普通比赛、只踢关键模式）：在比赛开始后立即尝试
        if (opts.ForfeitNormal)
        {
            if (await TryForfeitAsync(ct))
            {
                _result = MatchResult.Forfeited;
                // 弃权后直接进入赛后收尾流程（RunRewardsAsync 等价于原 PostMatchSequenceAsync）
                return MatchStage.Rewards;
            }
            Log("未能找到「放弃比赛」，改为正常完成本场。", LogLevel.Warn);
        }

        Log("比赛进行中（AI 操控），挂机等待结束…", LogLevel.Info);
        var matchDeadline = DateTime.Now.AddMinutes(opts.MatchTimeoutMin);
        while (DateTime.Now < matchDeadline)
        {
            ct.ThrowIfCancellationRequested();
            _obs = await ObserveAsync(ct);

            // 每次确认仍在比赛就顺延死线：超时只用于卡死（长时间看不到 InMatch）的兜底
            if (_obs.Is(GameScreen.InMatch))
                matchDeadline = DateTime.Now.AddMinutes(opts.MatchTimeoutMin);

            if (_obs.Is(GameScreen.Result)) return MatchStage.Result;
            if (_obs.Is(GameScreen.Rewards)) return MatchStage.Rewards;
            if (_obs.Is(GameScreen.FullTime)) return MatchStage.FullTime;

            // 场景特殊动作（国服：右上「跳过比赛」绿✓可用 → 暂停菜单快速结束）
            if (_specialInMatchAction is not null && await _specialInMatchAction(_obs))
                continue;

            // 疑似已回到菜单页。比赛中的回放/入场/换人等画面 OCR 可能误识出菜单词，
            // 单帧判定会把还在踢的比赛误判结束。这里必须隔 2.5s 复测一次：
            // 两次都稳定落在菜单页才认定比赛结束
            if (IsMenuPage(_obs.Screen))
            {
                Log($"疑似返回菜单页（{_obs.Screen}），2.5s 后复测确认", LogLevel.Debug);
                await _pad.Wait(2500, ct);
                var o2 = await ObserveAsync(ct);

                if (o2.Is(GameScreen.Result)) { _obs = o2; return MatchStage.Result; }
                if (o2.Is(GameScreen.Rewards)) { _obs = o2; return MatchStage.Rewards; }
                if (o2.Is(GameScreen.FullTime)) { _obs = o2; return MatchStage.FullTime; }

                if (IsMenuPage(o2.Screen))
                {
                    _obs = o2;
                    Log($"复测确认已返回菜单页（{o2.Screen}），比赛结束", LogLevel.Info);
                    // 已落到菜单页：结算早被 A 跳完，不能再连按 A（会在主页误触发前往比赛）
                    return MatchStage.Done;
                }

                // 复测是比赛中/加载/中场/未知：说明刚才是误识，继续挂机不动
                continue;
            }

            // 比赛内弹层（回放/进球/暂停/换人/蓝底"下一步/跳过"）指针移上去再按 A。
            // 排除右上 HUD「跳过比赛 累积进球 n/3」提示词：它是状态提示不是按钮。
            var skipWord = _obs.Ocr.FindAll("跳过")
                .FirstOrDefault(w => !(w.CenterX > 0.78 && w.CenterY < 0.14));
            var next = _obs.Ocr.FindFirst("下一步") ?? _obs.Ocr.FindFirst("确定") ?? _obs.Ocr.FindFirst("确认")
                       ?? skipWord ?? _obs.Ocr.FindFirst("ok") ?? _obs.Ocr.FindFirst("skip");
            if (next is not null)
            {
                Log($"比赛内出现「{next.Text}」，移动指针点击", LogLevel.Debug);
                await _actions.PressButtonAsync(new[] { "下一步", "确定", "确认", "跳过", "ok", "skip" }, ct);
                await _pad.Wait(1200, ct);
                continue;
            }

            // 比赛内暂停菜单：右侧播放/继续按钮（粉色高亮框内白色三角图标）
            if (_obs.Is(GameScreen.InMatch) && _obs.Frame is not null)
            {
                var playBtnRoi = new NormRect(0.82, 0.06, 0.16, 0.10);
                double redRatio = FrameAnalyzer.ColorRatioExact(_obs.Frame, HsvFilter.Red, playBtnRoi);
                double blueRatio = FrameAnalyzer.ColorRatioExact(_obs.Frame, HsvFilter.Blue, playBtnRoi);
                if (redRatio >= 0.08 && blueRatio >= 0.3)
                {
                    Log("检测到比赛暂停菜单播放按钮，点击继续", LogLevel.Debug);
                    await _pad.Confirm();
                    await _pad.Wait(1200, ct);
                    continue;
                }
            }

            // 左上"比赛回放/进球"、右上暂停/回放图标等字样 → 立即按 A。
            if (_obs.Ocr.ContainsJoined("回放") || _obs.Ocr.ContainsJoined("进球")
                || _obs.Ocr.Contains("暂停") || _obs.Ocr.Contains("replay") || _obs.Ocr.Contains("goal"))
            {
                await _pad.Confirm();
                await _pad.Wait(1200, ct);
                continue;
            }

            if (_obs.Is(GameScreen.HalfTime))
            {
                Log("中场休息，继续", LogLevel.Debug);
                await _pad.Confirm();
                await _pad.Wait(3000, ct);
                return MatchStage.Halftime;
            }

            // 比赛中：轻按 A 过回放/提示。非必须立即的连点，间隔随机，避免固定节奏被检测
            if (_obs.Is(GameScreen.InMatch))
            {
                await _pad.Confirm();
                await _pad.HumanWait(opts.IdleTapMinMs, opts.IdleTapMaxMs, ct);
                continue;
            }

            // 无 InMatch 信号：球场过场/回放特写/阵容展示/加载。一律以 A 继续，绝不按 B
            await _pad.Confirm();
            await _pad.Wait(1600, ct);
        }

        // 死线超时（长时间看不到比赛中/结算）：不当作完赛，不跑赛后连按
        Log("等待比赛结束超时，未确认完赛，退出比赛循环重新观察", LogLevel.Warn);
        _result = MatchResult.Timeout;
        return MatchStage.Done;
    }

    // ==================== 阶段 4：中场 ====================

    /// <summary>
    /// 中场休息：按 A 继续，观察落地画面。
    /// 退出：InMatch（回比赛中）或 FullTime/Result/Rewards。
    /// </summary>
    private async Task<MatchStage> RunHalftimeAsync(CancellationToken ct)
    {
        _obs = await ObserveAsync(ct);
        if (_obs.Is(GameScreen.FullTime)) return MatchStage.FullTime;
        if (_obs.Is(GameScreen.Result)) return MatchStage.Result;
        if (_obs.Is(GameScreen.Rewards)) return MatchStage.Rewards;
        return MatchStage.Playing;
    }

    // ==================== 阶段 5：全场结束 ====================

    /// <summary>
    /// 全场结束：按 A → 等 2.5s，观察是否进入结果页。
    /// 退出：Result；若已跳到 Rewards 或其他页面则交 Rewards 阶段处理。
    /// </summary>
    private async Task<MatchStage> RunFullTimeAsync(CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            await _pad.Confirm();
            await _pad.Wait(2500, ct);
            // 赛后阶段直接观察，不经过 PassThrough（与原 PostMatchSequence 一致）
            _obs = await _engine.ObserveAsync(ct);
            if (_obs.Is(GameScreen.Result)) return MatchStage.Result;
            if (_obs.Is(GameScreen.Rewards)) return MatchStage.Rewards;
            if (!_obs.Is(GameScreen.FullTime)) return MatchStage.Rewards;
        }
        return MatchStage.Rewards;
    }

    // ==================== 阶段 6：比赛结果 ====================

    /// <summary>
    /// 比赛结果/MVP 页：按 A 跳过。有确认按钮时走 PressButtonAsync 识别焦点。
    /// 退出：Rewards；若离开结果页则交 Rewards 阶段处理稳定检测。
    /// </summary>
    private async Task<MatchStage> RunResultAsync(CancellationToken ct)
    {
        string[] confirmBtns = { "下一步", "ok", "确定", "确认", "skip", "跳过" };
        for (int i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            _obs = await _engine.ObserveAsync(ct);
            if (_obs.Is(GameScreen.Rewards)) return MatchStage.Rewards;
            if (!_obs.Is(GameScreen.Result)) return MatchStage.Rewards;

            bool hasBtn = confirmBtns.Any(kw => _obs.Ocr.Contains(kw));
            if (hasBtn)
            {
                if (!await _actions.PressButtonAsync(confirmBtns, ct))
                {
                    await _pad.Confirm();
                    await _pad.Wait(700, ct);
                }
            }
            else
            {
                await _pad.Confirm();
                await _pad.Wait(700, ct);
            }
        }
        return MatchStage.Rewards;
    }

    // ==================== 阶段 7：奖励 / 赛后收尾 ====================

    /// <summary>
    /// 赛后结算 / 奖励 → 逐个点击返回。
    /// 检测到「下一步 / OK / 确定 / 确认」时走 PressButtonAsync（识别按钮→若不在默认焦点则移动→点击），
    /// 避免按钮非默认焦点时直接按 A 无效。无按钮的画面（加载等）直接按 A 继续。
    /// 退出：连续 3 帧画面稳定；超时 180s。
    /// </summary>
    private async Task<MatchStage> RunRewardsAsync(CancellationToken ct)
    {
        Log("处理赛后结算与奖励…", LogLevel.Info);
        var deadline = DateTime.Now.AddSeconds(180);
        int stable = 0;
        GameScreen? last = null;
        string[] confirmBtns = { "下一步", "ok", "确定", "确认", "skip", "跳过" };
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _obs = await _engine.ObserveAsync(ct);

            // 有明确确认按钮：走 PressButtonAsync（自动处理焦点移动）
            bool hasBtn = confirmBtns.Any(kw => _obs.Ocr.Contains(kw));
            if (hasBtn)
            {
                stable = 0;
                if (!await _actions.PressButtonAsync(confirmBtns, ct))
                {
                    await _pad.Confirm();
                    await _pad.Wait(700, ct);
                }
                continue;
            }

            if (_obs.Screen is GameScreen.Result or GameScreen.Rewards or GameScreen.FullTime
                or GameScreen.Dialog or GameScreen.Loading)
            {
                stable = 0;
                await _pad.Confirm();
                await _pad.Wait(700, ct);
                continue;
            }
            if (last == _obs.Screen) stable++;
            else stable = 0;
            last = _obs.Screen;
            if (stable >= 3) break;
            await _pad.Confirm();
            await _pad.Wait(700, ct);
        }
        return MatchStage.Done;
    }

    // ==================== 弃权子流程 ====================

    /// <summary>暂停 → 放弃比赛 → 确认。失败安全返回比赛。</summary>
    private async Task<bool> TryForfeitAsync(CancellationToken ct)
    {
        Log("普通比赛：执行弃权（只踢关键比赛模式）", LogLevel.Info);
        await _pad.Start();
        await _pad.Wait(2500, ct);
        var o = await _engine.ObserveAsync(ct);

        bool found = false;
        if (o.Ocr.Contains("放弃") || o.Ocr.Contains("认输") || o.Ocr.Contains("forfeit"))
            found = await _actions.SelectMenuByTextAsync(new[] { "放弃比赛", "放弃", "认输", "forfeit" }, ct, 10);

        if (!found)
        {
            // 再尝试逐行扫描
            found = await _actions.SelectMenuByTextAsync(new[] { "放弃比赛", "放弃", "认输", "forfeit" }, ct, 10);
        }

        if (!found)
        {
            await _pad.Back();
            await _pad.Wait(1000, ct);
            return false;
        }

        // 二次确认弹窗
        await _pad.Wait(1500, ct);
        var confirm = await _engine.ObserveAsync(ct);
        if (confirm.Is(GameScreen.Dialog) || confirm.Is(GameScreen.ShopConfirm)
            || confirm.Ocr.Contains("放弃") || confirm.Ocr.Contains("确认"))
        {
            await _actions.SelectMenuByTextAsync(new[] { "确认", "确定", "放弃", "yes", "ok" }, ct, 4);
            await _pad.Confirm();
        }
        await _pad.Wait(6000, ct);
        return true;
    }
}
