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
/// 是唯一可靠的释放手段。
/// 禁用/启用需管理员权限，由提权计划任务执行（--hidhide-config pad-hide / pad-restore）；
/// 禁用清单持久化到 <see cref="HidHideConfig.DisabledPadDevs"/>（先记录后禁用，
/// 中途崩溃也能在下次登录/启动时恢复）。pad-hide 任务带 ONLOGON 触发器，
/// 故用一次性请求凭证文件防止登录时误禁用。
/// </summary>
public static class PhysicalPadControl
{
    /// <summary>pad-hide 任务的一次性执行凭证（data\tmp\ 下；挂机启动前由 Worker 写入）。</summary>
    public const string HideRequestFileName = "padhide.req";

    // ---------------- 提权上下文执行（计划任务 / runas 子进程内） ----------------

    /// <summary>
    /// 禁用当前全部物理手柄设备节点以释放 XInput 玩家 1。
    /// 禁用后轮询 slot0；仍被占用则升级禁用各集合的父设备（手柄自身的 USB 设备节点，
    /// 永远不会是集线器——HID 集合的父必然是其所属设备）。返回是否成功释放。
    /// </summary>
    public static (bool Success, string Message) DisableAll()
    {
        var leaves = FindGamepadDevNodes();
        if (leaves.Count == 0)
            return (true, "未发现已启用的物理手柄（可能已禁用或未连接）");

        // 先记录后禁用：进程中途崩溃时恢复逻辑仍能按清单启用
        var record = AppendToRecord(leaves.Select(l => l.InstancePath));
        string disabled = string.Join(";", leaves.Select(l => l.InstancePath));
        foreach (var (_, devInst) in leaves)
            CM_Disable_DevNode(devInst, 0);

        if (WaitSlot0Free(TimeSpan.FromSeconds(4)))
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
            if (WaitSlot0Free(TimeSpan.FromSeconds(4)))
                return (true, $"已禁用手柄节点及父设备（{parents.Count} 个父节点）：{disabled}");
        }
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
        var merged = HidHideManager.MergeInto(cfg.HidHide.DisabledPadDevs, instancePaths.ToList());
        cfg.HidHide.DisabledPadDevs = merged;
        cfg.Save();
        return merged;
    }

    private static bool WaitSlot0Free(TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (!VirtualGamepad360.IsSlot0Occupied()) return true;
            Thread.Sleep(250);
        }
        return !VirtualGamepad360.IsSlot0Occupied();
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
