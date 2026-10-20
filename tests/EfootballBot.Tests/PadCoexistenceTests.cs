using System.Reflection;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 手柄共存模式单元测试：让步/恢复策略状态机、提交异常内层拆解、
/// 旧「禁用真实手柄」配置的迁移降级。
/// </summary>
public sealed class PadCoexistenceTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static PadCoexistencePolicy.State Yielded(bool driftHint = false)
        => new(true, T0.AddMinutes(-1), driftHint);

    // ---------------- Evaluate ----------------

    [Fact]
    public void 无活动_保持运行不触发让步()
    {
        var d = PadCoexistencePolicy.Evaluate(default, lastActivityUtc: null, T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.KeepState, d);
    }

    [Fact]
    public void 近期有活动_进入让步()
    {
        var d = PadCoexistencePolicy.Evaluate(default, T0.AddSeconds(-2), T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.Yield, d);
    }

    [Fact]
    public void 活动已过保持窗口_不触发让步()
    {
        // 3 秒保持窗之外的活动（如用户 10 秒前碰了一下手柄）不打断挂机
        var d = PadCoexistencePolicy.Evaluate(default, T0.AddSeconds(-4), T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.KeepState, d);
    }

    [Fact]
    public void 让步中静默未满_保持让步()
    {
        var d = PadCoexistencePolicy.Evaluate(Yielded(), T0.AddSeconds(-10), T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.KeepState, d);
    }

    [Fact]
    public void 让步中静默达标_恢复()
    {
        var d = PadCoexistencePolicy.Evaluate(Yielded(), T0.AddSeconds(-30), T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.Resume, d);
    }

    [Fact]
    public void 让步中无任何新活动_静默期从活动时间起算_到点恢复()
    {
        // 活动记录是让步前最后一次：30s 静默以它为准
        var d = PadCoexistencePolicy.Evaluate(Yielded(), T0.AddSeconds(-31), T0, quietSeconds: 30);
        Assert.Equal(PadCoexistencePolicy.Decision.Resume, d);
    }

    // ---------------- DriftHint ----------------

    [Fact]
    public void 让步超45分钟且未提示过_提示漂移()
    {
        var s = new PadCoexistencePolicy.State(true, T0.AddMinutes(-46), DriftHintShown: false);
        Assert.True(PadCoexistencePolicy.ShouldShowDriftHint(s, T0));
    }

    [Fact]
    public void 刚让步_或已提示过_不重复提示()
    {
        Assert.False(PadCoexistencePolicy.ShouldShowDriftHint(Yielded(), T0));
        var hinted = new PadCoexistencePolicy.State(true, T0.AddMinutes(-60), DriftHintShown: true);
        Assert.False(PadCoexistencePolicy.ShouldShowDriftHint(hinted, T0));
        Assert.False(PadCoexistencePolicy.ShouldShowDriftHint(default, T0)); // 未让步
    }

    // ---------------- FormatSubmitError ----------------

    [Fact]
    public void 提交异常拆解_穿透TargetInvocation取真实内层()
    {
        var inner = new InvalidOperationException("驱动通信失败");
        var ex = new TargetInvocationException(inner);
        // TIE 自身的 Message 是无信息量固定文案，绝不能出现在拆解结果里
        var text = HidMaestroPad.FormatSubmitError(ex);
        Assert.Contains("TargetInvocationException", text);
        Assert.Contains("InvalidOperationException: 驱动通信失败", text);
        Assert.DoesNotContain("Exception has been thrown by the target", text);
    }

    [Fact]
    public void 提交异常拆解_多层内层全链可见()
    {
        var root = new TimeoutException("设备句柄失效");
        var mid = new InvalidOperationException("SubmitState 失败", root);
        var ex = new TargetInvocationException(mid);
        var text = HidMaestroPad.FormatSubmitError(ex);
        Assert.Contains("TimeoutException: 设备句柄失效", text);
        Assert.Contains("→", text);
    }

    [Fact]
    public void 提交异常拆解_普通异常原样可见()
    {
        var text = HidMaestroPad.FormatSubmitError(new ApplicationException("boom"));
        Assert.Equal("ApplicationException: boom", text);
    }

    // ---------------- 旧禁用机制配置迁移 ----------------

    [Fact]
    public void 旧配置迁移_HidePhysicalGamepad强制关闭()
    {
        var cfg = new HidHideConfig { HidePhysicalGamepad = true };
        cfg.MigrateDefaults();
        Assert.False(cfg.HidePhysicalGamepad); // 禁用机制已移除，旧 true 值一律压回
    }

    [Fact]
    public void 旧配置迁移_从未应用过隔离_翻默认开启()
    {
        var cfg = new HidHideConfig { Enabled = false, AppliedBlacklist = new() };
        cfg.MigrateDefaults();
        Assert.True(cfg.Enabled);
    }

    [Fact]
    public void 旧配置迁移_用户已主动关隔离_尊重选择不强行打开()
    {
        var cfg = new HidHideConfig { Enabled = false, AppliedBlacklist = new() { @"HID\HIDCLASS\1&x&0&0000" } };
        cfg.MigrateDefaults();
        Assert.False(cfg.Enabled);
    }
}
