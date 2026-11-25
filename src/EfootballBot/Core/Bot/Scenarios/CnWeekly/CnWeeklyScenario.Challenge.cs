using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· CnWeeklyScenario 状态机
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `CnWeeklyScenario.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

public sealed partial class CnWeeklyScenario : IScenario
{
    // 本文件小活动：挑战型活动 —— 清单推进/选卡/撤退重进（状态字段也在本文件）。

    // ---------------- 挑战型活动子流程（2026-10-17 实机） ----------------
    /// <summary>进入挑战子流程：激活挑战模式并切到清单阶段（游戏默认光标已在待挑战卡上）。</summary>
    private int EnterChallengeListPhase()
    {
        if (!_challengeMode)
        {
            _challengeMode = true;
            _log("[国服周活动] 检测到挑战清单：进入挑战子流程（默认光标即待挑战卡，评估后直接进入）", LogLevel.Success);
        }
        SetPhase(CwPhase.ChallengeList);
        return 1200;
    }

    /// <summary>
    /// 挑战卡详情页通关判定步（杯赛/巡回赛主页样式通用）：右栏胜场读满（"1/1" 经 Norm 成
    /// "11"，进行中 "0/1"→"01" 不命中）或通关探针漏读但本卡已打满 2 局 → 记忆已通关并
    /// B 回清单换下一张。同一卡再次判通关 = 光标不在待挑战卡上（勾探针漏读的已通关卡），
    /// 走重进清单复位光标。未通关返回 null，调用方继续正常开赛流程。
    /// </summary>
    private async Task<int?> ChallengeCardDoneStepAsync(Observation obs)
    {
        _challengeMode = true; // 半途启动直接落在卡详情页也激活挑战模式
        // 右栏奖励勾像素探针（2026-10-17 实机 (0.96,0.51)）：奖励已领取=本卡已完成，
        // B 回清单换下一张。★比「首次以后的奖励」词探针可靠——OCR 常漏读该词
        //（23:21 实机 24 词帧漏读后再次空开比赛）
        if (CnWeeklyBrain.ChallengeRewardClaimed(obs.Frame))
        {
            if (_chCurrentCard > 0) _chLocalDone.Add(_chCurrentCard);
            _chCurPlays = 0;
            SetPhase(CwPhase.ChallengeList);
            _log("[国服周活动·挑战] 卡详情页右栏奖励带勾=本卡已完成，B 回清单换下一张", LogLevel.Info);
            await _engine.Pad.Back();
            return 2200;
        }
        // 选错卡信号（用户 2026-10-17 实机）：奖励栏「首次以后的奖励」=重复奖励=该卡已打过。
        // 拉黑后 B 回清单换下一张（游标探测失败的兜底——勾卡在清单上可能仍是白色聚焦）
        if (obs.Ocr.ContainsJoined("首次以后") && _chCurrentCard > 0 &&
            !_chLocalDone.Contains(_chCurrentCard))
        {
            _chLocalDone.Add(_chCurrentCard);
            _chCurPlays = 0;
            SetPhase(CwPhase.ChallengeList);
            _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 奖励为「首次以后的奖励」=已打过，拉黑后 B 回清单换下一张", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2200;
        }
        bool progressDone = WeeklyBrain.ChallengeProgressDone(obs.Ocr);
        // ★积分/进度可读且未达标（积分 3/4 等）：绝不定通关——「已打满 2 局」兜底在
        // 积分制卡上会漏拿主要奖励（用户 2026-10-31 实机卡 03 积分 3/4 被误判通关）。
        // 护栏压过胜场读满：页面上若出现 3/4 类比值说明目标还没到，胜场探针读满可能是
        // 别处数字误命中
        if (CnWeeklyBrain.ChallengeScoreBehind(obs.Ocr))
        {
            if (_chCurPlays >= 2)
                _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 已打 {_chCurPlays} 局但积分/进度未达标，继续打（不按兜底判通关）", LogLevel.Info);
            return null;
        }
        if (!progressDone && _chCurPlays < 2) return null;
        bool repeat = _chCurrentCard > 0 && _chLocalDone.Contains(_chCurrentCard);
        if (_chCurrentCard > 0) _chLocalDone.Add(_chCurrentCard);
        _chCurPlays = 0;
        if (repeat)
            return await RefreshChallengeListAsync("已通关的卡再次进入（光标不在待挑战卡上）");
        SetPhase(CwPhase.ChallengeList);
        _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 已通关（{(progressDone ? "右栏胜场读满" : "已打满 2 局按通关处理")}），B 返回清单换下一张",
            LogLevel.Success);
        await _engine.Pad.Back();
        return 2200;
    }

    /// <summary>
    /// 挑战清单（用户 2026-10-17 晚实机规则）：目标卡 = 最后带勾的后一张 = 第一张上锁的
    /// 前一张（顺序结构）。★游标不信任——打完的勾卡可能仍是白色聚焦+参赛中，探测聚焦白卡
    /// 位置差值导航到目标卡再 A（读不出聚焦卡则直接 A 试）。看见绿勾直接拉黑；进入时可能
    /// 弹「参加新的挑战？」（默认焦点在取消），上移选进入再 A。★卡数不固定且可见卡≠全部卡：
    /// 可见全勾/全锁 ≠ 真没有——盲点右移翻页（连续 3 次可见集无变化=真末尾）后才重进刷新；
    /// 重进仍无 = 全部完成，停止挂机。
    /// </summary>
    private async Task<int?> HandleChallengeListAsync(CwScreen cw, MatchScreen ml, Observation obs)
    {
        // 按进卡详情页成功：交回 Flow（开赛流程 + 通关拦截都在那边）
        if (cw is CwScreen.EventHomeTour or CwScreen.EventHome or CwScreen.ChallengeCardHome)
        {
            _chStaleA = 0;
            _chRefreshs = 0;
            SetPhase(CwPhase.Flow);
            return 600;
        }
        if (cw == CwScreen.EventSettings) { SetPhase(CwPhase.Flow); return 700; }
        if (cw == CwScreen.EventsList) { SetPhase(CwPhase.Flow); return 800; }
        if (cw == CwScreen.EventIntro)
        {
            // 清单被关掉了（异常按键/全通关自动退场）：A 重进清单
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 600; }
            _log("[国服周活动·挑战] 清单已关闭，A 重进", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2600;
        }
        // 比赛上下文（A 直落比赛/结算弹窗链）：交回共享流程，清完自会回清单/卡页
        if (ml is MatchScreen.InMatch or MatchScreen.MatchStartPage or MatchScreen.FullTime or MatchScreen.TitleScreen)
        {
            SetPhase(CwPhase.Flow);
            return null;
        }
        if (ml == MatchScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 600; }
            // ★统一弹窗规则：有 取消/关闭 先上移到推进键再按 A（参加新的挑战弹窗
            // 有专属分支在更早处，走不到这里）
            bool dismissable = obs.Ocr.ContainsJoined("取消") || obs.Ocr.ContainsJoined("关闭");
            _log("[国服周活动·挑战] 清单弹窗：A 确认", LogLevel.Info);
            if (dismissable) await _engine.Pad.Dpad(PadDir.Up);
            await _engine.Pad.Confirm();
            return 2200;
        }
        if (cw != CwScreen.ChallengeList)
        {
            if (++_attempts > 10) { SetPhase(CwPhase.Flow); return 600; } // 过场/未知帧超预算：回屏幕驱动自愈
            return 1200;
        }
        if (_streak < 1) return 800; // 开场动画/淡入防抖（卡号 OCR 空帧同此等待）
        // 「参加新的挑战？」确认弹窗（进入下一张卡时可能弹出，2026-10-17 实机）：弹窗默认
        // 焦点在底部的「取消」上——盲按 A 会取消进入、留在已通关的卡上。必须先上移选
        // 「进入」再 A（若焦点已在进入上，上移不动、A 等效）
        if (obs.Ocr.ContainsJoined("参加新的挑战") ||
            (obs.Ocr.ContainsJoined("进入") && obs.Ocr.ContainsJoined("取消")))
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 600; }
            _log("[国服周活动·挑战] 参加新挑战弹窗：上移选「进入」→ A", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await _engine.Pad.Confirm();
            return 2600;
        }
        var cards = CnWeeklyBrain.ScanChallengeCards(obs.Ocr, obs.Frame);
        if (cards.Count == 0)
        {
            // OCR 一张卡号都没扫出来 ≠ 全部打完：先等待/重试，超限刷新清单而不误判完成
            if (++_chEmptyReads <= 4)
            {
                _log($"[国服周活动·挑战] 挑战清单未扫出卡号，等待重试（{_chEmptyReads}/4）", LogLevel.Debug);
                return 1200;
            }
            _chEmptyReads = 0;
            return await RefreshChallengeListAsync("挑战清单未扫出卡号");
        }
        _chEmptyReads = 0;
        // 看见绿勾直接拉黑（用户 2026-10-17 规则）：勾卡=已通关，本运行内绝不再进
        foreach (var c in cards.Where(c => c.Checked))
            _chLocalDone.Add(c.Index);
        // 可见卡集变化（翻页显出屏外卡/重进刷新复位）：边界探测归零
        string visible = string.Join(",", cards.Select(c => c.Index).OrderBy(i => i));
        if (visible != _chLastVisibleCards) _chEdgeProbes = 0;
        _chLastVisibleCards = visible;
        var playable = cards
            .Where(c => !c.Locked && !c.Checked &&
                        !_chLocalDone.Contains(c.Index) && !_chBlocked.Contains(c.Index))
            .ToList();
        if (playable.Count == 0)
        {
            // ★清单卡数不固定且可见卡≠全部卡（实机 01-03 全勾仍有第 4 卡屏外残边）：
            // 可见全勾/全锁 ≠ 真没有——盲点右移翻页，直到连续 3 次可见卡集无变化
            //（= 已在最末卡上，Right 无效）才允许重进刷新/报全部完成
            if (_chEdgeProbes < 3)
            {
                _chEdgeProbes++;
                _log($"[国服周活动·挑战] 可见卡 {visible} 无可挑战：右移翻页确认是否有屏外卡（第 {_chEdgeProbes} 次）", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Right);
                return 2200;
            }
            return await RefreshChallengeListAsync("清单上没有可挑战的卡（全勾/全锁）");
        }
        if (_chStaleA >= 3)
            return await RefreshChallengeListAsync("连续按 A 未进入挑战卡");
        // 目标卡 = 最后带勾的后一张 = 第一张上锁的前一张（顺序结构下即第一张无勾无锁卡）。
        // ★游标不信任：清单打开时光标可能停在刚打完的勾卡上（实机勾卡仍是白色聚焦+参赛中），
        // 探测白色聚焦卡位置，差值导航到目标卡再 A；读不出聚焦卡则保持旧行为直接按 A
        var target = playable[0];
        int steps = 0;
        int? focused = CnWeeklyBrain.DetectFocusedChallengeCard(obs.Ocr, obs.Frame);
        if (focused is not null)
        {
            var cursorCard = cards.FirstOrDefault(c => c.Index == focused.Value);
            if (cursorCard is not null)
                steps = target.Slot - cursorCard.Slot;
        }
        bool returningFromCard = _chStaleA == 0;
        _chStaleA++;
        // 同一张卡反复进入（确实离过清单又回来，不是清单页内 A 键丢包 _chStaleA > 0）却始终没有比赛开始：
        // 按已通关跳过——否则 清单↔卡页 无限打转；清单页原地丢包不误拉黑未打的卡。
        if (returningFromCard && _chCurrentCard == target.Index && !_chMatchSinceEnter)
        {
            _chRepeatEnters++;
            if (_chRepeatEnters >= 2)
            {
                _chLocalDone.Add(target.Index);
                _chRepeatEnters = 0;
                _log($"[国服周活动·挑战] 卡 {target.Index:00} 反复进入却始终没有比赛开始（应已通关，完成态未读出），按通关跳过", LogLevel.Warn);
                return await RefreshChallengeListAsync($"卡 {target.Index:00} 反复进入无比赛开始");
            }
        }
        else if (returningFromCard)
        {
            _chRepeatEnters = 0;
        }
        _chMatchSinceEnter = false;
        _chCurrentCard = target.Index;
        _chCurPlays = 0;
        if (steps > 0)
        {
            _log($"[国服周活动·挑战] 游标在勾卡上：右移×{steps} 到待挑战卡 {target.Index:00}（上锁的前一张）→ A", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Right, steps);
        }
        else if (steps < 0)
        {
            _log($"[国服周活动·挑战] 游标在目标右侧：左移×{-steps} 回待挑战卡 {target.Index:00} → A", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Left, -steps);
        }
        else
        {
            _log(focused is not null
                ? $"[国服周活动·挑战] 进入卡 {target.Index:00}（游标已在待挑战卡上）"
                : $"[国服周活动·挑战] 进入卡 {target.Index:00}（游标位置未读出，按 A 试）", LogLevel.Info);
        }
        await _engine.Pad.Confirm();
        return 2600;
    }

    /// <summary>B 退介绍页重进清单：游戏会把光标复位到待挑战卡并刷新锁态（通关后解锁有延迟）。
    /// 超过 3 次仍无进展则停止挂机——最后原因是无可挑战卡 = 全部完成（报成功），否则报错误。</summary>
    private async Task<int> RefreshChallengeListAsync(string reason)
    {
        bool noPlayable = reason.Contains("没有可挑战");
        _chRefreshs++;
        _chStaleA = 0;
        _chEdgeProbes = 0;
        if (_chRefreshs > 3)
        {
            if (noPlayable)
            {
                // 本挑战活动已完成：优先退回活动列表切下一个槽位（周顺序常为 挑战赛 → 巡回赛）；
                // 仅当槽位已达上限时才走全部完成收尾。
                ResetChallengeState();
                if (_slotIndex >= MaxEventSlots)
                    return await FinishAllEventsAsync("重进清单仍无可挑战的卡且已达末尾活动槽位：全部活动已完成");
                SetPhase(CwPhase.Retreating);
                _log("[国服周活动·挑战] 本挑战活动已全部通关，退回活动列表继续下一个周活动槽位", LogLevel.Success);
                await _engine.Pad.Back();
                return 2200;
            }
            ResetChallengeState();
            _log("[国服周活动] 挑战清单多次刷新仍无法进入挑战卡，停止挂机（请手动检查活动状态）", LogLevel.Error);
            _engine.StopWithError("挑战清单多次刷新仍无法进卡");
            return 0;
        }
        _chRetreatReenter = true;
        SetPhase(CwPhase.ChallengeRetreat);
        _log($"[国服周活动·挑战] {reason}，B 重进清单刷新（第 {_chRefreshs} 次）", LogLevel.Info);
        await _engine.Pad.Back();
        return 2200;
    }

    /// <summary>挑战退出/重进：B 到介绍页；重进模式在介绍页按 A 回清单（光标复位+锁态刷新）；
    /// 退到活动列表则复位并顺延或重进，不直接判死停机。</summary>
    private async Task<int?> HandleChallengeRetreatAsync(CwScreen cw, MatchScreen ml, Observation obs)
    {
        if (cw == CwScreen.ChallengeList)
        {
            if (_streak < 1) return 800; // 退场防抖，防与 RefreshChallengeListAsync 叠加连按两次 B
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 800; }
            await _engine.Pad.Back();
            return 2000;
        }
        if (cw == CwScreen.EventIntro)
        {
            if (_chRetreatReenter)
            {
                _chRetreatReenter = false;
                _chEdgeProbes = 0;
                SetPhase(CwPhase.ChallengeList);
                _log("[国服周活动·挑战] 重进清单（光标复位到待挑战卡）", LogLevel.Info);
                await _engine.Pad.Confirm(); // 介绍页光标默认在「进入」
                return 2600;
            }
            if (++_attempts <= 4)
            {
                await _engine.Pad.Back();
                return 2200;
            }
            SetPhase(CwPhase.Flow);
            return 800;
        }
        if (cw == CwScreen.EventsList)
        {
            if (_chRetreatReenter)
            {
                _chRetreatReenter = false;
                _chEdgeProbes = 0;
                SetPhase(CwPhase.Flow);
                _log("[国服周活动·挑战] 重进刷新途中已回到活动列表，回 Flow 重新进入当前活动", LogLevel.Warn);
                return 800;
            }
            ResetChallengeState();
            _slotIndex++;
            SetPhase(CwPhase.Flow);
            _log($"[国服周活动·挑战] 已退回活动列表，顺延至槽位 {_slotIndex}", LogLevel.Success);
            return 800;
        }
        if (ml == MatchScreen.HasConfirm)
        {
            // 退出途中的确认弹窗：先 A 清（可能是通关奖励弹窗），多次不消改 B（退出确认类）。
            // ★统一弹窗规则：有 取消/关闭 先上移到推进键再按 A
            if (++_attempts <= 5)
            {
                bool dismissable = obs.Ocr.ContainsJoined("取消") || obs.Ocr.ContainsJoined("关闭");
                _log("[国服周活动·挑战] 退出途中弹窗：A 确认", LogLevel.Info);
                if (dismissable) await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                return 2000;
            }
            await _engine.Pad.Back();
            return 2000;
        }
        if (++_attempts > 8) SetPhase(CwPhase.Flow);
        return 1500;
    }

    /// <summary>挑战子流程状态复位（停止挂机/换活动收尾时调用）。</summary>
    private void ResetChallengeState()
    {
        _challengeMode = false;
        _chLocalDone.Clear();
        _chBlocked.Clear();
        _chCurrentCard = 0;
        _chCurPlays = 0;
        _chStaleA = 0;
        _chRefreshs = 0;
        _chRetreatReenter = false;
        _chRepeatEnters = 0;
        _chMatchSinceEnter = false;
        _chEdgeProbes = 0;
        _chEmptyReads = 0;
        _chLastVisibleCards = "";
    }
}
