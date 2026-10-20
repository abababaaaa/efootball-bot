using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// XInput 槽位探测（P/Invoke）。3.2.0 起主要消费者是 <see cref="RealPadActivityMonitor"/>
/// （真实手柄活动轮询）；纯 HID 虚拟手柄（DS4 档）不占 XInput 槽位，连接流程不再做槽位探测。
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
}
