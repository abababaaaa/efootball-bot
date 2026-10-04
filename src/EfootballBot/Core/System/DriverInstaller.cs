using System.Diagnostics;
using System.IO;
using EfootballBot.Core.Input;

namespace EfootballBot.Core.System;

/// <summary>一次安装尝试的结果。</summary>
/// <param name="Kind">结果类别。</param>
/// <param name="Message">给用户看的完整说明（失败时含安装日志要点）。</param>
/// <param name="DriverReady">复测时驱动是否已可用。</param>
public sealed record DriverSetupResult(DriverSetupResultKind Kind, string Message, bool DriverReady);

/// <summary>释放并静默安装内置 ViGEmBus 驱动；首次复测不就绪时自动补跑一次。</summary>
public sealed class DriverInstaller
{
    private const string SetupExeName = "ViGEmBusSetup.exe";
    private const string SetupLogName = "vigembus_install.log";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private const string FallbackUrl = "https://github.com/nefarius/ViGEmBus/releases";

    private readonly Action<string> _log;

    public DriverInstaller(Action<string> log)
    {
        _log = log;
    }

    public async Task<DriverSetupResult> InstallAsync(CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "EfootballBot");
        string exe = Path.Combine(dir, SetupExeName);
        string logPath = Path.Combine(dir, SetupLogName);

        _log("正在释放内置驱动安装包…");
        await DriverPayload.ReleaseToAsync(exe, ct);

        string hash = DriverPayload.ComputeSha256(exe);
        if (!hash.Equals(DriverPayload.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new DriverSetupResult(
                DriverSetupResultKind.Failed,
                $"安装包校验失败（SHA256={hash}），可能已损坏或被篡改。可手动下载官方安装器：{FallbackUrl}",
                false);
        }

        var first = await RunOnceAsync(exe, logPath, ct);
        if (first.Kind is DriverSetupResultKind.UserCancelled or DriverSetupResultKind.Timeout)
        {
            return BuildResult(first.Kind, false);
        }

        if (VirtualGamepad360.IsDriverInstalled())
        {
            return BuildResult(first.Kind, true);
        }

        _log("驱动尚未就绪（常见于旧版就地升级），按官方说明自动补跑第二次安装…");
        var second = await RunOnceAsync(exe, logPath, ct);
        if (second.Kind is DriverSetupResultKind.UserCancelled or DriverSetupResultKind.Timeout)
        {
            return BuildResult(second.Kind, false);
        }

        bool ready = VirtualGamepad360.IsDriverInstalled();

        if (!ready && second.Kind is DriverSetupResultKind.Installed or DriverSetupResultKind.RebootRequired)
        {
            return new DriverSetupResult(
                DriverSetupResultKind.RebootRequired,
                "安装器已完成，但驱动暂未生效，请重启电脑后再打开本程序。",
                false);
        }

        return BuildResult(second.Kind, ready);
    }

    /// <summary>构造 Advanced Installer 静默安装参数（专有开关必须在前）。</summary>
    internal static string BuildArguments(string logPath)
        => $"/exenoui /qn /norestart /exelog \"{logPath}\"";

    private async Task<(DriverSetupResultKind Kind, int Code)> RunOnceAsync(
        string exe, string logPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = BuildArguments(logPath),
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("安装进程启动失败。");
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗点了"否"
            _log("已取消管理员授权。");
            return (DriverSetupResultKind.UserCancelled, 1602);
        }

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(InstallTimeout);
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出，忽略
            }
            _log("安装超时（超过 5 分钟），已中止。");
            return (DriverSetupResultKind.Timeout, -1);
        }

        int code = process.ExitCode;
        _log($"驱动安装器退出码：{code}");
        return (MsiExitCodes.Map(code), code);
    }

    private DriverSetupResult BuildResult(DriverSetupResultKind kind, bool ready)
    {
        string message = kind switch
        {
            DriverSetupResultKind.Installed => "ViGEmBus 驱动安装完成。",
            DriverSetupResultKind.RebootRequired => "ViGEmBus 驱动安装完成，需要重启电脑后生效。",
            DriverSetupResultKind.AlreadyPresent when ready => "ViGEmBus 驱动已安装并可用。",
            DriverSetupResultKind.AlreadyPresent => $"系统中已有其他版本但当前不可用，可手动运行官方安装器：{FallbackUrl}",
            DriverSetupResultKind.UserCancelled => "已取消管理员授权，驱动未安装。",
            DriverSetupResultKind.Timeout => "驱动安装超时（超过 5 分钟），已中止。",
            _ => $"驱动安装失败，可手动下载官方安装器：{FallbackUrl}",
        };

        if (kind == DriverSetupResultKind.Failed)
        {
            string tail = TryReadLogTail();
            if (!string.IsNullOrEmpty(tail))
            {
                message += Environment.NewLine + tail;
            }
        }

        return new DriverSetupResult(kind, message, ready);
    }

    private string TryReadLogTail()
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "EfootballBot", SetupLogName);
            if (!File.Exists(logPath))
            {
                return "";
            }
            string[] lines = File.ReadLines(logPath).TakeLast(20).ToArray();
            return "安装日志末尾：" + string.Join(" | ", lines);
        }
        catch
        {
            return "";
        }
    }
}
