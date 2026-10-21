using System.IO;
using System.Text.Json;
using EfootballBot.Core.System;

namespace EfootballBot.Core.Input;

/// <summary>
/// --pad-config 子进程执行器（提权上下文）。HidHide 整链剔除后仅剩真实手柄接管：
///   EfootballBot.exe --pad-config "&lt;job文件路径&gt;"  —— GUI 写入 job，本进程执行后把结果写回同文件
///   EfootballBot.exe --pad-config pad-hide           —— 计划任务触发：禁用真实手柄释放玩家 1
///   EfootballBot.exe --pad-config pad-restore        —— 计划任务触发：恢复被禁用的真实手柄
///   EfootballBot.exe --pad-config tasks              —— 仅注册提权计划任务
/// （旧参数 --hidhide-config 仍按本入口解析：升级过渡期旧计划任务/shim 尚未重注册前不断链。）
/// </summary>
public static class PadConfigJob
{
    public sealed class PadJobDto
    {
        /// <summary>目前仅 tasks（注册计划任务）；预留扩展位。</summary>
        public string Mode { get; set; } = "tasks";
    }

    public sealed class PadResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string Message { get; set; } = "";
    }

    private static readonly string LogPath =
        Path.Combine(AppPaths.DataDir, "pad-control.log");

    public static int Run(string arg) => arg switch
    {
        "pad-hide" => RunPadHide(),
        "pad-restore" => RunPadRestore(),
        "tasks" => RunEnsureTasks(),
        _ => RunFileJob(arg),
    };

    private static int RunFileJob(string jobPath)
    {
        var result = new PadResultDto();
        try
        {
            var job = JsonSerializer.Deserialize<PadJobDto>(File.ReadAllText(jobPath))
                ?? throw new InvalidDataException("job 文件内容无效");

            result.Success = PhysicalPadControl.EnsureTasksRegistered();
            result.Message = result.Success
                ? "提权计划任务已注册（禁用/恢复真实手柄就绪）"
                : "计划任务注册失败（schtasks 返回非 0）";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        WriteResult(jobPath, result);
        return result.Success ? 0 : 1;
    }

    private static void WriteResult(string jobPath, PadResultDto result)
    {
        try
        {
            File.WriteAllText(jobPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 结果写回失败时 GUI 按超时处理 */ }
    }

    private static int RunPadHide()
    {
        // ONLOGON 触发器也会启动本模式：无请求凭证＝非挂机启动，跳过以防误禁用
        if (!PhysicalPadControl.ConsumeHideRequest())
        {
            AppendLog($"[pad-hide {Now}] 无请求凭证，跳过");
            return 0;
        }
        var (ok, msg) = PhysicalPadControl.DisableAll();
        AppendLog($"[pad-hide {Now}] {(ok ? "成功" : "失败")}：{msg}");
        return ok ? 0 : 1;
    }

    private static int RunPadRestore()
    {
        var (ok, msg) = PhysicalPadControl.RestoreAll();
        AppendLog($"[pad-restore {Now}] {(ok ? "成功" : "失败")}：{msg}");
        return ok ? 0 : 1;
    }

    private static int RunEnsureTasks()
    {
        PhysicalPadControl.LegacyHidHideCleanupOnce();
        bool ok = PhysicalPadControl.EnsureTasksRegistered();
        AppendLog($"[tasks {Now}] {(ok ? "计划任务注册成功" : "计划任务注册失败")}");
        return ok ? 0 : 1;
    }

    private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static void AppendLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }
}
