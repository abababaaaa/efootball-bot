using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.System;

/// <summary>
/// 自动更新，双通道：Gitee（国内，优先）→ GitHub（兜底）。
/// 两源对用户是同一逻辑目标：先查 Gitee，任何失败（超时/限流/解析失败/没有 Release）
/// 都自动回退 GitHub；两源都失败才报错。下载 URL 直接来自检查结果（Gitee 附件走国内 CDN），
/// 下载与替换流程对源无感知。
/// Gitee 免费版单附件 ≤ 100MB，而发布 zip 约 160MB——CI 侧按 95MB 分卷
/// （文件名 .zip.001/.zip.002…），本类检查时把分卷按序拼成下载列表，
/// 下载时顺序追加到同一个文件再解压；GitHub 侧仍是单文件。
/// </summary>
public static class Updater
{
    /// <summary>GitHub 仓库地址（owner/repo），无需用户手动填写。</summary>
    public const string DefaultRepository = "abababaaaa/efootball-bot";
    /// <summary>Gitee 国内镜像仓库地址（owner/repo）。CI 发版时自动同步；不可用时客户端自动回退 GitHub。</summary>
    public const string DefaultGiteeRepository = "abababaaaa/efootball-bot";

    private const string GiteeApiBase = "https://gitee.com/api/v5";

    /// <summary>GitHub Release API 的简化模型。</summary>
    private record GhRelease(
        string Tag_Name,
        string? Name,
        bool Prerelease,
        DateTime Published_at,
        string? Body,
        List<GhAsset> Assets
    );

    private record GhAsset(
        string Name,
        long Size,
        string Browser_Download_Url
    );

    /// <summary>Gitee 发行版模型（字段 snake_case，Created_At 需自行解析）。</summary>
    private record GiteeRelease(
        string Tag_Name,
        string? Name,
        bool Prerelease,
        string? Body,
        string Created_At,
        List<GiteeAsset> Assets
    );

    private record GiteeAsset(
        string Name,
        long Size,
        string Browser_Download_Url
    );

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>本地版本号（来自 csproj Version 属性，保留全部字段——测试版 4 段如 2.0.5.4）。
    /// 旧写法 ToString(3) 把测试版截成 2.0.5：与远端 2.0.5.3 比较永远更小 → 更新提示永不消失、
    /// 更新完还提示更新；关于页/横幅/日志显示的版本号也停在旧的 v2.0.5
    /// （用户 2026-10-15 "本地还是老版本" 实测）。</summary>
    public static Version CurrentVersion { get; } =
        typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>版本号展示：正式版（程序集 revision 恒补 0）或未定义 revision（-1）渲染三段
    /// v2.0.6；测试版第 4 段非 0，渲染四段 v2.0.5.4。直接 ToString() 会把正式版显示成 2.0.6.0，
    /// 而 ToString(4) 对 3 段版本不会省略 -1 字段。</summary>
    public static string FormatVersion(Version? v)
    {
        if (v is null) return "0.0.0";
        return v.Revision == 0 || v.Revision == -1 ? v.ToString(3) : v.ToString(4);
    }

    // ---- 缓存：避免频繁调用 API 触发限流（两源统一缓存，用户感知的是同一个"最新版本"） ----
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static UpdateResult? _cachedResult;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static string? _cacheKey; // 区分不同 includePrerelease

    /// <summary>可选 GitHub Token（从环境变量 GITHUB_TOKEN 读取），用于提高 API 限流上限。</summary>
    private static string? GithubToken => Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    /// <summary>检查最新版本：Gitee 优先、GitHub 兜底（带 30 分钟缓存）。</summary>
    /// <param name="force">true 时跳过缓存（手动检查时用）。</param>
    public static async Task<UpdateResult> CheckAsync(string? repository = null, bool includePrerelease = false, bool force = false)
    {
        repository ??= DefaultRepository;
        string key = $"{includePrerelease}";
        if (!force && _cachedResult is not null && _cacheKey == key && DateTime.Now - _cacheTime < CacheTtl)
            return _cachedResult;

        var gitee = await CheckGiteeAsync(includePrerelease);
        if (!gitee.IsError)
            return Cache(gitee, key);

        var github = await CheckGitHubAsync(repository, includePrerelease);
        if (!github.IsError)
            return Cache(github, key);

        return UpdateResult.Error("Gitee 与 GitHub 均无法连接，请检查网络后重试");
    }

