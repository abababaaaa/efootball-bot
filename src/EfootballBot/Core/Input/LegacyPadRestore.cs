using System.Runtime.InteropServices;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.Input;

/// <summary>
/// 旧版「挂机禁用真实手柄」机制（3.2.0 随共存模式整体移除）的<b>遗留恢复器</b>：
/// 只保留 CM_Enable_DevNode 恢复能力。老版本升级用户的手柄可能仍卡在禁用态
/// （旧版崩溃/强杀没走恢复流程），启动时一次性消费 <see cref="HidHideConfig.DisabledPadDevs"/>
/// 遗留记录并兜底扫描已禁用的 XInput 形节点，全部启用后清空记录。这是恢复，不是禁用。
/// 共存模式下本类不产生任何新禁用。
/// </summary>
public static class LegacyPadRestore
{
    /// <summary>是否存在遗留禁用（恢复清单非空，或存在「已禁用」态的真实 XInput 手柄节点）。</summary>
    public static bool HasLegacyDisabled()
    {
        try
        {
            if (AppConfig.Load().HidHide.DisabledPadDevs.Count > 0) return true;
        }
        catch { /* 配置读取失败按扫描兜底 */ }
        return HasDisabledGamepadNode();
    }

    /// <summary>启用全部遗留禁用的手柄节点：先按记录逆序（父设备先于叶子），再兜底扫描
    /// 所有「已禁用」态的非虚拟 XInput 形节点。幂等——启用已启用设备是无害空操作。
    /// 需要管理员权限（本程序常规已提权）；失败静默，下次启动重试。</summary>
    public static (int Restored, string Message) RestoreAll()
    {
        int restored = 0;
        List<string> record = new();
        try { record = AppConfig.Load().HidHide.DisabledPadDevs; }
        catch { /* 记录不可读时仅走扫描兜底 */ }

        for (int i = record.Count - 1; i >= 0; i--)
        {
            if (CM_Locate_DevNodeW(out int devInst, record[i], 0) == CrSuccess)
            {
                if (CM_Enable_DevNode(devInst, 0) == CrSuccess) restored++;
            }
        }
        if (record.Count > 0)
        {
            try
            {
                var cfg = AppConfig.Load();
                cfg.HidHide.DisabledPadDevs = new();
                cfg.Save();
            }
            catch { /* 清单清理失败不影响恢复本身 */ }
        }

        // 兜底扫描：记录之外的已禁用手柄节点（旧 GUI 陈旧快照覆盖过记录等）
        int swept = 0;
        foreach (var node in HidDeviceEnumerator.EnumerateAllInstances())
        {
            if (!HidDeviceEnumerator.IsXInputStyleInstance(node.InstancePath)) continue;
            if (HidDeviceEnumerator.IsVirtualBusChild(node.DevInst)) continue;
            if (!IsDevNodeDisabled(node.DevInst)) continue;
            if (CM_Enable_DevNode(node.DevInst, 0) == CrSuccess) swept++;
        }

        int total = restored + swept;
        return (total, total == 0
            ? (record.Count == 0 ? "无需恢复（无禁用记录）" : $"记录 {record.Count} 项已处理")
            : $"已恢复 {total} 个手柄节点（记录 {restored} + 扫描 {swept}）");
    }

    /// <summary>当前是否存在处于「已禁用」状态的 XInput 手柄节点（记录丢失时的检测兜底）。</summary>
    public static bool HasDisabledGamepadNode()
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

    // ---------------- 内部 ----------------

    private const int CrSuccess = 0;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22; // CM_PROB_DISABLED

    private static bool IsDevNodeDisabled(int devInst)
        => CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CrSuccess
           && (status & DnHasProblem) != 0 && problem == CmProbDisabled;

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(int dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int dnDevInst, string pDeviceId, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblem,
        int dnDevInst, uint ulFlags);
}
