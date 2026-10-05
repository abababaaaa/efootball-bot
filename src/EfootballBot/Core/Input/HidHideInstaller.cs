using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.Input;

/// <summary>
/// HidHide 驱动安装器：GitHub 官方 Release 下载 Burn bundle exe → 静默安装 → 服务就绪轮询。
/// 复用自动更新模块的 GitHub 客户端模式（UA + GITHUB_TOKEN + 限流提示）。
/// </summary>
public static partial class HidHideInstaller
{
    private const string ReleasesApi = "https://api.github.com/repos/Nefarius/HidHide/releases";

    /// <summary>HidHide 安装器资产名：HidHide_&lt;版本&gt;_x64.exe（排除 CLI / x86 / Source code）。</summary>
    [GeneratedRegex(@"^HidHide_\d[\w.]*_x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupAssetRegex();

    public static bool IsHidHideSetupAsset(string assetName)
        => SetupAssetRegex().IsMatch(assetName);

    private sealed record GhAsset(string Name, long Size, string Browser_Download_Url);

    private sealed record GhRelease(bool Prerelease, List<GhAsset> Assets);

    private static string? GithubToken => Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    /// <summary>查询最新正式版 x64 安装器下载 URL。失败返回 null（out 带错误文案）。</summary>
    public static async Task<(string? Url, string Error)> QueryLatestSetupUrlAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
        if (!string.IsNullOrWhiteSpace(GithubToken))
            http.DefaultRequestHeaders.Authorization = new
                global::System.Net.Http.Headers.AuthenticationHeaderValue("token", GithubToken);

        using var resp = await http.GetAsync(ReleasesApi);
        if ((int)resp.StatusCode is 403 or 429)
            return (null, "GitHub API 限流，请稍后再试或配置 GITHUB_TOKEN 环境变量");
        resp.EnsureSuccessStatusCode();

        var releases = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var asset = (releases ?? new())
            .Where(r => !r.Prerelease)
            .SelectMany(r => r.Assets)
            .FirstOrDefault(a => IsHidHideSetupAsset(a.Name));
        return asset is null
            ? (null, "官方 Release 未找到 x64 安装器资产，请手动下载安装")
            : (asset.Browser_Download_Url, "");
    }

    /// <summary>
    /// 下载安装器并静默安装。调用方应已具备管理员权限（--hidhide-config 提权子进程）。
    /// 不开浏览器、不弹 UAC（高权限下 Burn bundle /quiet 直接执行）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallElevatedAsync(IProgress<string>? progress = null)
    {
        string? setupPath = null;
        try
        {
            progress?.Report("正在查询 Nefarius/HidHide 最新版本…");
            var (url, err) = await QueryLatestSetupUrlAsync();
            if (url is null) return (false, err);

            progress?.Report("下载 HidHide 安装器…");
            setupPath = Path.Combine(Path.GetTempPath(), $"HidHideSetup_{Guid.NewGuid():N}.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
                using var dl = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                dl.EnsureSuccessStatusCode();
                await using var src = await dl.Content.ReadAsStreamAsync();
                await using var file = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(file);
            }
            if (new FileInfo(setupPath).Length < 1024 * 1024)
                throw new InvalidDataException("下载的安装器异常（小于 1MB），已中止");

            progress?.Report("静默安装 HidHide…");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/quiet /norestart",
                UseShellExecute = false, // 已是高权限进程，直接创建不再弹 UAC
            }) ?? throw new InvalidOperationException("安装器启动失败");
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return (false, $"安装器退出码 {proc.ExitCode}");

            // 轮询驱动服务就绪（≤20s；安装器退出后驱动注册可能滞后）
            for (int i = 0; i < 20; i++)
            {
                if (HidHideManager.IsDriverInstalled())
                    return (true, "HidHide 驱动安装成功");
                await Task.Delay(1000);
            }
            return (false, "安装器已执行但未检测到驱动服务，请重启电脑后重试");
        }
        catch (Exception ex)
        {
            return (false, $"自动安装失败：{ex.Message}");
        }
        finally
        {
            if (setupPath is not null) { try { File.Delete(setupPath); } catch { } }
        }
    }
}
