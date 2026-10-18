using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

// Vortice 家族捕获 interop 冒烟：与 SharpDX 版 CaptureInteropTests 同标准——
// 适配器枚举非空（真机至少 1 块硬件 GPU）、名称不含软件适配器、
// PickHardwareAdapter 挑选规则可用（CI 虚拟机无硬件 GPU 时合法跳过）
public class CaptureInteropVorticeTests
{
    [Fact]
    public void GetHardwareAdapterNames_ReturnsAtLeastOne_RealMachine()
    {
        // 与 SharpDX 版对齐：真机空集是回归故障信号（枚举/描述读取失败），照常失败；
        // CI 虚拟机没有硬件 GPU（只剩 WARP、被 Basic 过滤排除）→ 合法空集跳过
        var names = CaptureInteropVortice.GetHardwareAdapterNames();
        if (names.Count == 0 && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            return;
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.DoesNotContain("Basic", n)); // 软件适配器（WARP/基本渲染）不入选
    }

    [Fact]
    public void PickHardwareAdapter_ReturnsAdapterWithName_RealMachine()
    {
        // 真机必须能选出硬件适配器并给出名字；pickedForOutputs 仅在
        // "显存最大的卡无输出、改绑有输出的卡"的矿卡环境为 true，两种取值都合法
        var adapter = CaptureInteropVortice.PickHardwareAdapter(out var name, out var pickedForOutputs);
        try
        {
            if (adapter is null && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                return;
            Assert.NotNull(adapter);
            Assert.NotEqual("", name);
            _ = pickedForOutputs;
        }
        finally
        {
            adapter?.Dispose();
        }
    }
}
