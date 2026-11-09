using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.System;

/// <summary>
/// 自动更新。版本检查：主源清单（对象存储/CDN 直连，10 秒超时）→ GitHub Releases API（兜底，
/// api.github.com 与 github.com 下载域不同路，实测裸网可达）。下载不区分来源，统一走
/// 候选链：主源直连 → 加速镜像前缀 → GitHub 直连（UpdateApplier 逐个尝试，60 秒无数据
/// 即换下一个），下载完成后按 SHA256 校验（主源清单或 Release 正文提供）——镜像被篡改或
/// 发错版本都会因哈希不匹配被弃用并切换下一通道。
/// <para>主源基址的优先级：环境变量 <see cref="PrimaryBaseUrlEnvVar"/> → config.json 的
/// Update.PrimaryBaseUrl → 编译期常量 <see cref="BuiltInPrimaryBaseUrl"/>。空 = 未启用，
/// 候选链自动跳过该通道（发布时只需在 CI 里配好桶，客户端不用改代码）。</para>
/// </summary>
public static class Updater
{
    /// <summary>GitHub 仓库地址（owner/repo），无需用户手动填写。</summary>
    public const string DefaultRepository = "abababaaaa/efootball-bot";

    /// <summary>
    /// 编译期内置的主源基址（Cloudflare R2 公开桶）。所有新版本开箱走主源；
    /// config.json 与 <see cref="Configure"/> 里的值优先级更高，换桶/换域名不必重新发版。
    /// 空 = 未内置，只有显式配置过主源的机器才启用该通道。
    /// </summary>
    public const string BuiltInPrimaryBaseUrl = "https://pub-5f1aff2ad4fd4fd4a12a51f3e73fa02a.r2.dev";

    /// <summary>环境变量：临时把主源指向新桶/另一台机器做验证，不必改 config.json。</summary>
    public const string PrimaryBaseUrlEnvVar = "EFOOTBALL_UPDATE_BASE";

    /// <summary>当前生效的主更新源基址（对象存储 / CDN 直链根，协议 + 域名 + 路径，末尾不带 /）。
    /// 空 = 未启用，候选链退回「加速镜像 → GitHub 直连」。启动时由 <see cref="Configure"/> 按
    /// 环境变量 → config.json → 内置常量 的顺序赋值。</summary>
    public static string PrimaryBaseUrl { get; private set; } = BuiltInPrimaryBaseUrl;

    /// <summary>旧名保留（模型包下载与既有调用方在用），等价于 <see cref="PrimaryBaseUrl"/>。
    /// internal set 供测试切换来源；赋值统一走 <see cref="NormalizeBaseUrl"/>，脏值落成空串。</summary>
    public static string R2PublicBaseUrl
    {
        get => PrimaryBaseUrl;
        internal set => PrimaryBaseUrl = NormalizeBaseUrl(value);
    }

    /// <summary>应用配置里的主源基址（启动时调一次；不调则用内置常量）。
    /// 优先级：环境变量 &gt; 传入值（config.json）&gt; 内置常量。切源后版本检查缓存作废。</summary>
    public static void Configure(string? configuredBaseUrl)
    {
        string? env = Environment.GetEnvironmentVariable(PrimaryBaseUrlEnvVar);
        string picked = !string.IsNullOrWhiteSpace(env) ? env!
            : !string.IsNullOrWhiteSpace(configuredBaseUrl) ? configuredBaseUrl!
            : BuiltInPrimaryBaseUrl;
        PrimaryBaseUrl = NormalizeBaseUrl(picked);
        _cachedResult = null;
        _cacheKey = null;
        _cacheTime = DateTime.MinValue;
    }

