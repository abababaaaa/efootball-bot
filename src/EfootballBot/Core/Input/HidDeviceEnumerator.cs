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

    public static bool IsViGEmInstance(string instancePath)
        => instancePath.StartsWith(ViGEmInstancePrefix, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>当前系统中存在的全部 HID 设备实例路径（Present only）。</summary>
    public static List<string> EnumerateAllInstances()
    {
        var list = new List<string>();
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
                    list.Add(sb.ToString());
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return list;
    }

    /// <summary>当前存在的 ViGEm 虚拟手柄实例路径。</summary>
    public static List<string> FindViGEmInstances()
        => EnumerateAllInstances().Where(IsViGEmInstance).ToList();

    /// <summary>
    /// 非 ViGEm 的物理手柄/摇杆实例。判定：HID 顶层集合 UsagePage=1（Generic Desktop）
    /// 且 Usage=4（Joystick）或 5（Game Pad）。
    /// </summary>
    public static List<string> FindPhysicalGamepadInstances()
    {
        var result = new List<string>();
        foreach (var (devicePath, instancePath) in EnumerateHidInterfaces())
        {
            if (IsViGEmInstance(instancePath)) continue;
            if (IsGamepadInterface(devicePath))
                result.Add(instancePath);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- HID 接口枚举 + Usage 判定 ----------

    private sealed record HidInterfaceInfo(string DevicePath, string InstancePath);

    private static List<HidInterfaceInfo> EnumerateHidInterfaces()
    {
        var list = new List<HidInterfaceInfo>();
        Guid ifcGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030"); // GUID_DEVINTERFACE_HID
        IntPtr h = SetupDiGetClassDevs(ref ifcGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs(接口) 失败");
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref ifcGuid, i, ref ifData); i++)
            {
                string? devicePath = GetInterfaceDevicePath(h, ref ifData);
                if (devicePath is null) continue;
                list.Add(new HidInterfaceInfo(devicePath, InterfacePathToInstancePath(devicePath)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return list;
    }

    private static string? GetInterfaceDevicePath(IntPtr h, ref SP_DEVICE_INTERFACE_DATA ifData)
    {
        // 两段式：先拿所需字节数，再真取。
        SetupDiGetDeviceInterfaceDetail(h, ref ifData, null, 0, out uint needed, IntPtr.Zero);
        if (needed == 0) return null;
        var buf = new byte[needed];
        if (!SetupDiGetDeviceInterfaceDetail(h, ref ifData, buf, (uint)buf.Length, out _, IntPtr.Zero))
            return null;
        // 布局：int cbSize（不读）→ 紧随的 null 结尾 UTF-16 设备路径。
        return Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 4));
    }

    private static bool IsGamepadInterface(string devicePath)
    {
        const uint GENERIC_READ = 0x80000000u;
        using var h = CreateFile(devicePath, 0 /* 不占用：仅查询 */, 0x1 | 0x2 | 0x4,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;
        if (!HidD_GetPreparsedData(h, out IntPtr preparsed)) return false;
        try
        {
            var caps = new HidpCaps();
            if (HidP_GetCaps(preparsed, ref caps) != 0x110000 /* HIDP_STATUS_SUCCESS */) return false;
            return caps.UsagePage == 0x01 && caps.Usage is 0x04 or 0x05;
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    // ---------- Win32 ----------

    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData,
        ref Guid interfaceClassGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo,
        ref SP_DEVICE_INTERFACE_DATA data, byte[]? detailDataBuffer, uint detailBufferSize,
        out uint requiredSize, IntPtr detailData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(int devInst, StringBuilder buffer, int bufferLen, int flags);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid classGuid;
        public int devInst;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public IntPtr reserved;
    }

    /// <summary>完整 HIDP_CAPS（hidsdi.h 契约，结构体大小必须精确，勿删字段）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
