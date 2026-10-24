using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EfootballBot.Core.Input;

/// <summary>
/// HID 设备枚举：物理手柄实例（HID Usage 判定）与虚拟总线子节点判定（真伪手柄唯一可靠判据）。
/// （v3.3.2 起虚拟手柄黑名单随 HidHide 整链剔除，ViGEm 实例路径匹配函数已删。）
/// </summary>
public static class HidDeviceEnumerator
{
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
                    || id.StartsWith(@"ROOT\SYSTEM\", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (CM_Get_Parent(out cur, cur, 0) != 0)
                    return false; // 到达树根：整链查清，非虚拟
            }
            return false; // 深度用尽未见虚拟特征：按物理
        }
        catch { return true; }
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

    /// <summary>
    /// XInput 可见手柄的 HID 集合实例路径特征：XUSB/xinputhid 子设备必带 &amp;IG_00…IG_03 段
    /// （Xbox 360/One/兼容 pad 均如此）。仅凭路径判定、不需要打开设备——
    /// 旧的「开接口读 Usage」判定在驱动过滤层拦截查询时恒空（2026-10-12 实测）。
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

    /// <summary>当前存在的物理手柄设备节点中「真的连着」的那部分：
    /// 蓝牙配对但已关机的手柄节点依然 present+enabled（2026-10-24 反馈包实机：Xbox 手柄蓝牙
    /// 配对后关机，每次启动都被当成在用的真实手柄误禁用一轮，禁用升级白耗 16-20 秒启动）。
    /// 连接态以 DEVPKEY_Device_IsConnected 为准（自节点与祖先链任一明确报「未连接」即算未连接；
    /// 全链查不到该属性——USB 节点常见——按已连接兜底，宁可多禁不漏禁）。</summary>
    public static List<HidInstance> FindConnectedPhysicalGamepadDevNodes()
        => FindPhysicalGamepadDevNodes()
            .Where(n => IsDevNodeConnected(n.DevInst)).ToList();

    /// <summary>设备节点（含祖先链）是否「当前物理连接」。蓝牙栈在 BTHLE/BTHENUM 节点上维护
    /// DEVPKEY_Device_IsConnected，是「配对」与「连接」的唯一可靠区分；属性缺失或查询异常按
    /// 已连接兜底（保持旧行为）。</summary>
    public static bool IsDevNodeConnected(int devInst)
    {
        try
        {
            int cur = devInst;
            for (int depth = 0; depth <= 3; depth++)
            {
                var (hasProperty, connected) = QueryDevNodeIsConnected(cur);
                if (hasProperty) return connected;
                if (CM_Get_Parent(out cur, cur, 0) != 0)
                    return true; // 到达树根整链无该属性：按已连接兜底
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>读单个节点的 DEVPKEY_Device_IsConnected。hasProperty=false = 节点无此属性。</summary>
    private static (bool HasProperty, bool Connected) QueryDevNodeIsConnected(int devInst)
    {
        var key = new DEVPROPKEY
        {
            FmtId = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), // DEVPKEY_Device_IsConnected
            Pid = 2,
        };
        var buf = new byte[1];
        uint size = (uint)buf.Length;
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0);
        if (cr == CrBufferSmall)
        {
            buf = new byte[size];
            cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
        }
        if (cr != 0) return (false, true);
        if (type != DevpropTypeBoolean || size < 1) return (false, true);
        return (true, buf[0] != 0);
    }

    // ---------- Win32 ----------

    private const int CrBufferSmall = 0x1A; // CR_BUFFER_SMALL
    private const uint DevpropTypeBoolean = 0x0000000B;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

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
