using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EfootballBot.Core.Input;

/// <summary>
/// HID 设备枚举：虚拟手柄实例（路径前缀/驱动特征判定）与物理手柄实例（HID Usage 判定）。
/// 黑名单条目格式 = 设备实例路径（如 HID\VID_045E&PID_028E\6&2DF92C3&0&0000）。
/// ViGEm 前缀常量保留：旧版本写入 HidHide 黑名单的 ViGEm 实例条目仍需识别以精确清理。
/// </summary>
public static class HidDeviceEnumerator
{
    /// <summary>旧 ViGEm 虚拟 Xbox360 手柄在 HID 层的固定硬件 ID 前缀（仅用于清理旧黑名单条目）。</summary>
    public const string ViGEmInstancePrefix = @"HID\VID_045E&PID_028E";

    /// <summary>旧 ViGEm 虚拟 DualShock4 手柄的硬件 ID 前缀（与真实 DS4 同形，靠父设备判真假）。</summary>
    public const string ViGEmDs4InstancePrefix = @"HID\VID_054C&PID_05C4";

    /// <summary>HIDMaestro 虚拟总线节点前缀（探针 2026-10-20 实测定案）：ROOT\HIDCLASS\HM_<hash>，
    /// hash 由 identityKey 决定性生成；实例路径不含 VID/PID，也不含"HIDMAESTRO"字样。</summary>
    public const string HidMaestroBusPrefix = @"ROOT\HIDCLASS\HM_";

    /// <summary>HIDMaestro 的 HID 集合节点前缀：HID\HIDCLASS\1&amp;<hash>&amp;0&amp;0000（其父即虚拟总线节点）。
    /// "HIDCLASS" 硬件 ID 仅 HIDMaestro 使用，真实设备不会撞此形态。</summary>
    public const string HidMaestroHidPrefix = @"HID\HIDCLASS\";

