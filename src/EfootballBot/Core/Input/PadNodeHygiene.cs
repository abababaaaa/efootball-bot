using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EfootballBot.Core.Input;

/// <summary>手柄类 PnP 设备节点（ViGEm 虚拟手柄 / 真实手柄 / 其他虚拟手柄）。</summary>
public sealed record PadDeviceNode(
    string InstanceId,
    string Name,
    string DeviceClass,
    bool Started,
    bool IsVirtual,
    bool RealBus,
    string Service);

/// <summary>设备节点归类（纯逻辑，可单测）。</summary>
public enum PadNodeKind
{
    /// <summary>真实总线上的手柄，且正在运行。</summary>
    Physical,
    /// <summary>ViGEm 虚拟手柄，运行中（本程序或残留的别家客户端）。</summary>
    VirtualLive,
    /// <summary>ViGEm 虚拟手柄残留节点（Disconnected/Phantom），需要卸载后再重载。</summary>
    VirtualStale,
    /// <summary>其他软件枚举出来的虚拟手柄（SWD/ROOT/VHF），同样会抢 slot0。</summary>
    OtherVirtual,
    /// <summary>其余（被禁用的真实手柄、非手柄类 HID 等），不参与决策。</summary>
    Inactive,
}

/// <summary>
/// 需求 1 的落点：**先判断真实手柄在不在用**，只有"没有真实手柄在用"时才做设备管理器层面的
/// 卸载 + 重载（`pnputil /remove-device` → `/scan-devices`）。
///
/// 为什么必须闸门：卸载/重载会短暂扰动 XInput 枚举，用户在用手柄时做这件事等于打断他；
/// 而且真实手柄占着 slot0 时，重载虚拟手柄也抢不回来——正确做法是走进程内代理
/// （见 <see cref="ProxyPad"/>），真实手柄原地不动。
///
/// 判定依据（2026-11-11 实机核对）：
/// - 真实手柄：`USB\\` / `HID\\{GUID}` / `BTHENUM\\` 等真实总线，父链不含 ViGEmBus；
/// - ViGEm 虚拟手柄：父链祖先 Service=ViGEmBus（本机 `ROOT\\SYSTEM\\0001`）；
/// - 其他虚拟手柄：`SWD\\`、`ROOT\\`、`VHF\\` 枚举。
///
/// 实现要点：设备名字/服务用 SetupAPI（SPDRP_*，与设备管理器同源）；状态与父链用 cfgmgr32。
/// 2026-11-11 踩坑：cfgmgr32 的 CM_DRP_* 对 HID 集合读不到 FriendlyName/DeviceDesc，
/// 只看它会把"明明有手柄"判成 0 个节点。
/// </summary>
public static class PadNodeHygiene
{
    private const uint CrSuccess = 0;
    private const uint DN_STARTED = 0x00000008;
    /// <summary>cfgmgr32 的"只取在场设备"过滤位（CM_GETIDLIST_FILTER_PRESENT）。</summary>
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    /// <summary>cfgmgr32 的 CM_DRP_SERVICE（实测 5；8=CLASS、9=CLASSGUID、13=FRIENDLYNAME）。</summary>
    private const uint CM_DRP_SERVICE = 0x00000005;

    // SetupAPI 的 SPDRP_*
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint SPDRP_SERVICE = 0x00000004;
    private const uint SPDRP_CLASS = 0x00000007;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint SPDRP_COMPATIBLEIDS = 0x0000000D;

    private static readonly Guid XnaCompositeClass = new("D61CA365-5AF4-4486-998B-9DB4734C6CA3");
    private static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    // ---------------- 纯逻辑（可单测） ----------------

    /// <summary>设备节点归类。</summary>
    public static PadNodeKind Classify(bool isVirtual, bool started, bool realBus)
    {
        if (isVirtual) return started ? PadNodeKind.VirtualLive : PadNodeKind.VirtualStale;
        if (!started) return PadNodeKind.Inactive;
        return realBus ? PadNodeKind.Physical : PadNodeKind.OtherVirtual;
    }

    /// <summary>是否手柄类名字（FriendlyName / DeviceDesc）。</summary>
    public static bool LooksLikePad(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("controller", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gamepad", StringComparison.OrdinalIgnoreCase)
            || name.Contains("joystick", StringComparison.OrdinalIgnoreCase)
            || name.Contains("手柄");
    }

    /// <summary>HID 游戏控制器（usage page 0x01 / usage 0x05）的兼容 ID。</summary>
    public static bool IsHidGameController(string? compatibleIds)
        => !string.IsNullOrEmpty(compatibleIds)
           && compatibleIds.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase);

    /// <summary>实例路径是否真实总线（USB 有线 / HID 蓝牙 / 蓝牙枚举）。</summary>
    public static bool IsRealBusInstance(string instanceId)
        => instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"BTHHFENUM\", StringComparison.OrdinalIgnoreCase);

