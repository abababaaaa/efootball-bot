using System.IO;
using System.Text.Json;

namespace EfootballBot.Core.Input;

/// <summary>
/// --hidhide-config 子进程执行器（提权上下文）。
/// 两种入口：
///   EfootballBot.exe --hidhide-config "&lt;job文件路径&gt;"  —— GUI 写入 job，本进程应用后把结果写回同文件
///   EfootballBot.exe --hidhide-config sync              —— 计划任务触发：读 AppConfig 静默同步，结果写日志
/// </summary>
public static class HidHideConfigJob
{
    public sealed class HidHideJobDto
    {
        /// <summary>setup = 驱动未装时先下载安装再配置；apply = 仅配置；clear = 解除隔离。</summary>
        public string Mode { get; set; } = "setup";
        public bool HideVigem { get; set; } = true;
        public bool HidePhysical { get; set; }
        public string GameImagePath { get; set; } = "";
    }

    public sealed class HidHideResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public List<string> AppliedBlacklist { get; set; } = new();
        /// <summary>实际生效的游戏白名单路径（自动解析或传入），GUI 回写配置。</summary>
        public string ResolvedGameImagePath { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>本次是否执行了驱动安装（GUI 可据此提示重启）。</summary>
        public bool DriverInstalled { get; set; }
    }

    private static readonly string SyncLogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EfootballBot", "hidhide-sync.log");

    public static int Run(string arg)
    {
        if (arg == "sync") return RunSync();
        return RunFileJob(arg);
    }

    private static int RunFileJob(string jobPath)
    {
        var result = new HidHideResultDto();
        try
        {
            var job = JsonSerializer.Deserialize<HidHideJobDto>(File.ReadAllText(jobPath))
                ?? throw new InvalidDataException("job 文件内容无效");

            HidHideManager.EnsureServiceStarted(); // DEMAND_START 服务重启后未加载时先拉起

            if (job.Mode == "clear")
            {
                var (ok, err) = HidHideManager.ClearIsolation(
                    LoadAppliedSnapshot(), job.GameImagePath);
                result.Success = ok;
                result.Error = err;
                if (ok)
                {
                    HidHideManager.DeleteSyncTask();
                    result.Message = "隔离已关闭";
                }
            }
            else
            {
                // setup：驱动未安装（或 IOCTL 打不开）时先静默下载安装
                if (job.Mode == "setup" && !HidHideManager.IsDriverInstalled())
                {
                    var (instOk, instMsg) = HidHideInstaller.InstallElevatedAsync().GetAwaiter().GetResult();
                    if (!instOk)
                    {
                        result.Error = instMsg;
                        WriteResult(jobPath, result);
                        return 1;
                    }
                    result.DriverInstalled = true;
                    HidHideManager.EnsureServiceStarted();
                }

                var (ok, err, applied, resolved) = HidHideManager.ApplyIsolation(
                    job.HideVigem, job.HidePhysical, job.GameImagePath);
                result.Success = ok;
                result.Error = err;
                result.AppliedBlacklist = applied;
                result.ResolvedGameImagePath = resolved;
                if (ok)
                {
                    bool taskOk = HidHideManager.EnsureSyncTaskRegistered();
                    result.Message = taskOk
                        ? $"手柄隔离已开启（黑名单已配置 {applied.Count} 项）"
                        : $"手柄隔离已开启，但同步计划任务注册失败（实例变化时需重新开关一次）";
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        WriteResult(jobPath, result);
        return result.Success ? 0 : 1;
    }

    private static void WriteResult(string jobPath, HidHideResultDto result)
    {
        try
        {
            File.WriteAllText(jobPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 结果写回失败时 GUI 按超时处理 */ }
    }

    /// <summary>clear 模式的回滚快照来源：job 未带时从 AppConfig 读。</summary>
    private static List<string> LoadAppliedSnapshot()
    {
        try { return EfootballBot.Core.Config.AppConfig.Load().HidHide.AppliedBlacklist; }
        catch { return new List<string>(); }
    }

    private static int RunSync()
    {
        // 计划任务上下文：读配置静默重写黑名单+白名单（只追加，幂等）
        try
        {
            HidHideManager.EnsureServiceStarted(); // 重启电脑后 DEMAND_START 服务未加载时先拉起
            var appCfg = EfootballBot.Core.Config.AppConfig.Load();
            var cfg = appCfg.HidHide;
            if (!cfg.Enabled) return 0;

            var (ok, err, applied, resolved) = HidHideManager.ApplyIsolation(
                hideVigem: true, hidePhysical: cfg.HidePhysicalGamepad, gameImagePath: cfg.GameImagePath);
            if (ok)
            {
                // 快照合并：applied 仅本次新增，物理手柄条目靠该快照在 clear 时回滚
                var merged = HidHideManager.MergeInto(cfg.AppliedBlacklist, applied);
                bool changed = merged.Count != cfg.AppliedBlacklist.Count;
                if (changed) cfg.AppliedBlacklist = merged;
                if (!string.IsNullOrWhiteSpace(resolved) &&
                    !string.Equals(resolved, cfg.GameImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    // 游戏首次运行后才解析到路径：回写配置，之后白名单永久带上
                    cfg.GameImagePath = resolved;
                    changed = true;
                }
                if (changed) appCfg.Save();
            }
            AppendSyncLog($"[sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                (ok ? $"成功（托管 {applied.Count}：{string.Join(";", applied)}；game={resolved}）" : $"失败：{err}"));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppendSyncLog($"[sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}] 异常：{ex}");
            return 1;
        }
    }

    private static void AppendSyncLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SyncLogPath)!);
            File.AppendAllText(SyncLogPath, line + Environment.NewLine);
        }
        catch { }
    }
}