    /// <summary>国内源检查：Gitee Releases API（匿名可访问，15 秒超时）。</summary>
    private static async Task<UpdateResult> CheckGiteeAsync(bool includePrerelease)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            using var resp = await http.GetAsync($"{GiteeApiBase}/repos/{DefaultGiteeRepository}/releases?per_page=20&direction=desc");
            resp.EnsureSuccessStatusCode();

            var releases = await resp.Content.ReadFromJsonAsync<List<GiteeRelease>>(JsonOpts);
            var latest = (releases ?? new())
                .Where(r => includePrerelease || !r.Prerelease)
                .OrderByDescending(r => ParseDate(r.Created_At))
                .FirstOrDefault();

            if (latest is null)
                return UpdateResult.Error("Gitee 仓库没有 Release");
            if (!Version.TryParse(latest.Tag_Name.TrimStart('v'), out var remoteVer))
                return UpdateResult.Error($"无法解析版本号：{latest.Tag_Name}");
            if (remoteVer <= CurrentVersion)
                return UpdateResult.AlreadyLatest($"已是最新版本 v{FormatVersion(CurrentVersion)}");

            var picked = PickAssets(latest.Assets);
            if (picked is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有可用的 exe/zip 资产");

            var (urls, total, singleExe) = picked.Value;
            return UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name, urls, total, latest.Body, singleExe);
        }
        catch (Exception ex)
        {
            return UpdateResult.Error($"Gitee 检查失败：{ex.Message}");
        }
    }

    /// <summary>兜底源检查：GitHub Releases API（现有逻辑）。</summary>
    private static async Task<UpdateResult> CheckGitHubAsync(string repository, bool includePrerelease)
    {
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
            return UpdateResult.Error("未配置 GitHub 仓库地址（格式：owner/repo）");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"https://api.github.com/repos/{repository}/releases";
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            if (!string.IsNullOrWhiteSpace(GithubToken))
                http.DefaultRequestHeaders.Authorization =
                    new global::System.Net.Http.Headers.AuthenticationHeaderValue("token", GithubToken);

            using var resp = await http.GetAsync(url);
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
                return UpdateResult.Error("GitHub API 限流，请稍后再试（匿名 60 次/小时）");
            resp.EnsureSuccessStatusCode();

            var releases = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(JsonOpts);
            var latest = (releases ?? new())
                .Where(r => includePrerelease || !r.Prerelease)
                .OrderByDescending(r => r.Published_at)
                .FirstOrDefault();

            if (latest is null)
                return UpdateResult.Error("仓库没有 Release");
            if (!Version.TryParse(latest.Tag_Name.TrimStart('v'), out var remoteVer))
                return UpdateResult.Error($"无法解析版本号：{latest.Tag_Name}");
            if (remoteVer <= CurrentVersion)
                return UpdateResult.AlreadyLatest($"已是最新版本 v{FormatVersion(CurrentVersion)}");

            // 新更新器优先裸 exe；老 Release 只有 zip 时兜底
            var single = latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (single is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有 exe/zip 资产");

            return UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name,
                new List<string> { single.Browser_Download_Url }, single.Size, latest.Body,
                single.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException ex)
        {
            return UpdateResult.Error($"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return UpdateResult.Error($"检查失败：{ex.Message}");
        }
    }

    private static DateTime ParseDate(string? s)
        => DateTime.TryParse(s, out var d) ? d : DateTime.MinValue;

    /// <summary>
    /// 从资产列表选发布包：优先单个 .exe/.zip；否则取 .exe.001/.zip.001… 分卷（按序号排序）。
    /// 返回（下载 URL 序列, 总字节数, 是否裸 exe）；没有可用资产返回 null。
    /// </summary>
    private static (List<string> Urls, long Total, bool SingleExecutable)? PickAssets(List<GiteeAsset>? assets)
    {
        var list = assets ?? new();
        var single = list.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (single is not null)
            return (new List<string> { single.Browser_Download_Url }, single.Size,
                single.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        var parts = list
            .Select(a => (Asset: a, Match: Regex.Match(a.Name, @"\.(exe|zip)\.(\d+)$", RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[2].Value))
            .ToList();
        if (parts.Count == 0)
            return null;
        var isExe = parts[0].Asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || parts[0].Asset.Name.Contains(".exe.", StringComparison.OrdinalIgnoreCase);
        return (parts.Select(x => x.Asset.Browser_Download_Url).ToList(),
            parts.Sum(x => x.Asset.Size), isExe);
    }

    private static UpdateResult Cache(UpdateResult r, string key)
    {
        _cachedResult = r;
        _cacheKey = key;
        _cacheTime = DateTime.Now;
        return r;
    }

    /// <summary>下载并应用更新——下载（多源地址时按序追加为同一文件）到唯一临时目录；
    /// 裸 exe 直接替换本程序后重启，zip 解压后整目录覆盖。</summary>
    public static async Task<UpdateResult> DownloadAndApplyAsync(UpdateInfo info,
        IProgress<(long Bytes, long Total)>? progress = null)
    {
        try
        {
            // 每次用唯一临时目录（Guid），避免和上次残留文件冲突。
            // 临时下载放 exe 旁 data\tmp\（2026-10-11 用户要求：所有操作收在程序目录内）
            string tempDir = Path.Combine(AppPaths.TempDir, "update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string downloadPath = Path.Combine(tempDir,
                info.SingleExecutable ? Path.GetFileName(Environment.ProcessPath!) : "update.zip");

            // 下载（Gitee 分卷或 GitHub 单文件统一按 URL 序列顺序追加）
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            long total = info.TotalBytes;
            long read = 0;
            progress?.Report((0, total));
            using (var file = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                foreach (var url in info.DownloadUrls)
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n));
                        read += n;
                        progress?.Report((read, total));
                    }
                }
            } // using 结束自动释放文件句柄

            // 生成批处理脚本（比 PowerShell 更兼容，无执行策略问题）。
            // 关键设计（v2.0.5.2 重构，修复"更新后旧版复活"）：
            // ① bat 文件本体纯 ASCII——所有含中文的路径改由命令行参数传入（%1~%6），
            //   cmd 以 Unicode 处理参数、不经过文件字节解析。旧版把中文路径内嵌在 bat 里
            //   且先 chcp 65001：cmd 中途切代码页后按新编码重读文件，非 ASCII 字节行为
            //   未定义，copy 等行可能整段被跳过——更新静默失败、旧版复活且无日志可查；
            // ② 替换重试最多 10 次 + 裸 exe 大小校验——GUI/Worker 退出与杀软扫描新下载的
            //   172MB exe 都会短暂锁住目标文件（v2.0.4 只等 1 秒、失败不检查照常 start 旧版）；
            // ③ 全程写 data\update.log——远程排障可直接要这份文件；
            // ④ 不再在 bat 里删临时目录（运行中自删会让 cmd 后续行不执行），
            //   残留由 CleanupStaleTemp 在应用启动时清理。
            string exePath = Environment.ProcessPath!;
            string exeName = Path.GetFileName(exePath);
            string exeDir = Path.GetDirectoryName(exePath)!;
            string logPath = Path.Combine(AppPaths.DataDir, "update.log");

            // zip 分支：先在托管侧解压（bat 只做纯 ASCII 的树拷贝），替换源改为解压目录
            string replaceSource = downloadPath;
            if (!info.SingleExecutable)
            {
                string extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(downloadPath, extractDir);
                try { File.Delete(downloadPath); } catch { }
                replaceSource = extractDir;
            }

            // 参数语义： %1=替换源(新exe/解压目录) %2=替换目标(exe路径/安装目录)
            //            %3=exe目录(pdb清理)  %4=日志路径  %5=进程名(taskkill)  %6=启动目标(exe路径)
            string bat = @"@echo off
setlocal
echo [%date% %time%] === update begin ===>>""%~4""
taskkill /F /IM ""%~5"" >>""%~4"" 2>&1
ping -n 2 127.0.0.1 >nul
taskkill /F /IM ""%~5"" >>""%~4"" 2>&1
set /a tries=0
:retry
set /a tries+=1
";
            bat += info.SingleExecutable
                ? @"copy /Y ""%~1"" ""%~2"" >>""%~4"" 2>&1
"
                : @"xcopy /E /I /Y /Q ""%~1\*"" ""%~2\"" >>""%~4"" 2>&1
";
            bat += @"if not errorlevel 1 goto verify
echo [%date% %time%] replace failed, try %tries% >>""%~4""
if %tries% geq 10 goto fail
ping -n 3 127.0.0.1 >nul
goto retry
:verify
";
            if (info.SingleExecutable)
            {
                // 大小校验：copy 报成功但目标残缺（磁盘满/杀软截断）也重试
                bat += @"for %%A in (""%~1"") do set SRC=%%~zA
for %%A in (""%~2"") do set DST=%%~zA
if ""%SRC%""==""%DST%"" goto ok
echo [%date% %time%] size mismatch src=%SRC% dst=%DST%, try %tries% >>""%~4""
if %tries% geq 10 goto fail
ping -n 2 127.0.0.1 >nul
goto retry
";
            }
            bat += @":ok
echo [%date% %time%] replace ok, relaunch >>""%~4""
del /Q ""%~3\EfootballBot.pdb"" >nul 2>&1
start """" ""%~6""
echo [%date% %time%] === update done ===>>""%~4""
exit /b 0
:fail
echo [%date% %time%] === update FAILED after retries, relaunch old exe ===>>""%~4""
start """" ""%~6""
exit /b 1
";
            string batPath = Path.Combine(tempDir, "update.bat");
            File.WriteAllText(batPath, bat);

            // 启动脚本（独立进程，等当前进程退出后执行替换）
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\"\" \"{replaceSource}\" \"{(info.SingleExecutable ? exePath : exeDir)}\" \"{exeDir}\" \"{logPath}\" \"{exeName}\" \"{exePath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return UpdateResult.Success($"v{info.Version} 下载完成，正在应用…");
        }
        catch (Exception ex)
        {
            return UpdateResult.Error($"更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理历史更新残留的临时目录（v2.0.5.2 起更新 bat 不再自删临时目录——运行中自删会让
    /// cmd 读不到后续行；残留目录含下载的旧 exe，攒着占盘）。保留 24 小时内的目录：
    /// 更新刚完成后新进程若需对照排查还要用。应用启动时后台调用，失败静默。
    /// </summary>
    public static void CleanupStaleTemp()
    {
        try
        {
            string root = Path.Combine(AppPaths.TempDir, "update");
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (DateTime.Now - Directory.GetLastWriteTime(dir) <= TimeSpan.FromDays(1)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* 占用中下轮再清 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }
}

/// <param name="DownloadUrls">下载地址序列：GitHub 单文件为 1 个；Gitee 大包分卷时按 .001/.002… 顺序多个。</param>
/// <param name="SingleExecutable">true=裸 exe（下载后直接替换本体）；false=zip（解压整目录覆盖）。</param>
public record UpdateInfo(Version Version, string Name, IReadOnlyList<string> DownloadUrls, long TotalBytes, string? Body, bool SingleExecutable);

public class UpdateResult
{
    public bool IsError { get; init; }
    public bool IsAvailable { get; init; }
    public bool UpToDate { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = "";
    public UpdateInfo? Info { get; init; }

    public static UpdateResult AlreadyLatest(string msg) => new() { UpToDate = true, Message = msg };
    public static UpdateResult Available(Version v, string name, IReadOnlyList<string> urls, long size, string? body, bool singleExecutable)
        => new() { IsAvailable = true, Info = new(v, name, urls, size, body, singleExecutable), Message = $"发现新版本 v{v}" };
    public static UpdateResult Error(string msg) => new() { IsError = true, Message = msg };
    public static UpdateResult Success(string msg) => new() { Applied = true, Message = msg };
}
