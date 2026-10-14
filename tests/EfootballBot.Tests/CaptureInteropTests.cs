using EfootballBot.Core.Capture;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 捕获 interop 冒烟：适配器枚举（GetDesc vtable 槽位 8 修复的回归）与 GPU 偏好探测
public class CaptureInteropTests
{
    [Fact]
    public void GetHardwareAdapterNames_ReturnsAtLeastOne_RealMachine()
    {
        // 2026-10-11 回归：GetDesc 曾错用 adapter vtbl 槽 7（EnumOutputs），
        // 所有适配器"读不出"描述 → 显式 GPU 绑定永远落空。修好后真机至少枚举出 1 块硬件 GPU。
        var names = CaptureInterop.GetHardwareAdapterNames();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.DoesNotContain("Basic", n)); // 软件适配器（WARP/基本渲染）不入选
    }

    [Fact]
    public void EnsureHighPerformance_DoesNotThrow_AndReportsMultiGpu()
    {
        // 幂等探测：真机上不抛异常即通过；双显卡机器上 written=true 表示写入了独显偏好
        var (written, multiGpu, adapters) = GpuPreference.EnsureHighPerformance();
        Assert.False(multiGpu && adapters.Length == 0); // 双显卡报告必须带适配器名
        _ = written;
    }
}
