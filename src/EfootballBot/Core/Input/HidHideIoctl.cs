using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace EfootballBot.Core.Input;

/// <summary>
/// Nefarius HidHide 过滤驱动 IOCTL 层。
/// 契约核对自官方 v1.5.230.0 源码（HidHide/src/Logic.h），勿凭记忆改动常量。
/// 控制设备是独占设备：句柄必须短生命周期（using），冲突时由调用方重试。
/// 白名单/黑名单均为 REG_MULTI_SZ 多字符串（UTF-16 双 null 结尾）。
/// </summary>
public static class HidHideIoctl
{
    public const string ControlDevicePath = @"\\.\HidHide";

    // CTL_CODE(32769, fn, METHOD_BUFFERED, FILE_READ_DATA) = (32769<<16)|(1<<14)|(fn<<2)
    public const uint IoctlGetWhitelist = 0x80016000;
    public const uint IoctlSetWhitelist = 0x80016004;
    public const uint IoctlGetBlacklist = 0x80016008;
    public const uint IoctlSetBlacklist = 0x8001600C;
    public const uint IoctlGetActive    = 0x80016010;
    public const uint IoctlSetActive    = 0x80016014;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>打开控制设备。失败抛 Win32Exception；错误 5（拒绝访问）多为设备被其他客户端独占。</summary>
    public static SafeFileHandle OpenControlDevice()
    {
        var h = CreateFile(ControlDevicePath, GENERIC_READ, FILE_SHARE_READ_WRITE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new Win32Exception(err,
                $"打开 {ControlDevicePath} 失败（Win32 错误 {err}）。" +
                (err == 5 ? "设备可能正被 HidHide 客户端或 CLI 占用（独占设备），请稍后重试。" : ""));
        }
        return h;
    }

    /// <summary>独占冲突安全打开：最多重试 5 次、间隔 200ms。</summary>
    public static SafeFileHandle OpenControlDeviceWithRetry()
    {
        Win32Exception? last = null;
        for (int i = 0; i < 5; i++)
        {
            try { return OpenControlDevice(); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                last = ex;
                Thread.Sleep(200);
            }
        }
        throw last!;
    }

    /// <summary>读多字符串（官方两次调用协议：先探测 needed，再真取）。空列表返回空集合。</summary>
    public static List<string> GetMultiString(uint ioctl)
    {
        using var h = OpenControlDevice();
        bool firstOk = DeviceIoControl(h, ioctl, null, 0, null, 0, out uint needed, IntPtr.Zero);
        if (!firstOk)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_MORE_DATA)
                throw new Win32Exception(err, $"IOCTL 0x{ioctl:X} 探测缓冲大小失败");
        }
        if (needed == 0) return new List<string>();

        var buf = new byte[needed];
        if (!DeviceIoControl(h, ioctl, null, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"IOCTL 0x{ioctl:X} 读取失败");
        return MultiStringToList(buf);
    }

    /// <summary>写多字符串。需在提权上下文中调用。</summary>
    public static void SetMultiString(uint ioctl, IReadOnlyList<string> items)
    {
        byte[] buf = ListToMultiString(items);
        using var h = OpenControlDevice();
        if (!DeviceIoControl(h, ioctl, buf, (uint)buf.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"IOCTL 0x{ioctl:X} 写入失败");
    }

    public static bool GetActive()
    {
        using var h = OpenControlDevice();
        var buf = new byte[1];
        if (!DeviceIoControl(h, IoctlGetActive, null, 0, buf, 1, out uint ret, IntPtr.Zero) || ret != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 HidHide Active 状态失败");
        return buf[0] != 0;
    }

    public static void SetActive(bool active)
    {
        using var h = OpenControlDevice();
        if (!DeviceIoControl(h, IoctlSetActive, new[] { (byte)(active ? 1 : 0) }, 1, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "写入 HidHide Active 状态失败");
    }

    // ---------- MultiString 纯函数（可单测）----------

    public static byte[] ListToMultiString(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return new byte[4]; // 空多字符串 = 两个 UTF-16 null

        int chars = 1;
        foreach (var s in items) chars += s.Length + 1;
        var arr = new char[chars];
        int o = 0;
        foreach (var s in items)
        {
            s.CopyTo(0, arr, o, s.Length);
            o += s.Length;
            arr[o++] = '\0';
        }
        arr[o] = '\0'; // 最终双 null 结尾
        return Encoding.Unicode.GetBytes(arr);
    }

    public static List<string> MultiStringToList(byte[] data)
    {
        var result = new List<string>();
        if (data.Length < 2) return result;
        foreach (var part in Encoding.Unicode.GetString(data).Split('\0'))
            if (part.Length > 0) result.Add(part);
        return result;
    }
}
