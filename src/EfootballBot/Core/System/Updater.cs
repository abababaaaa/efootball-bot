using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.System;

/// <summary>
/// 自动更新。版本检查：R2 清单（国内直连，10 秒超时）→ GitHub Releases API（兜底，
/// api.github.com 与 github.com 下载域不同路，实测裸网可达）。下载不区分来源，统一走
/// 候选链：R2 直连源 → 加速镜像前缀 → GitHub 直连（UpdateApplier 逐个尝试，60 秒无数据
/// 即换下一个），下载完成后按 SHA256 校验（R2 清单或 Release 正文提供）——镜像被篡改或
/// 发错版本都会因哈希不匹配被弃用并切换下一通道，最后一档 GitHub 直连是信任锚点。
/// </summary>
public static class Updater
{
    /// <summary>GitHub 仓库地址（owner/repo），无需用户手动填写。</summary>
    public const string DefaultRepository = "abababaaaa/efootball-bot";

    /// <summary>R2 公开桶基地址（如 https://pub-xxxx.r2.dev ），CI 发版时同步 exe 与版本清单；
    /// 空 = 未配置，自动跳过该通道（建桶后填入即可生效）。internal set 供单测切换。</summary>
    public static string R2PublicBaseUrl { get; internal set; } = "";

    /// <summary>下载加速镜像前缀（第三方 GitHub 反代，拼在完整原始 URL 前）。2026-10-16 实测
    /// 存活：gh-proxy.com、ghproxy.net、ghproxy.cn（速度随时段波动）；ghfast.top / ghproxy.cc /
    /// gh.llkk.cc / github.moeyy.xyz 等一批已死，勿照抄旧清单。全部失败自动回退 GitHub 直连。</summary>
    public static readonly string[] DownloadMirrorPrefixes =
        { "https://gh-proxy.com/", "https://ghproxy.net/", "https://ghproxy.cn/" };

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

    /// <summary>R2 版本清单（CI 发版时写入桶内 latest.json / latest-pre.json）。</summary>
    private sealed record R2Manifest(string Version, long Size, string? Sha256);

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

    /// <summary>检查最新版本：R2 清单优先、GitHub API 兜底（带 30 分钟缓存）。</summary>
    /// <param name="force">true 时跳过缓存（手动检查时用）。</param>
    public static async Task<UpdateResult> CheckAsync(string? repository = null, bool includePrerelease = false, bool force = false)
    {
        repository ??= DefaultRepository;
        string key = $"{includePrerelease}";
        if (!force && _cachedResult is not null && _cacheKey == key && DateTime.Now - _cacheTime < CacheTtl)
            return _cachedResult;

        var r2 = await CheckR2Async(includePrerelease);
        if (r2 is not null)
            return Cache(r2, key);

        var github = await CheckGitHubAsync(repository, includePrerelease);
        if (!github.IsError)
            return Cache(github, key);

        return UpdateResult.Error("无法连接更新源（直连清单与 GitHub 均失败），请检查网络后重试");
    }

    /// <summary>国内直连源检查：读 R2 公开桶版本清单（匿名、无 API 限流）。任何失败或"没有
    /// 比本机更新的版本"都返回 null，静默交给 GitHub 检查兜底——清单只反映"上一次 CI 构建"，
    /// GitHub 才是完整事实源。</summary>
    private static async Task<UpdateResult?> CheckR2Async(bool includePrerelease)
    {
        if (string.IsNullOrWhiteSpace(R2PublicBaseUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            string manifestName = includePrerelease ? "latest-pre.json" : "latest.json";
            var m = await http.GetFromJsonAsync<R2Manifest>(
                $"{R2PublicBaseUrl.TrimEnd('/')}/{manifestName}", JsonOpts);
            if (m?.Version is null || !Version.TryParse(m.Version, out var remoteVer)) return null;
            if (remoteVer <= CurrentVersion) return null;

            return UpdateResult.Available(remoteVer, $"v{remoteVer}",
                BuildDownloadUrls(remoteVer), m.Size, null, singleExecutable: true,
                expectedSha256: string.IsNullOrWhiteSpace(m.Sha256) ? null : m.Sha256.ToLowerInvariant());
        }
        catch
        {
            return null; // R2 不可达/清单损坏：回退 GitHub，不打扰用户
        }
    }

    /// <summary>兜底源检查：GitHub Releases API。</summary>
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
                BuildDownloadUrls(remoteVer, single.Name), single.Size, latest.Body,
                single.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                expectedSha256: ParseExpectedSha256(latest.Body, single.Name));
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

    /// <summary>构建下载候选地址序列（同一份字节的多个来源，任一成功即可，非分卷拼接）：
    /// R2 直连源（已配置且为目标是 exe 时）→ 各加速镜像 → GitHub 直连（恒在最后兜底）。
    /// CI 用同一构建产物发布到所有来源，ExpectedSha256 对全部候选一体适用。</summary>
    internal static List<string> BuildDownloadUrls(Version v, string fileName = "EfootballBot.exe")
    {
        string gh = $"https://github.com/{DefaultRepository}/releases/download/v{v}/{fileName}";
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(R2PublicBaseUrl) &&
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            urls.Add($"{R2PublicBaseUrl.TrimEnd('/')}/{fileName}");
        foreach (var p in DownloadMirrorPrefixes)
            urls.Add(p + gh);
        urls.Add(gh);
        return urls;
    }

    /// <summary>从 Release 正文解析 CI 追加的「SHA256 文件名: 64位十六进制」（取首个匹配）。
    /// 正文无此行（旧版 Release）返回 null = 下载不做哈希校验，行为与历史版本一致。</summary>
    internal static string? ParseExpectedSha256(string? body, string assetName)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(assetName)) return null;
        var m = Regex.Match(body,
            $@"SHA256\s+{Regex.Escape(assetName)}\s*:\s*([0-9a-fA-F]{{64}})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static UpdateResult Cache(UpdateResult r, string key)
    {
        _cachedResult = r;
        _cacheKey = key;
        _cacheTime = DateTime.Now;
        return r;
    }
}

/// <param name="DownloadUrls">下载候选地址序列（同一文件的多个来源，按优先级排列，
/// UpdateApplier 逐个尝试直到成功；不是分卷拼接）。</param>
/// <param name="SingleExecutable">true=裸 exe（下载后直接替换本体）；false=zip（解压整目录覆盖）。</param>
/// <param name="ExpectedSha256">期望的 SHA256（R2 清单或 Release 正文提供）；null=不做校验
/// （仅旧版 Release 无哈希行时发生）。</param>
public record UpdateInfo(Version Version, string Name, IReadOnlyList<string> DownloadUrls, long TotalBytes,
    string? Body, bool SingleExecutable, string? ExpectedSha256 = null);

public class UpdateResult
{
    public bool IsError { get; init; }
    public bool IsAvailable { get; init; }
    public bool UpToDate { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = "";
    public UpdateInfo? Info { get; init; }

    public static UpdateResult AlreadyLatest(string msg) => new() { UpToDate = true, Message = msg };
    public static UpdateResult Available(Version v, string name, IReadOnlyList<string> urls, long size, string? body,
        bool singleExecutable, string? expectedSha256 = null)
        => new() { IsAvailable = true, Info = new(v, name, urls, size, body, singleExecutable, expectedSha256), Message = $"发现新版本 v{v}" };
    public static UpdateResult Error(string msg) => new() { IsError = true, Message = msg };
    public static UpdateResult Success(string msg) => new() { Applied = true, Message = msg };
}
