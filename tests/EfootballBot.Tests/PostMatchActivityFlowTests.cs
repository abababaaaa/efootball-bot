using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 赛后活动任务状态机全流程回归（手柄动作用录制假体验证按键序列）：
// 自动推进 = 每轮 [Y 回当前位置 → ↑ → 下一步 → 弹窗 ↑ → 下一步] × x → 退出 [→ 下一步]；
// 未开启自动推进 = 直接退出。
public class PostMatchActivityFlowTests
{
    /// <summary>录制语义动作的假手柄。</summary>
    private sealed class FakePad : IPmActivityPad
    {
        public List<string> Calls { get; } = new();
        public Task Y() { Calls.Add("Y"); return Task.CompletedTask; }
        public Task Dpad(PadDir dir) { Calls.Add($"dpad:{dir}"); return Task.CompletedTask; }
        public Task Confirm() { Calls.Add("A"); return Task.CompletedTask; }
    }

    private static OcrWord W(string text, double cx, double cy, double h = 0.03)
        => new(text, cx - 0.05, cy - h / 2, 0.1, h);

    private static OcrResult MapOcr() => new(new[]
    {
        W("奖励", 0.78, 0.08), W("下一步", 0.87, 0.83), W("当前位置", 0.58, 0.93),
        W("总积分:1/9", 0.115, 0.354, 0.028), W("C'", 0.089, 0.284, 0.049),
    }, 1280, 720);

    private static OcrResult PopupOcr() => new(new[]
    {
        W("获取机会交易×1?", 0.5, 0.25), W("消耗1点地图前进步数以获得奖励。", 0.5, 0.56),
        W("获取", 0.5, 0.66), W("取消", 0.5, 0.76),
    }, 1280, 720);

    private static readonly OcrResult EmptyOcr = OcrResult.Empty;

    private static void NoLog(string message, LogLevel level) { }

    [Fact]
    public async Task AutoAdvance_X1_FullSequence()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        int countReads = 0;

        // 进图：读 x=1 → 先按 Y
        Assert.Equal(1500, await c.StepAsyncCore(MapOcr(), autoAdvance: true,
            _ => { countReads++; return Task.FromResult<int?>(1); }, pad, NoLog, "测试"));
        Assert.Equal(new[] { "Y" }, pad.Calls);

        // 地图稳定帧：↑ + 下一步
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true,
            _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(new[] { "Y", "dpad:Up", "A" }, pad.Calls);

        // 弹窗：上移选获取 + 下一步（x 消费完）
        Assert.Equal(2200, await c.StepAsyncCore(PopupOcr(), true,
            _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(new[] { "Y", "dpad:Up", "A", "dpad:Up", "A" }, pad.Calls);

        // 回到地图（剩余 0）：直接退出 → 下一步
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true,
            _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(new[] { "Y", "dpad:Up", "A", "dpad:Up", "A", "dpad:Right", "A" }, pad.Calls);

        // Done：等离屏，不再按键
        await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试");
        Assert.Equal(7, pad.Calls.Count);

        // 离屏 3 帧后复位
        for (int i = 0; i < 3; i++)
            Assert.Null(await c.StepAsyncCore(EmptyOcr, true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(7, pad.Calls.Count);
        Assert.False(c.Active);

        // x 只在首次进图读一次（含复位后重新进图前的那次已计）
        Assert.Equal(1, countReads);
    }

    [Fact]
    public async Task AutoAdvance_X2_TwoRoundsThenExit()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        

        Assert.Equal(1500, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(2), pad, NoLog, "测试")); // 进图按一次 Y（仅此一次）
        for (int round = 0; round < 2; round++)
        {
            Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(2), pad, NoLog, "测试")); // ↑+下一步
            Assert.Equal(2200, await c.StepAsyncCore(PopupOcr(), true, _ => Task.FromResult<int?>(2), pad, NoLog, "测试")); // 弹窗
        }
        // 两轮 = 一次 Y + ↑A ↑A ↑A ↑A；随后退出 → A
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(2), pad, NoLog, "测试"));
        Assert.Equal(new[] { "Y", "dpad:Up", "A", "dpad:Up", "A", "dpad:Up", "A", "dpad:Up", "A", "dpad:Right", "A" },
            pad.Calls);
    }

    [Fact]
    public async Task AutoOff_ExitsImmediately()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), autoAdvance: false,
            _ => throw new InvalidOperationException("关闭时不得读次数"), pad, NoLog, "测试"));
        Assert.Equal(new[] { "dpad:Right", "A" }, pad.Calls);
    }

    [Fact]
    public async Task CountUnreadable_ConservativeExit()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), autoAdvance: true,
            _ => Task.FromResult<int?>(null), pad, NoLog, "测试"));
        Assert.Equal(new[] { "dpad:Right", "A" }, pad.Calls);
    }

    [Fact]
    public async Task TakeoverOnPopup_ConfirmsThenExits()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        // 半途启动恰好落在弹窗上：上移确认清掉，然后走退出
        Assert.Equal(2200, await c.StepAsyncCore(PopupOcr(), autoAdvance: true,
            _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(new[] { "dpad:Up", "A" }, pad.Calls);
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(new[] { "dpad:Up", "A", "dpad:Right", "A" }, pad.Calls);
    }

    [Fact]
    public async Task PopupMissed_TreatedAsConsumed_NoDeadLoop()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        
        // x=1：按了 ↑+下一步 但弹窗没出现（次数读多/无交易）——重试一帧后视为已消费并退出
        Assert.Equal(1500, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试"));
        Assert.Equal(1200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试")); // 弹窗未现：再等一帧
        Assert.Equal(1000, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试")); // 视为本步已消费
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), true, _ => Task.FromResult<int?>(1), pad, NoLog, "测试")); // 剩余 0 → 退出
        Assert.Equal(new[] { "Y", "dpad:Up", "A", "dpad:Right", "A" }, pad.Calls);
    }

    [Fact]
    public async Task ExitNotEffective_RetriesAfterSixFrames()
    {
        var pad = new FakePad();
        var c = new PostMatchActivityController();
        
        Assert.Equal(2200, await c.StepAsyncCore(MapOcr(), false, null, pad, NoLog, "测试"));
        for (int i = 0; i < 6; i++)
            await c.StepAsyncCore(MapOcr(), false, null, pad, NoLog, "测试");
        int before = pad.Calls.Count;
        await c.StepAsyncCore(MapOcr(), false, null, pad, NoLog, "测试"); // 第 7 帧仍在地图：重试退出
        Assert.True(pad.Calls.Count > before);
        Assert.Contains("dpad:Right", pad.Calls.Skip(before));
    }
}
