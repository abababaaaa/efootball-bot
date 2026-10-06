using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

public class FocusGraphSmokeTests
{
    private static FocusGraph LineGraph() => new FocusGraph()
        .AddSlot("a", 0.2, 0.5)
        .AddSlot("b", 0.5, 0.5)
        .AddSlot("c", 0.8, 0.5)
        .AddEdge("a", PadDir.Right, "b")
        .AddEdge("b", PadDir.Left, "a")
        .AddEdge("b", PadDir.Right, "c")
        .AddEdge("c", PadDir.Left, "b");

    [Fact]
    public void BFS_finds_shortest_directional_path()
    {
        var p = LineGraph().FindPath("a", "c");
        Assert.NotNull(p);
        Assert.Equal(2, p!.Count);
        Assert.Equal((PadDir.Right, "b"), p[0]);
        Assert.Equal((PadDir.Right, "c"), p[1]);
    }

    [Fact]
    public void BFS_same_slot_is_empty()
    {
        Assert.Empty(LineGraph().FindPath("a", "a")!);
    }

    [Fact]
    public void BFS_disconnected_returns_null()
    {
        var g = new FocusGraph().AddSlot("a", 0, 0).AddSlot("z", 1, 1);
        Assert.Null(g.FindPath("a", "z"));
    }
}
