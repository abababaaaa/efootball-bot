using System.Text;

namespace EfootballBot.Core.Input;

/// <summary>
/// 手柄链路结构化诊断（P1-3）：把"为什么连不上 / 为什么游戏没反应"变成一张能直接看的表。
///
/// 背景：用户反馈里最常见的一条是「无真实手柄连接，但无法开始，一直显示虚拟手柄无法连接」
/// （feedback_analysis/20261024_220229）。旧实现最终只抛一句"虚拟手柄连接失败，请查看上方日志"，
/// 用户拿不到任何可操作信息，作者也得靠反馈包里的日志逐行猜。
/// 这里把 XInput 槽位、物理节点、接管开关、驱动状态、微码读回值一次性拼出来：
/// 进日志 + 进异常消息（GUI 弹窗直接显示），用户截图就能定位。
/// </summary>
public static class PadDiagnostics
{
    /// <summary>4 个 XInput 槽位的占用表（微秒级，随时可调）。</summary>
    public static string DescribeSlots()
    {
        var sb = new StringBuilder("XInput 槽位：");
        for (int i = 0; i < 4; i++)
        {
            bool occupied;
            try { occupied = PadBackendProbe.IsSlotOccupied(i); } catch { occupied = false; }
            if (i > 0) sb.Append("  ");
            sb.Append(i).Append('=').Append(occupied ? "占用" : "空闲");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 完整诊断（多行）。任何一步探测失败都不抛，只在对应行写明"探测失败"——
    /// 诊断本身绝不能成为新的故障点。
    /// </summary>
    /// <param name="takeoverEnabled">设置里「挂机时禁用真实手柄」是否开启。</param>
    /// <param name="pad">当前虚拟手柄（可为 null）。</param>
    /// <param name="witness">最近一次微码探测结果（可为 null）。</param>
    public static string Collect(bool takeoverEnabled, IVirtualPad? pad,
        PadSlotWitness.WitnessResult? witness = null)
    {
        var lines = new List<string>();

        string slots;
        try { slots = DescribeSlots(); }
        catch (Exception ex) { slots = "XInput 槽位：探测失败（" + ex.Message + "）"; }
        lines.Add(slots);

        try
        {
            var nodes = HidDeviceEnumerator.FindPhysicalGamepadDevNodes();
            if (nodes.Count == 0)
            {
                lines.Add("物理手柄节点：0 个已启用");
            }
            else
            {
                int connected = 0;
                foreach (var n in nodes)
                    if (HidDeviceEnumerator.IsDevNodeConnected(n.DevInst)) connected++;
                lines.Add($"物理手柄节点：{nodes.Count} 个已启用，其中已连接 {connected} 个");
                foreach (var n in nodes.Take(4))
                    lines.Add("  · " + n.InstancePath);
            }
        }
        catch (Exception ex)
        {
            lines.Add("物理手柄节点：枚举失败（" + ex.Message + "）");
        }

        try
        {
            bool driver = VigemPad.IsDriverInstalled();
            lines.Add($"ViGEmBus 驱动：{(driver ? "已注册" : "未安装")}");
        }
        catch { lines.Add("ViGEmBus 驱动：探测失败"); }

        lines.Add($"接管开关：{(takeoverEnabled ? "开（挂机时禁用真实手柄）" : "关（不碰真实手柄）")}");

        if (pad is null)
        {
            lines.Add("虚拟手柄：未创建");
        }
        else
        {
            string idx = pad.UserIndex >= 0 ? $"玩家 {pad.UserIndex + 1}" : "槽位未知";
            lines.Add($"虚拟手柄：{(pad.IsConnected ? "已连接" : "未连接")}（{idx}）");
        }

        if (witness is { } w)
            lines.Add(PadSlotWitness.DescribeWitness(w, VigemPad.IdentityCode));

        return string.Join(Environment.NewLine, lines);
    }
}