    /// <summary>HIDMaestro 虚拟设备实例判定（在线检查与黑名单共用；路径纯字符串，不打开设备）。</summary>
    public static bool IsHidMaestroInstance(string instancePath)
        => instancePath.StartsWith(HidMaestroBusPrefix, StringComparison.OrdinalIgnoreCase)
           || instancePath.StartsWith(HidMaestroHidPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsViGEmInstance(string instancePath)
        => instancePath.StartsWith(ViGEmInstancePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 黑名单/名单用的「虚拟手柄实例路径」判定（纯字符串，不打开设备）：
    /// ViGEm Xbox360 前缀 ∥ ViGEm DS4 前缀 ∥ HIDMaestro 节点前缀（ROOT\HIDCLASS\HM_ ∥ HID\HIDCLASS\，
    /// 2026-10-20 探针实测定案；旧的 Contains("HIDMAESTRO") 永远匹配不上真实路径，导致隔离空转）。
    /// 真实 DS4 与虚拟 DS4 路径同形——凡需分辨真假（如禁用物理手柄）必须再过父设备判定，
    /// 此处仅用于「我们加入黑名单的虚拟设备」。
    /// </summary>
    public static bool IsVirtualPadInstancePath(string instancePath)
        => IsViGEmInstance(instancePath)
           || instancePath.StartsWith(ViGEmDs4InstancePrefix, StringComparison.OrdinalIgnoreCase)
           || IsHidMaestroInstance(instancePath);

    /// <summary>
    /// 设备节点的祖先链（最多 4 级）里是否出现虚拟总线特征——真假手柄唯一可靠判据。
    /// 两种实机拓扑（2026-10-19 现场探测）：
    /// ① ViGEm Xbox360：HID 集合的父即 ViGEmBus 命名节点；
    /// ② ViGEm DS4：HID 集合 → USB\VID_054C&PID_05C4&REV_xxxx → <b>ROOT\SYSTEM\00xx</b>（根枚举，
    ///    本机无任何名为 ViGEmBus 的节点）。真实 USB/蓝牙设备挂在集线器/ACPI 下，永不撞 ROOT\SYSTEM。
    /// 父节点链中途查询失败按虚拟兜底（保守：宁可多拉黑不漏黑名单）；走到树根查清则如实判定。
    /// </summary>
    public static bool IsVirtualBusChild(int devInst)
    {
        try
        {
            int cur = devInst;
            var sb = new StringBuilder(512);
            for (int depth = 0; depth <= 4; depth++)
            {
                if (CM_Get_Device_ID(cur, sb, sb.Capacity, 0) != 0)
                    return true; // 节点查询失败：按虚拟兜底
                var id = sb.ToString();
                if (id.StartsWith("ViGEmBus", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"ROOT\SYSTEM\", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"ROOT\HIDCLASS\", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (CM_Get_Parent(out cur, cur, 0) != 0)
                    return false; // 到达树根：整链查清，非虚拟
            }
            return false; // 深度用尽未见虚拟特征：按物理
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

    /// <summary>当前存在的虚拟手柄实例路径（当前仅 HIDMaestro；含旧黑名单里的 ViGEm 条目形态）。</summary>
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
    /// 蓝牙 LE 连接的手柄：输入经 BTHLEDEVICE 的 GATT HID 服务（UUID 1812），
    /// <b>不产生经典 HID\VID…&IG_ 集合</b>（2026-10-19 实机：Xbox Series 手柄 BLE 连接只有
    /// BTHLEDEVICE\{00001812…}_DEV_VID&02045E_PID&0B22 一个可枚举节点）。
    /// 1812 服务键鼠也会用，按 VID 白名单限定手柄厂商（Xbox/Sony/Nintendo/Hori/PowerA/8BitDo）。
    /// </summary>
    private static readonly string[] BleGamepadVidTags =
    {
        "_DEV_VID&02045E_", "_DEV_VID&02054C_", "_DEV_VID&02057E_",
        "_DEV_VID&020F0D_", "_DEV_VID&0220D6_", "_DEV_VID&022DC8_",
    };

    public static bool IsBleGamepadInstance(string instancePath)
        => instancePath.StartsWith(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase)
           && BleGamepadVidTags.Any(t => instancePath.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 当前存在的物理手柄设备节点（<b>仅统计已启用的</b>——上次挂机禁用过的节点依然 PRESENT）：
    /// XInput 可见（&amp;IG_ 段特征）∥ PlayStation 系前缀 ∥ 蓝牙 LE 手柄（BTHLEDEVICE GATT 节点），
    /// 且祖先链非虚拟总线（排除本程序自己的虚拟手柄——Xbox360 档与真实 360 手柄、
    /// DS4 档与真实 DS4 路径均完全同形）。全程不打开设备。
    /// </summary>
    public static List<HidInstance> FindPhysicalGamepadDevNodes()
    {
        var result = new List<HidInstance>();
        foreach (var inst in EnumerateAllInstances())
        {
            bool gamepad = IsXInputStyleInstance(inst.InstancePath)
                || PhysicalPlayStationPrefixes.Any(p =>
                    inst.InstancePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || IsBleGamepadInstance(inst.InstancePath);
            if (!gamepad) continue;
            if (IsVirtualBusChild(inst.DevInst)) continue;
            if (IsDevNodeProblemDisabled(inst.DevInst)) continue;
            result.Add(inst);
        }
        return result;
    }

    // ---------- Win32 ----------

    private const uint DIGCF_PRESENT = 0x2;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22; // CM_PROB_DISABLED

    /// <summary>设备节点是否处于「已禁用」状态（上次挂机禁用过的节点依然 PRESENT，
    /// 等待循环若把它们算作「在用的真实手柄」就永远不会退出）。</summary>
    public static bool IsDevNodeProblemDisabled(int devInst)
    {
        try
        {
            return CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0
                && (status & DnHasProblem) != 0 && problem == CmProbDisabled;
        }
        catch { return false; }
    }

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblem,
        int dnDevInst, uint ulFlags);

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
