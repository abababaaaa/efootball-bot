#if CHILD_SESSION_BETA
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using EfootballBot.Core.Bot;

namespace EfootballBot.Core.System.VirtualDisplay;

/// <summary>
/// 虚拟显示器后台（测试版）：给系统接一块已签名的虚拟显示器（Amyuni usbmmIdd，
/// Microsoft 硬件兼容性签名，免测试模式），挂机时把游戏窗口停靠到这块"看不见的屏幕"上。
///
/// 为什么不用子会话：eFootball 检测到 RDP 会话会拒绝运行（「与远程登录不兼容」），
/// 而子会话本质就是 RDP 会话。虚拟显示器跑在控制台会话里，游戏检测不到远程痕迹：
/// 渲染走真实显示路径（不会触发停靠的缩窗问题）、WGC 照常捕获、ViGEm 手柄照常生效。
///
/// 驱动文件来自 build_local\virtualdisplay\usbmmidd_v2（usbmmidd_v2.zip，amyuni.com 官方下载，
/// SHA256 见方案文档）。发布时随包放到 exe 旁 drivers\virtualdisplay\。
/// </summary>
public static class VirtualDisplayManager
{
    /// <summary>适配器名关键字（Win32_VideoController / 显示器适配器字符串）。</summary>
    public const string AdapterKeyword = "USB Mobile Monitor Virtual Display";

    private const string InfName = "usbmmidd.inf";
    private const string InstallerName = "deviceinstaller64.exe";

    /// <summary>驱动载荷目录：exe 旁 drivers\virtualdisplay\。</summary>
    public static string PayloadDir =>
        Path.Combine(AppPaths.ExeDir, "drivers", "virtualdisplay");

    private static string MonitorsRegPath =>
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\usbmmIdd\Parameters\Monitors";

