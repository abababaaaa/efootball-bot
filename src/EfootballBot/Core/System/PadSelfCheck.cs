using System.Diagnostics;
using System.IO;
using EfootballBot.Core.Input;
using Microsoft.Win32;

namespace EfootballBot.Core.System;

/// <summary>自检结论分类。每类对应一种明确的「下一步动作」，绝不猜原因。</summary>
public enum PadCheckKind
{
    /// <summary>驱动与服务就绪，可以开始挂机。</summary>
    Ready,
    /// <summary>驱动未安装（服务注册项不存在）。</summary>
    DriverNotInstalled,
    /// <summary>服务已注册但处于停止状态（可一键启动）。</summary>
    ServiceStopped,
    /// <summary>服务注册项存在但驱动文件缺失（安装被拦截/被杀软删除）。</summary>
    DriverFileMissing,
    /// <summary>驱动装好了但从没成功加载过（常见于装完未重启）。</summary>
    NeedsReboot,
    /// <summary>当前进程未提权，无法安装/启动驱动服务。</summary>
    NeedsElevation,
    /// <summary>探测本身失败（权限/系统异常），只报事实不给结论。</summary>
    Unknown,
}

/// <summary>一次自检结果。<see cref="Report"/> 是给排障用的原始探测明细。</summary>
public sealed record PadCheckResult(
    PadCheckKind Kind,
    string Title,
    string Detail,
    string SuggestedFix,
    bool CanAutoFix,
    string Report);

/// <summary>
/// 手柄驱动自检（依赖链：ViGEmBus 驱动 → 内核服务 → 虚拟手柄客户端）。
/// 首次使用失败的最大原因就在这里，而用户能给出的信息往往只有一句「连不上」。
///
/// 设计原则（重要）：**只陈述探测到的事实，不猜原因**。
/// 每一项探测的原始结果都写进 Report，用户点「导出报告」即可把真实状态发回来，
/// 文案可以据此再修——所以本功能不依赖事先拿到各种失败态截图。
/// </summary>
public static class PadSelfCheck
{
    public const string ServiceName = "ViGEmBus";

