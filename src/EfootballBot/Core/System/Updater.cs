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

    /// <summary>本地版本号（来自 csproj Version 属性）。</summary>
    public static Version CurrentVersion { get; } =
        new Version(typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

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
                return UpdateResult.AlreadyLatest($"已是最新版本 v{CurrentVersion}");

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
                return UpdateResult.AlreadyLatest($"已是最新版本 v{CurrentVersion}");

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
            // 关键：GUI 退出后 Worker 子进程（同名 EfootballBot.exe）可能还活着并锁住 exe 文件，
            // 直接 copy/xcopy 会静默失败、旧程序原地复活（2026-10-14 用户实测踩坑）——
            // 所以必须先 taskkill 清掉所有同名残留进程再替换。
            string exePath = Environment.ProcessPath!;
            string exeName = Path.GetFileName(exePath);
            string exeDir = Path.GetDirectoryName(exePath)!;
            string batPath = Path.Combine(tempDir, "update.bat");
            string kill = $@"taskkill /F /IM ""{exeName}"" >nul 2>&1
timeout /t 1 /nobreak >nul";
            string bat;
            if (info.SingleExecutable)
            {
                // 裸 exe：清残留进程 → 直接覆盖本体 → 重启（免解压）
                bat = $@"@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
{kill}
copy /Y ""{downloadPath}"" ""{exePath}""
del /Q ""{exeDir}\EfootballBot.pdb"" >nul 2>&1
start """" ""{exePath}""
rmdir /S /Q ""{tempDir}""
";
            }
            else
            {
                // zip：清残留进程 → 解压 → 整目录覆盖 → 重启
                string extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(downloadPath, extractDir);
                try { File.Delete(downloadPath); } catch { }
                bat = $@"@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
{kill}
xcopy /E /I /Y /Q ""{extractDir}\*"" ""{exeDir}""
del /Q ""{exeDir}\EfootballBot.pdb"" >nul 2>&1
start """" ""{exePath}""
rmdir /S /Q ""{tempDir}""
";
            }
            File.WriteAllText(batPath, bat);

            // 启动脚本（独立进程，等当前进程退出后执行替换）
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\"\"",
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
