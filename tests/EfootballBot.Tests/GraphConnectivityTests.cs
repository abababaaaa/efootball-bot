using EfootballBot.Core.Graph;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public class GraphConnectivityTests
{
    private static readonly string[] GraphNodes =
    {
        nameof(GameScreen.Home),
        nameof(GameScreen.MatchHub),
        nameof(GameScreen.EventHub),
        nameof(GameScreen.EventInfo),
        nameof(GameScreen.MyLeagueHome),
        nameof(GameScreen.Schedule),
        nameof(GameScreen.Shop),
        nameof(GameScreen.ShopConfirm),
        nameof(GameScreen.PreMatch),
        nameof(GameScreen.DifficultySelect),
        nameof(GameScreen.EventSettings),
        nameof(GameScreen.ChallengeHome),
        nameof(GameScreen.ChallengeList),
        nameof(GameScreen.ActivityDetail),
        nameof(GameScreen.NoEvent),
        nameof(GameScreen.CampaignBoard),
        nameof(GameScreen.DailyRewards),
        nameof(GameScreen.ItemsBar),
        nameof(GameScreen.KeyMatchBadge),
    };

    public static IEnumerable<object[]> Targets =>
        new[] { nameof(GameScreen.MyLeagueHome), nameof(GameScreen.EventHub) }
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Targets))]
    public void Every_graph_node_reaches_target_without_purchase_context(string target)
    {
        var graph = new PageGraph(PageCatalog.Edges);
        foreach (var node in GraphNodes)
        {
            var path = graph.FindPath(node, target, new NavContext { NeedBuyItems = false });
            Assert.NotNull(path);
            if (node != target) Assert.NotEmpty(path!);
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Every_graph_node_reaches_target_with_purchase_context(string target)
    {
        var graph = new PageGraph(PageCatalog.Edges);
        foreach (var node in GraphNodes)
        {
            var path = graph.FindPath(node, target, new NavContext { NeedBuyItems = true });
            Assert.NotNull(path);
            if (node != target) Assert.NotEmpty(path!);
        }
    }

    [Fact]
    public void Exactly_18_new_edges_are_unverified()
    {
        var unverified = PageCatalog.Edges.Where(e => !e.Verified).ToList();
        Assert.Equal(18, unverified.Count);
    }
}
