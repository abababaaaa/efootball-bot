using System.IO;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· WeeklyEventScenario 状态机
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `WeeklyEventScenario.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

public sealed partial class WeeklyEventScenario : IScenario
{
    // 本文件小活动：挑战活动 —— 清单选卡/卡页/撤退 + 地区·球队选择前置（状态字段也在这里）。

    // ---------------- 球队选择前置流程（挑战活动新子内容） ----------------
    /// <summary>
    /// 「选择地区 / 选择球队」页（用户 2026-11-08 实机规则）：每页只按 <b>一次</b> Y 随机，
    /// 随机点完直接下一步，绝不在同一页来回随机。两页都没有确认键（底栏只有 B 返回 / Y 随机），
    /// 所以随机之后唯一的推进动作就是 A（游戏把随机高亮项当选中项，A 即「下一步」）：
    /// ① 本页还没随机 → Y×1；② 随机刚按完 → 等过渡帧（转场约 1~2s，抢按会打断动画）；
    /// ③ 随机已按过、页面没动 → A 下一步；④ 下一步用满 3 次仍不动 → B 退出该子流程；
    /// B 退出累计 3 次仍在原页 → 停止挂机（退出受阻，交人工看状态）。
    /// 决策逻辑见 <see cref="WeeklyBrain.DecidePickStep"/>（纯函数，单测锁定「只随机一次」）。
    /// </summary>
    private async Task<int> HandleTeamPickAsync(WkScreen wk)
    {
        if (_pickPage != wk)
        {
            _pickPage = wk;
            _pickAttempts = 0;
            _pickRandomPressed = false;
            _pickSamePageFrames = 0;
        }
        string page = wk == WkScreen.RegionSelect ? "地区选择" : "球队选择";
        var step = WeeklyBrain.DecidePickStep(_pickRandomPressed, _pickSamePageFrames, _pickAttempts, _pickEscapes);
        switch (step)
        {
            case WeeklyBrain.WkPickAction.PressRandom:
                _pickRandomPressed = true;
                _pickSamePageFrames = 0;
                _log($"[周活动] {page}页：Y 随机×1（随机只按这一次，按完直接下一步）", LogLevel.Info);
                await _engine.Pad.Y();
                return 2000;
            case WeeklyBrain.WkPickAction.Wait:
                _pickSamePageFrames++;
                return 1400;
            case WeeklyBrain.WkPickAction.PressNext:
                _pickAttempts++;
                _log($"[周活动] {page}页：随机已按过、页面未跳（该页无确认键），A 下一步（第 {_pickAttempts} 次）", LogLevel.Info);
                await _engine.Pad.Confirm();
                return 2000;
            case WeeklyBrain.WkPickAction.Escape:
                _pickEscapes++;
                _pickAttempts = 0;
                _pickSamePageFrames = 0;
                _log($"[周活动] {page}页随机后 A 下一步多次未推进，B 退出该子流程（第 {_pickEscapes} 次，若反复出现请回报日志）", LogLevel.Warn);
                await _engine.Pad.Back();
                return 2500;
            default: // WeeklyBrain.WkPickAction.Stop
                _log($"[周活动] {page}页 B 退出受阻（连续 {_pickEscapes} 次仍在原页），停止挂机（请手动检查活动状态）", LogLevel.Error);
                _engine.StopWithError($"{page}页退出受阻");
                return 0;
        }
    }

    /// <summary>
    /// 球队确认弹窗「要使用&lt;队名&gt;吗？」：纵排 进入(上) / 取消(下)，★默认焦点在「取消」，
    /// 直接 A 会取消回选球队页（用户 2026-11-08 实机）。上×1 → A 进入；连按两次弹窗仍在
    /// （焦点本就落在「进入」的变体）改直接 A；再不行 B 取消回球队页重选，绝不无限按。
    /// </summary>
    private async Task<int> HandleTeamConfirmAsync()
    {
        if (++_teamConfirmAttempts > 3)
        {
            _teamConfirmAttempts = 0;
            _log("[周活动] 球队确认弹窗多次未推进，B 取消后回到球队页重选", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }
        if (_teamConfirmAttempts <= 2)
        {
            _log("[周活动] 球队确认弹窗（默认焦点在「取消」）：上×1 → A 进入", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await _engine.Pad.Confirm();
            return 2600;
        }
        _log("[周活动] 球队确认弹窗仍在：焦点可能已在「进入」，直接 A", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2400;
    }

    // ---------------- 挑战活动子流程 ----------------
    /// <summary>
    /// 挑战解锁弹窗处置（用户 2026-11-08 国际服实机帧「挑战2已解锁 / 通过此全新挑战测试自己的
    /// 技巧! / OK」）：弹窗只有一个 OK（默认焦点），A 即关闭；关闭后清单回到「可打第一张无锁
    /// 无勾卡」的初始排版，所以清完直接按清单阶段重新归位扫描（SetPhase 复位游标/消失计数）。
    /// 连续 6 次 A 仍在=识别误判或弹窗不吃 A：回 Flow 暂停一拍重判，绝不对普通页面无限按 A。
    /// </summary>
    private async Task<int> HandleChallengeUnlockPopupAsync()
    {
        if (_unlockPopupPresses >= MaxUnlockPopupPresses)
        {
            _unlockPopupPresses = 0;
            _log("[周活动·挑战] 解锁弹窗连按 A 未消失，暂停 5s 回屏幕驱动重判（若反复出现请回报日志）", LogLevel.Warn);
            SetPhase(WkPhase.Flow);
            return 5000;
        }
        _unlockPopupPresses++;
        if (!_challengeMode)
        {
            _challengeMode = true;
            _log("[周活动] 检测到挑战解锁弹窗：确认本活动为挑战活动，进入挑战子流程", LogLevel.Success);
        }
        _log($"[周活动·挑战] 「挑战N已解锁」弹窗盖在清单上（第 {_unlockPopupPresses} 次）：A 确认关闭，关闭后重新归位扫描", LogLevel.Info);
        await _engine.Pad.Confirm();
        // 弹窗吃掉期间的方向输入全部无效：清完按清单阶段从头归位（SetPhase 复位扫描游标）
        SetPhase(WkPhase.ChallengeList);
        return 2200;
    }

    /// <summary>进入挑战子流程：激活挑战模式并切到清单扫描阶段（下一轮先饱和左移归位）。
    /// 本槽位已放弃/已完成时绝不重进——实机 2026-10-17 死循环根因：拉黑后退出被介绍页
    /// 弹回、预算耗尽回 Flow 又被这里静默重进，永无止境；改为回撤退流程继续 B，
    /// 反复弹回超限停止挂机。</summary>
    private async Task<int> EnterChallengeListPhaseAsync()
    {
        _hubStep = 0;
        _hubRound = 0;
        if (_doneSlots.Contains(_slotIndex))
        {
            _chRetreatReenter = false;
            _chRetreatBlacklist = true;
            if (++_chStuckBouts > 8)
            {
                _log("[周活动·挑战] 已放弃的挑战活动清单反复弹回，退出受阻，停止挂机（请手动检查活动状态）", LogLevel.Error);
                _engine.StopWithError("挑战清单反复弹回退出受阻");
                return 0;
            }
            SetPhase(WkPhase.ChallengeRetreat);
            _log("[周活动·挑战] 已放弃的挑战活动清单仍在屏上，B 继续退出", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2200;
        }
        if (!_challengeMode)
        {
            _challengeMode = true;
            _log("[周活动] 检测到挑战清单：进入挑战子流程（逐卡挑战，卡难度固定不可选）", LogLevel.Success);
        }
        SetPhase(WkPhase.ChallengeList);
        return 1500;
    }

    /// <summary>
    /// 挑战清单扫描（用户规则：永远打第一张既没锁又没勾的卡；等价于「锁前面那张 /
    /// 无锁时最后一张 / 最后一个勾的下一张」）。归位后逐卡右扫：已通关（绿勾/本运行
    /// 内已通关/已拉黑）→ 右移；待挑战 → A 进入卡详情页；撞锁 = 锁态矛盾 → B 退出
    /// 重进清单刷新（超限放弃本活动）；游标卡号消失按 DecideChallengeVanish 处置——
    /// 全部通关必须「越过已勾卡 + 重扫确证」双重条件，绝不凭一次消失假报全完成
    /// （实机 2026-10-17：A 进卡后详情页被卡号兜底误判成清单，假全完成跳过整个活动）。
    /// 5 张以上需要翻页：右移会自动滚动列表，卡号 OCR 跟踪当前卡位（左/右卡位固定，
    /// 卡号随翻页变化）。已通关判定先于锁判定：刚通关的卡渲染初期可能同时命中灰锁
    /// 探针（实机 2026-10-17，重启游戏后才显绿勾），已勾卡定义上必已解锁。
    /// </summary>
    private async Task<int> HandleChallengeListAsync(WkScreen wk, MatchScreen ml, Observation obs)
    {
        if (wk == WkScreen.EventHome) { _chReentry = 0; SetPhase(WkPhase.AtHome); return 600; }
        if (wk == WkScreen.EventSettings) { SetPhase(WkPhase.Settings); return 800; }
        if (wk == WkScreen.EventsList) { _challengeMode = false; SetPhase(WkPhase.Flow); return 800; }
        if (wk == WkScreen.EntryPopup || ml == MatchScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(WkPhase.Flow); return 600; }
            return await PressConfirmAsync("挑战清单弹窗：确认");
        }
        if (wk != WkScreen.ChallengeList)
        {
            if (++_attempts > 10) { SetPhase(WkPhase.Flow); return 600; } // 过场/未知帧超预算：回屏幕驱动自愈
            return 1200;
        }
        _unlockPopupPresses = 0; // 清单干净可见：解锁弹窗已关，连按计数归零
        if (!_chScanReady)
        {
            _chScanReady = true;
            _chScan = 1;
            _chMissing = 0;
            _chCursorDone = false;
            _log("[周活动·挑战] 挑战清单：饱和左移归位到第 1 张卡", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Left, 6);
            return 1500;
        }
        var cur = WeeklyBrain.ScanChallengeCards(obs.Ocr, obs.Frame)
            .FirstOrDefault(c => c.Index == _chScan);
        if (cur is null)
        {
            // 卡详情页误判防线（实机 2026-10-17 反馈）：详情页的大字数字+底栏「返回」
            // 会满足清单卡号兜底特征，且详情页上找不到当前卡号——带「完成的条件」
            // 一律按卡详情页处理（AtHome 的挑战分支认该词，照常设置/开赛）
            if (WeeklyBrain.IsChallengeCardHome(obs.Ocr))
            {
                _chReentry = 0;
                SetPhase(WkPhase.AtHome);
                return 900;
            }
            var verdict = WeeklyBrain.DecideChallengeVanish(++_chMissing, _chCursorDone, _chVanishRescans);
            switch (verdict)
            {
                case WeeklyBrain.ChVanishDecision.WaitFrame:
                    return 1000; // 翻页/进入动画丢帧，两帧确认
                case WeeklyBrain.ChVanishDecision.ConfirmTail:
                    // 全部挑战已完成 → 拉黑槽位当场生效（issue #1 实机 2026-10-30：不拉黑时
                    // 退出受阻回 Flow，介绍页又按 A 重进同一活动 → 完成→退出→重进无限循环）
                    _doneSlots.Add(_slotIndex);
                    _chStuckBouts = 0;
                    _log($"[周活动·挑战] 卡 {_chScan:00} 之后没有更多卡（重扫确证）：全部挑战已完成，拉黑槽位 {_slotIndex}，B 退出挑战活动", LogLevel.Success);
                    _chRetreatReenter = false;
                    _chRetreatBlacklist = false;
                    SetPhase(WkPhase.ChallengeRetreat);
                    await _engine.Pad.Back();
                    return 2200;
                case WeeklyBrain.ChVanishDecision.Abort:
                    _chRetreatBlacklist = true;
                    _chRetreatReenter = false;
                    _doneSlots.Add(_slotIndex); // 拉黑当场生效，不依赖退出能否抵达列表
                    _chStuckBouts = 0;
                    _log($"[周活动·挑战] 卡 {_chScan:00} 反复消失且无法确证清单状态，放弃本活动（拉黑槽位）", LogLevel.Error);
                    SetPhase(WkPhase.ChallengeRetreat);
                    await _engine.Pad.Back();
                    return 2200;
                default: // Rescan：非越勾后消失（刚对无勾卡按过 A = 进详情过渡/丢帧）
                         // 或首次表尾特征——重新归位从头扫一遍确证
                    _chMissing = 0;
                    _chVanishRescans++;
                    _log($"[周活动·挑战] 卡 {_chScan:00} 不可见：重新归位扫描确证（第 {_chVanishRescans} 次）", LogLevel.Info);
                    _chScanReady = false;
                    return 1200;
            }
        }
        _chMissing = 0;
        // 已通关判定先于锁判定：勾/本运行内通关/已拉黑 → 右移
        if (cur.Checked || _chLocalDone.Contains(cur.Index) || _chBlocked.Contains(cur.Index))
        {
            bool blockedOnly = _chBlocked.Contains(cur.Index) && !cur.Checked && !_chLocalDone.Contains(cur.Index);
            _chCursorDone = !blockedOnly; // 拉黑未通关的卡不算「越过已勾卡」
            string why = cur.Checked ? "右上角绿勾"
                : _chLocalDone.Contains(cur.Index) ? "本运行内已通关" : "多次进入未生效已拉黑";
            _chScan++;
            _log($"[周活动·挑战] 卡 {cur.Index:00} 已完成（{why}），右移下一张", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Right);
            return 1100;
        }
        if (cur.Locked)
        {
            // 撞锁（游标越过已通关卡/首卡锁定）：游戏解锁可能有延迟（通关即打勾、
            // 重进清单才解锁；刚通关的卡渲染初期可能只呈灰态），B 退介绍页重进刷新；
            // 重进仍锁 = 真矛盾（活动过期/进度异常），放弃本活动
            _chContradictions++;
            _chRetreatBlacklist = _chContradictions > 2;
            string lockWhy = _chScan <= 1
                ? "首张卡显示锁定（活动未开放或进度未同步）"
                : $"卡 {cur.Index:00} 显示锁定但前置卡应已通关";
            _log(lockWhy +
                 (_chRetreatBlacklist ? "，重进未解锁，放弃本活动（拉黑槽位）" : $"，B 重进清单刷新锁态（第 {_chContradictions} 次）"),
                _chRetreatBlacklist ? LogLevel.Error : LogLevel.Warn);
            _chRetreatReenter = !_chRetreatBlacklist;
            if (_chRetreatBlacklist)
            {
                _doneSlots.Add(_slotIndex); // 拉黑当场生效，不依赖退出能否抵达列表
                _chStuckBouts = 0;
            }
            SetPhase(WkPhase.ChallengeRetreat);
            await _engine.Pad.Back();
            return 2200;
        }
        // 待挑战卡：按 A 进入（重复进入同一卡计数，超 2 次拉黑防死循环）
        _chCursorDone = false; // 游标停在待挑战卡上：此后的卡号消失不得判全完成
        if (_chCurrentCard == cur.Index)
        {
            if (++_chReentry > 2)
            {
                _chBlocked.Add(cur.Index);
                _chScan++;
                _chCursorDone = false; // 拉黑未通关的卡不算「越过已勾卡」
                _log($"[周活动·挑战] 卡 {cur.Index:00} 连续进入未生效，拉黑跳过", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Right);
                return 1100;
            }
        }
        else
        {
            _chCurrentCard = cur.Index;
            _chReentry = 1;
        }
        _log($"[周活动·挑战] 进入卡 {cur.Index:00}", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2600;
    }

    /// <summary>挑战卡详情页（活动主页样式）：已通关（胜场读满/主要奖励完成）→ 记忆并
    /// B 回清单换下一张；未通关 → X 开设置核验（无难度，只看自动控制/弹性）后开赛。</summary>
    private async Task<int> HandleChallengeCardHomeAsync(WkScreen wk, Observation obs)
    {
        _challengeMode = true; // 半途启动直接落在卡详情页（「完成的条件」特征）也激活挑战模式
        // 详情页可能被清单卡号兜底误判成 ChallengeList（实机 2026-10-17）：带「完成的条件」
        // 一律按卡详情页处理，不被屏幕分类劫持
        bool onHome = wk == WkScreen.EventHome || WeeklyBrain.IsChallengeCardHome(obs.Ocr);
        if (wk == WkScreen.ChallengeList && !onHome) { SetPhase(WkPhase.ChallengeList); return 800; }
        if (wk == WkScreen.EventSettings) { SetPhase(WkPhase.Settings); return 800; }
        if (!onHome)
        {
            SetPhase(WkPhase.Flow); // 弹窗/过场：回屏幕驱动推进，清完再回主页判断
            return 600;
        }
        if (WeeklyBrain.IsMainRewardDone(obs.Ocr, obs.Frame) || WeeklyBrain.ChallengeProgressDone(obs.Ocr))
        {
            if (_chCurrentCard > 0) _chLocalDone.Add(_chCurrentCard);
            _log($"[周活动·挑战] 卡 {_chCurrentCard:00} 已通关，B 返回挑战清单换下一张", LogLevel.Success);
            SetPhase(WkPhase.ChallengeList);
            await _engine.Pad.Back();
            return 2200;
        }
        SetPhase(WkPhase.Settings);
        await _engine.Pad.X();
        _log("[周活动·挑战] 按 X 打开活动设置（挑战卡无难度，只核验自动控制/弹性）", LogLevel.Info);
        return 1800;
    }

    /// <summary>挑战活动设置浮层（2 行：自动控制/弹性自动控制，光标默认在首行）：
    /// 自动控制强制开（归位直接 A）、弹性强制关（归位下移 1 次 A），就绪 B 返回。
    /// 挑战卡没有赛事等级行——难度由卡片固定，绝不能沿用普通浮层的下×2/下×3 计步。</summary>
    private async Task<int> HandleChallengeSettingsAsync(WkActivitySettings s)
    {
        var unread = new List<string>();
        if (s.AutoControl is null) unread.Add("自动控制");
        if (s.FlexibleAutoControl is null) unread.Add("弹性自动控制");
        if (unread.Count > 0)
            _log($"[周活动·挑战] 设置项未读出（{string.Join("、", unread)}），保守跳过不按键", LogLevel.Warn);
        if (++_attempts > 6)
        {
            _log("[周活动·挑战] 设置核验多轮未收敛，B 返回直接开赛", LogLevel.Warn);
            SetPhase(WkPhase.LaunchReady);
            await _engine.Pad.Back();
            return 2000;
        }
        if (s.AutoControl == false)
        {
            _log("[周活动·挑战] 自动控制为关：上×4 归位 → A 改为开（强制项）", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Up, 4);
            await _engine.Pad.Confirm();
            return 2000;
        }
        if (s.FlexibleAutoControl == true)
        {
            _log("[周活动·挑战] 弹性自动控制为开：上×4 归位 → 下×1 → A 改为关（强制项）", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Up, 4);
            await _engine.Pad.Dpad(PadDir.Down);
            await _engine.Pad.Confirm();
            return 2000;
        }
        _log("[周活动·挑战] 设置核验完成，B 返回卡详情页开赛", LogLevel.Success);
        SetPhase(WkPhase.LaunchReady);
        await _engine.Pad.Back();
        return 1800;
    }

    /// <summary>挑战退出/重进：B 到介绍页；重进模式在介绍页按 A 回清单刷新锁态，
    /// 否则继续 B 回活动列表（到达后复位挑战模式、按需拉黑槽位并顺延到下一个活动）。</summary>
    private async Task<int> HandleChallengeRetreatAsync(WkScreen wk, MatchScreen ml)
    {
        if (wk == WkScreen.EventsList)
        {
            if (_chRetreatBlacklist) _doneSlots.Add(_slotIndex);
            _slotIndex++;
            _challengeMode = false;
            _chCurrentCard = 0;
            _chReentry = 0;
            _chContradictions = 0;
            _chStuckBouts = 0;
            _chLocalDone.Clear();
            _chBlocked.Clear();
            _log($"[周活动·挑战] 已退出挑战活动，改刷槽位 {_slotIndex}", LogLevel.Success);
            SetPhase(WkPhase.Flow);
            return 800;
        }
        if (wk == WkScreen.ChallengeList)
        {
            if (++_attempts > 6) { SetPhase(WkPhase.Flow); return 800; }
            await _engine.Pad.Back();
            return 2000;
        }
        if (wk == WkScreen.EventIntro)
        {
            if (_chRetreatReenter)
            {
                _chRetreatReenter = false;
                SetPhase(WkPhase.ChallengeList);
                await _engine.Pad.Confirm(); // 介绍页光标默认在「进入」：重进清单
                return 2600;
            }
            if (++_attempts <= 4)
            {
                await _engine.Pad.Back();
                return 2200;
            }
            SetPhase(WkPhase.Flow);
            return 800;
        }
        if (wk == WkScreen.EntryPopup || ml == MatchScreen.HasConfirm)
        {
            // 退出途中的弹窗：先 A 清（可能是通关奖励弹窗），多次不消改 B（退出确认类）
            if (++_attempts <= 5) return await PressConfirmAsync("挑战退出途中弹窗：确认");
            await _engine.Pad.Back();
            return 2000;
        }
        if (++_attempts > 8) SetPhase(WkPhase.Flow);
        return 1500;
    }

    /// <summary>
    /// 阵容挑战入场弹窗「球员卡限制 / OK」（2026-11-19 用户国际服实机）：只有一个 OK 键，
    /// 盖在阵容挑战主页上，固定副标题「在之前胜场中使用的球员卡,将无法在后续比赛中使用。」。
    /// 单键弹窗 → 直接 A（光标默认在 OK，不需要任何方向键）。连续
    /// <see cref="MaxSquadPopupPresses"/> 次仍判命中 = 识别误判或弹窗不吃 A：回 Flow 暂停一拍
    /// 重判。★绝不对同一页无限按 A——主页上的 A 就是「前往比赛」，会提前开赛。
    /// </summary>
    private async Task<int> HandlePlayerCardRestrictAsync()
    {
        if (_squadPopupPresses >= MaxSquadPopupPresses)
        {
            _squadPopupPresses = 0;
            _log("[周活动·阵容挑战] 「球员卡限制」弹窗连按 A 未消失，暂停 5s 回屏幕驱动重判（若反复出现请回报日志）", LogLevel.Warn);
            SetPhase(WkPhase.Flow);
            return 5000;
        }
        _squadPopupPresses++;
        _attempts = 0; // 已处理入场弹窗：清零当前阶段超时计数，防止关弹窗后加载过渡瞬撞上限 B 退回列表
        _log($"[周活动·阵容挑战] 入场弹窗「球员卡限制」（第 {_squadPopupPresses} 次）：A 确认关闭", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2200;
    }

    /// <summary>「选择地区/选择球队」页随机后已按过的「下一步」（A）次数（换页清零；
    /// 用满 WeeklyBrain.MaxPickNextAttempts 就 B 退出该子流程）。</summary>
    private int _pickAttempts;

    /// <summary>当前选择页是否已经按过 Y 随机（用户 2026-11-08 规则：每页随机只按一次，
    /// 之后直接下一步；只有换页才重置，绝不在同一页来回随机）。</summary>
    private bool _pickRandomPressed;

    /// <summary>按完随机后同一页仍在屏上的帧数（满 WeeklyBrain.PickConfirmGraceFrames
    /// 才认随机没把页面带走，等转场动画走完再按下一步）。</summary>
    private int _pickSamePageFrames;

    /// <summary>上一次按 Y 随机时所在的页面（换页清零 _pickAttempts/_pickRandomPressed）。</summary>
    private WkScreen _pickPage = WkScreen.None;

    /// <summary>球队确认弹窗「上×1 → A」的连续无进展次数（前两次上移确认，第三次直接 A，
    /// 再不行 B 取消回球队页，绝不无限按）。</summary>
    private int _teamConfirmAttempts;

    /// <summary>选择页按 B 仍退不出去（下一帧还在同一页）的次数；超 3 次停止挂机（退出受阻）。</summary>
    private int _pickEscapes;

    // ---- 挑战活动子流程（挑战清单弹窗出现时激活；退回活动列表时复位） ----
    /// <summary>当前活动是挑战活动（以「挑战清单」弹窗或卡详情页「完成的条件」识别）。</summary>
    private bool _challengeMode;

    /// <summary>清单扫描游标：下一张待判定的卡号（1 起；饱和左移归位后从 1 向右扫）。</summary>
    private int _chScan = 1;

    /// <summary>清单扫描是否已归位（进阶段后先饱和左移到卡 1）。</summary>
    private bool _chScanReady;

    /// <summary>游标卡号连续未见于 OCR 的帧数（≥2 判定扫过表尾 = 全部通关）。</summary>
    private int _chMissing;

    /// <summary>本运行内已确认通关的卡号（进卡页读到 胜场 N/N 记录；绿勾探针未校准的兜底）。</summary>
    private readonly HashSet<int> _chLocalDone = new();
    /// <summary>进卡多次未生效的卡号（游戏拒绝进入/锁误判，跳过防死循环）。</summary>
    private readonly HashSet<int> _chBlocked = new();
    /// <summary>最近一次按 A 进入的卡号（重复进入同一卡计数用）。</summary>
    private int _chCurrentCard;

    /// <summary>对同一张卡连续按 A 进入的次数（超 2 拉黑该卡）。</summary>
    private int _chReentry;

    /// <summary>锁态矛盾（游标越过已通关卡却撞锁）次数；超 2 放弃本活动拉黑槽位。</summary>
    private int _chContradictions;

    /// <summary>ChallengeRetreat 子模式：B 退到介绍页后按 A 重进清单（刷新锁态用）。</summary>
    private bool _chRetreatReenter;

    /// <summary>ChallengeRetreat 子模式：放弃本活动（返回列表后拉黑槽位再换下一个）。</summary>
    private bool _chRetreatBlacklist;

    /// <summary>清单扫描游标最后一次可见状态是已通关/已跳过（卡号消失判「全完成」的门槛：
    /// 只有越过已通关卡后的消失才可能是表尾——刚对无勾卡按过 A 的消失是进详情过渡，实机 2026-10-17）。</summary>
    private bool _chCursorDone;

    /// <summary>游标卡号消失后从头重扫的次数（重扫确证表尾；超限放弃防死循环）。</summary>
    private int _chVanishRescans;

    /// <summary>已放弃的挑战活动清单反复弹回的轮数（介绍页 B 无效时防无限弹循环，超限停止挂机）。</summary>
    private int _chStuckBouts;

    /// <summary>「挑战N已解锁」弹窗连续按 A 的累计次数（弹窗消失即清零；超限回屏幕驱动重判，
    /// 防 OCR 误判时对着普通页面无限按 A）。</summary>
    private int _unlockPopupPresses;

    /// <summary>同一枚解锁弹窗最多连按几次 A；超限认为弹窗不是 A 能关的（或识别误判），
    /// 回 Flow 让屏幕驱动重判并暂停一拍。</summary>
    private const int MaxUnlockPopupPresses = 6;
}
