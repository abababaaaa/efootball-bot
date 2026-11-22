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
    // 本文件小活动：阵容挑战 —— 「比赛计划失效」修复（PlanStep 状态机与 _plan* 字段）。

    /// <summary>比赛计划失效修复的子步骤（<see cref="WkPhase.MatchPlanSetup"/> 内部，屏幕驱动）。</summary>
    private enum PlanStep
    {
        /// <summary>该按 Y 打开比赛计划（或刚从「无法进行比赛」弹窗回来，重新确认红点）。</summary>
        Open,
        /// <summary>已按 Y，等比赛计划页/弹窗出现（弹窗先按弹窗规则清掉）。</summary>
        Waiting,
        /// <summary>阵容页：左移归位侧边栏 → 下移到「自动配置球员」→ A 打开选项。</summary>
        Lineup,
        /// <summary>选项弹窗：下×1 选中「重视能力值」→ A 下一步；确认后 B 完成退出。</summary>
        Options,
    }

    /// <summary>
    /// 赛后「无法进行比赛」弹窗（用户 2026-11-22 规则：点 A 继续）：单枚 OK → 直接 A，
    /// 不按任何方向键。关掉后转 MatchPlanSetup 重新确认主页红点——红点还在就 Y 修阵容，
    /// 不在就直接开下一场。连按 <see cref="MaxPlanInvalidPresses"/> 次仍判命中 = 识别误判或
    /// 弹窗不吃 A：回 Flow 暂停一拍重判，绝不对主页无限按 A（主页上的 A 就是「前往比赛」）。
    /// </summary>
    private async Task<int> HandleMatchPlanInvalidAsync()
    {
        if (_planInvalidPresses >= MaxPlanInvalidPresses)
        {
            _planInvalidPresses = 0;
            _log("[周活动·阵容挑战] 「无法进行比赛」弹窗连按 A 未消失，暂停 5s 回屏幕驱动重判（若反复出现请回报日志）", LogLevel.Warn);
            SetPhase(WkPhase.Flow);
            return 5000;
        }
        _planInvalidPresses++;
        _log($"[周活动·阵容挑战] 赛后弹窗「无法进行比赛」（第 {_planInvalidPresses} 次）：A 继续，随后按红点检查比赛计划", LogLevel.Info);
        await _engine.Pad.Confirm();
        SetPhase(WkPhase.MatchPlanSetup);
        _planStep = PlanStep.Waiting; // 弹窗关掉后先看主页红点，不要重复按 Y
        return 2200;
    }

    /// <summary>
    /// 主页「比赛计划」红色叹号 → Y 打开比赛计划修阵容（用户 2026-11-22 规则）。
    /// 同一处红点最多修 <see cref="MaxPlanFixAttempts"/> 次：比赛计划里有失效球员卡时游戏恒定
    /// 拒绝开赛，修不好继续跑只会空转，超限停止挂机交人工。
    /// </summary>
    private async Task<int> BeginMatchPlanFixAsync()
    {
        if (_planFixAttempts >= MaxPlanFixAttempts)
        {
            _log($"[周活动·阵容挑战] 比赛计划红色叹号连续 {MaxPlanFixAttempts} 次未修好，停止挂机（请手动检查比赛计划）", LogLevel.Error);
            _engine.StopWithError("比赛计划失效修复失败");
            return 0;
        }
        _planFixAttempts++;
        _log($"[周活动·阵容挑战] 底栏「比赛计划」有红色叹号（第 {_planFixAttempts} 次修复）：Y 打开比赛计划", LogLevel.Info);
        SetPhase(WkPhase.MatchPlanSetup);
        _planStep = PlanStep.Waiting; // Y 已按：等比赛计划页/弹窗
        await _engine.Pad.Y();
        return 2600;
    }

    /// <summary>
    /// 比赛计划修复总调度：弹窗优先用共享规则清掉，其余按 <see cref="PlanStep"/> 分派。
    /// 全过程屏幕驱动，任一帧都可由下一轮重新分类接管（半途启动也能接上）。
    /// </summary>
    private async Task<int> HandleMatchPlanSetupAsync(WkScreen wk, MatchScreen ml, Observation obs)
    {
        if (wk == WkScreen.EntryPopup || ml == MatchScreen.HasConfirm)
            return await HandlePopupAsync(obs, "比赛计划弹窗");
        return _planStep switch
        {
            PlanStep.Lineup => await HandlePlanLineupAsync(wk, obs),
            PlanStep.Options => await HandlePlanOptionsAsync(wk, obs),
            _ => await HandlePlanWaitingAsync(wk, ml, obs),
        };
    }

    /// <summary>按完 Y（或关掉弹窗）之后：等比赛计划页出现；已回到主页则用红点探针验收。</summary>
    private async Task<int> HandlePlanWaitingAsync(WkScreen wk, MatchScreen ml, Observation obs)
    {
        if (wk == WkScreen.SquadLineupEdit)
        {
            _planStep = PlanStep.Lineup;
            _attempts = 0;
            return 900;
        }
        if (wk == WkScreen.AutoConfigOptions)
        {
            _planStep = PlanStep.Options;
            _attempts = 0;
            return 900;
        }
        if (wk == WkScreen.TeamEffortHome)
        {
            if (WeeklyBrain.DetectPlanBadge(obs.Frame, obs.Ocr))
            {
                if (++_attempts > 3) return await AbandonPlanFixAsync("修完红点仍在");
                _log($"[周活动·阵容挑战] 比赛计划仍有红色叹号（第 {_attempts} 次）：再按 Y 重进修一次", LogLevel.Warn);
                await _engine.Pad.Y();
                return 2600;
            }
            _log("[周活动·阵容挑战] 比赛计划红色叹号已消失：回主页开下一场", LogLevel.Success);
            _planFixAttempts = 0;
            _planStep = PlanStep.Open;
            SetPhase(WkPhase.LaunchReady);
            return 900;
        }
        if (wk == WkScreen.None && ml == MatchScreen.LeagueSquadPage)
        {
            // 比赛计划的兄弟页（计划列表/球员详情/保存确认后的落点）：B 继续退回活动主页，
            // 红点只在阵容挑战主页版式上认，其它页不判。
            if (++_attempts > 4) return await AbandonPlanFixAsync("B 退不回活动主页");
            _log("[周活动·阵容挑战] 停在比赛计划周边页面：B 退回活动主页", LogLevel.Info);
            await _engine.Pad.Back();
            return 2200;
        }
        if (_planStep == PlanStep.Open)
        {
            if (++_attempts > 4) return await AbandonPlanFixAsync("Y 没打开比赛计划");
            _log($"[周活动·阵容挑战] Y 打开比赛计划（第 {_attempts} 次）", LogLevel.Info);
            _planStep = PlanStep.Waiting;
            await _engine.Pad.Y();
            return 2600;
        }
        if (++_attempts > 8) return await AbandonPlanFixAsync($"停在未识别界面（{wk}）");
        return 1200;
    }

    /// <summary>
    /// 比赛计划编辑页（用户 2026-11-22 规则）：向左移动光标到左列侧边栏 → 向下选
    /// 「自动配置球员」→ A 下一步。侧边栏聚焦行由白底探针判定（<see
    /// cref="WeeklyBrain.DetectFocusedSidebarItem"/>）：读不出高亮 = 光标还在阵容板上，
    /// 先左×4 饱和归位；读到其它项则每次下移一行。★只按屏幕状态走，绝不盲按固定次数。
    /// </summary>
    private async Task<int> HandlePlanLineupAsync(WkScreen wk, Observation obs)
    {
        if (wk == WkScreen.AutoConfigOptions) { _planStep = PlanStep.Options; _attempts = 0; return 900; }
        if (wk == WkScreen.TeamEffortHome) { _planStep = PlanStep.Waiting; _attempts = 0; return 900; }
        if (wk != WkScreen.SquadLineupEdit)
        {
            if (++_attempts > 8) return await AbandonPlanFixAsync($"阵容页不可见（{wk}）");
            return 1000;
        }
        if (++_attempts > 14) return await AbandonPlanFixAsync("阵容页导航未到位（侧边栏没走到自动配置球员）");
        var focus = WeeklyBrain.DetectFocusedSidebarItem(obs.Frame, obs.Ocr);
        const string target = "自动配置球员";
        if (focus == target)
        {
            _log("[周活动·阵容挑战] 侧边栏已聚焦「自动配置球员」：A 下一步（打开自动配置选项）", LogLevel.Info);
            _planStep = PlanStep.Options;
            _planOptionPicked = false;
            _attempts = 0;
            await _engine.Pad.Confirm();
            return 2400;
        }
        if (focus is null)
        {
            // 前两轮先左移归位（焦点多半还在阵容板的 GK 光标框上）；仍读不出高亮行就是
            // 白底探针没吃到侧边栏行块（实机 plan_editor 帧里四行中位亮度都是 41，
            // 只有真聚焦行才是 255）——改下×1 试探，绝不无限左移把自己卡在原地。
            if (_attempts >= 3)
            {
                _log("[周活动·阵容挑战] 侧边栏高亮探针读不出：改下×1 试探（假定光标已在侧边栏）", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Down);
                return 1100;
            }
            _log("[周活动·阵容挑战] 焦点还在阵容板上（侧边栏无白底高亮行）：左×4 归位左列侧边栏", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Left, 4);
            return 1200;
        }
        _log($"[周活动·阵容挑战] 侧边栏焦点「{focus}」：下×1（目标：自动配置球员）", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Down);
        return 1100;
    }

    /// <summary>
    /// 自动配置选项弹窗（用户 2026-11-22 规则）：下拉光标选中「重视能力值」后点下一步。
    /// 第一次只按一次下×1 + A（选项列表默认项之上就是「重视能力值」）；仍见弹窗只补 A，
    /// 绝不重复下移（会把光标挪到别的选项上，配置出来的阵容和用户要的不一样）。
    /// 弹窗消失后整队球员卡会一起变亮（用户说识别不到可忽略）——不靠像素判完成，
    /// 直接 B「完成」退出比赛计划，回主页用红点探针验收。
    /// </summary>
    private async Task<int> HandlePlanOptionsAsync(WkScreen wk, Observation obs)
    {
        if (wk == WkScreen.AutoConfigOptions)
        {
            if (++_attempts > 5) return await AbandonPlanFixAsync("自动配置选项弹窗未推进");
            if (!_planOptionPicked)
            {
                _planOptionPicked = true;
                _log("[周活动·阵容挑战] 自动配置选项：下×1 选中「重视能力值」→ A 下一步", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                return 2800;
            }
            _log("[周活动·阵容挑战] 选项弹窗仍在（光标可能已在「重视能力值」上）：直接 A", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2600;
        }
        if (wk == WkScreen.SquadLineupEdit)
        {
            _log("[周活动·阵容挑战] 自动配置已执行（阵容已重排）：B 完成退出比赛计划", LogLevel.Success);
            _planStep = PlanStep.Waiting;
            _attempts = 0;
            await _engine.Pad.Back();
            return 2800;
        }
        if (wk == WkScreen.TeamEffortHome) { _planStep = PlanStep.Waiting; _attempts = 0; return 900; }
        if (++_attempts > 8) return await AbandonPlanFixAsync($"选项确认后未回到编辑页（{wk}）");
        return 1000;
    }

    /// <summary>修复子流程中断：B 退回活动主页，交回 LaunchReady——红点还在会再走一轮
    /// （总量由 <see cref="MaxPlanFixAttempts"/> 兜住，不会无限重试）。</summary>
    private async Task<int> AbandonPlanFixAsync(string why)
    {
        _log($"[周活动·阵容挑战] 比赛计划修复中断（{why}），B 退回活动主页后重试", LogLevel.Warn);
        _planStep = PlanStep.Open;
        _planOptionPicked = false;
        SetPhase(WkPhase.LaunchReady);
        await _engine.Pad.Back();
        return 2400;
    }

    // ---- 阵容挑战「比赛计划失效」修复子流程（2026-11-22 用户实机规则） ----
    /// <summary>修复子步骤（<see cref="WkPhase.MatchPlanSetup"/> 内部）。</summary>
    private PlanStep _planStep = PlanStep.Open;

    /// <summary>同一处红色叹号已尝试修复的次数（红点消失即清零；超 3 次停手交人工，
    /// 绝不在「Y → 阵容页 → 红点还在 → Y」之间无限打转）。</summary>
    private int _planFixAttempts;

    /// <summary>本轮是否已经按过「下×1 + A」（选项弹窗里的「重视能力值」选择）；
    /// 已按过仍见弹窗就只补 A，绝不重复下移把光标挪到别的选项上。</summary>
    private bool _planOptionPicked;

    /// <summary>「无法进行比赛」弹窗连续按 A 未消失的次数（弹窗消失即清零）。</summary>
    private int _planInvalidPresses;

    /// <summary>同一枚「无法进行比赛」弹窗最多连按几次 A；超限回屏幕驱动重判（防识别误判时
    /// 对着主页无限按 A——主页上的 A 就是「前往比赛」）。</summary>
    private const int MaxPlanInvalidPresses = 5;

    /// <summary>「球员卡限制」入场弹窗（阵容挑战）连续按 A 的累计次数（弹窗消失即清零）。</summary>
    private int _squadPopupPresses;

    /// <summary>同一枚「球员卡限制」弹窗最多连按几次 A；超限回屏幕驱动重判（防识别误判时
    /// 对着主页无限按 A——主页上的 A 就是「前往比赛」，会提前开赛）。</summary>
    private const int MaxSquadPopupPresses = 6;

    /// <summary>阵容挑战「无积分栏 / 完成态探针未校准」提示只记一次。</summary>
    private bool _squadNoteLogged;
}
