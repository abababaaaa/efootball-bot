namespace EfootballBot.Core.Input;

/// <summary>
/// 手柄共存让步策略（纯函数，可单测）。背景：Windows 的 XInput 没有进程级隔离（HidHide 黑名单
/// 对 XInput 无效，实机已证伪），用户拿真实手柄前台玩其他游戏时，按键物理上也会进后台 eFootball。
/// 平台约束下的最优解：真实手柄全程不禁用；挂机侧感知到真实手柄活动即自动让步暂停（期间后台菜单
/// 可能被用户的按键乱走，属预期），停手静默一段时间后自动恢复——恢复时引擎补一发 B 归位，
/// 场景层的未知界面逃生机制（B 逃生 streak）兜底把流程拉回正轨。
/// </summary>
public static class PadCoexistencePolicy
{
    /// <summary>最近一次真实手柄活动距今小于该值视为「正在使用」。</summary>
    public const int ActiveHoldSeconds = 3;

    /// <summary>连续让步超过该时长提示检查手柄漂移（摇杆漂移会被差分误判为持续活动）。</summary>
    public const int DriftHintMinutes = 45;

    /// <summary>让步状态机当前状态（由引擎持有并回传给下一次 Evaluate）。</summary>
    public readonly record struct State(bool Yielded, DateTime YieldedSinceUtc, bool DriftHintShown);

    public enum Decision
    {
        /// <summary>维持现状。</summary>
        KeepState,
        /// <summary>进入让步（暂停挂机操作）。</summary>
        Yield,
        /// <summary>退出让步（恢复挂机操作）。</summary>
        Resume,
    }

    /// <summary>
    /// 判定下一步：未让步且近期有真实手柄活动 → Yield；
    /// 让步中且距上次活动已静默 ≥ <paramref name="quietSeconds"/>（无任何活动记录视为无限静默）→ Resume。
    /// </summary>
    public static Decision Evaluate(State state, DateTime? lastActivityUtc, DateTime nowUtc, int quietSeconds)
    {
        if (!state.Yielded)
            return lastActivityUtc is { } t && (nowUtc - t).TotalSeconds <= ActiveHoldSeconds
                ? Decision.Yield
                : Decision.KeepState;

        var quietFor = lastActivityUtc is { } t2 ? nowUtc - t2 : TimeSpan.FromDays(365);
        return quietFor.TotalSeconds >= quietSeconds ? Decision.Resume : Decision.KeepState;
    }

    /// <summary>让步持续过久（手柄漂移嫌疑）且尚未提示过 → 引擎应打一条日志。</summary>
    public static bool ShouldShowDriftHint(State state, DateTime nowUtc)
        => state.Yielded && !state.DriftHintShown
           && (nowUtc - state.YieldedSinceUtc).TotalMinutes >= DriftHintMinutes;
}