    /// <summary>pnputil 卸载一个设备节点的参数（纯函数便于断言）。</summary>
    public static string[] BuildRemoveArguments(string instanceId) => ["/remove-device", instanceId];

    /// <summary>重扫参数。</summary>
    public static string[] BuildScanArguments() => ["/scan-devices"];

    /// <summary>重启 ViGEmBus 根设备的参数（更强的一次重新枚举）。</summary>
    public static string[] BuildRestartArguments(string busInstanceId) => ["/restart-device", busInstanceId];

    /// <summary>
    /// 需求 1 的闸门：有真实手柄在用（PnP 节点 或 XInput slot0 被占）就不许动设备。
    /// <paramref name="slot0Occupied"/> 是 XInput 的硬事实——PnP 对蓝牙 HID 集合的状态
    /// 有时查不到（2026-11-11 实测），只信节点计数会漏判。
    /// </summary>
    public static bool CanTouchDevices(int physicalPadCount, bool slot0Occupied = false, bool force = false)
        => force || (physicalPadCount == 0 && !slot0Occupied);

    // ---------------- 扫描 ----------------

    /// <summary>
    /// 枚举手柄类设备节点：设备信息/名字/服务用 SetupAPI（与设备管理器同源），
    /// "在不在场"用 cfgmgr32 的在场列表（残留/幻影节点必须靠它区分）。
    /// </summary>
    public static IReadOnlyList<PadDeviceNode> Scan()
    {
        var present = GetPresentInstanceIds();
        var list = new List<PadDeviceNode>();

        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == InvalidHandleValue) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                var cls = GetString(set, ref data, SPDRP_CLASS);
                if (!cls.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                    && !cls.Equals("HIDClass", StringComparison.OrdinalIgnoreCase)) continue;

                var id = GetInstanceId(set, ref data);
                if (id.Length == 0) continue;

                var name = GetString(set, ref data, SPDRP_FRIENDLYNAME);
                if (name.Length == 0) name = GetString(set, ref data, SPDRP_DEVICEDESC);
                var compat = GetString(set, ref data, SPDRP_COMPATIBLEIDS);
                bool isPad = cls.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                             || LooksLikePad(name) || IsHidGameController(compat);
                if (!isPad) continue;

