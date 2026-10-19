using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>虚拟手柄后端：工厂契约、策略纯函数与两档纯 HID 后端的映射纯函数。</summary>
public class PadBackendTests
{
    [Fact]
    public void PadConfig_默认值_DS4隔离后端()
    {
        var cfg = new PadConfig();
        Assert.Equal(PadBackendKind.HidDs4, cfg.Backend);
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
    public void PadConfig_MigrateFrom_旧HidMaestro开关迁入后清零()
    {
        var legacy = new ModuleFallbackConfig { EnableHidMaestro = true };
        var pad = new PadConfig();
        pad.MigrateFrom(legacy);
        Assert.Equal(PadBackendKind.HidMaestro, pad.Backend);
        Assert.False(legacy.EnableHidMaestro); // 清零防下次加载反复覆盖用户选择

        // 未开启过旧开关：保持默认，不动配置
        var pad2 = new PadConfig();
        pad2.MigrateFrom(new ModuleFallbackConfig());
        Assert.Equal(PadBackendKind.HidDs4, pad2.Backend);
    }

    [Fact]
    public void 三个后端都实现IVirtualPad()
    {
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(VirtualDs4Pad)));
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(VirtualGamepad360)));
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(HidMaestroPad)));
    }

    [Fact]
    public void 后端种类_实例与声明一致()
    {
        Assert.Equal(PadBackendKind.HidDs4, new VirtualDs4Pad().Kind);
        Assert.Equal(PadBackendKind.Xbox360, new VirtualGamepad360().Kind);
        Assert.Equal(PadBackendKind.HidMaestro, new HidMaestroPad().Kind);
    }

    [Fact]
    public void 后端驱动探测_不抛异常返回布尔()
    {
        // 不关心本机装没装，只锁「可离线探测、不抛」的契约
        Assert.IsType<bool>(VirtualGamepad360.IsDriverInstalled());
        Assert.IsType<bool>(HidMaestroPad.IsDriverInstalled());
    }

    [Fact]
    public void 后端策略_XInput可见性_仅旧版Xbox360()
    {
        Assert.False(PadBackendInfo.IsXInputVisible(PadBackendKind.HidDs4));
        Assert.False(PadBackendInfo.IsXInputVisible(PadBackendKind.HidMaestro));
        Assert.True(PadBackendInfo.IsXInputVisible(PadBackendKind.Xbox360));

        // 玩家 1 仲裁只有 XInput 后端需要；纯 HID 后端无槽位概念
        Assert.False(PadBackendInfo.RequiresSlotZero(PadBackendKind.HidDs4));
        Assert.False(PadBackendInfo.RequiresSlotZero(PadBackendKind.HidMaestro));
        Assert.True(PadBackendInfo.RequiresSlotZero(PadBackendKind.Xbox360));
    }

    [Fact]
    public void 后端策略_回退链_纯HID互退最终落旧版()
    {
        Assert.Equal(new[] { PadBackendKind.HidDs4, PadBackendKind.Xbox360 },
            PadBackendInfo.FallbackChain(PadBackendKind.HidDs4));
        Assert.Equal(new[] { PadBackendKind.HidMaestro, PadBackendKind.HidDs4, PadBackendKind.Xbox360 },
            PadBackendInfo.FallbackChain(PadBackendKind.HidMaestro));
        Assert.Equal(new[] { PadBackendKind.Xbox360 },
            PadBackendInfo.FallbackChain(PadBackendKind.Xbox360));

        foreach (var kind in new[] { PadBackendKind.HidDs4, PadBackendKind.HidMaestro, PadBackendKind.Xbox360 })
        {
            var chain = PadBackendInfo.FallbackChain(kind);
            Assert.Equal(kind, chain[0]);       // 请求的后端必在首位
            Assert.Equal(PadBackendKind.Xbox360, chain[^1]); // 链尾必为旧版保底
        }
    }

    [Fact]
    public void VirtualDs4Pad_Map_按方位映射CrossCircleSquareTriangle()
    {
        Assert.Equal(DualShock4Button.Cross, VirtualDs4Pad.Map(PadButton.A));             // 底
        Assert.Equal(DualShock4Button.Circle, VirtualDs4Pad.Map(PadButton.B));            // 右
        Assert.Equal(DualShock4Button.Square, VirtualDs4Pad.Map(PadButton.X));            // 左
        Assert.Equal(DualShock4Button.Triangle, VirtualDs4Pad.Map(PadButton.Y));          // 顶
        Assert.Equal(DualShock4Button.ShoulderLeft, VirtualDs4Pad.Map(PadButton.LeftShoulder));
        Assert.Equal(DualShock4Button.ShoulderRight, VirtualDs4Pad.Map(PadButton.RightShoulder));
        Assert.Equal(DualShock4Button.Share, VirtualDs4Pad.Map(PadButton.Back));
        Assert.Equal(DualShock4Button.Options, VirtualDs4Pad.Map(PadButton.Start));
        Assert.Equal(DualShock4Button.ThumbLeft, VirtualDs4Pad.Map(PadButton.LeftThumb));
        Assert.Equal(DualShock4Button.ThumbRight, VirtualDs4Pad.Map(PadButton.RightThumb));
        Assert.Null(VirtualDs4Pad.Map(PadButton.DpadUp));           // 十字键走 DPad
        Assert.Null(VirtualDs4Pad.Map(PadButton.DpadLeft));
    }

    [Fact]
    public void VirtualDs4Pad_AxisByte_中位128与端点()
    {
        Assert.Equal(128, VirtualDs4Pad.AxisByte(0));
        Assert.Equal(255, VirtualDs4Pad.AxisByte(short.MaxValue));
        Assert.InRange(VirtualDs4Pad.AxisByte(short.MinValue), 0, 2); // 下端点（127 步进舍入到 1）
        Assert.Equal(229, VirtualDs4Pad.AxisByte(26000));             // Flick 档位 26000 → 约满 90%
        Assert.Equal(128, VirtualDs4Pad.AxisByte(96));                // 微小抖动归中位
    }

    [Fact]
    public void VirtualDs4Pad_MapDpad_方位映射()
    {
        Assert.Equal(DualShock4DPadDirection.North, VirtualDs4Pad.MapDpad(PadDir.Up));
        Assert.Equal(DualShock4DPadDirection.South, VirtualDs4Pad.MapDpad(PadDir.Down));
        Assert.Equal(DualShock4DPadDirection.West, VirtualDs4Pad.MapDpad(PadDir.Left));
        Assert.Equal(DualShock4DPadDirection.East, VirtualDs4Pad.MapDpad(PadDir.Right));
        Assert.Equal(DualShock4DPadDirection.None, VirtualDs4Pad.MapDpad(null)); // 中位（释放）
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
    public void 后端策略_下拉顺序映射_与枚举值解耦()
    {
        Assert.Equal(0, PadBackendInfo.UiIndexOf(PadBackendKind.HidDs4));
        Assert.Equal(1, PadBackendInfo.UiIndexOf(PadBackendKind.HidMaestro));
        Assert.Equal(2, PadBackendInfo.UiIndexOf(PadBackendKind.Xbox360));
        Assert.Equal(0, PadBackendInfo.UiIndexOf((PadBackendKind)99)); // 未知值兜底默认

        Assert.Equal(PadBackendKind.HidDs4, PadBackendInfo.FromUiIndex(0));
        Assert.Equal(PadBackendKind.HidMaestro, PadBackendInfo.FromUiIndex(1));
        Assert.Equal(PadBackendKind.Xbox360, PadBackendInfo.FromUiIndex(2));
        Assert.Equal(PadBackendKind.HidDs4, PadBackendInfo.FromUiIndex(-1)); // 越界兜底默认
        Assert.Equal(PadBackendKind.HidDs4, PadBackendInfo.FromUiIndex(99));
    }

    [Fact]
    public void 虚拟手柄实例路径判定_覆盖三后端且不误伤物理非PS手柄()
    {
        Assert.True(HidDeviceEnumerator.IsVirtualPadInstancePath(@"HID\VID_045E&PID_028E\6&2DF92C3&0&0000"));
        // 实机拓扑：ViGEm DS4 带 &REV_0100 段，前缀仍须命中
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
