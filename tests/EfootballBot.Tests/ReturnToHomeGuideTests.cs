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

    [Fact]
    public async Task SharedMatchFlow_ClearsGuidingAndNeverBacks()
    {
        // 回归（2026-11-24 实机日志）：一旦进入 MatchStartPage/InMatch/HasConfirm/FullTime 等
        // 共用比赛流程，即便上一帧曾被误判成残留页触发过引导，也必须立即放行并清零，不可在开赛页死锁按 B。
        var g = NewGuide();
        int backs = 0;
        Assert.NotNull(await g.StepAsync(Frame(), false, false, true, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(1, backs);

        foreach (var screen in new[]
                 {
                     MatchScreen.MatchStartPage, MatchScreen.InMatch, MatchScreen.FullTime,
                     MatchScreen.DifficultyPage, MatchScreen.HasConfirm,
                     MatchScreen.KeyMatchDialog, MatchScreen.LeagueSquadPage,
                 })
        {
            Assert.True(ReturnToHomeGuide.IsSharedMatchFlow(screen));
        }

        // 进入共用比赛流程后，引导立即放行且清零 _guiding
        Assert.Null(await g.StepAsync(Frame(), false, ReturnToHomeGuide.IsSharedMatchFlow(MatchScreen.MatchStartPage),
            false, () => { backs++; return Task.CompletedTask; }));
        // 之后遇到过场过渡 Unknown 帧，也不得再继续按 B
        Assert.Null(await g.StepAsync(Frame(), false, false, false, () => { backs++; return Task.CompletedTask; }));
        Assert.Equal(1, backs);
    }
}
