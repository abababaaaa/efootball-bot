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
        public string Mode { get; set; } = "apply"; // apply | clear
        public bool HideVigem { get; set; } = true;
        public bool HidePhysical { get; set; }
        public string GameImagePath { get; set; } = "";
    }

    public sealed class HidHideResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public List<string> AppliedBlacklist { get; set; } = new();
        public string Message { get; set; } = "";
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
                    result.Message = "隔离已解除";
                }
            }
            else
            {
                var (ok, err, applied) = HidHideManager.ApplyIsolation(
                    job.HideVigem, job.HidePhysical, job.GameImagePath);
                result.Success = ok;
                result.Error = err;
                result.AppliedBlacklist = applied;
                if (ok)
                {
                    bool taskOk = HidHideManager.EnsureSyncTaskRegistered();
                    result.Message = taskOk
                        ? $"隔离已应用（黑名单新增 {applied.Count} 项）"
                        : $"隔离已应用，但同步计划任务注册失败（实例变化时需手动重新应用）";
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        try
        {
            File.WriteAllText(jobPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 结果写回失败时 GUI 按超时处理 */ }
        return result.Success ? 0 : 1;
    }

    /// <summary>clear 模式的回滚快照来源：job 未带时从 AppConfig 读。</summary>
    private static List<string> LoadAppliedSnapshot()
    {
        try { return EfootballBot.Core.Config.AppConfig.Load().HidHide.AppliedBlacklist; }
        catch { return new List<string>(); }
    }

    private static int RunSync()
    {
        // 计划任务上下文：读配置静默重写黑名单（只追加，幂等）
        try
        {
            HidHideManager.EnsureServiceStarted(); // 重启电脑后 DEMAND_START 服务未加载时先拉起
            var cfg = EfootballBot.Core.Config.AppConfig.Load().HidHide;
            if (!cfg.Enabled) return 0;

            var (ok, err, applied) = HidHideManager.ApplyIsolation(
                hideVigem: true, hidePhysical: cfg.HidePhysicalGamepad, gameImagePath: cfg.GameImagePath);
            AppendSyncLog($"[sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                (ok ? $"成功（新增 {applied.Count}：{string.Join(";", applied)}）" : $"失败：{err}"));
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
