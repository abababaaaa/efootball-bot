using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace EfootballBot.Core.System;

/// <summary>
/// 更新应用器：下载新版本 → 校验 → 原子交换 → 重启，全程托管代码，无外部脚本。
///
/// 为什么是「改名交换」而不是复制覆盖（v2.0.5.6 重写，替代 v2.0.5.2 的 cmd/bat 方案）：
/// ① Windows 允许重命名正在运行的 exe（只禁止删除/覆盖）——先把当前 exe 改名为 *.old，
///   再把新 exe 移到原路径，两步各自瞬时完成，任何一步失败都能把 .old 移回来，本进程
///   始终在内存中运行、用户无感；而 copy /Y 覆盖失败或截断时旧版已被毁，只能靠 bat 重试
///   死循环赌杀软放手（"更新后旧版复活"系列事故的根源）。
/// ② 不再 taskkill /IM——按映像名强杀会连坐用户开的同名便携副本、且是硬中断（Worker
///   可能正写配置）。新流程由本进程走正常 Shutdown（存配置→停 Worker→恢复手柄），
///   新实例带 --updated 参数启动并轮询等待单实例锁释放。
/// ③ 旧 exe 保留为 .old，新实例稳定运行 10 秒后才删除——新版本若起不来（启动即崩），
///   备份仍在，可手动改回原名恢复；启动/清理链路兜底异常残留。
/// </summary>
public static class UpdateApplier
{
    /// <summary>更新重启的新实例参数（App.xaml.cs 据此等待旧实例退出并做收尾）。</summary>
    public const string UpdatedArg = "--updated";
    /// <summary>旧版本备份后缀（跟随原 exe 文件名，如 "EfootballBot (8).exe.old"）。</summary>
    public const string OldSuffix = ".old";

    /// <summary>新实例启动收尾动作（纯函数便于单测）。</summary>
    public enum StartupFinalizeAction { None, DeleteOld, RestoreOld }

    internal static string OldPathFor(string exePath) => exePath + OldSuffix;

    /// <summary>按 exe/备份的存在性决定启动收尾（四象限）：
    /// 都在=新版本已在跑，延迟删备份；只有备份=上次交换中断（掉电等），还原备份救命；
    /// 其余无事可做。</summary>
    internal static StartupFinalizeAction DecideFinalizeAction(bool exeExists, bool oldExists) =>
        (exeExists, oldExists) switch
        {
            (true, true) => StartupFinalizeAction.DeleteOld,
            (false, true) => StartupFinalizeAction.RestoreOld,
            _ => StartupFinalizeAction.None,
        };

