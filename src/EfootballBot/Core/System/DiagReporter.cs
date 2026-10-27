using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 一键诊断报告（设置 → 关于 → 生成诊断报告；原 DiagReport.bat 的内置版）。
/// 只读收集：系统/显卡/驱动/虚拟手柄栈/系统事件日志/Steam 位置 + 本程序 logs\data 摘要，
/// 打包成一个 zip 供远程分析。除自身输出目录外不改系统任何内容。
/// 采集实现约定：
///  - PowerShell 采集一律 -EncodedCommand 传入、脚本首句把输出编码切到 UTF-8，按 UTF-8 解码；
///  - 原生工具（systeminfo/tasklist 等按控制台代码页输出，中文系统为 GBK）单独运行、按 GBK 解码；
///  - dxdiag /t 先行启动（最慢一步），收尾时轮询等待，超时不算失败。
/// </summary>
public static class DiagReporter
{
    static DiagReporter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly UTF8Encoding Utf8Bom = new(true);

    /// <summary>
    /// 收集诊断并打包。返回 zip 路径；打包失败时返回报告目录路径。
    /// <paramref name="progress"/> 收到分步进度文案（后台线程回调）；
    /// <paramref name="userNote"/> 是用户在界面上填的问题描述（可选），写进包内 user_note.txt 与报告头部。
    /// <paramref name="screenshotPath"/> 是「发现问题手动停止」自动截取的游戏画面（可选），拷进包内 screenshots\。
    /// </summary>
    public static async Task<string> CollectAsync(Action<string>? progress = null, CancellationToken ct = default, string? userNote = null, string? screenshotPath = null)
    {
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string ver = Updater.FormatVersion(typeof(Updater).Assembly.GetName().Version);
        string root = Path.Combine(AppPaths.DataDir, "DiagReport");
        string dir = Path.Combine(root, $"反馈包_{ts}");
        string zip = Path.Combine(root, $"反馈包_v{ver}_{ts}.zip");
        Directory.CreateDirectory(dir);

        var report = new StringBuilder();
        void Head(string title)
        {
            report.AppendLine();
            report.AppendLine("============================================================");
            report.AppendLine($" {title}");
            report.AppendLine("============================================================");
        }

        // dxdiag 最慢，最先在后台启动
        progress?.Invoke("启动 dxdiag（后台收集显卡信息）…");
        string dxPath = Path.Combine(dir, "dxdiag.txt");
        Process? dx = StartDxdiag(dxPath);

        report.AppendLine("eFootball Bot diagnostic report");
        report.AppendLine($"Generated   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"App version : v{Updater.FormatVersion(typeof(Updater).Assembly.GetName().Version)}");
        report.AppendLine($"App dir     : {AppPaths.ExeDir}");
        report.AppendLine("note: report body is UTF-8; dxdiag.txt is kept exactly as dxdiag wrote it.");
        report.AppendLine("note: this scan is read-only, nothing on the system was changed.");

        // 「发现问题手动停止」自动截取的游戏画面：拷进包内 screenshots\，报告里注明文件名
        if (!string.IsNullOrWhiteSpace(screenshotPath) && File.Exists(screenshotPath))
        {
            progress?.Invoke("附带游戏画面截图…");
            string shotDir = Path.Combine(dir, "screenshots");
            Directory.CreateDirectory(shotDir);
            string shotName = $"game_{ts}.png";
            try
            {
                File.Copy(screenshotPath, Path.Combine(shotDir, shotName), true);
                report.AppendLine();
                report.AppendLine("============ GAME SCREENSHOT (点击「发现问题手动停止」时自动截取) ============");
                report.AppendLine($"screenshot: screenshots/{shotName}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"(screenshot copy failed: {ex.Message})");
            }
        }

        // 用户自己描述的问题现象放最显眼的位置（分析时先读这里）
        if (!string.IsNullOrWhiteSpace(userNote))
        {
            report.AppendLine();
            report.AppendLine("============ USER NOTE (用户对问题的描述) ============");
            report.AppendLine(userNote.Trim());
            try
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "user_note.txt"), userNote.Trim(), Utf8Bom, ct);
            }
            catch { /* 写不进也继续 */ }
        }

