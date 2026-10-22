using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 真实手柄让路：槽位归属判定（摇杆微码读回）、决策表、以及"抢不回玩家 1 就直接终止"的口径。
/// 背景（2026-10-22 用户实机）：bot 启动时有真实手柄 → 真实手柄占 XInput 玩家 1，
/// 虚拟手柄落玩家 2，eFootball 只认玩家 1 → bot 一点输入都没有。
/// </summary>
public sealed class PadSlotWitnessTests
{
    private const short Code = VigemPad.IdentityCode;

    private static PadBackendProbe.XINPUT_STATE State(short lx, short ly, ushort buttons = 0)
        => new() { dwPacketNumber = 1, wButtons = buttons, sThumbLX = lx, sThumbLY = ly };

    [Fact]
    public void 微码读回一致_判定虚拟手柄在玩家1()
        => Assert.True(PadSlotWitness.IsVirtualPadInSlot0(State(Code, Code), Code));

    [Fact]
    public void 真实手柄静置_摇杆为0_不算虚拟手柄()
        => Assert.False(PadSlotWitness.IsVirtualPadInSlot0(State(0, 0), Code));

    [Fact]
    public void 真人摇杆恰好不在微码_不算虚拟手柄()
        => Assert.False(PadSlotWitness.IsVirtualPadInSlot0(State(26000, 0), Code));

    [Fact]
    public void 单轴相等_不算虚拟手柄()
    {
        // 只有两轴同时等于微码才算（防止真实手柄某个轴恰好打到边界值造成误判）
        Assert.False(PadSlotWitness.IsVirtualPadInSlot0(State(Code, 0), Code));
        Assert.False(PadSlotWitness.IsVirtualPadInSlot0(State(0, Code), Code));
    }

    [Fact]
    public void 槽位未连接_null_不算虚拟手柄()
        => Assert.False(PadSlotWitness.IsVirtualPadInSlot0(null, Code));

    [Fact]
    public void 微码值在死区外且不等于中位()
    {
        Assert.NotEqual((short)0, Code);
        Assert.True(Math.Abs((int)Code) > 7849); // XInput 左摇杆死区上限 7849
    }

    [Theory]
    [InlineData(false, false, true, "空闲")]
    [InlineData(true, true, true, "是虚拟手柄")]
    [InlineData(true, false, true, "真实手柄占用")]
    [InlineData(true, false, false, "接不进来")]
    public void 归属描述_按状态给出结论(bool slot0, bool virtualIn, bool connected, string expect)
        => Assert.Contains(expect, PadSlotWitness.Describe(slot0, virtualIn, connected));

    [Theory]
    // 已连上且在玩家 1：不动
    [InlineData(true, true, true, true, false, PhysicalPadYieldPolicy.Step.Nothing)]
    // 已连上但玩家 1 是别人：禁用真实手柄后重进
    [InlineData(true, false, true, true, false, PhysicalPadYieldPolicy.Step.DisablePhysicalAndReconnect)]
    // 没连上、有真实手柄：先禁用再连
    [InlineData(false, false, true, true, false, PhysicalPadYieldPolicy.Step.DisablePhysicalThenConnect)]
    // 没连上、也没真实手柄：什么都不用做
    [InlineData(false, false, false, true, false, PhysicalPadYieldPolicy.Step.Nothing)]
    // 关闭接管 / 影子模式：一律不碰设备
    [InlineData(true, false, true, false, false, PhysicalPadYieldPolicy.Step.NoPadMode)]
    [InlineData(true, false, true, true, true, PhysicalPadYieldPolicy.Step.NoPadMode)]
    public void 决策表_按槽位归属与开关给出下一步(bool connected, bool virtualInSlot0,
        bool physicalPresent, bool takeover, bool dryRun, PhysicalPadYieldPolicy.Step expect)
        => Assert.Equal(expect,
            PhysicalPadYieldPolicy.Decide(connected, virtualInSlot0, physicalPresent, takeover, dryRun));

    [Fact]
    public void 抢回玩家1_不终止()
    {
        var (abort, message) = PhysicalPadYieldPolicy.AfterDisable(virtualInSlot0: true, physicalPadPresent: true);
        Assert.False(abort);
        Assert.Equal("", message);
    }

    [Fact]
    public void 禁用后仍被真实手柄占用_终止并请物理断开()
    {
        var (abort, message) = PhysicalPadYieldPolicy.AfterDisable(virtualInSlot0: false, physicalPadPresent: true);
        Assert.True(abort);
        Assert.Contains("拔掉 USB", message);
        Assert.Contains("物理断开", message);
    }

    [Fact]
    public void 禁用后仍被非物理设备占用_终止并提示其他虚拟手柄软件()
    {
        var (abort, message) = PhysicalPadYieldPolicy.AfterDisable(virtualInSlot0: false, physicalPadPresent: false);
        Assert.True(abort);
        Assert.Contains("虚拟手柄软件", message);
    }

    [Fact]
    public void 阻断异常_带错误码且可剥离()
    {
        var ex = new PadTakeoverBlockedException(
            PadTakeoverBlockedException.Code + " 请关闭手柄电源");
        Assert.True(PadTakeoverBlockedException.IsBlocked(ex.Message));
        Assert.Equal("请关闭手柄电源", PadTakeoverBlockedException.StripCode(ex.Message));
        Assert.NotEqual(0, PadTakeoverBlockedException.ExitCode);
    }

    [Fact]
    public void 普通异常_不被当成阻断()
    {
        Assert.False(PadTakeoverBlockedException.IsBlocked("未找到正在运行的 eFootball 游戏窗口"));
        Assert.False(PadTakeoverBlockedException.IsBlocked(null));
    }
}
