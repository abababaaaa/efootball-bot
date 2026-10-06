using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using Xunit;
using NodeState = EfootballBot.Core.Graph.NodeState;

namespace EfootballBot.Tests;

public class SmartNavigationTests
{
    // ---------------- HomeNode 判定表 ----------------

    [Theory]
    // capsule: 0=无效 1=主菜单窗口 2=其他 Tab 窗口
    [InlineData(1, true, false, NodeState.Anchored)]
    [InlineData(1, false, false, NodeState.Uncertain)]   // 胶囊在主窗口但无内容锚点：冲突
    [InlineData(2, false, true, NodeState.OffTrack)]     // 胶囊在其他 Tab + 跑偏词
    [InlineData(2, true, false, NodeState.Uncertain)]    // 冲突：胶囊在其他 Tab 但有主菜单锚点
    [InlineData(2, false, false, NodeState.Uncertain)]   // 胶囊在其他 Tab 但无内容词：不盲动
    [InlineData(0, false, true, NodeState.OffTrack)]     // 胶囊无效但命中跑偏词
    [InlineData(0, false, false, NodeState.Uncertain)]
    public void HomeNode_decision_table(int capsule, bool mainAnchor, bool offWord, NodeState expected)
    {
        Assert.Equal(expected, HomeNode.Decide(capsule, mainAnchor, offWord));
    }

    [Fact]
    public void HomeNode_recovery_step_is_LB()
    {
        var node = new HomeNode();
        var step = node.NextStep(null!);
        Assert.Equal(ActionType.Press, step[0].Type);
        Assert.Equal(PadButton.LeftShoulder, step[0].Button);
    }

    // ---------------- MatchHubNode 判定 ----------------

    [Fact]
    public void MatchHub_requires_page_and_card()
    {
        Assert.Equal(NodeState.Uncertain,
            MatchHubNode.Decide(false, MatchHubCards.Event, MatchHubCards.Event));
        Assert.Equal(NodeState.Uncertain,
            MatchHubNode.Decide(true, null, MatchHubCards.Event));
    }

    [Fact]
    public void MatchHub_anchored_when_card_equals_target()
    {
        Assert.Equal(NodeState.Anchored,
            MatchHubNode.Decide(true, MatchHubCards.MyLeague, MatchHubCards.MyLeague));
        Assert.Equal(NodeState.Anchored,
            MatchHubNode.Decide(true, MatchHubCards.Event, MatchHubCards.Event));
    }

    [Theory]
    [InlineData(MatchHubCards.Event)]
    [InlineData(MatchHubCards.EventLeague)]
    public void MatchHub_transit_on_path_cards_when_target_myleague(string card)
    {
        Assert.Equal(NodeState.Transit,
            MatchHubNode.Decide(true, card, MatchHubCards.MyLeague));
    }

    [Theory]
    [InlineData(MatchHubCards.Friend)]
    [InlineData(MatchHubCards.Friendly)]
    [InlineData(MatchHubCards.Custom)]
    [InlineData(MatchHubCards.Quick)]
    public void MatchHub_offtrack_on_forbidden_cards_when_target_myleague(string card)
    {
        Assert.Equal(NodeState.OffTrack,
            MatchHubNode.Decide(true, card, MatchHubCards.MyLeague));
    }

    [Fact]
    public void MatchHub_offtrack_even_for_myleague_card_when_target_event()
    {
        // 目标=活动时，「我的联赛」仍是合法路径卡（Transit，不是 OffTrack）
        Assert.Equal(NodeState.Transit,
            MatchHubNode.Decide(true, MatchHubCards.MyLeague, MatchHubCards.Event));
        Assert.Equal(NodeState.OffTrack,
            MatchHubNode.Decide(true, MatchHubCards.Friend, MatchHubCards.Event));
    }

    [Fact]
    public void MatchHub_constructor_rejects_unknown_card()
    {
        Assert.Throws<ArgumentException>(() => new MatchHubNode("nope"));
    }

    // ---------------- FocusGraph BFS 路径 ----------------

    private static List<(PadDir Dir, string To)> Path(string from, string to)
    {
        var p = MatchHubNode.BuildGraph().FindPath(from, to);
        Assert.NotNull(p);
        return p!;
    }

    [Fact]
    public void BFS_from_quick_to_myleague_goes_up_then_left()
    {
        var p = Path(MatchHubCards.Quick, MatchHubCards.MyLeague);
        Assert.Equal((PadDir.Up, MatchHubCards.Friend), p[0]);
        Assert.Equal((PadDir.Left, MatchHubCards.MyLeague), p[1]);
    }

    [Fact]
    public void BFS_from_custom_to_event_uses_up_then_lefts()
    {
        var p = Path(MatchHubCards.Custom, MatchHubCards.Event);
        Assert.Equal(3, p.Count);
        Assert.Equal(PadDir.Up, p[0].Dir);
        Assert.Equal(MatchHubCards.MyLeague, p[0].To);
        Assert.Equal(PadDir.Left, p[1].Dir);
        Assert.Equal(PadDir.Left, p[2].Dir);
    }

    [Fact]
    public void BFS_from_friendly_to_myleague_is_two_lefts()
    {
        var p = Path(MatchHubCards.Friendly, MatchHubCards.MyLeague);
        Assert.Equal(2, p.Count);
        Assert.Equal((PadDir.Left, MatchHubCards.Friend), p[0]);
        Assert.Equal((PadDir.Left, MatchHubCards.MyLeague), p[1]);
    }

    [Fact]
    public void BFS_from_event_to_myleague_is_two_rights()
    {
        var p = Path(MatchHubCards.Event, MatchHubCards.MyLeague);
        Assert.Equal(2, p.Count);
        Assert.All(p, x => Assert.Equal(PadDir.Right, x.Dir));
    }

    [Fact]
    public void BFS_from_eleague_right_lands_myleague()
    {
        var p = Path(MatchHubCards.EventLeague, MatchHubCards.MyLeague);
        Assert.Single(p);
        Assert.Equal((PadDir.Right, MatchHubCards.MyLeague), p[0]);
    }

    [Fact]
    public void BFS_same_card_is_empty_path()
    {
        Assert.Empty(Path(MatchHubCards.MyLeague, MatchHubCards.MyLeague));
    }

    [Fact]
    public void BFS_budgets_match_spec()
    {
        Assert.Equal(10, new MatchHubNode(MatchHubCards.MyLeague).StepBudget);
        Assert.Equal(3, new HomeNode().StepBudget);
    }
}
