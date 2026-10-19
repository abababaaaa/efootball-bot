using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>虚拟手柄后端：策略纯函数与 HIDMaestro 的映射纯函数（ViGEm 两档已移除）。</summary>
public class PadBackendTests
{
    [Fact]
    public void PadConfig_默认值_HIDMaestro后端()
    {
        var cfg = new PadConfig();
        Assert.Equal(PadBackendKind.HidMaestro, cfg.Backend);
    }

    [Fact]
    public void ModuleFallback_默认值_全走新版()
    {
        var cfg = new ModuleFallbackConfig();
        Assert.False(cfg.UseSharpDxCapture);
        Assert.False(cfg.UseFp32Ocr);
        Assert.False(cfg.DisableOcrDownscale);
        Assert.False(cfg.DisableMouseTrajectory);
        Assert.False(cfg.DisableFailureDumps);
    }

    [Fact]
    public void PadConfig_MigrateFrom_旧值归一化到HIDMaestro()
    {
        var legacy = new ModuleFallbackConfig { EnableHidMaestro = true };
        var pad = new PadConfig { Backend = (PadBackendKind)2 }; // 旧 Xbox 360 档遗留值
        pad.MigrateFrom(legacy);
        Assert.Equal(PadBackendKind.HidMaestro, pad.Backend);
        Assert.False(legacy.EnableHidMaestro); // 旧开关一律清零

        // 旧 DS4 档（0）与旧 HidMaestro 档（1）同样归一化
        var pad0 = new PadConfig { Backend = (PadBackendKind)1 };
        pad0.MigrateFrom(new ModuleFallbackConfig());
        Assert.Equal(PadBackendKind.HidMaestro, pad0.Backend);
        var padHm = new PadConfig();
        padHm.MigrateFrom(new ModuleFallbackConfig());
        Assert.Equal(PadBackendKind.HidMaestro, padHm.Backend);
    }

    [Fact]
    public void HidMaestroPad实现IVirtualPad且Kind一致()
    {
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(HidMaestroPad)));
        Assert.Equal(PadBackendKind.HidMaestro, new HidMaestroPad().Kind);
    }

    [Fact]
    public void 后端驱动探测_不抛异常返回布尔()
    {
        // 不关心本机装没装，只锁「可离线探测、不抛」的契约
        Assert.IsType<bool>(HidMaestroPad.IsDriverInstalled());
    }

    [Fact]
    public void 后端策略_纯HID_无XInput可见_不依赖槽位0()
    {
        Assert.False(PadBackendInfo.IsXInputVisible(PadBackendKind.HidMaestro));
        Assert.False(PadBackendInfo.RequiresSlotZero(PadBackendKind.HidMaestro));
    }

    [Fact]
    public void 后端策略_回退链_单后端()
    {
        var chain = PadBackendInfo.FallbackChain(PadBackendKind.HidMaestro);
        Assert.Equal(new[] { PadBackendKind.HidMaestro }, chain);
    }

    [Fact]
    public void HidMaestroPad_MapButton_按方位映射CrossCircleSquareY()
    {
        Assert.Equal(0x01, HidMaestroPad.MapButton(PadButton.A));             // Cross（底）
        Assert.Equal(0x02, HidMaestroPad.MapButton(PadButton.B));             // Circle（右）
        Assert.Equal(0x04, HidMaestroPad.MapButton(PadButton.X));             // Square（左）
        Assert.Equal(0x08, HidMaestroPad.MapButton(PadButton.Y));             // Y（顶）
        Assert.Equal(0x10, HidMaestroPad.MapButton(PadButton.LeftShoulder));  // LeftBumper
        Assert.Equal(0x20, HidMaestroPad.MapButton(PadButton.RightShoulder)); // RightBumper
        Assert.Equal(0x40, HidMaestroPad.MapButton(PadButton.Back));
        Assert.Equal(0x80, HidMaestroPad.MapButton(PadButton.Start));
        Assert.Equal(0x100, HidMaestroPad.MapButton(PadButton.LeftThumb));
        Assert.Equal(0x200, HidMaestroPad.MapButton(PadButton.RightThumb));
        Assert.Equal(0, HidMaestroPad.MapButton(PadButton.DpadUp));           // 十字键走 Hat
    }

    [Fact]
    public void HidMaestroPad_摇杆与扳机归一化()
    {
        Assert.Equal(0f, HidMaestroPad.StickNormalized(0));
        Assert.Equal(1f, HidMaestroPad.StickNormalized(short.MaxValue));
        Assert.Equal(-1f, HidMaestroPad.StickNormalized(short.MinValue));
        Assert.InRange(HidMaestroPad.StickNormalized(26000), 0.79f, 0.80f);

        Assert.Equal(0f, HidMaestroPad.TriggerNormalized(0));
        Assert.Equal(1f, HidMaestroPad.TriggerNormalized(255));
        Assert.InRange(HidMaestroPad.TriggerNormalized(128), 0.50f, 0.51f);
    }

    [Fact]
    public void PadBackendProbe_槽位占用提示_布尔语义()
    {
        Assert.Null(PadBackendProbe.SlotZeroOccupiedMessage(false));
        Assert.NotNull(PadBackendProbe.SlotZeroOccupiedMessage(true));
    }

    [Fact]
    public void 后端策略_下拉顺序映射_单后端()
    {
        Assert.Equal(0, PadBackendInfo.UiIndexOf(PadBackendKind.HidMaestro));
        Assert.Equal(0, PadBackendInfo.UiIndexOf((PadBackendKind)99)); // 未知值兜底默认

        Assert.Equal(PadBackendKind.HidMaestro, PadBackendInfo.FromUiIndex(0));
        Assert.Equal(PadBackendKind.HidMaestro, PadBackendInfo.FromUiIndex(-1)); // 越界兜底默认
        Assert.Equal(PadBackendKind.HidMaestro, PadBackendInfo.FromUiIndex(99));
    }

    [Fact]
    public void 虚拟手柄实例路径判定_含HIDMaestro与旧ViGEm黑名单形态()
    {
        // 旧版写入 HidHide 黑名单的 ViGEm 条目仍须识别（精确清理），本程序已不再产生
        Assert.True(HidDeviceEnumerator.IsVirtualPadInstancePath(@"HID\VID_045E&PID_028E\6&2DF92C3&0&0000"));
        Assert.True(HidDeviceEnumerator.IsVirtualPadInstancePath(@"HID\VID_054C&PID_05C4&REV_0100\2&526D6D&0&0000"));
        Assert.True(HidDeviceEnumerator.IsVirtualPadInstancePath(@"ROOT\HIDMAESTRO_EFOOTBALLBOT\1&123"));
        Assert.False(HidDeviceEnumerator.IsVirtualPadInstancePath(@"HID\VID_1532&PID_0900\6&abcd")); // Razer 鼠标等
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