    /// <summary>更新专用日志（data\update.log）：更新失败远程排障时直接要这一份文件。</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { /* 日志失败不影响更新 */ }
    }

    /// <summary>
    /// 下载并应用更新（GUI「立即更新」入口）。成功返回 Applied——此时新进程已带
    /// --updated 参数启动，调用方应尽快优雅退出（Application.Current.Shutdown）。
    /// </summary>
    public static async Task<UpdateResult> ApplyAsync(UpdateInfo info,
        IProgress<(long Bytes, long Total)>? progress = null)
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前 exe 路径");
            Log($"=== 更新开始：v{info.Version}，{(info.SingleExecutable ? "裸 exe" : "zip")}，" +
                $"来源 {HostOf(info.DownloadUrls.FirstOrDefault())}，约 {info.TotalBytes / 1024 / 1024} MB ===");

            // ① 下载到 exe 旁 data\tmp\update\<guid>\（所有操作收在程序目录内）
            string tempDir = Path.Combine(AppPaths.TempDir, "update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string downloadPath = Path.Combine(tempDir, "download.bin");

            // ①' 差量优先（最小可用版：只支持「上一版 → 新版」一跳）。
            //     补丁补齐后必须与整包 SHA256 完全一致才算数；任何失败都静默回退整包下载。
            string? deltaExe = null;
            try { deltaExe = await TryDeltaAsync(exePath, info, tempDir, progress); }
            catch (Exception ex) { Log($"差量更新不可用，改走整包：{ex.Message}"); }

            string newExePath;
            if (deltaExe is not null)
            {
                newExePath = deltaExe;
                Log("已用差量补丁生成新版本（本次未下载整包）");
            }
            else
            {
                long downloaded = await DownloadAsync(info.DownloadUrls, downloadPath, info.TotalBytes, progress,
                    info.ExpectedSha256);
                if (downloaded != info.TotalBytes)
                    throw new IOException($"下载不完整：收到 {downloaded} 字节，应为 {info.TotalBytes}");
                Log($"下载完成：{downloaded} 字节");

                // ② 取出新版 exe：裸 exe 直接用；zip（≤2.0.2 旧更新器兼容格式，根级单个 exe）
                //    解压后找根级（或一层子目录内的）exe
                newExePath = downloadPath;
                if (!info.SingleExecutable)
                {
                    string extractDir = Path.Combine(tempDir, "extracted");
                    ZipFile.ExtractToDirectory(downloadPath, extractDir);
                    newExePath = FindExtractedExe(extractDir)
                        ?? throw new InvalidOperationException("zip 中未找到 exe");
                }
            }

            // ③ 原子交换（失败自动回滚，本进程不受影响）
            SwapExe(exePath, newExePath);
            Log($"交换完成：新版本已就位（{Path.GetFileName(exePath)}），旧版本保留为 {OldSuffix}");

            // ④ 启动新实例（--updated：等待本进程退出后接管，并做备份清理）
            LaunchNewVersion(exePath);
            Log($"新实例已启动（pid {Environment.ProcessId} 的接替者），等待本进程退出");

            // 临时目录使命完成（download.bin 已移走），空壳即删；失败时保留供排查，
            // 由 CleanupStaleArtifacts 按 24h 兜底
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            return UpdateResult.Success($"v{info.Version} 已就位，正在重启…");
        }
        catch (Exception ex)
        {
            Log($"更新失败：{ex.Message}");
            return UpdateResult.Error($"更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 差量更新（最小可用版）：从同一发布源取 <c>patches/patch-{旧版本}-to-{新版本}.bin</c>，
    /// 对当前 exe 应用补丁，产出的文件必须与整包 SHA256 完全一致才返回。
    ///
    /// 返回 null 表示「没有差量可用」——没配 SHA256、没有补丁、补丁下载/应用/校验任一失败。
    /// 调用方在这种情况直接走整包，用户无感。这正是设计意图：差量只加速，不承担正确性。
    /// </summary>
    private static async Task<string?> TryDeltaAsync(string exePath, UpdateInfo info, string tempDir,
        IProgress<(long Bytes, long Total)>? progress)
    {
        // 没有期望哈希就无法证明补丁产物正确 → 不做差量（宁可多下 100MB，不能换上一个坏 exe）
        if (string.IsNullOrWhiteSpace(info.ExpectedSha256)) return null;

        string from = Updater.FormatVersion(Updater.CurrentVersion);
        string to = (info.Version?.ToString() ?? "").TrimStart('v', 'V');
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return null;

        string patchName = $"patch-{from}-to-{to}.bin";
        var urls = new List<string>();
        foreach (var u in info.DownloadUrls)
        {
            if (!u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            int slash = u.LastIndexOf('/');
            if (slash <= 0) continue;
            string dir = u[..(slash + 1)];
            // 两种发布约定都试：
            // ① GitHub Release 附件：与 exe 同目录的独立资产 <dir>/patch-xxx.bin
            // ② R2 桶目录：<dir>/patches/patch-xxx.bin
            urls.Add(dir + patchName);
            urls.Add(dir + "patches/" + patchName);
        }
        if (urls.Count == 0) return null;

        string patchPath = Path.Combine(tempDir, patchName);
        try
        {
            // 补丁本体不做哈希校验：即使被篡改，产物 SHA256 也必然对不上，等价于「下载失败」
            await DownloadAsync(urls, patchPath, 0, progress, expectedSha256: null);
        }
        catch (Exception ex)
        {
            Log($"未取到差量补丁（{patchName}）：{ex.Message}");
            try { File.Delete(patchPath); } catch { }
            return null;
        }

        long patchSize = new FileInfo(patchPath).Length;
        string outExe = Path.Combine(tempDir, "delta_new.exe");
        try
        {
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            DeltaPatch.Apply(exePath, patchPath, outExe);
            sw.Stop();

            long newSize = new FileInfo(outExe).Length;
            string actual = ComputeSha256(outExe);
            if (!string.Equals(actual, info.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log($"差量产物校验不符（{actual[..12]}…），回退整包");
                try { File.Delete(outExe); } catch { }
                return null;
            }
            if (info.TotalBytes > 0 && newSize != info.TotalBytes)
            {
                Log($"差量产物大小不符（{newSize} != {info.TotalBytes}），回退整包");
                try { File.Delete(outExe); } catch { }
                return null;
            }

            Log($"差量补丁生效：{DeltaPatch.Describe(patchSize, newSize)}，应用耗时 {sw.ElapsedMilliseconds}ms");
            try { File.Delete(patchPath); } catch { }
            return outExe;
        }
        catch (Exception ex)
        {
            Log($"差量补丁应用失败，回退整包：{ex.Message}");
            try { File.Delete(outExe); } catch { }
            return null;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 按候选顺序下载单个文件（候选=同一字节的多个来源：国内直连源 → 加速镜像 → GitHub 直连，
    /// Updater.BuildDownloadUrls 生成；不是分卷拼接）。单候选 60 秒收不到数据（含连接/首字节
    /// 等待）即放弃换下一个——慢镜像只要还在流数据就不熔断；候选失败回卷文件长度，防半截数据
    /// 污染后续候选。指定 expectedSha256 时每个候选下载完即校验，不匹配按该候选失败处理
    /// （镜像被篡改/反代吐错误页都拦在这），全部候选耗尽才抛最后的错误。返回实际字节数。
    /// </summary>
    internal static async Task<long> DownloadAsync(IReadOnlyList<string> candidates, string targetPath,
        long expectedTotal, IProgress<(long Bytes, long Total)>? progress, string? expectedSha256 = null)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
        var buffer = new byte[81920];
        Exception? last = null;
        // 文件流只写不读（FileAccess.Write），哈希随下载流增量计算，不回读文件
        using var hasher = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var url in candidates)
        {
            try
            {
                using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(stall.Token);
                long written = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer, stall.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), stall.Token);
                    hasher?.AppendData(buffer, 0, n);
                    written += n;
                    stall.CancelAfter(TimeSpan.FromSeconds(60)); // 收到数据即续命
                    progress?.Report((written, expectedTotal));
                }
                // 反代吐 200 错误页/连接提前收尾时不会有异常，用 Content-Length 兜住
                if (resp.Content.Headers.ContentLength is long d && d != written)
                    throw new IOException($"内容长度不符：收到 {written} 字节，标称 {d}");

                if (hasher is not null)
                {
                    string actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (actual != expectedSha256)
                        throw new InvalidDataException(
                            $"SHA256 校验失败：实际 {actual[..12]}…，期望 {expectedSha256[..12]}…");
                }

                Log($"下载成功：{HostOf(url)}（{written} 字节）");
                return written;
            }
            catch (Exception ex)
            {
                last = ex;
                hasher?.GetHashAndReset(); // 丢弃半截数据的哈希状态，下一候选从零累计
                file.Seek(0, SeekOrigin.Begin);
                file.SetLength(0);
                Log($"下载通道失败，切换下一通道：{HostOf(url)} — {ex.Message}");
            }
        }
        throw last ?? new IOException("所有下载通道均失败");
    }

    private static string? FindExtractedExe(string extractDir)
    {
        var exe = Directory.GetFiles(extractDir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (exe is null)
        {
            foreach (var sub in Directory.GetDirectories(extractDir))
            {
                exe = Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (exe is not null) break;
            }
        }
        return exe;
    }

    private static string HostOf(string? url)
    {
        try { return url is null ? "?" : new Uri(url).Host; }
        catch { return "?"; }
    }

    /// <summary>
    /// 核心交换（运行中的 exe 可改名、不可删除/覆盖）：当前 exe → *.old，新 exe → 原路径。
    /// 第二步失败把 *.old 移回原位（本进程在内存中运行，用户无感，磁盘状态复原）。
    /// 连回滚都失败时抛出并详记日志——此时原路径缺失、仅剩 *.old，由 FinalizeAfterUpdate
    /// 的 RestoreOld 分支在下次启动时救命。
    /// </summary>
    internal static void SwapExe(string exePath, string newExePath)
    {
        string oldPath = OldPathFor(exePath);
        try { File.Delete(oldPath); } catch { /* 无残留或被占用（上轮备份待清）则继续 */ }

        File.Move(exePath, oldPath);
        try
        {
            File.Move(newExePath, exePath);
        }
        catch (Exception ex)
        {
            Log($"新版本移入原路径失败（{ex.Message}），尝试回滚…");
            try
            {
                File.Move(oldPath, exePath);
                Log("回滚成功：当前版本继续运行，未做任何更改");
            }
            catch (Exception rex)
            {
                Log($"回滚也失败：{rex.Message}；原路径将缺失，下次启动从 {OldSuffix} 还原");
            }
            throw;
        }
    }

    /// <summary>启动新版本实例。必须走 ShellExecute（Verb=runas）：v2.0.6 起 exe 清单强制
    /// requireAdministrator，若本进程未提权（≤2.0.5.6 asInvoker 时代装的用户），CreateProcess
    /// 启动提权 exe 直接报 Win32 740（ERROR_ELEVATION_REQUIRED），交换已完成后新实例起不来；
    /// ShellExecute 则弹一次 UAC 征得同意。本进程已提权时无 UAC、无感继承。
    /// 用户拒绝 UAC 时抛 Win32Exception——交换已就位、.old 备份保留，下次手动启动新 exe
    /// 由 FinalizeAfterUpdate 收尾（四象限 DeleteOld）。</summary>
    internal static void LaunchNewVersion(string exePath)
    {
        using var p = Process.Start(new ProcessStartInfo(exePath, UpdatedArg)
        {
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    /// <summary>
    /// 新实例启动收尾（App 在取得单实例锁后调用）：按四象限处理 .old 备份。
    /// 删除采用 10 秒延迟——新版本若启动即崩（撑不过 10 秒），备份保留可供手动恢复。
    /// </summary>
    public static void FinalizeAfterUpdate()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            string oldPath = OldPathFor(exePath);
            bool exeExists = File.Exists(exePath);
            bool oldExists = File.Exists(oldPath);
            var action = DecideFinalizeAction(exeExists, oldExists);
            switch (action)
            {
                case StartupFinalizeAction.DeleteOld:
                    Log($"新版本 v{Updater.FormatVersion(Updater.CurrentVersion)} 启动成功，10 秒后清理旧版备份");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10_000);
                        try { File.Delete(oldPath); Log("旧版备份已清理，更新收尾完成"); }
                        catch (Exception ex) { Log($"旧版备份清理失败（下次启动再试）：{ex.Message}"); }
                    });
                    break;
                case StartupFinalizeAction.RestoreOld:
                    // 理论不可达（本进程正从该 exe 运行）；防御掉电后原路径缺失、残留备份的场景
                    File.Move(oldPath, exePath);
                    Log($"检测到上次更新中断，已从 {OldSuffix} 还原 exe");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"更新收尾异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理历史更新残留（应用启动时后台调用，失败静默）：①临时下载目录 >24h；
    /// ②exe 旁 .old 旧版备份 >24h（正常由新实例启动 10 秒后清理，这里兜底
    /// "新版本启动即崩、备份滞留" 与旧版 bat 方案的遗留）。
    /// </summary>
    public static void CleanupStaleArtifacts()
    {
        try
        {
            string root = Path.Combine(AppPaths.TempDir, "update");
            if (Directory.Exists(root))
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(dir) <= TimeSpan.FromDays(1)) continue;
                    try { Directory.Delete(dir, recursive: true); } catch { /* 占用中下轮再清 */ }
                }
            }
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                string oldPath = OldPathFor(exePath);
                if (File.Exists(oldPath) &&
                    DateTime.Now - File.GetLastWriteTimeUtc(oldPath) > TimeSpan.FromDays(1))
                {
                    try { File.Delete(oldPath); } catch { }
                }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }
}
