using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;

namespace EfootballBot.Core.Input;

/// <summary>
/// 真实手柄「玩家 1 释放」控制：设备管理器级禁用/启用（CM_Disable/Enable_DevNode）。
/// 为什么不用 HidHide 黑名单：HidHide 只过滤 HID 层打开请求，而 XInput（xinput1_4 →
/// XUSB/xinputhid IOCTL）不走该层——2026-10-12 实机验证：真实手柄已在黑名单且 Active=1，
/// xinput1_4 slot0 仍报告连接。禁用设备节点对 XInput/DInput/RawInput 全部生效，
/// 是唯一可靠的释放手段。（v3.3.2 HidHide 整链已剔除，本类是手柄隐藏的唯一实现。）
/// 禁用/启用需管理员权限，由提权计划任务执行（--pad-config pad-hide / pad-restore）；
/// 禁用清单持久化到 <see cref="PadTakeoverConfig.DisabledPadDevs"/>（先记录后禁用，
/// 中途崩溃也能在下次登录/启动时恢复）。pad-hide 任务带 ONLOGON 触发器，
/// 故用一次性请求凭证文件防止登录时误禁用。
/// </summary>
public static class PhysicalPadControl
{
    /// <summary>pad-hide 任务的一次性执行凭证（data\tmp\ 下；挂机启动前由 Worker 写入）。</summary>
    public const string HideRequestFileName = "padhide.req";

    /// <summary>禁用真实手柄任务（挂机启动前由 Worker 触发；请求凭证防登录误触发）。</summary>
    public const string PadHideTaskName = "EfootballBotPadHide";

    /// <summary>恢复真实手柄任务（程序退出/启动/登录时触发）。</summary>
    public const string PadRestoreTaskName = "EfootballBotPadRestore";

    /// <summary>已剔除的旧 HidHide 同步任务（一次性清理目标）。</summary>
    public const string LegacySyncTaskName = "EfootballBotHidHideSync";

    /// <summary>（任务名, --pad-config 参数）清单；shim 文件名与参数同名。</summary>
    public static readonly (string Name, string Arg)[] AllTasks =
    {
        (PadHideTaskName, "pad-hide"),
        (PadRestoreTaskName, "pad-restore"),
    };

    /// <summary>
    /// shim 目录：必须是与安装位置无关的稳定路径，否则 exe 搬家后任务照样失效。
    /// 仅存放自动生成的启动跳板（约 200B/个），不属于用户数据，故允许落在 %LOCALAPPDATA%。
    /// </summary>
    public static string ShimDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EfootballBot");

    /// <summary>shim vbs 文件路径（文件名与任务参数同名）。</summary>
    public static string ShimPathFor(string arg) => Path.Combine(ShimDir, $"pad-{arg}.vbs");

    /// <summary>生成 shim vbs 内容：静默（0=隐藏窗口）启动当前 exe 执行对应模式（可单测）。</summary>
    public static string BuildShimScript(string exePath, string mode) =>
        "' EfootballBot auto-generated launcher shim (stable target for scheduled tasks)\r\n" +
        "CreateObject(\"WScript.Shell\").Run \"\"\"" + exePath + "\"\" --pad-config " + mode + "\", 0, False\r\n";

    /// <summary>把 shim 重写为指向当前 exe（普通权限；内容不变则跳过写盘），并清掉旧 hidhide-*.vbs。</summary>
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

    /// <summary>挂机启动前请求禁用真实手柄：写一次性请求凭证 + 静默运行提权任务（无 UAC）。</summary>
    public static void RequestPadHide()
    {
        try { WriteHideRequest(); } catch { }
        RunTaskSilently(PadHideTaskName);
    }

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

    /// <summary>一次性清理已剔除的 HidHide 遗留（服务/任务/shim），幂等；
    /// 清理动作需提权，未提权时静默失败且不写标记（下次提权运行再试）。</summary>
    public static void LegacyHidHideCleanupOnce()
    {
        var marker = Path.Combine(AppPaths.DataDir, "hidhide-removed.flag");
        try { if (File.Exists(marker)) return; } catch { }

        try
        {
            if (RegistryHidHideServiceExists())
            {
                RunNative("sc.exe", "stop HidHide");
                RunNative("sc.exe", "delete HidHide");
                // 未提权时 delete 会失败：不写标记，下次再试
                if (RegistryHidHideServiceExists()) return;
            }
            RunNative("schtasks", $"/Delete /TN {LegacySyncTaskName} /F");
            try
            {
                foreach (var f in Directory.GetFiles(ShimDir, "hidhide-*.vbs"))
                    File.Delete(f);
            }
            catch { }
            try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { }
        }
        catch { }
    }

