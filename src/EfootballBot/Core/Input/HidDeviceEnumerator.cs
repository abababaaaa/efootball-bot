using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EfootballBot.Core.Input;

/// <summary>
/// HID 设备枚举：ViGEm 虚拟手柄实例（路径前缀判定）与物理手柄实例（HID Usage 判定）。
/// 黑名单条目格式 = 设备实例路径（如 HID\VID_045E&PID_028E\6&2DF92C3&0&0000）。
/// </summary>
public static class HidDeviceEnumerator
{
    /// <summary>ViGEm 虚拟 Xbox360 手柄在 HID 层的固定硬件 ID 前缀（真实微软手柄走 XUSB 枚举器，不撞此路径）。</summary>
    public const string ViGEmInstancePrefix = @"HID\VID_045E&PID_028E";

    /// <summary>ViGEm 虚拟 DualShock4 手柄的硬件 ID 前缀（与真实 DS4 同形，靠父设备判真假）。</summary>
    public const string ViGEmDs4InstancePrefix = @"HID\VID_054C&PID_05C4";

    public static bool IsViGEmInstance(string instancePath)
        => instancePath.StartsWith(ViGEmInstancePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 黑名单/名单用的「虚拟手柄实例路径」判定（纯字符串，不打开设备）：
    /// ViGEm Xbox360 前缀 ∥ ViGEm DS4 前缀 ∥ HIDMaestro 驱动特征（设备路径含驱动名，
    /// 具体形态待实机校准，先宽匹配）。真实 DS4 与虚拟 DS4 路径同形——凡需分辨真假
    /// （如禁用物理手柄）必须再过父设备判定，此处仅用于「我们加入黑名单的虚拟设备」。
    /// </summary>
    public static bool IsVirtualPadInstancePath(string instancePath)
        => IsViGEmInstance(instancePath)
           || instancePath.StartsWith(ViGEmDs4InstancePrefix, StringComparison.OrdinalIgnoreCase)
           || instancePath.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);