        progress?.Invoke("收集系统信息…");
        Head("SECTION 1 - RUN ENVIRONMENT");
        report.AppendLine($"AppDir={AppPaths.ExeDir}");
        report.AppendLine($"ProcArch={Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")}");
        report.AppendLine($"ProcId={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
        report.AppendLine($"OS={Environment.OSVersion.VersionString}");
        report.AppendLine($"Is64Bit={Environment.Is64BitOperatingSystem} / Is64BitProcess={Environment.Is64BitProcess}");
        report.AppendLine($"MachineName={Environment.MachineName}");
        report.AppendLine($"Uptime={TimeSpan.FromMilliseconds(Environment.TickCount64)}");

        Head("SECTION 2 - OS VERSION (registry)");
        report.AppendLine(await RunPsAsync(@"
$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$p = Get-ItemProperty -LiteralPath $k
'ProductName      = ' + $p.ProductName
'EditionID        = ' + $p.EditionID
'DisplayVersion   = ' + $p.DisplayVersion
'ReleaseId        = ' + $p.ReleaseId
'CurrentBuild     = ' + $p.CurrentBuildNumber + '.' + $p.UBR
"));

        Head("SECTION 3 - SYSTEMINFO (hotfixes, RAM, network, hypervisor)");
        report.AppendLine(await RunNativeAsync("systeminfo", "", 90, ct));

        Head("SECTION 4 - HARDWARE SUMMARY (WMI)");
        report.AppendLine("[machine]");
        report.AppendLine(await RunPsAsync(@"Get-CimInstance Win32_ComputerSystem | Format-List Manufacturer,Model,SystemFamily,PCSystemType,HypervisorPresent,TotalPhysicalMemory | Out-String -Width 200"));
        report.AppendLine("[cpu]");
        report.AppendLine(await RunPsAsync(@"Get-CimInstance Win32_Processor | Format-List Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed | Out-String -Width 200"));
        report.AppendLine("[os / memory]");
        report.AppendLine(await RunPsAsync(@"Get-CimInstance Win32_OperatingSystem | Format-List Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize,FreePhysicalMemory,SizeStoredInPagingFiles | Out-String -Width 200"));

        ct.ThrowIfCancellationRequested();
        progress?.Invoke("收集显卡信息…");
        Head("SECTION 5 - GPU ADAPTERS (hybrid graphics / driver version / WDDM)");
        report.AppendLine("[Win32_VideoController]");
        report.AppendLine(await RunPsAsync(@"Get-CimInstance Win32_VideoController | Sort-Object Name | Format-List Name,VideoProcessor,AdapterCompatibility,DriverVersion,DriverDate,Status,PNPDeviceID,VideoModeDescription,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,AdapterRAM | Out-String -Width 200"));
        report.AppendLine("[registry adapter entries with VRAM (HardwareInformation.qMemorySize)]");
        report.AppendLine(await RunPsAsync(@"Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue; if ($p.DriverDesc) { '{0}: {1} | VRAM={2}' -f $_.PSChildName, $p.DriverDesc, $p.'HardwareInformation.qMemorySize' } }"));
        report.AppendLine("[nvidia-smi if present]");
        report.AppendLine(await RunNativeAsync("nvidia-smi", "--query-gpu=name,driver_version,memory.total,compute_cap --format=csv", 20, ct));

        Head("SECTION 6 - PER-APP GPU PREFERENCE (Windows graphics preference registry)");
        report.AppendLine(await RunPsAsync(@"
foreach ($hive in 'HKCU:','HKLM:') {
  $k = $hive + '\SOFTWARE\Microsoft\DirectX\UserGpuPreferences'
  if (Test-Path -LiteralPath $k) { " + "\"[$k]\"" + @" ; Get-ItemProperty -LiteralPath $k | Out-String -Width 300 }
}"));

        Head("SECTION 7 - DISPLAYS AND DPI");
        report.AppendLine("[active screens]");
        report.AppendLine(await RunPsAsync(@"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { '{0} | Primary={1} | Bounds={2} | WorkArea={3}' -f $_.DeviceName, $_.Primary, $_.Bounds, $_.WorkingArea }"));
        report.AppendLine("[system DPI (96 = 100 percent)]");
        report.AppendLine(await RunPsAsync(@"(Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics').AppliedDPI"));
        report.AppendLine("[hdr (advanced color) per-output; HDR frames over-expose our color probes]");
        var hdrOuts = HdrStatus.QueryHdrOutputs();
        report.AppendLine(hdrOuts is null
            ? "hdr=detect-failed (DXGI enumeration failed)"
            : hdrOuts.Count == 0
                ? "hdr=off (all outputs SDR)"
                : $"hdr=ON: {string.Join(", ", hdrOuts)}");

        Head("SECTION 8 - POWER PLAN");
        report.AppendLine(await RunNativeAsync("powercfg", "/getactivescheme", 20, ct));

        ct.ThrowIfCancellationRequested();
        progress?.Invoke("收集虚拟手柄驱动状态…");
        Head("SECTION 9 - VIGEMBUS DRIVER + PAD TASKS (virtual gamepad stack)");
        report.AppendLine(await RunNativeAsync("sc", "query ViGEmBus", 20, ct));
        report.AppendLine(await RunNativeAsync("sc", "qc ViGEmBus", 20, ct));
        report.AppendLine("[pad takeover scheduled tasks]");
        report.AppendLine(await RunNativeAsync("schtasks", "/Query /TN EfootballBotPadHide /V /FO LIST", 20, ct));
        report.AppendLine(await RunNativeAsync("schtasks", "/Query /TN EfootballBotPadRestore /V /FO LIST", 20, ct));
        report.AppendLine("[driver sys file versions]");
        report.AppendLine(await RunPsAsync(@"foreach ($n in @('ViGEmBus.sys','xusb22.sys')) { $f = Join-Path $env:windir (Join-Path 'System32\drivers' $n); if (Test-Path -LiteralPath $f) { $i = Get-Item -LiteralPath $f; $v = $i.VersionInfo; '{0} | FileVersion={1} | Product={2} | Modified={3}' -f $f, $v.FileVersion, $v.ProductName, $i.LastWriteTime } else { '{0} | NOT FOUND' -f $n } }"));
        report.AppendLine("[running drivers matching vigem/xusb]");
        report.AppendLine(FilterLines(await RunNativeAsync("driverquery", "/v /fo csv", 60, ct),
            static l => l.Contains("vigem", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("hidhide", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("xusb", StringComparison.OrdinalIgnoreCase)));

        Head("SECTION 10 - INPUT DEVICES (HID/USB present devices; look for gamepads)");
        report.AppendLine(await RunPsAsync(@"Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Class -in @('HIDClass','USB','XboxPeripherals') } | Sort-Object Class | ForEach-Object { '{0} | {1} | {2}' -f $_.Class, $_.Status, $_.FriendlyName }", 60));

        Head("SECTION 11 - RUNNING PROCESSES (efootball / bot / steam)");
        report.AppendLine(FilterLines(await RunNativeAsync("tasklist", "/fo csv", 40, ct),
            static l => l.Contains("efootball", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("steam", StringComparison.OrdinalIgnoreCase)));

        Head("SECTION 12 - ANTIVIRUS PRODUCTS (may interfere with capture / HID drivers)");
        report.AppendLine(await RunPsAsync(@"Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { '{0} | productState={1} | {2}' -f $_.displayName, $_.productState, $_.pathToSignedProductExe }", 40));

        ct.ThrowIfCancellationRequested();
        progress?.Invoke("收集运行库与错误日志…");
        Head("SECTION 13 - .NET AND VC++ RUNTIMES");
        report.AppendLine("[dotnet runtimes]");
        report.AppendLine(await RunNativeAsync("dotnet", "--list-runtimes", 30, ct));
        report.AppendLine("[.NET Framework 4.x release]");
        report.AppendLine(await RunPsAsync(@"(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release"));
        report.AppendLine("[VC++ redistributes installed (both registry views)]");
        report.AppendLine(await RunPsAsync(@"
foreach ($view in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
  Get-ChildItem -LiteralPath $view -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
    if ($p.DisplayName -like '*Visual C++*') { '{0} | {1}' -f $p.DisplayName, $p.DisplayVersion }
  }
}"));

        Head("SECTION 14 - RECENT ERRORS (7 days): display-driver resets and system/app errors");
        report.AppendLine("[TDR: display driver stopped responding (Event 4101), last 10]");
        report.AppendLine(await RunPsAsync(@"Get-WinEvent -FilterHashtable @{LogName='System'; Id=4101} -MaxEvents 10 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1}' -f $_.TimeCreated, ($_.Message -replace '\s+', ' ') }", 60));
        report.AppendLine("[System log errors, last 25 in 7 days]");
        report.AppendLine(await RunPsAsync(@"Get-WinEvent -FilterHashtable @{LogName='System'; Level=2; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 25 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1} | Id={2} | {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' ') }", 60));
        report.AppendLine("[Application log errors, last 25 in 7 days]");
        report.AppendLine(await RunPsAsync(@"Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 25 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1} | Id={2} | {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' ') }", 60));

        Head("SECTION 15 - STEAM AND GAME INSTALL LOCATION");
        report.AppendLine(await RunPsAsync(@"
$steam = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
'SteamPath = ' + $steam
if ($steam) {
  $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
  if (Test-Path -LiteralPath $vdf) {
    Get-Content -LiteralPath $vdf | Where-Object { $_ -match 'path' } | ForEach-Object {
      $lib = ($_ -split [char]34)[-2]
      if ($lib) {
        $game = Join-Path (Join-Path $lib 'common') 'eFootball'
        'Library: ' + $lib + ' | eFootball installed: ' + (Test-Path -LiteralPath $game)
      }
    }
  }
}", 40));

        ct.ThrowIfCancellationRequested();
        progress?.Invoke("打包本程序日志与配置…");
        Head("SECTION 16 - APP FOLDER (bot logs/config copied; onnxruntime versions)");
        report.AppendLine(CopyAppFiles(dir, report));

        Head("SECTION 17 - RECENT CRASH DUMPS (auto-captured by CrashReporter)");
        progress?.Invoke("附带最近崩溃记录…");
        report.AppendLine(CopyCrashDumps(dir, report));

        progress?.Invoke("等待 dxdiag 完成…");
        Head("SECTION 18 - DXDIAG (full dxdiag.txt attached separately in this folder)");
        report.AppendLine(await WaitDxdiagAsync(dx, dxPath, ct));

        report.AppendLine();
        report.AppendLine("End of report.");
        await File.WriteAllTextAsync(Path.Combine(dir, "report.txt"), report.ToString(), Utf8Bom, ct);

        progress?.Invoke("创建 zip 压缩包…");
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, false);
            return zip;
        }
        catch
        {
            return dir; // 打包失败时至少把整个文件夹交给用户
        }
    }

    /// <summary>拷贝 logs（7 天内）与 data 配置/日志文件到报告目录，返回结果说明。
    /// 排除 captures 存帧与 tmp（体积大、无分析价值）。</summary>
    private static string CopyAppFiles(string dir, StringBuilder report)
    {
        int copied = 0;
        void CopyTree(string? srcRoot, string dstName, string[] patterns, string[] excludeDirs, int maxAgeDays)
        {
            if (srcRoot is null || !Directory.Exists(srcRoot)) return;
            string dst = Path.Combine(dir, dstName);
            foreach (var file in EnumerateFilesSafe(srcRoot, patterns, excludeDirs))
            {
                try
                {
                    if (maxAgeDays > 0 && File.GetLastWriteTime(file) < DateTime.Now.AddDays(-maxAgeDays)) continue;
                    var len = new FileInfo(file).Length;
                    if (len is < 1 or > 20 * 1024 * 1024) continue;
                    var rel = Path.GetRelativePath(srcRoot, file);
                    var target = Path.Combine(dst, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                    copied++;
                }
                catch { /* 单个文件失败不影响整体 */ }
            }
        }

        CopyTree(AppPaths.LogDir, "app_logs", new[] { "*.log", "*.yml", "*.txt" }, Array.Empty<string>(), 7);
        CopyTree(AppPaths.DataDir, "app_data", new[] { "*.log", "*.json", "*.txt" }, new[] { "tmp", "captures", "DiagReport" }, 0);
        report.AppendLine($"app files copied: {copied} (logs last 7 days -> app_logs\\, config -> app_data\\)");

        report.AppendLine("[ml/onnx related dll versions in app folder]");
        foreach (var n in new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_dml.dll", "DirectML.dll", "ViGEmClient.dll" })
        {
            var f = Path.Combine(AppPaths.ExeDir, n);
            report.AppendLine(File.Exists(f)
                ? $"{n} | FileVersion={FileVersionInfo.GetVersionInfo(f).FileVersion}"
                : $"{n} | not present");
        }
        return $"app files copied: {copied}";
    }

    /// <summary>附带最近崩溃 dump（最多 2 份，单份 ≤100MB——QQ 群文件友好），返回结果说明。</summary>
    private static string CopyCrashDumps(string dir, StringBuilder report)
    {
        try
        {
            var crashDir = Path.Combine(AppPaths.DataDir, "crash");
            if (!Directory.Exists(crashDir)) return "(no crash dump folder — no crashes recorded)";
            var dumps = Directory.GetFiles(crashDir, "*.dmp")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(2)
                .ToList();
            if (dumps.Count == 0) return "(no crash dumps)";

            string dst = Path.Combine(dir, "app_crash");
            Directory.CreateDirectory(dst);
            int copied = 0;
            var notes = new List<string>();
            foreach (var d in dumps)
            {
                if (d.Length > 100 * 1024 * 1024)
                {
                    notes.Add($"{d.Name} skipped (over 100MB)");
                    continue;
                }
                try { File.Copy(d.FullName, Path.Combine(dst, d.Name), true); copied++; }
                catch (Exception ex) { notes.Add($"{d.Name} copy failed: {ex.Message}"); }
            }
            report.AppendLine($"crash dumps on disk: newest {dumps[0].Name} ({dumps[0].Length / 1024 / 1024} MB, {dumps[0].LastWriteTime:yyyy-MM-dd HH:mm})");
            foreach (var n in notes) report.AppendLine(n);
            return $"crash dumps copied: {copied} -> app_crash\\";
        }
        catch (Exception ex)
        {
            return $"(crash dump copy failed: {ex.Message})";
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string[] patterns, string[] excludeDirs)
    {
        // 排除目录按前缀匹配（含其全部子目录）。SafeDirs 会把被排除目录的子目录也压栈，
        // 若只按目录本身判等，DiagReport 下的「反馈包_ts」不被排除——正在写入的报告目录
        // 被拷进 app_data，报告目录里又长出 app_data，递归自拷几十层
        // （v2.0.4 用户反馈包 2026-10-15 实测：app_data/DiagReport/反馈包_ts/app_data/... 嵌套）。
        var skip = excludeDirs
            .Select(d => Path.Combine(root, d) + Path.DirectorySeparatorChar)
            .ToList();
        var dirs = SafeDirs(root);
        foreach (var d in dirs)
        {
            var probe = d + Path.DirectorySeparatorChar;
            if (skip.Any(s => probe.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var pat in patterns)
                foreach (var f in SafeFiles(d, pat))
                    yield return f;
        }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            try { foreach (var sub in Directory.EnumerateDirectories(d)) stack.Push(sub); }
            catch { /* 拒绝访问跳过 */ }
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static string FilterLines(string output, Func<string, bool> predicate)
        => string.Join(Environment.NewLine, output.Split('\n').Where(predicate)).TrimEnd() is { Length: > 0 } s ? s : "(no matching lines)";

    private static Process? StartDxdiag(string outputPath)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "dxdiag.exe",
                Arguments = $"/t \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { return null; }
    }

    /// <summary>轮询等待 dxdiag 输出文件（最长 90 秒），找到后再等 3 秒让它写完（与 bat 一致）。</summary>
    private static async Task<string> WaitDxdiagAsync(Process? dx, string dxPath, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < 45 && !File.Exists(dxPath); i++)
                await Task.Delay(2000, ct);
            if (!File.Exists(dxPath))
                return "dxdiag.txt: TIMEOUT after ~90 seconds (skipped). System policy may block dxdiag.";
            await Task.Delay(3000, ct);
            return $"dxdiag.txt present, size: {new FileInfo(dxPath).Length} bytes";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"dxdiag wait failed: {ex.Message}"; }
        finally
        {
            try { if (dx is { HasExited: false }) dx.Kill(); } catch { }
            dx?.Dispose();
        }
    }

    /// <summary>PowerShell 采集：-EncodedCommand 传脚本（避开引号转义地狱），首句切 UTF-8 输出。</summary>
    private static async Task<string> RunPsAsync(string script, int timeoutSec = 45)
    {
        const string prefix = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ErrorActionPreference = 'Continue';\n";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(
                Encoding.Unicode.GetBytes(prefix + script)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        return await RunAsync(psi, Encoding.UTF8, timeoutSec);
    }

    /// <summary>原生命令采集：输出按系统 OEM 代码页（中文系统 GBK）解码。</summary>
    private static async Task<string> RunNativeAsync(string fileName, string arguments, int timeoutSec, CancellationToken ct)
    {
        Encoding oem;
        try { oem = Encoding.GetEncoding(0); }
        catch { oem = Encoding.UTF8; }
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try { return await RunAsync(psi, oem, timeoutSec, ct); }
        catch (Exception ex) { return $"({fileName} not available: {ex.Message})"; }
    }

    private static async Task<string> RunAsync(ProcessStartInfo psi, Encoding encoding, int timeoutSec, CancellationToken ct = default)
    {
        using var p = Process.Start(psi);
        if (p is null) return "(failed to start)";
        var so = p.StandardOutput.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(timeoutSec));
        var se = p.StandardError.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(timeoutSec));
        var exit = p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(timeoutSec));
        string stdout, stderr;
        try
        {
            await Task.WhenAll(so, se, exit);
            stdout = so.Result;
            stderr = se.Result;
        }
        catch (TimeoutException)
        {
            try { p.Kill(true); } catch { }
            return "(timeout)";
        }
        var text = stdout.TrimEnd() + (stderr.Length > 0 ? "\n[stderr] " + stderr.TrimEnd() : "");
        return text.Length == 0 ? "(no output)" : text;
    }
}