    /// <summary>基址归一化：去首尾空白、去末尾斜杠、只认 http/https 绝对地址。
    /// 手输/复制带的空格、结尾 /、或误填成 ftp:// 与裸域名都落成空串（= 不启用该通道），
    /// 而不是拼出 "https://host//latest.json" 这种照样 404 的地址。</summary>
    internal static string NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        if (string.IsNullOrEmpty(uri.Host)) return "";
        return trimmed;
    }

    /// <summary>清单里的 file 字段只允许「纯文件名」：带路径分隔符/查询串/上跳的脏值一律
    /// 回退到默认名，避免被远端清单把下载地址指到桶外或拼出越权路径。</summary>
    internal static string ResolveManifestFileName(string? file)
    {
        const string fallback = "EfootballBot.exe";
        if (string.IsNullOrWhiteSpace(file)) return fallback;
        string name = file.Trim();
        if (name.Length > 128) return fallback;
        if (name.Contains('/') || name.Contains('\\') || name.Contains('?') || name.Contains('#') ||
            name.Contains(':') || name.Contains("..")) return fallback;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fallback;
        return name;
    }

    /// <summary>设置页展示用的一句话通道描述（真实生效值，不是配置意向）。</summary>
    public static string DescribeChannel()
    {
        if (string.IsNullOrEmpty(PrimaryBaseUrl))
            return "加速镜像 → GitHub 直连（未配置主源）";
        string host = Uri.TryCreate(PrimaryBaseUrl, UriKind.Absolute, out var uri) ? uri.Host : PrimaryBaseUrl;
        return $"主源 {host} → 加速镜像 → GitHub 直连";
    }

    /// <summary>下载加速镜像前缀（第三方 GitHub 反代，拼在完整原始 URL 前）。镜像清单与「GitHub 访问加速」
    /// 页共用 <see cref="FastGithubKernel.MirrorPrefixes"/>：2026-11-09 实测 ghfast.top / ghproxy.net /
    /// gh-proxy.com 可用（速度随时段波动），用户在下载加速卡里选的默认镜像排在最前。全部失败回退直连。</summary>
    public static IReadOnlyList<string> DownloadMirrorPrefixes => FastGithubKernel.MirrorPrefixes;

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

    /// <summary>主源版本清单（CI 发版时写入桶内 latest.json / latest-pre.json）。
    /// File = 桶内二进制文件名（带版本号，正式版与测试版互不覆盖；老清单无此字段时
    /// 回退到固定的 EfootballBot.exe）。</summary>
    private sealed record R2Manifest(string Version, long Size, string? Sha256, string? File);

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

    /// <summary>检查最新版本：主源清单优先、GitHub API 兜底（带 30 分钟缓存）。</summary>
    /// <param name="force">true 时跳过缓存（手动检查时用）。</param>
    public static async Task<UpdateResult> CheckAsync(string? repository = null, bool includePrerelease = false, bool force = false)
    {
        repository ??= DefaultRepository;
        string key = $"{includePrerelease}";
        if (!force && _cachedResult is not null && _cacheKey == key && DateTime.Now - _cacheTime < CacheTtl)
            return _cachedResult;

        // 主源有结论（有新版本 / 已是最新）就以它为准，不再问 GitHub——否则「已是最新」的用户
        // 在 GitHub 连不上的网络里照样会看到「无法连接更新源」，等于主源白配。主源未配置 /
        // 不可达 / 清单损坏时才回退 GitHub，两条路都失败才报错。
        var primary = await CheckPrimaryAsync(includePrerelease);
        if (primary is not null)
            return Cache(primary, key);

        var github = await CheckGitHubAsync(repository, includePrerelease);
        if (!github.IsError)
            return Cache(github, key);

        return UpdateResult.Error("无法连接更新源（主源清单与 GitHub 均失败），请检查网络后重试");
    }

    /// <summary>主源检查：读桶内版本清单（匿名、无 API 限流、不用翻墙）。清单能读到就给出
    /// 结论（有新版本 / 已是最新）；只有「未配置、不可达、清单损坏」才返回 null 交 GitHub 兜底。
    /// 已是最新也返回结论，是为了让配了主源的机器彻底不依赖 GitHub——发版时 CI 会同时更新
    /// 两份清单，上传失败会让 release job 直接失败，不会静默停留在旧版本。</summary>
    private static async Task<UpdateResult?> CheckPrimaryAsync(bool includePrerelease)
    {
        if (string.IsNullOrEmpty(PrimaryBaseUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
            string manifestName = includePrerelease ? "latest-pre.json" : "latest.json";
            var m = await http.GetFromJsonAsync<R2Manifest>(
                $"{PrimaryBaseUrl}/{manifestName}", JsonOpts);
            if (m?.Version is null || !Version.TryParse(m.Version, out var remoteVer)) return null;
            if (remoteVer <= CurrentVersion)
                return UpdateResult.AlreadyLatest($"已是最新版本 v{FormatVersion(CurrentVersion)}");

            // 清单自带文件名（带版本号）时按它下；老清单没有该字段则用固定名。远端清单是
            // 不可信输入，文件名先过一遍净化，拼不出桶外路径。
            return UpdateResult.Available(remoteVer, $"v{remoteVer}",
                BuildDownloadUrls(remoteVer, ResolveManifestFileName(m.File)), m.Size, null, singleExecutable: true,
                expectedSha256: string.IsNullOrWhiteSpace(m.Sha256) ? null : m.Sha256.ToLowerInvariant());
        }
        catch
        {
            return null; // 主源不可达/清单损坏：回退 GitHub，不打扰用户
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
    /// 主源直连（已配置且目标是 exe 时）→ 各加速镜像 → GitHub 直连（恒在最后兜底）。
    /// CI 用同一构建产物发布到所有来源，ExpectedSha256 对全部候选一体适用。</summary>
    internal static List<string> BuildDownloadUrls(Version v, string fileName = "EfootballBot.exe")
    {
        string gh = $"https://github.com/{DefaultRepository}/releases/download/v{v}/{fileName}";
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(PrimaryBaseUrl) &&
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            urls.Add($"{PrimaryBaseUrl}/{fileName}");
        // 镜像链与内核下载同一条：默认镜像 → 其余预设 → 直连兜底（WithMirrors 内部去重）
        urls.AddRange(FastGithubKernel.WithMirrors(gh));
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
