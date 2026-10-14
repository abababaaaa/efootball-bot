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

            var picked = PickZipAssets(latest.Assets);
            if (picked is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有 .zip 资产");

            var (urls, total) = picked.Value;
            return UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name, urls, total, latest.Body);
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

            var single = latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (single is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有 .zip 资产");

            return UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name,
                new List<string> { single.Browser_Download_Url }, single.Size, latest.Body);
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
    /// 从资产列表选发布包：优先单个 .zip；否则取 .zip.001/.zip.002… 分卷（按序号排序）。
    /// 返回（下载 URL 序列, 总字节数）；没有可用资产返回 null。
    /// </summary>
    private static (List<string> Urls, long Total)? PickZipAssets(List<GiteeAsset>? assets)
    {
        var list = assets ?? new();
        var single = list.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (single is not null)
            return (new List<string> { single.Browser_Download_Url }, single.Size);

        var parts = list
            .Select(a => (Asset: a, Match: Regex.Match(a.Name, @"\.zip\.(\d+)$", RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .ToList();
        if (parts.Count == 0)
            return null;
        return (parts.Select(x => x.Asset.Browser_Download_Url).ToList(), parts.Sum(x => x.Asset.Size));
    }

    private static UpdateResult Cache(UpdateResult r, string key)
    {
        _cachedResult = r;
        _cacheKey = key;
        _cacheTime = DateTime.Now;
        return r;
    }

    /// <summary>下载并应用更新——下载（多源地址时按序追加为同一文件）到唯一临时目录，解压，启动 bat 脚本替换 exe 后重启。</summary>
    public static async Task<UpdateResult> DownloadAndApplyAsync(UpdateInfo info,
        IProgress<(long Bytes, long Total)>? progress = null)
    {
        try
        {
            // 每次用唯一临时目录（Guid），避免和上次残留文件冲突。
            // 临时下载放 exe 旁 data\tmp\（2026-10-11 用户要求：所有操作收在程序目录内）
            string tempDir = Path.Combine(AppPaths.TempDir, "update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "update.zip");

            // 下载（Gitee 分卷或 GitHub 单文件统一按 URL 序列顺序追加）
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            long total = info.TotalBytes;
            long read = 0;
            progress?.Report((0, total));
            using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
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

            // 解压
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            // 解压后删除 zip，释放文件句柄
            try { File.Delete(zipPath); } catch { }

            // 生成批处理脚本（比 PowerShell 更兼容，无执行策略问题）
            // 脚本逻辑：等 2 秒（让当前进程退出）→ 复制新文件 → 启动新 exe → 清理临时目录
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string batPath = Path.Combine(tempDir, "update.bat");
            string bat = $@"@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
xcopy /E /I /Y /Q ""{extractDir}\*"" ""{exeDir}""
start """" ""{Environment.ProcessPath}""
rmdir /S /Q ""{tempDir}""
";
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
public record UpdateInfo(Version Version, string Name, IReadOnlyList<string> DownloadUrls, long TotalBytes, string? Body);

public class UpdateResult
{
    public bool IsError { get; init; }
    public bool IsAvailable { get; init; }
    public bool UpToDate { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = "";
    public UpdateInfo? Info { get; init; }

    public static UpdateResult AlreadyLatest(string msg) => new() { UpToDate = true, Message = msg };
    public static UpdateResult Available(Version v, string name, IReadOnlyList<string> urls, long size, string? body)
        => new() { IsAvailable = true, Info = new(v, name, urls, size, body), Message = $"发现新版本 v{v}" };
    public static UpdateResult Error(string msg) => new() { IsError = true, Message = msg };
    public static UpdateResult Success(string msg) => new() { Applied = true, Message = msg };
}
