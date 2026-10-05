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

    /// <summary>下载并静默安装最新版 HidHide。失败时打开官方 Release 页（浏览器手动路径）。</summary>
    public static async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        IProgress<string>? progress = null)
    {
        string? msiPath = null;
        try
        {
            // 1. 查最新 Release（有 GITHUB_TOKEN 则认证请求）
            progress?.Report("正在查询 Nefarius/HidHide 最新版本…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            if (!string.IsNullOrWhiteSpace(GithubToken))
                http.DefaultRequestHeaders.Authorization = new
                    global::System.Net.Http.Headers.AuthenticationHeaderValue("token", GithubToken);

            using var resp = await http.GetAsync(ReleasesApi);
            if ((int)resp.StatusCode is 403 or 429)
                return (false, "GitHub API 限流，请稍后再试或配置 GITHUB_TOKEN 环境变量");
            resp.EnsureSuccessStatusCode();

            var releases = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var asset = (releases ?? new())
                .Where(r => !r.Prerelease)
                .SelectMany(r => r.Assets)
                .FirstOrDefault(a => IsHidHideSetupAsset(a.Name));
            if (asset is null)
                return (false, "官方 Release 未找到 x64 安装器资产，请手动下载安装");

            // 2. 下载到唯一临时路径（来源 URL 固定为官方 Release，best-effort 校验：非空 + 合理大小）
            progress?.Report($"下载 {asset.Name}…");
            string tempPath = Path.Combine(Path.GetTempPath(), $"HidHideSetup_{Guid.NewGuid():N}.exe");
            using (var dl = await http.GetAsync(asset.Browser_Download_Url, HttpCompletionOption.ResponseHeadersRead))
            {
                dl.EnsureSuccessStatusCode();
                await using var src = await dl.Content.ReadAsStreamAsync();
                await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(file);
            }
            msiPath = tempPath;
            if (new FileInfo(tempPath).Length < 1024 * 1024)
                throw new InvalidDataException("下载的安装器异常（小于 1MB），已中止");

            // 3. 静默安装（Burn bundle manifest 要求管理员 → 自动触发 UAC）
            progress?.Report("启动安装器，请在 UAC 窗口确认…");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/quiet /norestart",
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("安装器启动失败");
            await proc.WaitForExitAsync();

            // 4. 轮询驱动服务就绪（≤15s；安装器退出后驱动注册可能滞后）
            progress?.Report("等待驱动服务就绪…");
            for (int i = 0; i < 15; i++)
            {
                if (HidHideManager.IsDriverInstalled())
                    return (true, "HidHide 驱动安装成功");
                await Task.Delay(1000);
            }
            return (false, "安装器已执行但未检测到驱动服务，请重启应用或重启电脑后重试");
        }
        catch (Exception ex)
        {
            // 失败回退：打开官方 Release 页手动安装
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Nefarius/HidHide/releases/latest",
                    UseShellExecute = true,
                });
            }
            catch { }
            return (false, $"自动安装失败（{ex.Message}）。已打开官方下载页，请手动安装后重试。");
        }
        finally
        {
            if (msiPath is not null) { try { File.Delete(msiPath); } catch { } }
        }
    }
}
