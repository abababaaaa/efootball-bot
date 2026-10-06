using EfootballBot.Core.Input;

namespace EfootballBot.Core.Graph;

/// <summary>我的联赛主页焦点图：声明槽位与方向边，供 FocusGraph 寻路。</summary>
public static class MyLeagueHomeGraph
{
    /// <summary>构建我的联赛主页 FocusGraph（槽位坐标为归一化估计值，方向边已按真机布局修正）。</summary>
    public static FocusGraph Build()
    {
        var g = new FocusGraph();

        // 第一行：前往比赛 + i
        g.AddSlot("前往比赛", 0.13, 0.23, "前往比赛");
        g.AddSlot("i", 0.30, 0.23, "i");

        // 第二行：赛程表 | 兑换积分(上半) / 赛季记录(下半)
        g.AddSlot("赛程表", 0.13, 0.50, "赛程表");
        g.AddSlot("兑换积分", 0.42, 0.38, "兑换积分");  // 稍微上移，与赛季纪录拉开距离
        g.AddSlot("赛季纪录", 0.42, 0.62, "赛季纪录");  // 稍微下移

        // 第三行：赛事信息 | 建议
        g.AddSlot("赛事信息", 0.13, 0.78, "赛事信息");
        g.AddSlot("建议", 0.42, 0.78, "建议");

        // 方向边（真机布局：前往比赛 Right 只能到 i；赛程表 Right 大概率到赛季纪录）
        g.AddEdge("前往比赛", PadDir.Right, "i");
        g.AddEdge("前往比赛", PadDir.Down, "赛程表");
        g.AddEdge("i", PadDir.Down, "兑换积分");      // i 正下方是兑换积分上半区
        g.AddEdge("赛程表", PadDir.Right, "赛季纪录"); // 真机验证：大概率到赛季纪录
        g.AddEdge("赛程表", PadDir.Down, "赛事信息");
        g.AddEdge("兑换积分", PadDir.Down, "赛季纪录");
        g.AddEdge("赛季纪录", PadDir.Up, "兑换积分");  // 从赛季纪录回兑换积分
        g.AddEdge("赛季纪录", PadDir.Down, "建议");
        g.AddEdge("赛事信息", PadDir.Right, "建议");
        g.AddEdge("建议", PadDir.Up, "赛季纪录");

        // 反向边（Left/Up 回程）
        g.AddEdge("i", PadDir.Left, "前往比赛");
        g.AddEdge("赛程表", PadDir.Up, "前往比赛");
        g.AddEdge("兑换积分", PadDir.Left, "赛程表");
        g.AddEdge("赛季纪录", PadDir.Left, "赛程表");
        g.AddEdge("赛事信息", PadDir.Up, "赛程表");
        g.AddEdge("建议", PadDir.Left, "赛事信息");

        // 快捷路径：前往比赛直达兑换积分（避开 i 检测不到的问题）
        // 真机验证：前往比赛 Down→Down→Right 大概率到赛季纪录，再 Up 到兑换积分
        g.AddEdge("前往比赛", PadDir.Down, "赛程表");
        g.AddEdge("赛程表", PadDir.Down, "赛事信息");
        g.AddEdge("赛事信息", PadDir.Right, "建议");
        g.AddEdge("建议", PadDir.Up, "赛季纪录");
        g.AddEdge("赛季纪录", PadDir.Up, "兑换积分");

        return g;
    }
}