    /// <summary>设备节点的父设备是否为虚拟手柄总线（ViGEmBus / HIDMaestro）。</summary>
    public static bool IsVirtualBusChild(int devInst)
    {
        try
        {
            if (CM_Get_Parent(out int parent, devInst, 0) != 0) return true; // 无法判定→按虚拟兜底
            var sb = new StringBuilder(512);
            if (CM_Get_Device_ID(parent, sb, sb.Capacity, 0) != 0) return true;
            var id = sb.ToString();
            return id.StartsWith("ViGEmBus", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>
    /// 设备接口路径 → 设备实例路径：
    /// \\?\hid#vid_045e&amp;pid_028e#7&amp;2df92c3#{guid} → hid\vid_045e&amp;pid_028e\7&amp;2df92c3
    /// </summary>
    public static string InterfacePathToInstancePath(string interfacePath)
    {
        if (interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            interfacePath = interfacePath[4..];
        int hash = interfacePath.IndexOf("#{", StringComparison.Ordinal);
        if (hash >= 0) interfacePath = interfacePath[..hash];
        return Uri.UnescapeDataString(interfacePath.Replace('#', '\\'));
    }

    /// <summary>HID 设备实例（实例路径 + 设备节点句柄，父节点判定用）。</summary>
    public sealed record HidInstance(string InstancePath, int DevInst);

    /// <summary>当前系统中存在的全部 HID 设备实例（Present only）。</summary>
    public static List<HidInstance> EnumerateAllInstances()
    {
        var list = new List<HidInstance>();
        Guid hidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA"); // GUID_DEVCLASS_HIDCLASS
        IntPtr h = SetupDiGetClassDevs(ref hidClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (h == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs 失败");
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(h, i, ref data); i++)
            {
                var sb = new StringBuilder(512);
                if (CM_Get_Device_ID(data.devInst, sb, sb.Capacity, 0) == 0) // CR_SUCCESS
                    list.Add(new HidInstance(sb.ToString(), data.devInst));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return list;
    }

    /// <summary>
    /// 设备节点的父设备是否为 ViGEmBus（虚拟手柄 PDO 挂在 ViGEm 总线上，
    /// 而真实 Xbox 手柄挂在 USB/XUSB 枚举器下）——唯一可靠的真假判据：
    /// 真实 360 手柄的 HID 集合路径与 ViGEm 完全同形（HID\VID_045E&PID_028E&IG_xx），
    /// 仅靠前缀无法区分。父节点不可判定时按前缀结论兜底（视为 ViGEm）。
    /// </summary>
    public static bool IsViGEmBusChild(int devInst)
    {
        try
        {
            if (CM_Get_Parent(out int parent, devInst, 0) != 0) return true; // 无法判定→按前缀结论兜底
            var sb = new StringBuilder(512);
            if (CM_Get_Device_ID(parent, sb, sb.Capacity, 0) != 0) return true;
            return sb.ToString().StartsWith("ViGEmBus", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>设备节点的父节点（实例路径 + 句柄）；取不到返回 null。供禁用升级/父设备判定用。</summary>
    public static HidInstance? GetParent(int devInst)
    {
        try
        {
            if (CM_Get_Parent(out int parent, devInst, 0) != 0) return null;
            var sb = new StringBuilder(512);
            if (CM_Get_Device_ID(parent, sb, sb.Capacity, 0) != 0) return null;
            return new HidInstance(sb.ToString(), parent);
        }
        catch { return null; }
    }

    /// <summary>当前存在的 ViGEm 虚拟手柄实例路径（前缀 + 父设备为 ViGEmBus 双重判定）。</summary>
    public static List<string> FindViGEmInstances()
    {
        var result = new List<string>();
        foreach (var inst in EnumerateAllInstances())
        {
            if (!IsViGEmInstance(inst.InstancePath)) continue;
            if (IsViGEmBusChild(inst.DevInst)) result.Add(inst.InstancePath);
        }
        return result;
    }

    /// <summary>
    /// 当前存在的虚拟手柄实例路径（全部后端：ViGEm Xbox 360 / ViGEm DS4 / HIDMaestro）。
    /// HidHide 黑名单以此为准——DS4 默认后端的实例与真实 DS4 同形，必须经父设备双重判定。
    /// </summary>
    public static List<string> FindVirtualPadInstances()
    {
        var result = new List<string>();
        foreach (var inst in EnumerateAllInstances())
        {
            if (!IsVirtualPadInstancePath(inst.InstancePath)) continue;
            if (IsVirtualBusChild(inst.DevInst)) result.Add(inst.InstancePath);
        }
        return result;
    }

    /// <summary>
    /// XInput 可见手柄的 HID 集合实例路径特征：XUSB/xinputhid 子设备必带 &amp;IG_00…IG_03 段
    /// （Xbox 360/One/兼容 pad 均如此）。仅凭路径判定、不需要打开设备——
    /// HidHide Active 时对非白名单进程连 access=0 的查询打开都会拒绝，
    /// 旧的「开接口读 Usage」判定在手柄已被列入黑名单的机器上恒空（2026-10-12）。
    /// </summary>
    public static bool IsXInputStyleInstance(string instancePath) =>
        global::System.Text.RegularExpressions.Regex.IsMatch(instancePath,
            @"^HID\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}&IG_0[0-3]\\",
            global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 真实 PlayStation 系手柄的硬件 ID 前缀（DS4 两代/DualSense/Edge）：
    /// 不带 XInput 的 &amp;IG_ 段，旧正则看不见——纯 HID 后端下它们会经 DirectInput
    /// 路径与虚拟手柄同时驱动游戏，禁用清单必须覆盖（靠父设备判定排除虚拟 DS4）。
    /// </summary>
    private static readonly string[] PhysicalPlayStationPrefixes =
    {
        @"HID\VID_054C&PID_05C4", // DualShock4 一代
        @"HID\VID_054C&PID_09CC", // DualShock4 二代
        @"HID\VID_054C&PID_0CE6", // DualSense
        @"HID\VID_054C&PID_0E5F", // DualSense Edge
    };

    /// <summary>
    /// 当前存在的物理手柄设备节点：XInput 可见（&amp;IG_ 段特征）或 PlayStation 系前缀，
    /// 且父设备非虚拟总线（排除本程序自己的虚拟手柄——Xbox360 档与真实 360 手柄、
    /// DS4 档与真实 DS4 路径均完全同形）。全程不打开设备。
    /// </summary>
    public static List<HidInstance> FindPhysicalGamepadDevNodes()
    {
        var result = new List<HidInstance>();
        foreach (var inst in EnumerateAllInstances())
        {
            bool gamepad = IsXInputStyleInstance(inst.InstancePath)
                || PhysicalPlayStationPrefixes.Any(p =>
                    inst.InstancePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!gamepad) continue;
            if (IsVirtualBusChild(inst.DevInst)) continue;
            result.Add(inst);
        }
        return result;
    }

    // ---------- Win32 ----------

    private const uint DIGCF_PRESENT = 0x2;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(int devInst, StringBuilder buffer, int bufferLen, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out int pdnParent, int dnDevInst, int ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid classGuid;
        public int devInst;
        public IntPtr reserved;
    }
}