    /// <summary>适配器设备是否已安装（不看显示器是否激活）。</summary>
    public static bool IsAdapterInstalled()
    {
        try
        {
            using var searcher = new global::System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var o in searcher.Get())
                if (o["Name"]?.ToString()?.Contains(AdapterKeyword, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>虚拟显示器当前是否作为活动屏幕存在；是则返回其矩形。</summary>
    public static (int X, int Y, int Width, int Height)? FindVirtualScreen()
    {
        try
        {
            foreach (var monitor in EnumerateMonitors())
            {
                string adapter = GetAdapterName(monitor.DeviceName);
                if (!adapter.Contains(AdapterKeyword, StringComparison.OrdinalIgnoreCase)) continue;
                return (monitor.Rect.Left, monitor.Rect.Top,
                    monitor.Rect.Right - monitor.Rect.Left, monitor.Rect.Bottom - monitor.Rect.Top);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 确保虚拟显示器可用：未装驱动则从载荷目录静默安装（管理员），按需写默认分辨率，
    /// 然后激活一块虚拟显示器并等它出现在屏幕拓扑里。失败返回 false（调用方回退屏外停靠）。
    /// </summary>
    public static bool EnsureEnabled(int width, int height, Action<string, LogLevel> log)
    {
        if (FindVirtualScreen() is { } active)
        {
            log($"虚拟显示器已在运行（{active.Width}×{active.Height}，位于 {active.X},{active.Y}）", LogLevel.Info);
            return true;
        }

        if (!IsAdapterInstalled())
        {
            var installer = Path.Combine(PayloadDir, InstallerName);
            var inf = Path.Combine(PayloadDir, InfName);
            if (!File.Exists(installer) || !File.Exists(inf))
            {
                log($"虚拟显示器驱动缺失：请把 build_local\\virtualdisplay\\usbmmidd_v2 整个目录拷到 " +
                    $"{PayloadDir}\\（驱动为 WHQL 签名，来源 amyuni.com 官方包）", LogLevel.Error);
                return false;
            }
            log("虚拟显示器驱动未安装，正在静默安装（WHQL 签名驱动）…", LogLevel.Info);
            if (!RunHidden(installer, $"install {Quote(inf)} usbmmidd", out int installErr))
            {
                log($"虚拟显示器驱动安装失败（退出码 {installErr}）：需要管理员权限", LogLevel.Error);
                return false;
            }
            if (!WaitFor(() => IsAdapterInstalled(), TimeSpan.FromSeconds(20)))
            {
                log("驱动已下发安装但 20s 内未出现适配器", LogLevel.Error);
                return false;
            }
            log("虚拟显示器驱动已安装", LogLevel.Success);
        }

        // 默认分辨率：注册表 (Default) 值，须在显示器激活前写入才对新生效
        if (!WriteDefaultResolution(width, height, log)) return false;

        if (!RunHidden(Path.Combine(PayloadDir, InstallerName), "enableidd 1", out int enableErr))
        {
            log($"激活虚拟显示器失败（退出码 {enableErr}）：需要管理员权限", LogLevel.Error);
            return false;
        }

        if (!WaitFor(() => FindVirtualScreen() is not null, TimeSpan.FromSeconds(15)))
        {
            log("虚拟显示器已激活但 15s 内未出现在屏幕拓扑里", LogLevel.Error);
            return false;
        }

        var rect = FindVirtualScreen()!.Value;
        log($"虚拟显示器已就绪（{rect.Width}×{rect.Height}，位于 {rect.X},{rect.Y}）", LogLevel.Success);
        return true;
    }

    /// <summary>停用虚拟显示器（挂机结束时调用，可选）：pnputil 禁用设备节点。失败不阻塞停止流程。</summary>
    public static void TryDisable(Action<string, LogLevel> log)
    {
        try
        {
            string? instanceId = FindAdapterInstanceId();
            if (instanceId is null) return;
            using var p = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(
                "pnputil", $"/disable-device \"{instanceId}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15_000);
            log("虚拟显示器已停用（下次挂机会自动重新激活）", LogLevel.Info);
        }
        catch (Exception ex)
        {
            log($"停用虚拟显示器失败（不影响挂机）：{ex.Message}", LogLevel.Warn);
        }
    }

    // ---------------- 内部 ----------------

    private static string Quote(string s) => $"\"{s}\"";

    private static bool RunHidden(string exe, string args, out int exitCode)
    {
        try
        {
            using var p = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(30_000);
            exitCode = p?.ExitCode ?? -1;
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(ex);
            exitCode = -1;
            return false;
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>把默认分辨率写入驱动注册表（(Default)="w,h"）。需要管理员；失败仅提示不阻断。</summary>
    private static bool WriteDefaultResolution(int width, int height, Action<string, LogLevel> log)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MonitorsRegPath, writable: true);
            if (key is null)
            {
                log($"写分辨率配置失败：打不开注册表 {MonitorsRegPath}（需要管理员）", LogLevel.Warn);
                return true; // 非阻断：用驱动默认分辨率也能挂
            }
            string value = $"{width},{height}";
            if ((key.GetValue(null) as string) != value)
            {
                key.SetValue(null, value, Microsoft.Win32.RegistryValueKind.String);
                log($"虚拟显示器默认分辨率已设为 {width}×{height}", LogLevel.Info);
            }
            return true;
        }
        catch (Exception ex)
        {
            log($"写分辨率配置异常（用驱动默认分辨率继续）：{ex.Message}", LogLevel.Warn);
            return true;
        }
    }

    private static string? FindAdapterInstanceId()
    {
        try
        {
            using var searcher = new global::System.Management.ManagementObjectSearcher(
                "SELECT PNPDeviceID,Name FROM Win32_PnPEntity");
            foreach (var o in searcher.Get())
            {
                if (o["Name"]?.ToString()?.Contains(AdapterKeyword, StringComparison.OrdinalIgnoreCase) == true)
                    return o["PNPDeviceID"]?.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>把屏幕（\\.\DISPLAYx）映射到适配器名字符串。</summary>
    private static string GetAdapterName(string screenDeviceName)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(screenDeviceName, 0, ref dd, 0))
            return dd.DeviceString ?? "";
        return "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // ---------------- 显示器枚举（纯 P/Invoke；主工程无 WinForms 引用） ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    private static List<(string DeviceName, RECT Rect)> EnumerateMonitors()
    {
        var list = new List<(string, RECT)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr __) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info))
                list.Add((info.szDevice, info.rcMonitor));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        public uint StateFlags;
    }
}
#endif
