using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 按 B 逃生连续失败后的「先 A 再 B」换招：验证共享决策在周期 B 的第 3 次升级动作，
/// 且只在周期命中轮次触发（其余轮次维持原导航/等待行为）。
/// </summary>
public class BackEscapeEscalationTests
{
    private static readonly OcrResult EmptyOcr = new(Array.Empty<OcrWord>(), 1280, 720);

    [Fact]
    public void ThirdPeriodicBack_EscalatesToConfirmThenBack()
    {
        // k=(streak-NavEscapeStreak)/5：k=0→20、k=1→25、k=2→30（第 3 次 B，应换招）
        int t = MatchBrain.NavEscapeStreak;
        Assert.Equal(MatchActionKind.GamepadBack, MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t).Kind);
        Assert.Equal(MatchActionKind.GamepadBack, MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t + 5).Kind);
        var third = MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t + 10);
        Assert.Equal(MatchActionKind.GamepadBackAfterConfirm, third.Kind);
    }

    [Fact]
    public void EscalationRepeatsEveryThreeBacks()
    {
        int t = MatchBrain.NavEscapeStreak;
        // 第 3、6 次换招（k=2→+10、k=5→+25），第 4/5 次仍是普通 B
        Assert.Equal(MatchActionKind.GamepadBack, MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t + 15).Kind);
        Assert.Equal(MatchActionKind.GamepadBack, MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t + 20).Kind);
        Assert.Equal(MatchActionKind.GamepadBackAfterConfirm, MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, t + 25).Kind);
    }

    [Fact]
    public void BeforeThreshold_NoBackEscape()
    {
        // 未到逃生阈值时不产生 B 动作（主菜单走正常导航）
        Assert.NotEqual(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.MainMenu, EmptyOcr, MatchBrain.NavEscapeStreak - 1).Kind);
    }
}
