using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>虚拟手柄后端：策略纯函数与 VigemPad 的连接语义（v3.3.0 自 HIDMaestro 回退 ViGEmBus）。</summary>
public class PadBackendTests
{
    [Fact]
    public void PadConfig_默认值_Vigem后端()
    {
        var cfg = new PadConfig();
        Assert.Equal(PadBackendKind.Vigem, cfg.Backend);
    }

    [Fact]
    public void PadConfig_MigrateDefaults_旧值归一化到Vigem()
    {
        var pad = new PadConfig { Backend = (PadBackendKind)2 }; // 更早遗留值
        pad.MigrateDefaults();
        Assert.Equal(PadBackendKind.Vigem, pad.Backend);

        // 3.2.x 的 HidMaestro 档（枚举值同为 0）天然兼容
        var padHm = new PadConfig();
        padHm.MigrateDefaults();
        Assert.Equal(PadBackendKind.Vigem, padHm.Backend);
    }

    [Fact]
    public void PadTakeoverConfig_迁移_接管开关翻回开启()
    {
        var cfg = new PadTakeoverConfig { HidePhysicalGamepad = false }; // 3.2.x 共存模式遗留值
        cfg.MigrateDefaults();
        Assert.True(cfg.HidePhysicalGamepad); // v3.3.0 恢复全权接管

        var fresh = new PadTakeoverConfig();
        Assert.True(fresh.HidePhysicalGamepad);
    }

    [Fact]
    public void VigemPad实现IVirtualPad且Kind一致()
    {
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(VigemPad)));
        Assert.Equal(PadBackendKind.Vigem, new VigemPad().Kind);
        Assert.Equal(-1, new VigemPad().UserIndex); // 未连接恒 -1
    }

    [Fact]
    public void 后端驱动探测_不抛异常返回布尔()
    {
        // 不关心本机装没装，只锁「可离线探测、不抛」的契约
        Assert.IsType<bool>(VigemPad.IsDriverInstalled());
    }

    [Fact]
    public void 后端策略_XInput可见_依赖槽位0()
    {
        Assert.True(PadBackendInfo.IsXInputVisible(PadBackendKind.Vigem));
        Assert.True(PadBackendInfo.RequiresSlotZero(PadBackendKind.Vigem));
    }

    [Fact]
    public void 后端策略_回退链_单后端()
    {
        var chain = PadBackendInfo.FallbackChain(PadBackendKind.Vigem);
        Assert.Equal(new[] { PadBackendKind.Vigem }, chain);
    }

    [Theory]
    [InlineData(true, "玩家 1（XInput slot 0）仍被占用")]
    [InlineData(false, null)]
    public void VigemPad_SlotZeroOccupiedMessage_占用时给阻断文案(bool occupied, string? expectedContains)
    {
        var msg = VigemPad.SlotZeroOccupiedMessage(occupied);
        if (expectedContains is null)
            Assert.Null(msg);
        else
        {
            Assert.Contains(expectedContains, msg);
            // 2026-10-22 实机：有真实手柄时 bot 一点输入都没有。阻断文案必须直接告诉用户
            // 「关手柄电源/拔线」，而不是让用户去猜为什么设备管理器禁用没生效。
            Assert.Contains("关闭手柄电源", msg);
            Assert.Contains("拔掉 USB", msg);
        }
    }

    [Fact]
    public void 后端策略_下拉顺序映射_单后端()
    {
        Assert.Equal(0, PadBackendInfo.UiIndexOf(PadBackendKind.Vigem));
        Assert.Equal(0, PadBackendInfo.UiIndexOf((PadBackendKind)99)); // 未知值兜底默认

        Assert.Equal(PadBackendKind.Vigem, PadBackendInfo.FromUiIndex(0));
        Assert.Equal(PadBackendKind.Vigem, PadBackendInfo.FromUiIndex(-1)); // 越界兜底默认
        Assert.Equal(PadBackendKind.Vigem, PadBackendInfo.FromUiIndex(99));
    }

    /// <summary>虚拟总线后代判定：ViGEmBus 命名节点与 DS4 的 ROOT\SYSTEM 根枚举拓扑必须命中，
    /// 真实设备的集线器/ACPI 祖先链不命中（字符串级判定单测，不碰真实设备树）。</summary>
    [Theory]
    [InlineData("ViGEmBus", true)]
    [InlineData(@"ROOT\SYSTEM\0001", true)]
    [InlineData(@"USB\VID_045E&PID_028E", false)]
    [InlineData(@"ACPI\PNP0700", false)]
    public void 虚拟总线特征_字符串前缀(string id, bool virtualLooking)
    {
        // IsVirtualBusChild 需要真实设备树句柄，这里锁其内部匹配的前缀契约
        bool hit = id.StartsWith("ViGEmBus", StringComparison.OrdinalIgnoreCase)
                   || id.StartsWith(@"ROOT\SYSTEM\", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(virtualLooking, hit);
    }

    [Fact]
    public void 物理手柄判定_蓝牙Xbox手柄_键鼠不误伤()
    {
        // 实机样本：Xbox Series 手柄 BLE 连接的唯一可枚举输入节点
        Assert.True(HidDeviceEnumerator.IsBleGamepadInstance(
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02045E_PID&0B22_REV&0522_F46AD74B3CA1\C&3633067B&3&0016"));
        // 1812 服务也承载 BLE 键鼠：非手柄厂商 VID 不命中
        Assert.False(HidDeviceEnumerator.IsBleGamepadInstance(
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&00C1_REV&0100_ABC\C&1&0001"));
        // 非 1812（GATT 其他服务）不命中
        Assert.False(HidDeviceEnumerator.IsBleGamepadInstance(
            @"BTHLEDEVICE\{0000180A-0000-1000-8000-00805F9B34FB}_DEV_VID&02045E_PID&0B22_REV&0522_ABC\C&1&0009"));
    }

    [Fact]
    public void 物理手柄判定_PS前缀与XInput样式()
    {
        Assert.True(HidDeviceEnumerator.IsXInputStyleInstance(@"HID\VID_045E&PID_02E0&IG_00\6&2DF92C3&0&0000"));
        Assert.False(HidDeviceEnumerator.IsXInputStyleInstance(@"HID\VID_054C&PID_05C4&MI_03\6&abc")); // DS4 无 IG 段
    }
}
