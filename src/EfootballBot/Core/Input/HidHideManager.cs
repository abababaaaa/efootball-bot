using System.Diagnostics;
using System.IO;

namespace EfootballBot.Core.Input;

public sealed record HidHideStatus(
    bool DriverInstalled,
    bool Active,
    bool VigemHidden,
    int VigemInstanceCount,
    List<string> Whitelist,
    List<string> Blacklist,
    string? Error);

/// <summary>
/// HidHide 高层操作。读（GET）普通权限可用；写（SET）必须提权（由 --hidhide-config 子进程执行）。
/// </summary>
public static class HidHideManager
{
    /// <summary>计划任务名：最高权限静默同步虚拟手柄黑名单（首次应用隔离时注册）。</summary>
    public const string SyncTaskName = "EfootballBotHidHideSync";

    public static bool IsDriverInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\HidHide");
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// HidHide 服务为 DEMAND_START，重启电脑后不会自启，控制设备打不开（Win32 错误 2）。
    /// 在提权上下文（--hidhide-config 子进程 / 计划任务）中调用：服务已存在且停止则启动。
    /// 已运行或启动失败均静默返回（后续 IOCTL 会给出真实错误）。
    /// </summary>
    public static void EnsureServiceStarted()
    {
        try
        {
            using var h = HidHideIoctl.OpenControlDevice();
            return; // 设备可打开 = 服务已在运行
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc.exe", "start HidHide")
                { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(15000);
                Thread.Sleep(300); // 等控制设备对象创建
            }
            catch { /* 启动失败由后续 IOCTL 报错 */ }
        }
        catch { }
    }

