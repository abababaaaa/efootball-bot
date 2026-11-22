using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 启动引导：停在其他模块页时先 B 收敛回主页；主页/本模块界面跳过；6 次预算防死循环。
/// </summary>
public class ReturnToHomeGuideTests
{
    private static FrameData Frame() => new(new byte[1280 * 720 * 4], 1280, 720, 0);
    private static readonly List<string> NoopLogs = new();
    private static readonly List<string> NoopDumps = new();

    private static ReturnToHomeGuide NewGuide() => new(
        "测试",
        (msg, lv) => NoopLogs.Add(msg),
        (frame, tag, reason) => NoopDumps.Add($"{tag}:{reason}"));

    [Fact]
    public async Task MainMenu_NoGuide_Resets()
    {
        var g = NewGuide();
        int backs = 0;
        Assert.Null(await g.StepAsync(Frame(), isMainMenu: true, isOwnModulePage: false,
            isOtherModulePage: true, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(0, backs);
    }

    [Fact]
    public async Task OwnModulePage_NoGuide()
    {
        var g = NewGuide();
        int backs = 0;
        Assert.Null(await g.StepAsync(Frame(), isMainMenu: false, isOwnModulePage: true,
            isOtherModulePage: false, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(0, backs);
    }

    [Fact]
    public async Task OtherModulePage_BacksUntilMainMenu()
    {
        var g = NewGuide();
        int backs = 0;
        // 前两轮残留页 → B；第 3 轮到主页 → 结束
        Assert.NotNull(await g.StepAsync(Frame(), false, false, true, () => { backs++; return Task.CompletedTask; }));
        Assert.NotNull(await g.StepAsync(Frame(), false, false, true, () => { backs++; return Task.CompletedTask; }));
        Assert.Null(await g.StepAsync(Frame(), true, false, false, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(2, backs);
    }

    [Fact]
    public async Task SixFailures_PauseAndDump()
    {
        var g = NewGuide();
        NoopDumps.Clear();
        int backs = 0;
        int? seventh = null;
        for (int i = 0; i < 7; i++)
            seventh = await g.StepAsync(Frame(), false, false, true, () => { backs++; return Task.CompletedTask; });
        Assert.Equal(8000, seventh); // 第 7 次（>6）触发 8s 暂停并清零计数
        Assert.Contains(NoopDumps, d => d.Contains("home_guide_stuck"));
    }

    [Fact]
    public async Task NonModulePage_DoesNotInterfere()
    {
        var g = NewGuide();
        int backs = 0;
        // 既非主页、非本模块、也非残留页（如比赛中/加载页）：不接管
        Assert.Null(await g.StepAsync(Frame(), false, false, false, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(0, backs);
    }
}
