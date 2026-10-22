using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// XInput 槽位探测（P/Invoke）。消费者：<see cref="VigemPad"/> 连接流程（slot0 占用阻断、
/// 虚拟手柄槽位探测）、<see cref="PhysicalPadControl"/>（禁用后轮询玩家 1 是否释放）、
/// 以及 <see cref="PadSlotWitness"/>（判断 slot0 上跑的到底是虚拟手柄还是真实手柄）。
/// </summary>
public static class PadBackendProbe
{
    [DllImport("xinput1_4.dll")]
    internal static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    public static bool IsSlot0Occupied() => IsSlotOccupied(0);

    /// <summary>指定 XInput 槽位（0-3）当前是否连接了设备。</summary>
    public static bool IsSlotOccupied(int userIndex) => XInputGetState(userIndex, out _) == 0;

    /// <summary>读取指定槽位的原始状态；未连接返回 null。</summary>
    public static XINPUT_STATE? TryRead(int userIndex)
        => XInputGetState(userIndex, out var s) == 0 ? s : null;
}