    public static PadCheckResult Run()
    {
        var report = new List<string>();
        void R(string s) => report.Add(s);

        R($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        R($"程序版本：{Updater.FormatVersion(typeof(PadSelfCheck).Assembly.GetName().Version)}");
        R($"系统：{Environment.OSVersion.VersionString}");
        R($"管理员：{(Elevation.IsCurrentProcessElevated() ? "是" : "否")}");

        bool elevated = Elevation.IsCurrentProcessElevated();
        bool registered = VigemPad.IsDriverInstalled();
        R($"服务注册项 HKLM\\SYSTEM\\CurrentControlSet\\Services\\{ServiceName}：{(registered ? "存在" : "不存在")}");

        string driverPath = Path.Combine(Environment.SystemDirectory, "drivers", "ViGEmBus.sys");
        bool fileExists = File.Exists(driverPath);
        R($"驱动文件 {driverPath}：{(fileExists ? "存在" : "不存在")}");
        if (fileExists)
        {
            try
            {
                var fi = new FileInfo(driverPath);
                R($"驱动文件大小/时间：{fi.Length} 字节 / {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch { }
        }

        var (scOk, scOut) = RunSc("query", ServiceName);
        R($"sc query {ServiceName}：{(scOk ? "OK" : "失败")}");
        foreach (var line in scOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            R("  " + line);

        bool serviceRunning = scOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        bool serviceStopped = scOut.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);
        bool serviceNotFound = scOut.Contains("1060", StringComparison.Ordinal)
                               || scOut.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
        R($"判定：running={serviceRunning} stopped={serviceStopped} notFound={serviceNotFound}");

        // 设备节点：驱动服务跑起来后才会出现虚拟手柄设备（挂机连接成功时才有）
        int instances = 0;
        try { instances = HidDeviceEnumerator.EnumerateAllInstances().Count; } catch { }
        R($"当前 HID 实例数：{instances}");

        string reportText = string.Join(Environment.NewLine, report);

        if (serviceNotFound || !registered)
        {
            return new PadCheckResult(PadCheckKind.DriverNotInstalled,
                "未检测到 ViGEmBus 驱动",
                registered ? "服务注册项在，但系统报 Not Found（安装不完整）。" : "系统里没有 ViGEmBus 服务注册项，说明驱动从未安装成功。",
                elevated
                    ? "点「一键修复」安装内置驱动（约 10~30 秒，安装完可能需要重启）。"
                    : "请右键本程序选择「以管理员身份运行」，再点「一键修复」。",
                CanAutoFix: elevated,
                reportText);
        }

        if (!fileExists)
        {
            return new PadCheckResult(PadCheckKind.DriverFileMissing,
                "驱动服务存在，但驱动文件缺失",
                $"{driverPath} 不存在——通常是安装被安全软件拦截，或文件被清理工具删除。",
                elevated ? "点「一键修复」重新安装内置驱动。" : "请以管理员身份运行后点「一键修复」。",
                CanAutoFix: elevated,
                reportText);
        }

        if (serviceStopped)
        {
            return new PadCheckResult(PadCheckKind.ServiceStopped,
                "驱动已安装，但服务处于停止状态",
                "ViGEmBus 服务存在且驱动文件在位，但当前没有运行，虚拟手柄无法创建。",
                elevated ? "点「一键修复」启动服务（无需重装）。" : "请以管理员身份运行后点「一键修复」。",
                CanAutoFix: elevated,
                reportText);
        }

        if (!serviceRunning && fileExists && !elevated)
        {
            return new PadCheckResult(PadCheckKind.NeedsElevation,
                "无法确认驱动服务状态（权限不足）",
                "当前进程没有管理员权限，查询服务状态可能不完整。",
                "请右键本程序选择「以管理员身份运行」后再检测一次。",
                CanAutoFix: false,
                reportText);
        }

        if (!serviceRunning)
        {
            return new PadCheckResult(PadCheckKind.NeedsReboot,
                "驱动已安装但服务未运行",
                "服务注册项与驱动文件都在，服务却不是 RUNNING 状态——常见于刚安装完还没重启。",
                "请重启电脑后重新检测；重启仍不行，点「一键修复」重装驱动。",
                CanAutoFix: elevated,
                reportText);
        }

        return new PadCheckResult(PadCheckKind.Ready,
            "手柄依赖就绪",
            "ViGEmBus 驱动已安装、服务正在运行。开始挂机时会自动创建虚拟手柄。",
            "可以直接开始挂机。若游戏里仍无响应，请确认游戏内「手柄设置」里玩家 1 为 Xbox 手柄，且没有其它手柄抢占了玩家 1。",
            CanAutoFix: false,
            reportText);
    }

    /// <summary>
    /// 一键修复：只做与自检结论对应的那一步（启服务 / 装驱动），然后重新自检。
    /// 返回修复后的自检结果；无法自动修复时原样返回。
    /// </summary>
    public static async Task<PadCheckResult> TryFixAsync(Action<string> log, CancellationToken ct = default)
    {
        var before = Run();
        switch (before.Kind)
        {
            case PadCheckKind.ServiceStopped:
            case PadCheckKind.NeedsReboot:
                log($"自检修复：启动 {ServiceName} 服务…");
                var (ok, output) = RunSc("start", ServiceName);
                log(ok ? "服务启动命令已执行。" : $"服务启动失败：{FirstLine(output)}");
                break;

            case PadCheckKind.DriverNotInstalled:
            case PadCheckKind.DriverFileMissing:
                log("自检修复：安装内置 ViGEmBus 驱动…");
                var installer = new DriverInstaller(log);
                var result = await installer.InstallAsync(ct);
                log($"安装结果：{result.Message}");
                break;

            default:
                log("自检：当前状态无需自动修复。");
                return before;
        }

        await Task.Delay(1200, ct); // 给服务/安装器一点落地时间再复检
        return Run();
    }

    /// <summary>把自检报告写到 data\pad-selfcheck.txt（用户可直接发回）。</summary>
    public static string SaveReport(PadCheckResult r)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string path = Path.Combine(AppPaths.DataDir, "pad-selfcheck.txt");
            File.WriteAllText(path,
                $"结论：{r.Kind} / {r.Title}{Environment.NewLine}{r.Detail}{Environment.NewLine}{Environment.NewLine}{r.Report}{Environment.NewLine}");
            return path;
        }
        catch (Exception ex)
        {
            return $"保存失败：{ex.Message}";
        }
    }

    private static string FirstLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    /// <summary>执行 sc.exe（服务查询/启动）。失败不抛：返回是否成功 + 原始输出。</summary>
    private static (bool Ok, string Output) RunSc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "无法启动 sc.exe");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (p.ExitCode == 0, stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr));
        }
        catch (Exception ex)
        {
            return (false, $"sc.exe 执行异常：{ex.Message}");
        }
    }
}
