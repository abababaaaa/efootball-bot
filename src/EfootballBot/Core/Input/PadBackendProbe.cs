using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// XInput 槽位探测（P/Invoke + 玩家1占用判定），ViGEm 与 HIDMaestro 两个后端共用。
/// 也承载后端无关的「槽位 0 被占」用户提示。
/// </summary>
public static class PadBackendProbe
{
    [DllImport("xinput1_4.dll")]
    internal static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    internal struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    public static bool IsSlot0Occupied() => XInputGetState(0, out _) == 0;

    /// <summary>
    /// eFootball 菜单只响应 XInput Player 1（slot 0）。
    /// 连接前若真实手柄已占用 slot 0，虚拟手柄必然落到 slot 1+，输入被游戏忽略。
    /// 返回 null 表示 slot 0 空闲。
    /// </summary>
    public static string? SlotZeroOccupiedMessage(bool slot0Connected)
        => slot0Connected
            ? "检测到 XInput 玩家 1（slot 0）仍被真实手柄占用（自动禁用未生效：提权计划任务未注册或授权被拒）。请重启本程序并在授权窗口点「是」，或先拔下/关闭真实手柄，再点击开始挂机。"
            : null;

    /// <summary>连接前快照已占用槽位（供连接后差集找新设备的真实 slot）。</summary>
    public static HashSet<int> SnapshotOccupiedSlots()
    {
        var before = new HashSet<int>();
        for (int i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) before.Add(i);
        return before;
    }

    /// <summary>在 notIn 之外的槽位里探测新上线的手柄（最多等约 2.5 秒系统注册）。</summary>
    public static int ProbeNewSlot(HashSet<int> notIn)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(250);
            for (int i = 0; i < 4; i++)
                if (!notIn.Contains(i) && XInputGetState(i, out _) == 0) return i;
        }
        return -1;
    }
}
