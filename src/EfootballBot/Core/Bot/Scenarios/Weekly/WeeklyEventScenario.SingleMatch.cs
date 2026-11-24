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
    // 本文件小活动：单场周活动 —— 活动设置核验、赛事等级下拉、选择赛事等级页（issue #1）。

    /// <summary>活动设置浮层：逐项核验修正。每轮最多一个动作（改完下一轮重新解析再验下一项），
    /// 全部就绪或读不到（保守不按）时 B 返回。改选等级超预算则带 Warn 放弃，绝不卡死。</summary>
    private async Task<int> HandleSettingsAsync(WkScreen wk, MatchScreen ml, Observation obs)
    {
        if (wk == WkScreen.EventSettings && WeeklyBrain.IsEventSettingsDropdown(obs.Ocr))
        {
            SetPhase(WkPhase.LevelDropdown);
            return await HandleSettingsDropdownAsync(obs.Frame, obs.Ocr);
        }
        if (wk == WkScreen.EventSettings)
        {
            var cfg = _engine.Config.Weekly;
            var s = WeeklyBrain.ParseActivitySettings(obs.Ocr);
            // 挑战活动设置浮层：没有赛事等级行（难度由卡片固定），只核验自动控制/弹性。
            // 2 行布局、光标默认在「自动控制」行——导航计步与普通浮层（3 行、默认在等级行）不同。
            if (_challengeMode || obs.Ocr.ContainsJoined("完成的条件"))
                return await HandleChallengeSettingsAsync(s);
            var unread = new List<string>();
            if (s.Level is null) unread.Add("赛事等级");
            if (s.AutoControl is null) unread.Add("自动控制");
            if (s.FlexibleAutoControl is null) unread.Add("弹性自动控制");
            if (unread.Count > 0)
                _log($"[周活动] 设置项未读出（{string.Join("、", unread)}），保守跳过不按键", LogLevel.Warn);
            if (++_attempts > 6)
            {
                _log("[周活动] 设置核验多轮未收敛，B 返回直接开赛", LogLevel.Warn);
                SetPhase(WkPhase.LaunchReady);
                await _engine.Pad.Back();
                return 2000;
            }
            bool levelUnmet = false;
            // 1) 赛事等级（光标默认在等级行：饱和上移归位后直接 A）
            if (s.Level is not null && s.Level != cfg.EventLevel)
            {
                if (_levelAttempts < 2)
                {
                    _levelAttempts++;
                    _log($"[周活动] 赛事等级 {s.Level} → {cfg.EventLevel}：展开下拉（第 {_levelAttempts} 次）", LogLevel.Info);
                    SetPhase(WkPhase.LevelDropdown);
                    await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                    await _engine.Pad.Confirm();
                    return 2200;
                }
                levelUnmet = true;
                // 措辞写清楚是「设置未成功」——旧版曾误写成「设置成功」，让人以为按配置生效了；
                // 把目标值/当前值/后果都写明，避免误解为「已按传奇开赛」。
                _log($"[周活动] 赛事等级设置未成功：想要「{cfg.EventLevel}」但游戏仍为「{s.Level}」，本次按当前等级开赛（非配置等级）", LogLevel.Warn);
            }
            else if (s.Level == cfg.EventLevel)
            {
                _levelAttempts = 0;
            }
            // 2) 自动控制强制开：归位后下移 2 次
            if (s.AutoControl == false)
            {
                _log("[周活动] 自动控制为关：上×4 归位 → 下×2 → A 改为开（强制项）", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                await _engine.Pad.DpadRepeat(PadDir.Down, 2);
                await _engine.Pad.Confirm();
                return 2000;
            }
            // 3) 弹性自动控制强制关：归位后下移 3 次
            if (s.FlexibleAutoControl == true)
            {
                _log("[周活动] 弹性自动控制为开：上×4 归位 → 下×3 → A 改为关（强制项）", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                await _engine.Pad.DpadRepeat(PadDir.Down, 3);
                await _engine.Pad.Confirm();
                return 2000;
            }
            if (levelUnmet)
                _log($"[周活动] 设置核验结束（赛事等级未达成「{cfg.EventLevel}」，按当前「{s.Level}」开赛），B 返回活动主页", LogLevel.Warn);
            else
                _log("[周活动] 设置核验完成，B 返回活动主页", LogLevel.Success);
            SetPhase(WkPhase.LaunchReady);
            await _engine.Pad.Back();
            return 1800;
        }
        if (wk == WkScreen.EventHome)
        {
            if (++_attempts <= 2)
            {
                _log("[周活动] 活动设置未打开，重按 X", LogLevel.Warn);
                await _engine.Pad.X();
                return 1800;
            }
            _log("[周活动] 活动设置打不开，跳过核验直接开赛", LogLevel.Warn);
            SetPhase(WkPhase.LaunchReady);
            return 600;
        }
        if (wk == WkScreen.EntryPopup)
            return await PressConfirmAsync("设置层弹窗：确认"); // 盖在设置层上的弹窗先清掉
        if (ml == MatchScreen.HasConfirm)
        {
            SetPhase(WkPhase.IntroPopups); // 设置层上叠了弹窗：交给入场弹窗推进
            return await PressConfirmAsync("设置层弹窗：确认");
        }
        if (++_attempts > 4) { SetPhase(WkPhase.Flow); return 600; }
        return 1200;
    }

    /// <summary>
    /// 活动设置「赛事等级」实机下拉列表闭环导航（2026-11-24 实机截图校准）：
    /// 展开后右半屏保留「活动设置」标题与 7 行下拉项，白底高光为当前焦点行、右侧蓝勾为当前选中项，
    /// 底部无独立确认键，方向键移动焦点至目标项后直接按 A 即选中并收起下拉。
    /// 先校验白底焦点（回退蓝勾），未对齐先按方向键闭环移位并留下一帧视觉复核，确认焦点落在目标项才按 A。
    /// </summary>
    private async Task<int> HandleSettingsDropdownAsync(Capture.FrameData frame, OcrResult ocr)
    {
        string wanted = _engine.Config.Weekly.EventLevel;
        string? focused = WeeklyBrain.DetectDropdownFocusedLevel(frame, ocr);
        string? checkedLevel = WeeklyBrain.DetectDropdownCheckedLevel(frame, ocr);
        string? current = focused ?? checkedLevel;
        if (current is null || !MatchBrain.Difficulties.Contains(wanted))
        {
            _log($"[周活动] 赛事等级下拉当前项未读出（焦点「{focused ?? "null"}」/勾选「{checkedLevel ?? "null"}」），B 收起", LogLevel.Warn);
            SetPhase(WkPhase.Settings);
            await _engine.Pad.Back();
            return 1800;
        }
        if (current == wanted)
        {
            _log($"[周活动] 赛事等级下拉：焦点已在「{wanted}」，A 确认选中", LogLevel.Success);
            SetPhase(WkPhase.Settings);
            await _engine.Pad.Confirm();
            return 1800;
        }
        if (++_dropdownMoves > 6)
        {
            _log($"[周活动] 赛事等级下拉多次移位未对齐「{wanted}」（当前「{current}」），B 收起", LogLevel.Warn);
            SetPhase(WkPhase.Settings);
            await _engine.Pad.Back();
            return 1800;
        }
        int delta = WeeklyBrain.ComputeDropdownStepDelta(ocr, current, wanted);
        if (delta == 0)
        {
            _log($"[周活动] 赛事等级下拉：步距为 0（当前「{current}」），A 确认", LogLevel.Info);
            SetPhase(WkPhase.Settings);
            await _engine.Pad.Confirm();
            return 1800;
        }
        _attempts = 0; // 移位期间允许下一帧复核焦点，不触发未识别超时
        var dir = delta > 0 ? PadDir.Down : PadDir.Up;
        int steps = Math.Abs(delta);
        string dirName = delta > 0 ? "下" : "上";
        _log($"[周活动] 赛事等级下拉：{current} → {wanted}（{dirName}×{steps}，下帧复核焦点）", LogLevel.Info);
        await _engine.Pad.DpadRepeat(dir, steps);
        return 900;
    }

    /// <summary>赛事等级下拉选项（复用我的联赛难度页检测：蓝勾=当前所选，N+1-k 公式改选）。
    /// 下拉真实样式待实机校准；识别不出时由 LevelDropdown 的 B 收起兜底，绝不乱选。</summary>
    private async Task<int> HandleLevelOptionsAsync(Capture.FrameData frame, OcrResult ocr)
    {
        string wanted = _engine.Config.Weekly.EventLevel;
        var options = MyLeagueBrain.DetectDifficultyOptions(ocr);
        // ★优先用活动设置浮层专用蓝勾探针（实机 2026-11-23 下拉帧：勾在面板右缘 x≈0.92，
        //   旧 DetectSelectedDifficulty 只扫词 ±0.06 窗口 → null → 直接确认 → 改选从未生效）。
        //   旧探针作兜底兼容其它布局。
        var selected = WeeklyBrain.DetectDropdownCheckedLevel(frame, ocr)
                       ?? MyLeagueBrain.DetectSelectedDifficulty(frame, ocr);
        SetPhase(WkPhase.Settings); // 选完（无论是否改选）都回浮层重新核验
        // 所选不在列表 / 蓝勾未检出：按游戏默认直接确认（同我的联赛守则，绝不乱按方向）
        if (!options.Contains(wanted) || selected is null)
        {
            _log($"[周活动] 赛事等级下拉：所选「{selected ?? "未检出"}」，直接确认", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2500;
        }
        if (selected == wanted)
        {
            _log($"[周活动] 赛事等级已是「{wanted}」，直接确认", LogLevel.Success);
            await _engine.Pad.Confirm();
            return 2500;
        }
        int n = options.Count, k = options.IndexOf(wanted) + 1, steps = n + 1 - k;
        _log($"[周活动] 赛事等级 {selected} → {wanted}（{n} 项第 {k} 行）：上×{steps} 勾选 → 下×{steps} 右×1 确认", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, steps);
        await _engine.Pad.Confirm();
        await _engine.Pad.DpadRepeat(PadDir.Down, steps);
        await _engine.Pad.Dpad(PadDir.Right);
        await _engine.Pad.Confirm();
        return 3500;
    }

    /// <summary>
    /// 单场周活动「选择赛事等级」整页导航（2026-10-30 用户三张实机帧校准）：
    /// 页面 = 左列难度行（每屏 6 行、行距≈0.11，传奇需下滚）+ 右栏奖励，底部只有 B 返回；
    /// 进入时先弹「请选择赛事等级 / 查看提供的奖励并选择赛事等级。/ OK」说明弹窗。
    /// 决策每轮最多一个动作，屏幕驱动：① 屏中部 OK 弹窗 → A；② 目标行（配置的赛事等级）
    /// 不在屏 → 下×1 滚动查找；③ 焦点行（白底高亮探针 DetectFocusedDifficulty）≠ 目标 →
    /// 按行距差值上/下移；④ 焦点=目标 → A 确认。多轮未收敛按当前焦点 A 兜底继续开赛。
    /// </summary>
    private async Task<int> HandleEventDifficultyAsync(Observation obs)
    {
        var ocr = obs.Ocr;
        string wanted = _engine.Config.Weekly.EventLevel;
        // ① 入场说明弹窗（OK 在屏中部；底栏 B 返回的蓝框会被 YOLO 判 btn_confirm @y≈0.93，
        //    限定 y<0.85 与弹窗门控同款防误吃）
        var ok = ocr.FindButton("OK");
        if (ok is not null && ok.CenterY < 0.85)
            return await PressConfirmAsync("选择赛事等级·说明弹窗 OK");
        // ①' 最终确认弹窗（「是否要选择传奇？」+ 进入/取消，★默认焦点在取消）：
        //     直接 A 会取消回列表，必须 ↑ 移到「进入」再 A（用户 2026-10-30 实机反馈）
        if (WeeklyBrain.IsDifficultyConfirmPopup(ocr))
        {
            _diffAttempts = 0;
            _log("[周活动] 难度最终确认弹窗（默认焦点在取消）：上×1 → A 进入", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await _engine.Pad.Confirm();
            return 2600;
        }
        string? focus = WeeklyBrain.DetectFocusedDifficulty(obs.Frame, ocr);
        var target = ocr.Words.FirstOrDefault(w =>
            w.CenterX < 0.25 && w.Text.Replace(" ", "").Contains(wanted));
        // ④ 焦点已在目标行：A 确认（A 落在高亮行=选定该等级，后续弹窗/开赛由屏幕分类接管）
        if (focus == wanted && target is not null)
        {
            _diffAttempts = 0;
            _log($"[周活动] 赛事等级已选「{wanted}」，A 确认", LogLevel.Success);
            await _engine.Pad.Confirm();
            return 2400;
        }
        if (++_diffAttempts > 12)
        {
            _diffAttempts = 0;
            _log($"[周活动] 选择赛事等级多轮未收敛（焦点「{focus ?? "未检出"}」/目标「{wanted}」），按当前焦点 A 兜底继续", LogLevel.Warn);
            await _engine.Pad.Confirm();
            return 2400;
        }
        // ② 目标不在屏（传奇在列表底，需滚动）或焦点探针失效（弹窗遮挡/OCR 退化）：下×1 试探
        if (target is null || focus is null)
        {
            _log($"[周活动] 选择赛事等级：目标「{wanted}」{(target is null ? "不在屏，下×1 滚动查找" : "在屏")}，焦点「{focus ?? "未检出"}」下×1", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Down);
            return 1200;
        }
        // ③ 焦点与目标都在屏：按行距差值（0.11/行，实机帧校准）一次移到位
        var focusWord = ocr.Words.First(w =>
            w.CenterX < 0.25 && w.Text.Replace(" ", "").Contains(focus));
        int steps = Math.Clamp((int)Math.Round(Math.Abs(target.CenterY - focusWord.CenterY) / 0.11), 1, 6);
        var dir = target.CenterY > focusWord.CenterY ? PadDir.Down : PadDir.Up;
        string dirName = dir == PadDir.Down ? "下" : "上";
        _log($"[周活动] 选择赛事等级：{focus} → {wanted}（{dirName}×{steps}）", LogLevel.Info);
        await _engine.Pad.DpadRepeat(dir, steps);
        return 1400;
    }

    /// <summary>单场周活动「选择赛事等级」页导航累计动作数（超限按当前焦点 A 兜底继续，
    /// 绝不停在难度页干等——issue #1 实机：旧逻辑在此页零动作原地卡死）。</summary>
    private int _diffAttempts;

    /// <summary>赛事等级改选尝试次数（超 2 次放弃改选，继续用游戏当前值）。</summary>
    private int _levelAttempts;

    /// <summary>活动设置下拉列表单次改选的闭环移位次数（防探针异常时来回抖动）。</summary>
    private int _dropdownMoves;
}
