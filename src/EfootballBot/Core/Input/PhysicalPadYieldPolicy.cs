using static EfootballBot.Core.Input.PadBackendProbe;

namespace EfootballBot.Core.Input;

/// <summary>
/// 真实手柄让路策略（纯逻辑，便于单测）：把「slot0 现状」翻译成下一步动作。
///
/// 背景（实机 2026-10-21 用户反馈）：bot 启动时如果真实手柄在场，bot 一点输入都没有——
/// 真实手柄占着 XInput 玩家 1，ViGEm 虚拟手柄只能落到玩家 2，而 eFootball 只认玩家 1。
/// 现行流程只在「虚拟手柄连上之前」禁用真实手柄，而真实手柄可能在<b>虚拟手柄连上之后</b>
/// 才被系统认作玩家 1（尤其蓝牙手柄重连/开机时序），或者设备节点禁用后 XInput 仍报连接。
/// 因此必须<b>连上之后再验证一次槽位归属</b>，不成立就当场纠正。
/// </summary>
public static class PhysicalPadYieldPolicy
{
    public enum Step
    {
        /// <summary>slot0 已经是我们的虚拟手柄：什么都不用做。</summary>
        Nothing,
        /// <summary>slot0 被别的设备占着：禁用真实手柄节点，然后重进虚拟手柄（抢回玩家 1）。</summary>
        DisablePhysicalAndReconnect,
        /// <summary>虚拟手柄还没连上、slot0 也被占：先禁用真实手柄再连。</summary>
        DisablePhysicalThenConnect,
        /// <summary>没有虚拟手柄参与（观察模式）：不碰任何设备。</summary>
        NoPadMode,
    }

    /// <summary>
    /// 决策（纯函数）。
    /// </summary>
    /// <param name="virtualPadConnected">虚拟手柄当前是否已连接。</param>
    /// <param name="virtualInSlot0">虚拟手柄是否被证实占着 slot0（<see cref="PadSlotWitness"/> 判定）。</param>
    /// <param name="physicalPadPresent">系统里是否还有「已启用」的真实手柄设备节点。</param>
    /// <param name="takeoverEnabled">用户是否开启「挂机时接管手柄」。</param>
    /// <param name="dryRun">影子模式（只看不做）——绝不改动真实设备。</param>
    public static Step Decide(bool virtualPadConnected, bool virtualInSlot0, bool physicalPadPresent,
        bool takeoverEnabled, bool dryRun)
    {
        if (dryRun || !takeoverEnabled) return Step.NoPadMode;

        if (virtualPadConnected)
            return virtualInSlot0 ? Step.Nothing : Step.DisablePhysicalAndReconnect;

        return physicalPadPresent ? Step.DisablePhysicalThenConnect : Step.Nothing;
    }

    /// <summary>
    /// 「禁用真实手柄后仍然抢不回玩家 1」的处理口径：一次挂机流程直接终止并提示用户物理断开。
    /// 有线手柄在设备管理器层面禁用后仍可能被 XInput 报为连接（USB HID 设备节点复用），
    /// 此时继续挂机 = 每个按键都被游戏无视，用户只会看到 bot 在乱点并以为程序坏了。
    /// </summary>
    public static (bool Abort, string Message) AfterDisable(bool virtualInSlot0, bool physicalPadPresent)
    {
        if (virtualInSlot0) return (false, "");
        return physicalPadPresent
            ? (true,
                "真实手柄仍占用玩家 1（XInput slot 0），虚拟手柄无法接管，bot 的按键会被游戏完全忽略。\n" +
                "请关闭手柄电源或拔掉 USB 线后重新开始挂机。\n" +
                "（有线手柄在设备管理器里禁用后仍可能被系统认作已连接，物理断开是唯一可靠方式）")
            : (true,
                "玩家 1（slot 0）被非本程序的手柄设备占用，虚拟手柄无法接管，bot 的按键会被游戏完全忽略。\n" +
                "请检查是否运行了其他虚拟手柄软件（如 AMD/Steam 手柄模拟、DS4Windows、reWASD），关掉后重新开始挂机。");
    }
}
