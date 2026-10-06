using EfootballBot.Core.Input;

namespace EfootballBot.Core.Graph;

/// <summary>我的联赛主页焦点图：声明槽位与方向边，供 FocusGraph 寻路。</summary>
public static class MyLeagueHomeGraph
{
    /// <summary>构建我的联赛主页 FocusGraph（槽位坐标为归一化估计值，方向边待真机验证）。</summary>
    public static FocusGraph Build()
    {
        var g = new FocusGraph();

        // 左侧列
        g.AddSlot("前往比赛", 0.13, 0.23, "前往比赛");
        g.AddSlot("i", 0.30, 0.23, "i");
        g.AddSlot("赛程表", 0.13, 0.50, "赛程表");
        g.AddSlot("赛事信息", 0.13, 0.78, "赛事信息");

        // 中间列
        g.AddSlot("兑换积分", 0.42, 0.42, "兑换积分");
        g.AddSlot("赛季纪录", 0.42, 0.60, "赛季纪录");
        g.AddSlot("建议", 0.42, 0.78, "建议");

        // 方向边（根据截图布局推断，待真机验证）
        g.AddEdge("前往比赛", PadDir.Down, "赛程表");
        g.AddEdge("前往比赛", PadDir.Right, "i");
        g.AddEdge("i", PadDir.Down, "兑换积分");
        g.AddEdge("赛程表", PadDir.Right, "兑换积分");
        g.AddEdge("赛程表", PadDir.Down, "赛事信息");
        g.AddEdge("兑换积分", PadDir.Down, "赛季纪录");
        g.AddEdge("赛季纪录", PadDir.Down, "建议");
        g.AddEdge("赛事信息", PadDir.Right, "建议");
        g.AddEdge("建议", PadDir.Up, "赛季纪录");
        g.AddEdge("赛季纪录", PadDir.Up, "兑换积分");
        g.AddEdge("兑换积分", PadDir.Up, "i");
        g.AddEdge("兑换积分", PadDir.Left, "赛程表");
        g.AddEdge("赛事信息", PadDir.Up, "赛程表");

        return g;
    }
}
