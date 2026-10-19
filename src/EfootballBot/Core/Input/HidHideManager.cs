using System.Diagnostics;
using System.IO;
using System.Text;

namespace EfootballBot.Core.Input;

public sealed record HidHideStatus(
    bool DriverInstalled,
    bool Active,
    bool VirtualHidden,
    int VirtualInstanceCount,
    List<string> Whitelist,
    List<string> Blacklist,
    string? Error);

/// <summary>
/// HidHide 高层操作。读（GET）普通权限可用；写（SET）必须提权（由 --hidhide-config 子进程执行）。
/// 提权入口统一走计划任务（/RL HIGHEST，schtasks /Run 静默触发、无 UAC）：
/// 任务动作指向 %LOCALAPPDATA%\EfootballBot 下的稳定 shim vbs（每次 GUI 启动重写、
/// 指向当前 exe）——exe 移动/更新后任务不再失效（2026-10-12 实机教训：任务注册的
/// 绝对 exe 路径随项目搬迁变成 0x80070002 文件不存在，静默同步全部失效）。
/// 真实手柄的玩家 1 释放不走 HidHide（黑名单对 XInput 无效），见 <see cref="PhysicalPadControl"/>。
/// </summary>
public static class HidHideManager
{
    /// <summary>同步任务：登录/启动时拉服务、刷新虚拟手柄黑名单、恢复被禁用的真实手柄。</summary>
    public const string SyncTaskName = "EfootballBotHidHideSync";
    /// <summary>禁用真实手柄任务（挂机启动前由 Worker 触发；请求凭证防登录误触发）。</summary>
    public const string PadHideTaskName = "EfootballBotPadHide";
    /// <summary>恢复真实手柄任务（程序退出/启动/登录时触发）。</summary>
    public const string PadRestoreTaskName = "EfootballBotPadRestore";

    /// <summary>（任务名, --hidhide-config 参数）清单；shim 文件名与参数同名。</summary>
    public static readonly (string Name, string Arg)[] AllTasks =
    {
        (SyncTaskName, "sync"),
        (PadHideTaskName, "pad-hide"),
        (PadRestoreTaskName, "pad-restore"),
    };

    /// <summary>
    /// shim 目录：必须是与安装位置无关的稳定路径，否则 exe 搬家后任务照样失效。
    /// 仅存放自动生成的启动跳板（约 200B/个），不属于用户数据，故允许落在 %LOCALAPPDATA%。
    /// </summary>
    public static string ShimDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EfootballBot");

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
            var vpad = SafeEnumerateVirtualPads();
            return new HidHideStatus(true, active,
                vpad.Count > 0 && vpad.All(v => bl.Contains(v, StringComparer.OrdinalIgnoreCase)),
                vpad.Count, wl, bl, null);
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

    private static List<string> SafeEnumerateVirtualPads()
    {
        try { return HidDeviceEnumerator.FindVirtualPadInstances(); }
        catch { return new List<string>(); }
    }

    // ================= shim 与计划任务 =================

    /// <summary>shim vbs 文件路径（文件名与任务参数同名）。</summary>
    public static string ShimPathFor(string arg) => Path.Combine(ShimDir, $"hidhide-{arg}.vbs");

    /// <summary>生成 shim vbs 内容：静默（0=隐藏窗口）启动当前 exe 执行对应模式（可单测）。</summary>
    public static string BuildShimScript(string exePath, string mode) =>
        "' EfootballBot auto-generated launcher shim (stable target for scheduled tasks)\r\n" +
        "CreateObject(\"WScript.Shell\").Run \"\"\"" + exePath + "\"\" --hidhide-config " + mode + "\", 0, False\r\n";

