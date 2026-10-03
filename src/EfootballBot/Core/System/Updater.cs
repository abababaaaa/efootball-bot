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

    /// <summary>从 GitHub Releases API 检查最新版本。</summary>
    public static async Task<UpdateResult> CheckAsync(string? repository = null, bool includePrerelease = false)
    {
        repository ??= DefaultRepository;
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
            return UpdateResult.Error("未配置 GitHub 仓库地址（格式：owner/repo）");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // 默认排除 pre-release
            var url = $"https://api.github.com/repos/{repository}/releases";
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");

            var releases = await http.GetFromJsonAsync<List<GhRelease>>(url,
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
                return UpdateResult.AlreadyLatest($"已是最新版本 v{CurrentVersion}");

            var asset = latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return UpdateResult.Error($"Release v{remoteVer} 没有 .zip 资产");

            return UpdateResult.Available(remoteVer, latest.Name ?? latest.Tag_Name, asset.Browser_Download_Url, asset.Size);
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

    /// <summary>下载并应用更新——下载 zip 到临时目录，解压到 exe 同级，然后启动 updater 脚本替换 exe。</summary>
    public static async Task<UpdateResult> DownloadAndApplyAsync(UpdateInfo info,
        IProgress<(long Bytes, long Total)>? progress = null)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "EfootballBotUpdate");
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "update.zip");

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var file = File.Create(zipPath);

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

            // 解压
            string extractDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // 生成替换脚本（exe 运行时无法覆盖自身）
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string scriptPath = Path.Combine(tempDir, "update.ps1");
            string script = $@"
$ErrorActionPreference = 'Stop'
Start-Sleep -Seconds 2
Copy-Item -Path '{extractDir}\*' -Destination '{exeDir}' -Recurse -Force
Remove-Item -Path '{tempDir}' -Recurse -Force
Start-Process '{Environment.ProcessPath}'
";
            File.WriteAllText(scriptPath, script);

            // 启动脚本（在独立进程里，等当前进程退出后替换）
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
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

public record UpdateInfo(Version Version, string Name, string DownloadUrl, long TotalBytes);

public class UpdateResult
{
    public bool IsError { get; init; }
    public bool IsAvailable { get; init; }
    public bool UpToDate { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = "";
    public UpdateInfo? Info { get; init; }

    public static UpdateResult AlreadyLatest(string msg) => new() { UpToDate = true, Message = msg };
    public static UpdateResult Available(Version v, string name, string url, long size)
        => new() { IsAvailable = true, Info = new(v, name, url, size), Message = $"发现新版本 v{v}" };
    public static UpdateResult Error(string msg) => new() { IsError = true, Message = msg };
    public static UpdateResult Success(string msg) => new() { Applied = true, Message = msg };
}
