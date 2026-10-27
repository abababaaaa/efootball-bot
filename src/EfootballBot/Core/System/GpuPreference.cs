using EfootballBot.Core.Capture;
using Microsoft.Win32;

namespace EfootballBot.Core.System;

/// <summary>
/// 进程级 GPU 偏好（等效 Windows 设置→系统→屏幕→显示卡（图形设置）→「高性能」）：
/// 核显+独显双显卡笔记本上，Windows 默认把未指定的应用放在核显（节能）运行，
/// 而捕获链路在"进程默认适配器与显示输出属主不一致"时会拿到全黑画面
/// （2026-10-11 用户实机：RTX 3060 Laptop 不设高性能→捕获黑屏；图形设置→高性能→恢复正常）。
/// 这里在启动时自动为本 exe 写入 HKCU 用户级 GpuPreference=2（无需管理员）。
/// 该偏好在进程创建时生效：GUI 先写入 → 之后每次点开始新起的 Worker 子进程即按独显运行；
/// GUI/Worker 各自查一次（幂等），Worker 单独 --console 调试时也能自愈。
/// </summary>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Windows 图形设置「高性能」对应的注册表值（0=让 Windows 决定 1=节能/核显 2=高性能/独显）。</summary>
    private const string HighPerformanceValue = "GpuPreference=2;";

    /// <summary>
    /// 检测多显卡（≥2 个不同名的硬件适配器）且本 exe 未设独显偏好时，写入 GpuPreference=2。
    /// 返回 written=本次是否写入了新偏好（本进程不受影响，下次进程启动生效）；
    /// multiGpu=是否多显卡环境；adapters=硬件适配器名（日志诊断用）。
    /// </summary>
    public static (bool Written, bool MultiGpu, string Adapters) EnsureHighPerformance()
    {
        // 枚举内已按 Luid 去重，但 AMD 的虚拟显示适配器（Indirect Display，ROOT\DISPLAY）
        // 与真卡同名不同 Luid——2026-10-24 反馈包实机：单卡 7800 XT 连同名一起被列出 3 次，
        // 误报「双显卡环境（7800 XT + 7800 XT + 7800 XT）」。这里再按名字去重。
        var adapters = CaptureInteropVortice.GetHardwareAdapterNames()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string desc = string.Join(" + ", adapters);
        if (adapters.Count < 2)
            return (false, false, desc); // 单显卡（台式机/无独显本）：Windows 默认即唯一 GPU，无需处理

        string exePath = Environment.ProcessPath
            ?? global::System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? "";
        if (exePath.Length == 0)
            return (false, true, desc);

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (key is null)
            return (false, true, desc);

        var existing = key.GetValue(exePath) as string;
        if (existing is not null && existing.Contains("GpuPreference=2"))
            return (false, true, desc); // 已设置：本进程启动时即按独显运行

        key.SetValue(exePath, HighPerformanceValue);
        return (true, true, desc);
    }
}
