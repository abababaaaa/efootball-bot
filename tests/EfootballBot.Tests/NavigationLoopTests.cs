using EfootballBot.Core.Bot;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public class NavigationLoopTests
{
    private const string League = nameof(GameScreen.MyLeagueHome);

    private static NavigationService NewService(INavIO io, NavLimits? limits = null, PageGraph? graph = null)
        => new(
            graph ?? new PageGraph(PageCatalog.Edges),
            io,
            limits ?? new NavLimits(25, 6, 10),
            (_, _) => { },
            null);

    private static Observation Obs(GameScreen s)
        => new(null!, new OcrResult(new List<OcrWord>(), 1280, 720), s, 5, new[] { "x" });

    [Fact]
    public async Task Happy_path_home_to_league_executes_two_hops()
    {
        var io = new FakeIO(GameScreen.Home, GameScreen.MatchHub, GameScreen.MyLeagueHome);
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        Assert.Equal(2, io.Hops);
        Assert.Contains(io.ActionLog, a => a.Contains("Right"));
        Assert.Contains(io.ActionLog, a => a.Contains("A"));
    }

    [Fact]
    public async Task Already_at_target_returns_immediately()
    {
        var io = new FakeIO(GameScreen.MyLeagueHome);
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        Assert.Empty(io.ActionLog);
    }

    [Fact]
    public async Task Landing_on_wrong_known_page_replans_via_back_edge()
    {
        var io = new FakeIO(
            GameScreen.Home,
            GameScreen.EventHub, GameScreen.EventHub,
            GameScreen.MatchHub,
            GameScreen.MyLeagueHome);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        Assert.Contains(io.ActionLog, a => a.Contains("B"));
    }

    [Fact]
    public async Task Unknown_page_presses_B_then_rejoins_graph()
    {
        var io = new FakeIO(
            GameScreen.Unknown, GameScreen.Unknown, GameScreen.Unknown,
            GameScreen.Schedule,
            GameScreen.MyLeagueHome);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        // B 共 2 次：步进器 1 次 + Schedule→MyLeagueHome 回退边 1 次
        Assert.Equal(2, io.CountPress("B"));
    }

    [Fact]
    public async Task Unknown_budget_exhaustion_aborts_with_exactly_6_B_presses()
    {
        var script = Enumerable.Repeat(GameScreen.Unknown, 21).ToArray();
        var io = new FakeIO(script);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedUnknown, result.Outcome);
        Assert.Equal(6, io.CountPress("B"));
        Assert.DoesNotContain(io.ActionLog, a => a is "A" or "Right" or "Left" or "Up" or "Down");
    }

    [Fact]
    public async Task InMatch_aborts_without_any_press()
    {
        var io = new FakeIO(GameScreen.InMatch);
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedInMatch, result.Outcome);
        Assert.Empty(io.ActionLog);
    }

    [Theory]
    [InlineData(GameScreen.HalfTime)]
    [InlineData(GameScreen.FullTime)]
    [InlineData(GameScreen.Result)]
    [InlineData(GameScreen.Rewards)]
    public async Task Post_match_pages_abort_without_press(GameScreen page)
    {
        var io = new FakeIO(page);
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedInMatch, result.Outcome);
        Assert.Empty(io.ActionLog);
    }

    [Fact]
    public async Task Five_dialogs_then_leave_succeeds()
    {
        var io = new FakeIO(
            GameScreen.Dialog, GameScreen.Dialog, GameScreen.Dialog,
            GameScreen.Dialog, GameScreen.Dialog,
            GameScreen.Home, GameScreen.MatchHub, GameScreen.MyLeagueHome);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        Assert.Equal(5, io.PopupCount);
    }

    [Fact]
    public async Task Sixth_consecutive_dialog_aborts_stuck()
    {
        var io = new FakeIO(Enumerable.Repeat(GameScreen.Dialog, 6).ToArray());
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedStuck, result.Outcome);
        Assert.Equal(5, io.PopupCount);
    }

    [Fact]
    public async Task Loading_over_90s_aborts_stuck()
    {
        var io = new FakeIO(Enumerable.Repeat(GameScreen.Loading, 31).ToArray());
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedStuck, result.Outcome);
        Assert.Equal(30, io.ActionLog.Count(a => a.Contains("3000")));
    }

    [Fact]
    public async Task TitleScreen_presses_A_then_continues()
    {
        var io = new FakeIO(
            GameScreen.TitleScreen,
            GameScreen.Home, GameScreen.MatchHub, GameScreen.MyLeagueHome);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        // A 共 3 次：标题点屏 1 + Home→MatchHub 边收尾 1 + MatchHub→MyLeagueHome 边收尾 1
        Assert.Equal(3, io.CountPress("A"));
    }

    [Fact]
    public async Task Disconnected_then_title_then_home_chain_recovers()
    {
        var io = new FakeIO(
            GameScreen.Disconnected,
            GameScreen.TitleScreen,
            GameScreen.Home, GameScreen.MatchHub, GameScreen.MyLeagueHome);

        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.ReachedTarget, result.Outcome);
        Assert.Equal(1, io.PopupCount);
        Assert.Equal(3, io.CountPress("A"));
    }

    [Fact]
    public async Task Three_hops_staying_on_same_page_aborts_stuck()
    {
        var io = new FakeIO(GameScreen.Shop, GameScreen.Shop, GameScreen.Shop, GameScreen.Shop);
        var result = await NewService(io).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedStuck, result.Outcome);
        Assert.Equal(3, io.Hops);
    }

    [Fact]
    public async Task Known_node_without_path_aborts_no_path()
    {
        var graph = new PageGraph(Array.Empty<Edge>());
        var io = new FakeIO(GameScreen.Home);
        var result = await NewService(io, graph: graph).NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedNoPath, result.Outcome);
        Assert.Empty(io.ActionLog);
    }

    [Fact]
    public async Task Zero_timeout_aborts_timeout_before_observing()
    {
        var io = new FakeIO();
        var result = await NewService(io, limits: new NavLimits(25, 6, 0))
            .NavigateToAsync(League, new NavContext(), default);

        Assert.Equal(NavOutcome.AbortedTimeout, result.Outcome);
        Assert.Equal(0, io.Observations);
    }

    private sealed class FakeIO : INavIO
    {
        private readonly Queue<GameScreen> _script;
        public List<string> ActionLog { get; } = new();
        public int PopupCount { get; private set; }
        public int Hops { get; private set; }
        public int Observations { get; private set; }

        public FakeIO(params GameScreen[] script) => _script = new Queue<GameScreen>(script);

        public Task<Observation> ObserveAsync(CancellationToken ct)
        {
            Observations++;
            if (_script.Count == 0)
                throw new InvalidOperationException("测试观察脚本已耗尽");
            return Task.FromResult(Obs(_script.Dequeue()));
        }

        public Task RunActionsAsync(IReadOnlyList<NavAction> actions, CancellationToken ct)
        {
            Hops++;
            foreach (var a in actions)
            {
                string tag = a.Type switch
                {
                    ActionType.Press => a.Button!.Value.ToString(),
                    ActionType.Dpad => a.Direction!.Value.ToString(),
                    ActionType.Wait => a.Ms!.Value.ToString(),
                    _ => "?",
                };
                ActionLog.Add(tag);
            }
            return Task.CompletedTask;
        }

        public Task<bool> HandleTransientAsync(Observation obs, CancellationToken ct)
        {
            PopupCount++;
            return Task.FromResult(true);
        }

        public Task RecoverAsync(Observation obs, CancellationToken ct)
        {
            ActionLog.Add("recover");
            return Task.CompletedTask;
        }

        public int CountPress(string button) => ActionLog.Count(a => a == button);
    }
}
