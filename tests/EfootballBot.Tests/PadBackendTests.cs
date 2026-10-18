using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>虚拟手柄后端：工厂契约与 HIDMaestro 反射映射纯函数。</summary>
public class PadBackendTests
{
    [Fact]
    public void ModuleFallback_默认值_全走新版且后端为ViGEmBus()
    {
        var cfg = new ModuleFallbackConfig();
        Assert.False(cfg.UseSharpDxCapture);
        Assert.False(cfg.UseFp32Ocr);
        Assert.False(cfg.DisableOcrDownscale);
        Assert.False(cfg.EnableHidMaestro);   // 默认 ViGEmBus
        Assert.False(cfg.DisableMouseTrajectory);
        Assert.False(cfg.DisableFailureDumps);
    }

    [Fact]
    public void 两个后端都实现IVirtualPad()
    {
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(VirtualGamepad360)));
        Assert.True(typeof(IVirtualPad).IsAssignableFrom(typeof(HidMaestroPad)));
    }

    [Fact]
    public void 后端驱动探测_不抛异常返回布尔()
    {
        // 不关心本机装没装，只锁「可离线探测、不抛」的契约
        Assert.IsType<bool>(VirtualGamepad360.IsDriverInstalled());
        Assert.IsType<bool>(HidMaestroPad.IsDriverInstalled());
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
}