                var service = GetString(set, ref data, SPDRP_SERVICE);
                bool started = present.Contains(id);
                bool isVirtual = service.Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase)
                                 || DescendsFromViGEmBus(data.DevInst);

                list.Add(new PadDeviceNode(id, name, cls, started, isVirtual, IsRealBusInstance(id), service));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    /// <summary>cfgmgr32 的"当前在场"设备实例 ID 集合（不带 Phantom）。</summary>
    private static HashSet<string> GetPresentInstanceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 两个来源取并集：cfgmgr32 的在场列表 + SetupAPI 的 DIGCF_PRESENT 枚举。
        // 2026-11-11 实测：蓝牙 HID 集合在 cfgmgr32 列表里查不到，但设备管理器显示"已启动"，
        // 只信一个来源会把手柄判成残留节点。
        foreach (var (guid, _) in new[] { (XnaCompositeClass, ""), (HidClass, "") })
        foreach (var id in EnumeratePresentByClass(guid))
            ids.Add(id);

        if (CM_Get_Device_ID_List_SizeW(out var len, null, CM_GETIDLIST_FILTER_PRESENT) != CrSuccess || len == 0)
            return ids;
        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW(null, buffer, len, CM_GETIDLIST_FILTER_PRESENT) != CrSuccess) return ids;

        var sb = new StringBuilder();
        foreach (var ch in buffer)
        {
            if (ch == '\0')
            {
                if (sb.Length == 0) break;
                ids.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        return ids;
    }

    private static IEnumerable<string> EnumeratePresentByClass(Guid classGuid)
    {
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == InvalidHandleValue) yield break;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                var id = GetInstanceId(set, ref data);
                if (id.Length > 0) yield return id;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool DescendsFromViGEmBus(uint dn)
    {
        var cur = dn;
        for (int depth = 0; depth < 4; depth++)
        {
            if (CM_Get_Parent(out var parent, cur, 0) != CrSuccess) return false;
            if (ReadService(parent).Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase)) return true;
            cur = parent;
        }
        return false;
    }

    /// <summary>读某个 DEVINST 的 Service（cfgmgr32；读不到返回空串）。</summary>
    private static string ReadService(uint dn)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_SERVICE, out _, null, ref len, 0);
        if (len == 0) return "";
        var buf = new char[len];
        if (CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_SERVICE, out _, buf, ref len, 0) != CrSuccess) return "";
        return new string(buf).TrimEnd('\0');
    }

    // ---------------- 执行（提权上下文调用） ----------------

    public sealed record HygieneReport(
        int PhysicalCount,
        int StaleVirtualCount,
        int LiveVirtualCount,
        int OtherVirtualCount,
        bool TouchedDevices,
        string Message);

    /// <summary>
    /// 按需求 1 的闸门执行节点卫生：真实手柄在用 → 只读；否则卸载 ViGEm 残留节点并重扫。
    /// <paramref name="apply"/>=false 时只做判定（供 UI 预览/日志）。
    /// </summary>
    public static HygieneReport Run(bool apply, bool force = false, bool restartBus = false,
                                    Action<string>? log = null, bool? slot0Occupied = null)
    {
        var nodes = Scan();
        bool slot0 = slot0Occupied ?? PadBackendProbe.IsSlotOccupied(0);
        var physical = nodes.Where(n => Classify(n.IsVirtual, n.Started, n.RealBus) == PadNodeKind.Physical).ToList();
        var stale = nodes.Where(n => Classify(n.IsVirtual, n.Started, n.RealBus) == PadNodeKind.VirtualStale).ToList();
        var live = nodes.Where(n => Classify(n.IsVirtual, n.Started, n.RealBus) == PadNodeKind.VirtualLive).ToList();
        var other = nodes.Where(n => Classify(n.IsVirtual, n.Started, n.RealBus) == PadNodeKind.OtherVirtual).ToList();

        log?.Invoke($"手柄节点：真实 {physical.Count} / 虚拟在线 {live.Count} / 虚拟残留 {stale.Count} / 其他虚拟 {other.Count}");
        foreach (var n in physical) log?.Invoke($"  真实手柄：{n.Name} [{n.InstanceId}]");
        foreach (var n in other) log?.Invoke($"  其他虚拟手柄（会抢 slot0）：{n.Name} [{n.InstanceId}]");

        if (!CanTouchDevices(physical.Count, slot0, force))
        {
            var msg = $"真实手柄在用（PnP {physical.Count} 个节点 / XInput slot0 占用={slot0}）" +
                      " → 跳过设备卸载/重载（改用进程内代理，真实手柄不动）";
            log?.Invoke(msg);
            return new HygieneReport(physical.Count, stale.Count, live.Count, other.Count, false, msg);
        }

        if (stale.Count == 0)
        {
            const string msg = "无残留虚拟手柄节点，无需卸载";
            log?.Invoke(msg);
            return new HygieneReport(physical.Count, 0, live.Count, other.Count, false, msg);
        }

        if (!apply)
        {
            var msg = $"预览：将卸载 {stale.Count} 个残留节点，然后 /scan-devices 重新枚举";
            log?.Invoke(msg);
            return new HygieneReport(physical.Count, stale.Count, live.Count, other.Count, false, msg);
        }

        int removed = 0;
        foreach (var n in stale)
        {
            var (code, output) = RunPnputil(BuildRemoveArguments(n.InstanceId));
            if (code == 0) { removed++; log?.Invoke($"已卸载：{n.InstanceId}"); }
            else log?.Invoke($"卸载失败({code})：{n.InstanceId} :: {output}");
        }

        var (scanCode, scanOut) = RunPnputil(BuildScanArguments());
        log?.Invoke($"重新枚举设备：exit={scanCode} {scanOut}".Trim());

        if (restartBus)
        {
            var bus = nodes.FirstOrDefault(n => n.Service.Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase));
            if (bus is not null)
            {
                var (rc, ro) = RunPnputil(BuildRestartArguments(bus.InstanceId));
                log?.Invoke($"重启 ViGEmBus 根设备 {bus.InstanceId}：exit={rc} {ro}".Trim());
            }
            else
            {
                log?.Invoke("未找到 ViGEmBus 根设备（可能驱动未安装）");
            }
        }

        string summary = removed == 0
            ? $"卸载失败（需要管理员权限）：0/{stale.Count}，未改动任何设备"
            : $"已卸载 {removed}/{stale.Count} 个残留节点并重新枚举";
        return new HygieneReport(physical.Count, stale.Count, live.Count, other.Count, removed > 0, summary);
    }

    /// <summary>执行 pnputil 并返回退出码与输出（无窗口）。</summary>
    public static (int Code, string Output) RunPnputil(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (-1, "pnputil 启动失败");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (-2, "pnputil 超时"); }
            var text = (stdout + stderr).Trim();
            return (p.ExitCode, text.Length > 400 ? text[..400] : text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ---------------- SetupAPI / cfgmgr32 底层 ----------------

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        uint required = 0;
        SetupDiGetDeviceInstanceIdW(set, ref data, null, 0, out required);
        if (required == 0) return "";
        var buf = new char[required];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buf, required, out _)
            ? new string(buf).TrimEnd('\0')
            : "";
    }

    private static string GetString(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        uint required = 0;
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out required);
        if (required == 0) return "";
        var bytes = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, bytes, required, out required))
            return "";
        // REG_MULTI_SZ 用 \0 分隔：统一成单串（判前缀/关键字足够）
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Replace('\0', '|');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        [Out] char[]? deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, [Out] byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint regDataType,
        [Out] char[]? buffer, ref uint pulLength, uint ulFlags);
}