    /// <summary>查询当前隔离状态（GUI 徽标用）。任何失败都降级为带 Error 的状态，不抛。</summary>
    public static HidHideStatus QueryStatus()
    {
        if (!IsDriverInstalled())
            return new HidHideStatus(false, false, false, 0, new(), new(), "HidHide 驱动未安装");

        try
        {
            var wl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetWhitelist);
            var bl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetBlacklist);
            bool active = HidHideIoctl.GetActive();
            var vigem = SafeEnumerateViGEm();
            return new HidHideStatus(true, active,
                vigem.Count > 0 && vigem.All(v => bl.Contains(v, StringComparer.OrdinalIgnoreCase)),
                vigem.Count, wl, bl, null);
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new HidHideStatus(true, false, false, 0, new(), new(),
                "HidHide 驱动服务未启动（重启电脑后需重新应用隔离或启动一次挂机来拉起）");
        }
        catch (Exception ex)
        {
            return new HidHideStatus(true, false, false, 0, new(), new(), ex.Message);
        }
    }

    private static List<string> SafeEnumerateViGEm()
    {
        try { return HidDeviceEnumerator.FindViGEmInstances(); }
        catch { return new List<string>(); }
    }

    // ---------- 合并纯函数（可单测）----------

    public static List<string> MergeInto(IReadOnlyList<string> existing, IReadOnlyList<string> add)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var p in add) set.Add(p);
        return set.ToList();
    }

    public static List<string> RemoveAll(IReadOnlyList<string> existing, IReadOnlyList<string> remove)
    {
        var rm = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
        return existing.Where(p => !rm.Contains(p)).ToList();
    }

    /// <summary>按 ViGEm 前缀拆分：返回（保留的非 ViGEm 条目, 被拆出的 ViGEm 条目）。</summary>
    public static (List<string> Keep, List<string> VigemEntries) SplitByVigemPrefix(
        IReadOnlyList<string> list)
    {
        var keep = new List<string>();
        var vigem = new List<string>();
        foreach (var p in list)
        {
            if (HidDeviceEnumerator.IsViGEmInstance(p)) vigem.Add(p);
            else keep.Add(p);
        }
        return (keep, vigem);
    }

    // ---------- 写操作（提权上下文调用）----------

    /// <summary>国服/国际服游戏进程名（白名单解析用）。</summary>
    private static readonly string[] GameProcessNames = { "eFootballOnline", "eFootball" };

    /// <summary>
    /// 解析当前运行中游戏的完整镜像路径（白名单条目）。两个进程名都找不到返回 null。
    /// </summary>
    public static string? ResolveGameImagePath()
    {
        foreach (var name in GameProcessNames)
        {
            try
            {
                var procs = global::System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path)) return path;
                    }
                    finally { p.Dispose(); }
                }
            }
            catch { /* 权限不足或进程退出，试下一个 */ }
        }
        return null;
    }

    /// <summary>
    /// 枚举 ViGEm 虚拟手柄实例；设备当前不在线（用户没在挂机）时临时创建一个，
    /// 枚举完即断开。实例路径真机实测跨连接稳定（同机同序列），且黑名单持久化，
    /// 故配置时不要求挂机正在运行。
    /// </summary>
    private static List<string> FindViGEmInstancesWithProbe()
    {
        var vigem = HidDeviceEnumerator.FindViGEmInstances();
        if (vigem.Count > 0) return vigem;

        VirtualGamepad360? probe = null;
        try
        {
            probe = new VirtualGamepad360();
            probe.Connect();
            for (int i = 0; i < 10; i++)
            {
                vigem = HidDeviceEnumerator.FindViGEmInstances();
                if (vigem.Count > 0) return vigem;
                Thread.Sleep(300);
            }
        }
        catch { /* ViGEmBus 未装等情况：返回空，由调用方报错 */ }
        finally
        {
            try { probe?.Dispose(); } catch { }
        }
        return vigem;
    }

    /// <summary>
    /// 应用隔离：白名单并入游戏完整镜像路径（未传则自动从运行中的游戏进程解析）；
    /// 黑名单并入当前 ViGEm 实例（hideVigem，设备不在线时临时创建探测）
    /// 与物理手柄实例（hidePhysical）。只追加、不删除已有条目（旧实例路径残留无害）。
    /// 返回本次确认由本功能托管的黑名单条目全量（ViGEm/物理当前实例，去重）：
    /// GUI/sync 合并入配置快照，供解除隔离时精确回滚（ViGEm 另按前缀兜底清理）。
    /// </summary>
    public static (bool Success, string Error, List<string> AppliedBlacklist, string ResolvedGameImagePath) ApplyIsolation(
        bool hideVigem, bool hidePhysical, string? gameImagePath)
    {
        try
        {
            var wl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetWhitelist);
            var bl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetBlacklist);

            var managed = new List<string>();
            if (hideVigem)
            {
                var vigem = FindViGEmInstancesWithProbe();
                if (vigem.Count == 0)
                    return (false, "未枚举到 ViGEm 虚拟手柄设备（请确认 ViGEmBus 驱动已安装）", new(), gameImagePath ?? "");
                managed.AddRange(vigem);
            }
            if (hidePhysical)
                managed.AddRange(HidDeviceEnumerator.FindPhysicalGamepadInstances());

            // 白名单路径：显式传入优先；否则尝试从运行中的游戏进程快照
            string? resolved = string.IsNullOrWhiteSpace(gameImagePath)
                ? ResolveGameImagePath()
                : gameImagePath;

            var newWl = string.IsNullOrWhiteSpace(resolved)
                ? wl.ToList()
                : MergeInto(wl, new[] { resolved! });
            var newBl = MergeInto(bl, managed);

            HidHideIoctl.SetMultiString(HidHideIoctl.IoctlSetWhitelist, newWl);
            HidHideIoctl.SetMultiString(HidHideIoctl.IoctlSetBlacklist, newBl);
            HidHideIoctl.SetActive(true);
            return (true, "", managed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), resolved ?? "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message, new(), gameImagePath ?? "");
        }
    }

    /// <summary>
    /// 解除隔离：黑名单移除全部 ViGEm 前缀条目 + 快照条目；白名单移除游戏路径；Active 关闭。
    /// </summary>
    public static (bool Success, string Error) ClearIsolation(
        IReadOnlyList<string> appliedBlacklist, string gameImagePath)
    {
        try
        {
            var bl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetBlacklist);
            var (keep, _) = SplitByVigemPrefix(bl);
            var newBl = RemoveAll(keep, appliedBlacklist);
            HidHideIoctl.SetMultiString(HidHideIoctl.IoctlSetBlacklist, newBl);

            if (!string.IsNullOrWhiteSpace(gameImagePath))
            {
                var wl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetWhitelist);
                HidHideIoctl.SetMultiString(HidHideIoctl.IoctlSetWhitelist,
                    RemoveAll(wl, new[] { gameImagePath }));
            }
            HidHideIoctl.SetActive(false);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>注册/删除计划任务（提权上下文调用）。挂机启动时经 schtasks /Run 静默同步。</summary>
    public static bool EnsureSyncTaskRegistered()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return false;
            // /TR 内部嵌套引号：值整体加引号，内部路径引号用 \" 转义
            var psi = new ProcessStartInfo("schtasks",
                $"/Create /TN {SyncTaskName} /TR \"\\\"{exe}\\\" --hidhide-config sync\" " +
                "/SC ONLOGON /RL HIGHEST /F")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p is null || (p.HasExited && p.ExitCode == 0);
        }
        catch { return false; }
    }

    public static void DeleteSyncTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks",
                $"/Delete /TN {SyncTaskName} /F")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(15000);
        }
        catch { /* 任务不存在等情况忽略 */ }
    }

    /// <summary>挂机启动时静默触发同步（非提权：schtasks /Run 最高权限任务无 UAC 弹窗）。</summary>
    public static void RunSyncTaskSilently()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks",
                $"/Run /TN {SyncTaskName}")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(10000);
        }
        catch { /* 同步失败不阻塞挂机 */ }
    }
}