    /// <summary>把三个 shim 重写为指向当前 exe（普通权限；内容不变则跳过写盘）。</summary>
    public static void WriteShimScripts()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        Directory.CreateDirectory(ShimDir);
        foreach (var (_, arg) in AllTasks)
        {
            var path = ShimPathFor(arg);
            var content = BuildShimScript(exe, arg);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == content) continue;
                // UTF-16 BOM：wscript 按系统代码页读 ANSI vbs，非 ASCII 安装路径会乱码
                File.WriteAllText(path, content, Encoding.Unicode);
            }
            catch { /* 被占用等：下次启动再写 */ }
        }
    }

    /// <summary>注册全部提权计划任务（提权上下文调用）。先写 shim 再注册。</summary>
    public static bool EnsureTasksRegistered()
    {
        WriteShimScripts();
        bool all = true;
        foreach (var (name, arg) in AllTasks)
            all &= RegisterTask(name, arg);
        return all;
    }

    private static bool RegisterTask(string name, string arg)
    {
        try
        {
            // wscript（GUI 子系统）执行 shim：登录触发时不闪控制台窗口
            string action = $"\"{Environment.SystemDirectory}\\wscript.exe\" \"{ShimPathFor(arg)}\"";
            // /TR 内部嵌套引号：值整体加引号，内部路径引号用 \" 转义
            var psi = new ProcessStartInfo("schtasks",
                $"/Create /TN {name} /TR \"{action.Replace("\"", "\\\"")}\" " +
                "/SC ONLOGON /RL HIGHEST /F")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p is null || (p.HasExited && p.ExitCode == 0);
        }
        catch { return false; }
    }

    /// <summary>挂机启动时静默触发同步（非提权：schtasks /Run 最高权限任务无 UAC 弹窗）。</summary>
    public static void RunSyncTaskSilently() => RunTaskSilently(SyncTaskName);

    /// <summary>静默触发指定提权任务（无 UAC；任务不存在时 schtasks 静默失败）。
    /// waitForExit=false 为火后不管——停止挂机的 pad-restore 不应拖慢停止流程
    /// （等待会把 Worker 的 stop 处理拉长到数秒，放大 GUI 关闭竞态）。</summary>
    public static void RunTaskSilently(string taskName, bool waitForExit = true)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks",
                $"/Run /TN {taskName}")
            { CreateNoWindow = true, UseShellExecute = false });
            if (waitForExit) p?.WaitForExit(10000);
        }
        catch { /* 同步失败不阻塞挂机 */ }
    }

    /// <summary>挂机启动前请求禁用真实手柄：写一次性请求凭证 + 静默运行提权任务（无 UAC）。</summary>
    public static void RequestPadHide()
    {
        try { PhysicalPadControl.WriteHideRequest(); } catch { }
        RunTaskSilently(PadHideTaskName);
    }

    /// <summary>
    /// 三个提权任务是否都已注册且指向当前 shim（PowerShell 查询，区域设置无关）。
    /// PowerShell 不可用时退化为 schtasks 存在性检查（无法识别旧 exe 路径的遗留任务）。
    /// </summary>
    public static bool TasksReady()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                string.Join("; ", AllTasks.Select(t =>
                    $"$t=Get-ScheduledTask -TaskName '{t.Name}' -ErrorAction SilentlyContinue; " +
                    $"if($t){{ $a=$t.Actions[0]; '{t.Name}|' + $a.Execute + ' ' + $a.Arguments }} " +
                    $"else{{ '{t.Name}|MISSING' }}")) +
                "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p is null) return FallbackTaskExists();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            bool ready = true;
            foreach (var (name, arg) in AllTasks)
            {
                var line = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(name + "|"));
                if (line is null) { ready = false; continue; }
                // 任务存在即可迁移判定：动作里找不到当前 shim 文件名＝遗留旧路径任务
                if (!line.Contains($"hidhide-{arg}.vbs", StringComparison.OrdinalIgnoreCase))
                    ready = false;
            }
            return ready;
        }
        catch { return FallbackTaskExists(); }
    }

    private static bool FallbackTaskExists()
    {
        try
        {
            foreach (var (name, _) in AllTasks)
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Query /TN {name}")
                { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(5000);
                if (p is null || p.ExitCode != 0) return false;
            }
            return true;
        }
        catch { return false; }
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

    /// <summary>按虚拟手柄特征拆分：返回（保留的非虚拟手柄条目, 被拆出的虚拟手柄条目）。</summary>
    public static (List<string> Keep, List<string> VirtualEntries) SplitByVirtualPrefix(
        IReadOnlyList<string> list)
    {
        var keep = new List<string>();
        var vpad = new List<string>();
        foreach (var p in list)
        {
            if (HidDeviceEnumerator.IsVirtualPadInstancePath(p)) vpad.Add(p);
            else keep.Add(p);
        }
        return (keep, vpad);
    }

    // ---------- 写操作（提权上下文调用）----------

    /// <summary>国服/国际服游戏进程名（白名单解析用）。</summary>
    private static readonly string[] GameProcessNames = { "eFootball", "eFootballOnline" };

    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = global::System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint flags, global::System.Text.StringBuilder text, ref uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 解析运行中游戏的完整镜像路径（白名单条目）。
    /// 用 QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION）：即使游戏提权运行
    /// 也能跨权限读到路径——旧 MainModule 方案对提权进程抛拒绝访问，导致白名单 game= 恒为空
    /// （实机 2026-10-10：白名单空 → 隔离模型坏：要么全盲要么不生效）。
    /// </summary>
    public static string? ResolveGameImagePath()
    {
        foreach (var name in GameProcessNames)
        {
            foreach (var p in global::System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == nint.Zero) continue;
                    try
                    {
                        var sb = new global::System.Text.StringBuilder(1024);
                        uint size = 1024;
                        if (QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0)
                        {
                            var path = sb.ToString(0, (int)size);
                            if (!string.IsNullOrWhiteSpace(path)) return path;
                        }
                    }
                    finally { CloseHandle(h); }
                }
                catch { /* 下一个进程 */ }
                finally { p.Dispose(); }
            }
        }
        return null;
    }

    /// <summary>
    /// 枚举虚拟手柄实例；设备当前不在线（用户没在挂机）时按配置后端临时创建一个探测，
    /// 枚举完即断开（HIDMaestro 探测需下载 SDK，不做临时创建——挂机中同步时设备必在线）。
    /// 实例路径真机实测跨连接稳定（同机同序列），且黑名单持久化，故配置时不要求挂机正在运行。
    /// </summary>
    private static List<string> FindVirtualPadInstancesWithProbe()
    {
        var vpad = HidDeviceEnumerator.FindVirtualPadInstances();
        if (vpad.Count > 0) return vpad;

        var kind = LoadConfiguredBackend();
        if (kind == PadBackendKind.HidMaestro) return vpad;

        IVirtualPad? probe = null;
        try
        {
            probe = kind == PadBackendKind.Xbox360 ? new VirtualGamepad360() : new VirtualDs4Pad();
            probe.Connect();
            // 等集合数量稳定再返回：DS4 有多个 HID 集合且注册有先后，只取先到的
            // 会导致另一个集合不被拉黑（泄漏通道）
            int lastCount = -1;
            var stableSince = DateTime.MinValue;
            var deadline = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < deadline)
            {
                vpad = HidDeviceEnumerator.FindVirtualPadInstances();
                if (vpad.Count != lastCount)
                {
                    lastCount = vpad.Count;
                    stableSince = DateTime.Now;
                }
                else if (vpad.Count > 0 && (DateTime.Now - stableSince).TotalMilliseconds >= 1500)
                    break;
                Thread.Sleep(300);
            }
        }
        catch { /* ViGEmBus 未装等情况：返回空，由调用方报错 */ }
        finally
        {
            try { probe?.Dispose(); } catch { }
        }
        return vpad;
    }

    /// <summary>读配置里的手柄后端（提权子进程与 GUI 共享同一 config.json）；读取失败按默认 DS4。</summary>
    private static PadBackendKind LoadConfiguredBackend()
    {
        try { return EfootballBot.Core.Config.AppConfig.Load().Pad.Backend; }
        catch { return PadBackendKind.HidDs4; }
    }

    /// <summary>
    /// 应用隔离：白名单并入游戏与本程序的完整镜像路径（游戏未传则自动从运行中的游戏进程解析；
    /// 本程序自身也要能看到虚拟手柄——HIDMaestro 重连需重新打开设备句柄）；
    /// 黑名单并入当前虚拟手柄实例（设备不在线时按配置后端临时创建探测）。
    /// 物理手柄不进黑名单（对 XInput 无效，见类注释）——反而把已知物理手柄实例
    /// 从黑名单移除（旧版本遗留条目会让 HID 层应用在挂机结束后仍看不到手柄）。
    /// 返回本次确认由本功能托管的黑名单条目全量（供解除隔离时精确回滚）。
    /// </summary>
    public static (bool Success, string Error, List<string> AppliedBlacklist, string ResolvedGameImagePath) ApplyIsolation(
        bool hideVigem, string? gameImagePath)
    {
        try
        {
            var wl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetWhitelist);
            var bl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetBlacklist);

            var managed = new List<string>();
            if (hideVigem)
            {
                var vpad = FindVirtualPadInstancesWithProbe();
                if (vpad.Count == 0)
                    return (false, "未枚举到虚拟手柄设备（请确认手柄驱动已安装且挂机已连接过一次）", new(), gameImagePath ?? "");
                managed.AddRange(vpad);
            }

            // 白名单路径：游戏显式传入优先，否则尝试从运行中的游戏进程快照；本程序自身必须放行
            string? resolved = string.IsNullOrWhiteSpace(gameImagePath)
                ? ResolveGameImagePath()
                : gameImagePath;
            string? botPath = Environment.ProcessPath;

            var newWl = wl.ToList();
            if (!string.IsNullOrWhiteSpace(resolved))
                newWl = MergeInto(newWl, new[] { resolved! });
            if (!string.IsNullOrWhiteSpace(botPath))
                newWl = MergeInto(newWl, new[] { botPath! });
            var newBl = MergeInto(bl, managed);
            // 迁移清理：物理手柄可见性改由设备节点禁用机制管理，黑名单里的旧条目全部摘除
            newBl = RemoveAll(newBl, SafePhysicalGamepadInstances());

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

    private static List<string> SafePhysicalGamepadInstances()
    {
        try
        {
            return HidDeviceEnumerator.FindPhysicalGamepadDevNodes()
                .Select(n => n.InstancePath).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 解除隔离：黑名单移除全部虚拟手柄条目 + 快照条目；白名单移除游戏路径；Active 关闭。
    /// </summary>
    public static (bool Success, string Error) ClearIsolation(
        IReadOnlyList<string> appliedBlacklist, string gameImagePath)
    {
        try
        {
            var bl = HidHideIoctl.GetMultiString(HidHideIoctl.IoctlGetBlacklist);
            var (keep, _) = SplitByVirtualPrefix(bl);
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
}
