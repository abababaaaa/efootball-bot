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
    // 本文件小活动：赛事等级/难度 —— 难度页导航与确认。

    /// <summary>「选择比赛难度」页（2026-10-17 实机 cn_tour_difficulty，前往比赛后弹出）：
    /// 与我的联赛难度核对同一守则——读当前选中（白底高亮卡）与配置比对：相同/读不到/
    /// 目标不在卡行 = 直接确认，绝不盲导航；不同 = 按规范难度序左右移差值后确认。
    /// 底栏写 RT 确定，A 通常同样生效（对阵表「RT 下一步」同款实测）：A 两次未生效换 RT。</summary>
    private async Task<int> HandleCnDifficultyAsync(Observation obs)
    {
        if (_streak < 1) return 800; // 页面淡入防抖
        if (++_attempts > 4)
        {
            _log("[国服周活动] 难度页确认多次未生效，B 返回重走流程", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        string wanted = _engine.Config.CnWeekly.Difficulty;
        string? selected = CnWeeklyBrain.DetectSelectedCnDifficulty(obs.Frame, obs.Ocr);
        if (selected is not null && selected != wanted)
        {
            int from = Array.IndexOf(MatchBrain.Difficulties, selected);
            int to = Array.IndexOf(MatchBrain.Difficulties, wanted);
            if (from >= 0 && to >= 0)
            {
                int steps = to - from;
                _log($"[国服周活动] 难度核对：当前 {selected} → 目标 {wanted}，{(steps > 0 ? "右" : "左")}×{Math.Abs(steps)} 后确认", LogLevel.Info);
                await _engine.Pad.DpadRepeat(steps > 0 ? PadDir.Right : PadDir.Left, Math.Abs(steps));
                await ConfirmDifficultyAsync();
                return 3000;
            }
        }
        string why = selected is null ? "未读出当前难度" : selected == wanted ? $"已是 {wanted}" : "目标难度不在卡行";
        _log($"[国服周活动] 难度核对：{why}，直接确认", LogLevel.Info);
        await ConfirmDifficultyAsync();
        return 3000;
    }

    /// <summary>难度页确认键：默认 A，连续两次未生效换 RT 一次（底栏标 RT 确定），仍不行回 A。</summary>
    private async Task ConfirmDifficultyAsync()
    {
        if (_attempts == 3)
        {
            _log("[国服周活动] 难度页：A 两次未生效，改按 RT 确定", LogLevel.Info);
            await _engine.Pad.RT();
        }
        else
        {
            await _engine.Pad.Confirm();
        }
    }
}