    private static bool RegistryHidHideServiceExists()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\HidHide");
            return key is not null;
        }
        catch { return false; }
    }

    private static void RunNative(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(15000);
        }
        catch { }
    }

    // ---------------- 会话标记（sync 任务判断“挂机进行中，禁止恢复手柄”） ----------------

    private static string SessionMarkerPath => Path.Combine(AppPaths.TempDir, "session.active");

    /// <summary>挂机会话开始时写入（含 Worker PID）；sync 任务凭它跳过 pad-restore。</summary>
    public static void WriteSessionMarker()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            File.WriteAllText(SessionMarkerPath, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void RemoveSessionMarker()
    {
        try { File.Delete(SessionMarkerPath); } catch { }
    }

    /// <summary>挂机会话是否进行中（标记存在且 PID 仍存活且进程名匹配）。进程已死＝上次崩溃残留，允许恢复。</summary>
    public static bool IsBotSessionActive()
    {
        try
        {
            if (!File.Exists(SessionMarkerPath)) return false;
            int pid = int.Parse(File.ReadAllText(SessionMarkerPath).Trim());
            using var p = global::System.Diagnostics.Process.GetProcessById(pid);
            return p is { HasExited: false }
                && p.ProcessName.Contains("EfootballBot", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------- 提权上下文执行（计划任务 / runas 子进程内） ----------------

    /// <summary>
    /// 禁用当前全部物理手柄设备节点以释放 XInput 玩家 1。
    /// 禁用后轮询 slot0；仍被占用则升级禁用各集合的父设备（手柄自身的 USB 设备节点，
    /// 永远不会是集线器——HID 集合的父必然是其所属设备）。返回是否成功释放。
    /// </summary>
    public static (bool Success, string Message) DisableAll()
    {
        // 只禁「真的连着」的手柄：蓝牙配对但已关机的节点禁了也不产生输入，
        // 还会把禁用升级（叶子→父设备，最长 16s）整条流程白跑一遍
        var leaves = FindGamepadDevNodes()
            .Where(n => HidDeviceEnumerator.IsDevNodeConnected(n.DevInst)).ToList();
        if (leaves.Count == 0)
            return (true, "未发现已启用的物理手柄（可能已禁用或未连接）");

        // 先记录后禁用：进程中途崩溃时恢复逻辑仍能按清单启用
        var record = AppendToRecord(leaves.Select(l => l.InstancePath));
        string disabled = string.Join(";", leaves.Select(l => l.InstancePath));
        foreach (var (_, devInst) in leaves)
            CM_Disable_DevNode(devInst, 0);

        if (WaitSlot0Free(TimeSpan.FromSeconds(8)))
            return (true, $"已禁用 {leaves.Count} 个手柄节点：{disabled}");

        // 升级：禁用父设备（真实 360 手柄的 XInput 接口可能挂在父节点而非 HID 集合上）
        var parents = new List<HidDeviceEnumerator.HidInstance>();
        foreach (var (_, devInst) in leaves)
        {
            if (HidDeviceEnumerator.GetParent(devInst) is not { } parent) continue;
            if (parent.InstancePath.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
            if (parents.Any(p => p.DevInst == parent.DevInst)) continue;
            parents.Add(parent);
        }
        if (parents.Count > 0)
        {
            AppendToRecord(parents.Select(p => p.InstancePath));
            foreach (var p in parents)
                CM_Disable_DevNode(p.DevInst, 0);
            if (WaitSlot0Free(TimeSpan.FromSeconds(8)))
                return (true, $"已禁用手柄节点及父设备（{parents.Count} 个父节点）：{disabled}");
        }
        // 节点已禁用但玩家 1 仍被占用：XInput 形虚拟手柄软件（AMD Controller Emulation 等）
        // 常驻槽位。本程序虚拟手柄也是 Xbox 360 形态，必须有 slot0 才能被游戏消费——算失败。
        return (false, $"已禁用 {leaves.Count + parents.Count} 个节点但玩家 1 仍被占用（可能被非物理设备占据）");
    }

    /// <summary>
    /// 恢复全部被禁用的手柄设备节点：按记录逆序启用（父设备先于叶子）并清空记录；
    /// 再兜底扫描所有处于「已禁用」状态的 XInput 手柄节点（记录被陈旧配置覆盖时仍能恢复）。
    /// 幂等——启用已启用的设备是无害空操作。
    /// </summary>
    public static (bool Success, string Message) RestoreAll()
    {
        var cfg = AppConfig.Load();
        var record = cfg.HidHide.DisabledPadDevs;
        int restored = 0;

        for (int i = record.Count - 1; i >= 0; i--)
        {
            if (CM_Locate_DevNodeW(out int devInst, record[i], 0) == CrSuccess)
            {
                if (CM_Enable_DevNode(devInst, 0) == CrSuccess) restored++;
            }
        }
        if (record.Count > 0)
        {
            cfg.HidHide.DisabledPadDevs = new();
            cfg.Save();
        }

        // 兜底扫描：记录之外的已禁用手柄节点（如 GUI 陈旧快照覆盖过记录）
        int swept = 0;
        foreach (var node in HidDeviceEnumerator.EnumerateAllInstances())
        {
            if (!HidDeviceEnumerator.IsXInputStyleInstance(node.InstancePath)) continue;
            if (HidDeviceEnumerator.IsVirtualBusChild(node.DevInst)) continue;
            if (!IsDevNodeDisabled(node.DevInst)) continue;
            if (CM_Enable_DevNode(node.DevInst, 0) == CrSuccess) swept++;
        }

        int total = restored + swept;
        return (true, total == 0
            ? (record.Count == 0 ? "无需恢复（无禁用记录）" : $"记录 {record.Count} 项已处理")
            : $"已恢复 {total} 个手柄节点（记录 {restored} + 扫描 {swept}）");
    }

    // ---------------- 普通权限可用 ----------------

    /// <summary>是否存在未恢复的禁用记录（GUI 启动/退出时判断是否触发恢复任务）。</summary>
    public static bool HasPendingRestore()
    {
        try { return AppConfig.Load().HidHide.DisabledPadDevs.Count > 0; }
        catch { return false; }
    }

    /// <summary>当前是否存在处于「已禁用」状态的 XInput 手柄节点（记录丢失时的检测兜底）。</summary>
    public static bool HasDisabledGamepad()
    {
        try
        {
            return HidDeviceEnumerator.EnumerateAllInstances().Any(n =>
                HidDeviceEnumerator.IsXInputStyleInstance(n.InstancePath)
                && !HidDeviceEnumerator.IsVirtualBusChild(n.DevInst)
                && IsDevNodeDisabled(n.DevInst));
        }
        catch { return false; }
    }

    /// <summary>写入 pad-hide 一次性请求凭证。</summary>
    public static void WriteHideRequest()
    {
        Directory.CreateDirectory(AppPaths.TempDir);
        File.WriteAllText(Path.Combine(AppPaths.TempDir, HideRequestFileName),
            DateTime.Now.ToString("O"));
    }

    /// <summary>消费请求凭证：存在则删除并返回 true（不存在＝登录触发等误调用，跳过）。</summary>
    public static bool ConsumeHideRequest()
    {
        try
        {
            var path = Path.Combine(AppPaths.TempDir, HideRequestFileName);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    // ---------------- 内部 ----------------

    private const int CrSuccess = 0;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22; // CM_PROB_DISABLED

    private static bool IsDevNodeDisabled(int devInst)
        => CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CrSuccess
           && (status & DnHasProblem) != 0 && problem == CmProbDisabled;

    private static List<HidDeviceEnumerator.HidInstance> FindGamepadDevNodes()
        => HidDeviceEnumerator.FindPhysicalGamepadDevNodes();

    /// <summary>把实例路径并入禁用记录并立即落盘（返回合并后的全量记录）。</summary>
    private static List<string> AppendToRecord(IEnumerable<string> instancePaths)
    {
        var cfg = AppConfig.Load();
        var add = instancePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = cfg.HidHide.DisabledPadDevs.ToList();
        merged.RemoveAll(p => add.Contains(p));
        merged.AddRange(add);
        cfg.HidHide.DisabledPadDevs = merged;
        cfg.Save();
        return merged;
    }

    private static bool WaitSlot0Free(TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (!PadBackendProbe.IsSlot0Occupied()) return true;
            Thread.Sleep(250);
        }
        return !PadBackendProbe.IsSlot0Occupied();
    }

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(int dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(int dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int dnDevInst, string pDeviceId, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblem,
        int dnDevInst, uint ulFlags);
}
