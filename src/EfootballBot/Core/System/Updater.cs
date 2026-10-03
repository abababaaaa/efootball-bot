using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace EfootballBot.Core.System;

/// <summary>GitHub Releases 自动更新。</summary>
public static class Updater
{
    /// <summary>固定仓库地址（owner/repo），无需用户手动填写。</summary>
    public const string DefaultRepository = "abababaaaa/efootball-bot";
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

    /// <summary>本地版本号（来自 csproj Version 属性）。</summary>
    public static Version CurrentVersion { get; } =
        new Version(typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    // ---- 缓存：避免频繁调用 GitHub API 触发 60次/小时 限流 ----
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static UpdateResult? _cachedResult;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static string? _cacheKey; // 区分不同 includePrerelease

    /// <summary>可选 GitHub Token（从环境变量 GITHUB_TOKEN 读取），用于提高 API 限流上限。</summary>
    private static string? GithubToken => Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    /// <summary>从 GitHub Releases API 检查最新版本（带 30 分钟缓存）。</summary>
    /// <param name="force">true 时跳过缓存（手动检查时用）。</param>
    public static async Task<UpdateResult> CheckAsync(string? repository = null, bool includePrerelease = false, bool force = false)
    {
        repository ??= DefaultRepository;
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
            return UpdateResult.Error("未配置 GitHub 仓库地址（格式：owner/repo）");

        string key = $"{repository}|{includePrerelease}";
        if (!force && _cachedResult is not null && _cacheKey == key && DateTime.Now - _cacheTime < CacheTtl)
            return _cachedResult;

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
            {
                // 限流：返回缓存（如果有）或友好提示
                if (_cachedResult is not null && _cacheKey == key) return _cachedResult;
                return UpdateResult.Error("GitHub API 限流，请稍后再试（匿名 60 次/小时）");
            }
            resp.EnsureSuccessStatusCode();

            var releases = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var latest = (releases ?? new())
                .Where(r => includePrerelease || !r.Prerelease)
                .OrderByDescending(r => r.Published_at)
                .FirstOrDefault();

            if (latest is null)
                return UpdateResult.Error("仓库没有 Release");

            if (!Version.TryParse(latest.Tag_Name.TrimStart('v'), out var remoteVer))
                return UpdateResult.Error($"无法解析版本号：{latest.Tag_Name}");

            if (remoteVer <= CurrentVersion)
                return Cache(UpdateResult.AlreadyLatest($"已是最新版本 v{CurrentVersion}"), key);

            var asset = latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有 .zip 资产");

            return Cache(UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name, asset.Browser_Download_Url, asset.Size, latest.Body), key);
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

    private static UpdateResult Cache(UpdateResult r, string key)
    {
        _cachedResult = r;
        _cacheKey = key;
        _cacheTime = DateTime.Now;
        return r;
    }

    /// <summary>下载并应用更新——下载 zip 到唯一临时目录，解压，启动 bat 脚本替换 exe 后重启。</summary>
    public static async Task<UpdateResult> DownloadAndApplyAsync(UpdateInfo info,
        IProgress<(long Bytes, long Total)>? progress = null)
    {
        try
        {
            // 每次用唯一临时目录（Guid），避免和上次残留文件冲突
            string tempDir = Path.Combine(Path.GetTempPath(), "EfootballBotUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "update.zip");

            // 下载
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = info.TotalBytes;
                long read = 0;
                var buffer = new byte[81920];
                int n;
                progress?.Report((0, total));
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    progress?.Report((read, total));
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

public record UpdateInfo(Version Version, string Name, string DownloadUrl, long TotalBytes, string? Body);

public class UpdateResult
{
    public bool IsError { get; init; }
    public bool IsAvailable { get; init; }
    public bool UpToDate { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = "";
    public UpdateInfo? Info { get; init; }

    public static UpdateResult AlreadyLatest(string msg) => new() { UpToDate = true, Message = msg };
    public static UpdateResult Available(Version v, string name, string url, long size, string? body)
        => new() { IsAvailable = true, Info = new(v, name, url, size, body), Message = $"发现新版本 v{v}" };
    public static UpdateResult Error(string msg) => new() { IsError = true, Message = msg };
    public static UpdateResult Success(string msg) => new() { Applied = true, Message = msg };
}
