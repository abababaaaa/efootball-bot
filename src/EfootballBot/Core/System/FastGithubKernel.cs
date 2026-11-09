using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace EfootballBot.Core.System;

/// <summary>
/// FastGithub 内核（开源项目 Yoolk233/FastGithub）：hosts 择优直连 + 镜像下载加速。
/// <para>与上一代 GitPort 内核的取舍：FastGithub 不装证书、不起本地代理、不接管系统代理，
/// 只把 21 个 GitHub 域名指向实测最快的边缘节点（hosts 固定标记段，可一键还原）；
/// 下载加速走镜像前缀（ghfast.top 等），与本程序更新通道共用同一份镜像链。</para>
/// <para>本类只做四件事：按需下载单文件内核、调 <c>--apply/--restore/--selftest</c> 子命令、
/// 解析 hosts 加速段给界面回显、加速段被其它软件清掉时按缓存自动补回。</para>
/// </summary>
public static class FastGithubKernel
{
    /// <summary>上游仓库（owner/repo）。</summary>
    public const string Repo = "Yoolk233/FastGithub";

    /// <summary>Release tag（上游用固定 tag "fast" 发版，不跟随语义化标签）。</summary>
    public const string ReleaseTag = "fast";

    /// <summary>内核版本（下载地址与说明文本共用；升级时改这里 + AssetName）。</summary>
    public const string Version = "1.2.0";

    /// <summary>Windows 单文件发布包文件名（38 MB 压缩 → 解包后 44 MB 单文件 exe）。</summary>
    public const string AssetName = "FastGithub1.2.0_Windows.zip";

    /// <summary>解包后 exe 文件名。</summary>
    public const string ExeName = "FastGithub.exe";

    /// <summary>压缩包内层目录。</summary>
    private const string InnerDir = "FastGithub_Light";

    /// <summary>hosts 加速段起止标记（必须与内核一致，否则内核认不出本程序写的块）。</summary>
    public const string MarkBegin = "# ==== FastGithub BEGIN (auto generated) ====";
    public const string MarkEnd = "# ==== FastGithub END ====";

    /// <summary>内核把「其它软件（Steam++ / Watt Toolkit 等）占用的行」注释成这个前缀，还原时去掉。</summary>
    private const string OverridePrefix = "#FG# ";

    /// <summary>上一代 hosts 优化器写的静态 IP 段（6.3 及更早），apply 前先清掉，避免两套方案打架。</summary>
    private const string LegacyMarkBegin = "# ==== GitHub optimize (auto generated) ====";
    private const string LegacyMarkEnd = "# ==== GitHub optimize end ====";

    /// <summary>
    /// 下载加速镜像预设（顺序即优先级）。2026-11-09 实测：ghfast.top 是 FastGithub 的默认源且可用；
    /// ghproxy.net / gh-proxy.com 存活；mirror.ghproxy.com 仅作兜底。全部失败自动回退 GitHub 直连。
    /// </summary>
    public static readonly string[] MirrorPrefixes =
    {
        "https://ghfast.top/",
        "https://ghproxy.net/",
        "https://gh-proxy.com/",
        "https://mirror.ghproxy.com/",
    };

    /// <summary>镜像测速样本：上游自己的 39 KB 小 Release 资产，够快、够真、不会把测速变成下载大包。</summary>
    private const string MirrorSampleUrl =
        "https://github.com/Yoolk233/FastGithub/releases/download/fast/FastGithub-1.2.0-.zip";

    // ---------------- 路径 ----------------

    /// <summary>内核目录：exe 旁 data\fastgithub\（随程序文件夹整体迁移）。</summary>
    public static string Dir => Path.Combine(AppPaths.DataDir, "fastgithub");

    /// <summary>内核 exe 路径。</summary>
    public static string ExePath => Path.Combine(Dir, ExeName);

    /// <summary>系统 hosts（需要管理员写权限）。</summary>
    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>内核自己的配置目录 %APPDATA%\FastGithub（镜像选择 mirror.txt、节点缓存 nodes.txt）。</summary>
    private static string KernelDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastGithub");

    private static string MirrorFile => Path.Combine(KernelDataDir, "mirror.txt");
    private static string CacheFile => Path.Combine(KernelDataDir, "nodes.txt");
    private static string CliReportFile => Path.Combine(Path.GetTempPath(), "fastgithub_cli.txt");

    /// <summary>内核下载包地址（未套镜像的原始地址）。</summary>
    public static string ReleaseAssetUrl =>
        $"https://github.com/{Repo}/releases/download/{ReleaseTag}/{AssetName}";

    // ---------------- 状态 ----------------

    /// <summary>内核是否已下载到本地。</summary>
    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>本地内核版本（读 PE 版本信息，未安装返回空串）。</summary>
    public static string InstalledVersion
    {
        get
        {
            try
            {
                return IsInstalled ? FileVersionInfo.GetVersionInfo(ExePath).ProductVersion ?? Version : "";
            }
            catch { return ""; }
        }
    }

    /// <summary>hosts 里当前生效的加速映射（域名 → IP）；空字典 = 未加速。</summary>
    public static IReadOnlyDictionary<string, string> AppliedDomains => ParseBlock(ReadHosts());

    /// <summary>加速是否生效（以 hosts 实读为准，不看进程）。</summary>
    public static bool IsApplied => AppliedDomains.Count > 0;

    /// <summary>内核自愈/还原时的提示（界面订阅后用 AppendLog 落日志）。</summary>
    public static event Action<string>? Notice;

    // ---------------- 下载与安装 ----------------

    /// <summary>
    /// 下载并安装内核（镜像链 → GitHub 直连）。progress 收到形如「已下载 12.3 / 38.0 MB」的文本。
    /// </summary>
    public static async Task DownloadAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        string tmpZip = Path.Combine(Path.GetTempPath(), "fastgithub-" + Guid.NewGuid().ToString("N") + ".zip");
        Exception? last = null;
        try
        {
            foreach (var candidate in WithMirrors(ReleaseAssetUrl))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report($"正在下载内核（{HostOf(candidate)}）…");
                    await DownloadFileAsync(candidate, tmpZip, progress, ct).ConfigureAwait(false);
                    ValidateZip(tmpZip);
                    progress?.Report("正在解包内核…");
                    ExtractExe(tmpZip);
                    progress?.Report($"内核已就绪（FastGithub {Version}）");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    progress?.Report($"该通道失败（{HostOf(candidate)}）：{ex.Message}");
                }
                finally { TryDelete(tmpZip); }
            }
            throw new InvalidOperationException($"所有下载通道均失败：{last?.Message}");
        }
        finally { TryDelete(tmpZip); }
    }

    private static async Task DownloadFileAsync(string url, string targetPath, IProgress<string>? progress,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 38_886_278;

        long done = 0;
        // ★句柄必须先关再返回：Windows 上拿着未释放的写句柄，后面 File.Move/解包会直接 IOException
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long reported = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                // 每 256 KB 报一次：38 MB 的包不会把界面刷新成刷屏（Progress 每次都要切回 UI 线程）
                if (done - reported < 262144 && done < total) continue;
                reported = done;
                progress?.Report($"已下载 {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");
            }
        }
    }

    /// <summary>校验：够大 + 是 ZIP（防镜像返回 HTML 错误页被当成安装包）。</summary>
    private static void ValidateZip(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 5_000_000) throw new IOException($"安装包不完整（{info.Length} 字节）");
        using var fs = File.OpenRead(path);
        var head = new byte[4];
        if (fs.Read(head, 0, 4) < 4 || head[0] != 0x50 || head[1] != 0x4B)
            throw new IOException("下载内容不是 ZIP（镜像返回了错误页）");
    }

    /// <summary>解包并把 FastGithub.exe 落到 data\fastgithub\（先写 .new 再原子替换，失败不留半个 exe）。</summary>
    private static void ExtractExe(string zipPath)
    {
        string work = Path.Combine(Path.GetTempPath(), "fastgithub-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, work, overwriteFiles: true);
            string exe = Path.Combine(work, InnerDir, ExeName);
            if (!File.Exists(exe))
                exe = Directory.EnumerateFiles(work, ExeName, SearchOption.AllDirectories).FirstOrDefault() ?? "";
            if (exe.Length == 0) throw new IOException("安装包里找不到 FastGithub.exe");

            string staged = ExePath + ".new";
            File.Copy(exe, staged, true);
            File.Move(staged, ExePath, true);

            string doc = Path.Combine(work, InnerDir, "FastGithub 使用说明.txt");
            if (File.Exists(doc))
            {
                try { File.Copy(doc, Path.Combine(Dir, "FastGithub 使用说明.txt"), true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ---------------- 加速 / 还原 / 测速 ----------------

    /// <summary>一键加速结果：是否成功、接管域名数、逐域名探测明细（界面直接回填延迟表）。</summary>
    public sealed record ApplyResult(bool Ok, int Domains, IReadOnlyList<FastGithubNode> Nodes, string Message)
    {
        public static ApplyResult Fail(string message) => new(false, 0, Array.Empty<FastGithubNode>(), message);
    }

    /// <summary>单个域名的探测结果（与内核内部 Node 对齐：GOOD/WEAK 都算可用）。</summary>
    public sealed record FastGithubNode(string Host, string Ip, long Ms, string State, int Code)
    {
        public bool Usable => State is "GOOD" or "WEAK";

        public string StateText => State switch
        {
            "GOOD" => "可用",
            "WEAK" => "受限",
            "BAD" => "不可用",
            "FAIL" => "失败",
            _ => "未测",
        };
    }

    /// <summary>
    /// 一键加速：清掉旧版静态 IP 段 → 内核测速 21 个域名并写 hosts → 解析本次结果。
    /// 需要管理员权限（本程序清单已 requireAdministrator，子进程直接继承）。
    /// </summary>
    public static async Task<ApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return ApplyResult.Fail("FastGithub 内核未下载");
        if (!GitHostsOptimizer.IsAdministrator())
            return ApplyResult.Fail("需要管理员权限：hosts 只能由提权进程改写，请以管理员身份运行本程序");

        StripLegacyOptimizeBlock();
        TryDelete(CliReportFile);

        var (exit, tail) = await RunCliAsync("--apply", TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);
        var nodes = ParseNodeLines(SafeRead(CliReportFile));
        var map = ParseBlock(ReadHosts());
        if (map.Count == 0)
        {
            string why = exit == -2
                ? tail
                : exit == -1
                    ? "内核测速超时未返回"
                    : "hosts 未写入（请确认以管理员身份运行，且安全软件未拦截 hosts 修改）";
            return new ApplyResult(false, 0, nodes, string.IsNullOrWhiteSpace(tail) ? why : why + "；" + tail);
        }
        // 内核的「WEAK(403/404) 也算可用」会把只回 404 的黑洞节点写进 hosts——实测表现为
        // 浏览器打开 github.com 变成 GitHub Pages 的 404 页。所以写完之后必须用真实 HTTP 再验收一遍。
        var repair = await VerifyAndRepairAsync(ct).ConfigureAwait(false);
        int applied = ParseBlock(ReadHosts()).Count;
        StartWatchdog();

        var summary = new StringBuilder($"已加速 {applied} 个域名（hosts 已指向实测最快节点）；{repair.Message}");
        foreach (var line in repair.Repairs) summary.Append("；").Append(line);
        foreach (var line in repair.Removed) summary.Append("；").Append(line);
        return new ApplyResult(true, applied, nodes, summary.ToString());
    }

    /// <summary>停止并还原：内核 --restore 优先，失败则按标记手动删块 + 还原被注释的第三方行。</summary>
    public static async Task<(bool Ok, string Message)> RestoreAsync(CancellationToken ct = default)
    {
        StopWatchdog();
        if (!HasBlock(ReadHosts()))
            return (true, "当前没有生效的加速段，hosts 无需还原");

        if (IsInstalled)
        {
            await RunCliAsync("--restore", TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            if (!HasBlock(ReadHosts())) return (true, "hosts 已还原，GitHub 走默认解析");
        }

        bool ok = RestoreByMarkers();
        return (ok, ok ? "内核未完成还原，已按标记手动还原 hosts" : "还原失败：hosts 被占用或权限不足");
    }

    /// <summary>内核自检结果：逐域名明细 + 一句人话结论（失败时说明原因，界面直接显示）。</summary>
    public sealed record ProbeOutcome(IReadOnlyList<FastGithubNode> Nodes, string Message);

    /// <summary>内核自检（--selftest：只测速不写 hosts，内核自己也会拒绝在非提权下跑）。</summary>
    public static async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return new ProbeOutcome(Array.Empty<FastGithubNode>(), "FastGithub 内核未下载");
        Directory.CreateDirectory(AppPaths.TempDir);
        string outFile = Path.Combine(AppPaths.TempDir, "fastgithub-selftest.txt");
        TryDelete(outFile);
        var (exit, tail) = await RunCliAsync($"--selftest --out=\"{outFile}\"", TimeSpan.FromMinutes(3), ct)
            .ConfigureAwait(false);
        var nodes = ParseNodeLines(SafeRead(outFile));
        if (nodes.Count > 0)
            return new ProbeOutcome(nodes, $"内核测速完成：{nodes.Count} 个域名，可用 {nodes.Count(n => n.Usable)} 个");
        string message = exit switch
        {
            -2 => tail,
            -1 => "内核测速超时未返回",
            _ => tail.Length > 0 ? tail : "内核未返回测速结果",
        };
        return new ProbeOutcome(nodes, message);
    }
    /// <summary>用系统默认浏览器以外的默认方式打开内核面板（它自己的 Avalonia 界面：可视化测速/镜像开关）。</summary>
    public static void OpenPanel()
    {
        if (!IsInstalled) return;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = Dir, UseShellExecute = true });
        }
        catch { }
    }

    // ---------------- 下载加速（镜像链） ----------------

    /// <summary>当前默认镜像前缀（读写内核自己的 %APPDATA%\FastGithub\mirror.txt，面板与本程序共用一份配置）。</summary>
    public static string DefaultMirror
    {
        get => NormalizeMirror(SafeRead(MirrorFile));
        set
        {
            try
            {
                Directory.CreateDirectory(KernelDataDir);
                File.WriteAllText(MirrorFile, NormalizeMirror(value), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>镜像前缀归一：空/非 http(s) 落回预设首选，补末尾 /。</summary>
    internal static string NormalizeMirror(string? prefix)
    {
        string text = (prefix ?? "").Trim();
        if (text.Length == 0) return MirrorPrefixes[0];
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return MirrorPrefixes[0];
        return text.EndsWith('/') ? text : text + "/";
    }

    /// <summary>把原始 GitHub 链接套上镜像前缀（先剥掉已知旧前缀，避免二次套娃）。</summary>
    public static string AccelerateUrl(string url, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return NormalizeMirror(prefix ?? DefaultMirror) + StripUrl(url);
    }

    /// <summary>剥掉已知镜像前缀，还原成原始链接。</summary>
    public static string StripUrl(string url)
    {
        string text = (url ?? "").Trim();
        foreach (var prefix in MirrorPrefixes)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..];
        return text;
    }

    /// <summary>
    /// 一个原始地址的完整候选链：默认镜像 → 其余预设镜像 → 直连（恒在最后兜底）。
    /// 更新下载与内核下载共用，任何一环失效都不影响装得上。
    /// </summary>
    public static IReadOnlyList<string> WithMirrors(string url)
    {
        var list = new List<string>(MirrorPrefixes.Length + 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate)
        {
            if (seen.Add(candidate)) list.Add(candidate);
        }
        Add(DefaultMirror + url);
        foreach (var prefix in MirrorPrefixes) Add(prefix + url);
        Add(url);
        return list;
    }

    /// <summary>镜像可用性测速结果（Ms &lt; 0 = 连不上；StatusCode 为 HTTP 状态码）。</summary>
    public sealed record MirrorProbe(string Prefix, int Ms, int StatusCode)
    {
        public bool Ok => Ms >= 0 && StatusCode is >= 200 and < 400;
    }

    /// <summary>实测某个镜像前缀是否可用：拉样本资产的首个字节块，取到即计时。</summary>
    public static async Task<MirrorProbe> ProbeMirrorAsync(string prefix, CancellationToken ct = default)
    {
        string url = NormalizeMirror(prefix) + MirrorSampleUrl;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            return new MirrorProbe(NormalizeMirror(prefix), (int)sw.ElapsedMilliseconds, (int)resp.StatusCode);
        }
        catch
        {
            return new MirrorProbe(NormalizeMirror(prefix), -1, 0);
        }
    }

    // ---------------- 加速后的「真实可用性」验收 ----------------

    /// <summary>
    /// 必须验收的关键域名与探测路径。FastGithub 自己的判定只要「TLS 握手成功且状态码 200-399」
    /// 就算 GOOD，403/404 也算 WEAK 可用 —— 实测存在对任何路径都回 404 的边缘节点（甚至是黑洞），
    /// 被当可用节点写进 hosts 后，浏览器打开 github.com 就是那个 GitHub Pages 404 页。
    /// 这里对映射结果再发一次真实 HTTP 请求做验收。
    /// </summary>
    private static readonly (string Host, string Path)[] CriticalHosts =
    {
        ("github.com", "/"),
        ("api.github.com", "/"),
        ("codeload.github.com", "/"),
        ("raw.githubusercontent.com", "/dotnet/runtime/main/README.md"),
    };

    /// <summary>验收结果：某域名指向某 IP 时的真实 HTTP 状态与耗时。</summary>
    public sealed record HostCheck(string Host, string Ip, int StatusCode, int Ms)
    {
        /// <summary>只有 2xx/3xx 算可用；404（Pages 兜底页/黑节点）与连接失败都不算。</summary>
        public bool Ok => StatusCode is >= 200 and < 400;
    }

    /// <summary>验收 + 纠偏的结果（Repairs/Removed 会拼进加速日志给用户看）。</summary>
    public sealed record RepairResult(int Checked, int Ok, IReadOnlyList<string> Repairs,
        IReadOnlyList<string> Removed, string Message);

    /// <summary>验收失败时的重挑候选（顺序按实测延迟从好到差）。</summary>
    private static readonly Dictionary<string, string[]> RepairPool = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github.com"] = new[]
        {
            "20.27.177.113", "20.205.243.166", "140.82.112.3", "140.82.113.3",
            "140.82.114.3", "140.82.116.3", "4.237.22.38", "20.26.156.215", "20.200.245.247",
        },
        ["api.github.com"] = new[] { "20.205.243.168", "140.82.112.6", "140.82.113.6", "140.82.114.6", "140.82.116.6" },
        ["codeload.github.com"] = new[] { "20.205.243.165", "140.82.112.9", "140.82.113.9", "140.82.114.9" },
        ["raw.githubusercontent.com"] = new[] { "185.199.108.133", "185.199.109.133", "185.199.110.133", "185.199.111.133" },
    };

    /// <summary>对「域名 + 指定 IP」发一次真实 HTTPS GET（强制走该 IP，SNI 与 Host 仍是域名）。</summary>
    public static async Task<HostCheck> CheckAsync(string host, string ip, string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(6),
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(IPAddress.Parse(ip), context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            using var resp = await http.GetAsync($"https://{host}{path}", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return new HostCheck(host, ip, (int)resp.StatusCode, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return new HostCheck(host, ip, 0, -1);
        }
    }

    /// <summary>
    /// 验收当前 hosts 加速段：关键域名真实打不开的，从候选池重挑一个能打开的 IP 换上去；
    /// 候选全不行就把该域名从加速段摘掉（回落系统 DNS，总比指着 404 黑节点强）。
    /// 只有真的发生替换/删除才写 hosts，其余情况一个字节都不动。
    /// </summary>
    public static async Task<RepairResult> VerifyAndRepairAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(ParseBlock(ReadHosts()), StringComparer.OrdinalIgnoreCase);
        if (map.Count == 0)
            return new RepairResult(0, 0, Array.Empty<string>(), Array.Empty<string>(), "当前没有生效的加速段");

        var repairs = new List<string>();
        var removed = new List<string>();
        int checkedCount = 0, ok = 0;

        foreach (var (host, path) in CriticalHosts)
        {
            if (!map.TryGetValue(host, out var current)) continue;
            checkedCount++;

            var first = await CheckAsync(host, current, path, ct).ConfigureAwait(false);
            if (first.Ok)
            {
                ok++;
                continue;
            }

            string? replacement = null;
            int bestMs = int.MaxValue;
            if (RepairPool.TryGetValue(host, out var pool))
            {
                foreach (var candidate in pool)
                {
                    if (string.Equals(candidate, current, StringComparison.Ordinal)) continue;
                    var check = await CheckAsync(host, candidate, path, ct).ConfigureAwait(false);
                    if (!check.Ok) continue;
                    if (check.Ms < bestMs)
                    {
                        bestMs = check.Ms;
                        replacement = candidate;
                    }
                    if (check.Ms < 1200) break; // 够快就不再多试，省用户等待
                }
            }

            if (replacement is not null)
            {
                map[host] = replacement;
                repairs.Add($"{host}: {current}（{Describe(first)}）→ {replacement}（{bestMs} ms）");
                ok++;
            }
            else
            {
                map.Remove(host);
                removed.Add($"{host}: {current}（{Describe(first)}）已移除，改用系统解析");
            }
        }

        if (repairs.Count > 0 || removed.Count > 0)
        {
            string merged = StripBlock(ReadHosts()).TrimEnd('\r', '\n') + "\r\n\r\n" + BuildBlock(map);
            if (WriteHosts(merged))
            {
                SaveCache(map);
                FlushDns();
            }
        }

        string message = checkedCount == 0
            ? "无需验收"
            : $"可用性验收 {ok}/{checkedCount}" +
              (repairs.Count > 0 ? $"，替换 {repairs.Count} 个坏节点" : "") +
              (removed.Count > 0 ? $"，移除 {removed.Count} 个无解域名" : "");
        return new RepairResult(checkedCount, ok, repairs, removed, message);
    }

    private static string Describe(HostCheck check) => check.StatusCode == 0 ? "连接失败" : $"HTTP {check.StatusCode}";

    /// <summary>把加速段写回内核自己的节点缓存（自愈看门狗按它补段）。</summary>
    private static void SaveCache(IReadOnlyDictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(KernelDataDir);
            var sb = new StringBuilder();
            foreach (var kv in map) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append("\r\n");
            File.WriteAllText(CacheFile, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------------- hosts 读写（与内核同一套标记） ----------------

    internal static string ReadHosts()
    {
        try { return File.ReadAllText(HostsPath); } catch { return ""; }
    }

    internal static bool HasBlock(string text) => text.Contains(MarkBegin, StringComparison.Ordinal);

    /// <summary>删掉加速段，其余内容原样保留。</summary>
    internal static string StripBlock(string text)
    {
        var sb = new StringBuilder();
        bool inside = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed == MarkBegin) { inside = true; continue; }
            if (trimmed == MarkEnd) { inside = false; continue; }
            if (!inside) sb.Append(line).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>还原被内核注释掉的第三方行（去掉 "#FG# " 前缀）。</summary>
    internal static string RestoreOverrides(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append(line.StartsWith(OverridePrefix, StringComparison.Ordinal)
                ? line[OverridePrefix.Length..]
                : line).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>按「域名 → IP」生成加速段（标记与内核一致）。</summary>
    internal static string BuildBlock(IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder();
        sb.Append(MarkBegin).Append("\r\n");
        sb.Append("# 由 FastGithub 生成：删除本块或点「停止还原」即可恢复\r\n");
        foreach (var kv in map) sb.Append(kv.Value).Append('\t').Append(kv.Key).Append("\r\n");
        sb.Append(MarkEnd).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>解析加速段（域名 → IP）。</summary>
    internal static Dictionary<string, string> ParseBlock(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line == MarkBegin) { inside = true; continue; }
            if (line == MarkEnd) { inside = false; continue; }
            if (!inside || line.Length == 0 || line.StartsWith('#')) continue;
            var parts = Regex.Split(line, @"\s+");
            if (parts.Length >= 2 && IsIPv4(parts[0])) map[parts[1]] = parts[0];
        }
        return map;
    }

    private static bool IsIPv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
            if (p.Length is 0 or > 3 || !byte.TryParse(p, out _)) return false;
        return true;
    }

    private static bool WriteHosts(string text)
    {
        try
        {
            string backup = HostsPath + ".fastgithub.bak";
            if (!File.Exists(backup))
            {
                try { File.Copy(HostsPath, backup, false); } catch { }
            }
            File.WriteAllText(HostsPath, text, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>不依赖内核的还原：删块 + 还原被注释行 + 刷 DNS。</summary>
    private static bool RestoreByMarkers()
    {
        string text = ReadHosts();
        if (text.Length == 0) return false;
        string restored = RestoreOverrides(StripBlock(text)).TrimEnd('\r', '\n') + "\r\n";
        if (!WriteHosts(restored)) return false;
        FlushDns();
        return true;
    }

    /// <summary>清掉 6.3 及更早版本 GitHostsOptimizer 写的静态 IP 段（两套方案不能同时写 github.com）。</summary>
    private static void StripLegacyOptimizeBlock()
    {
        string text = ReadHosts();
        if (text.Length == 0 || !text.Contains(LegacyMarkBegin, StringComparison.Ordinal)) return;
        var sb = new StringBuilder();
        bool inside = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed == LegacyMarkBegin) { inside = true; continue; }
            if (trimmed == LegacyMarkEnd) { inside = false; continue; }
            if (!inside) sb.Append(line).Append("\r\n");
        }
        if (WriteHosts(sb.ToString())) FlushDns();
    }

    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    // ---------------- 节点缓存 + 自愈 ----------------

    private static Dictionary<string, string> LoadCache()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in SafeRead(CacheFile).Replace("\r\n", "\n").Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && IsIPv4(parts[1].Trim())) map[parts[0].Trim()] = parts[1].Trim();
            }
        }
        catch { }
        return map;
    }

    /// <summary>加速段被其它软件（Steam++ / 安全软件 / 系统更新）清掉后，按缓存自动补回。</summary>
    public static bool ReapplyFromCache(out int count)
    {
        count = 0;
        var cache = LoadCache();
        if (cache.Count == 0) return false;
        string merged = StripBlock(ReadHosts()).TrimEnd('\r', '\n') + "\r\n\r\n" + BuildBlock(cache);
        if (!WriteHosts(merged)) return false;
        FlushDns();
        count = cache.Count;
        return true;
    }

    private static Timer? _watchdog;

    /// <summary>每 60 秒检查一次 hosts：加速段丢了就按缓存补回（与内核面板的自愈行为一致）。</summary>
    public static void StartWatchdog()
    {
        _watchdog ??= new Timer(_ => TickWatchdog(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public static void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private static void TickWatchdog()
    {
        try
        {
            if (HasBlock(ReadHosts())) return;
            if (!ReapplyFromCache(out int count) || count == 0) return;
            Notice?.Invoke($"hosts 加速段被其它软件清除，已按缓存自动补回 {count} 个域名");
        }
        catch { }
    }

    // ---------------- 子进程 ----------------

    /// <summary>跑内核命令行子命令；返回 (退出码, 输出尾部)。超时按 -1 返回。</summary>
    private static async Task<(int ExitCode, string Tail)> RunCliAsync(string arguments, TimeSpan timeout,
        CancellationToken ct)
    {
        if (!IsInstalled) return (-1, "内核未下载");
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            WorkingDirectory = Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return (-1, "内核进程启动失败");
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // 内核 exe 自带 requireAdministrator 清单：非提权进程直接起不来（CreateProcess 返回 740）
            return (-2, "内核需要管理员权限：请以管理员身份运行本程序");
        }
        catch (Exception ex) { return (-1, "内核进程启动失败：" + ex.Message); }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return (-1, "内核进程超时未返回");
        }
        string tail = ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
        if (tail.Length > 400) tail = tail[^400..];
        return (process.ExitCode, tail);
    }

    // ---------------- 解析内核报告 ----------------

    private static readonly Regex NodeLinePattern = new(
        @"^\s*(?<host>[A-Za-z0-9._-]+)\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<ms>\d+)\s*ms\s+(?<state>[A-Z]+)\s+code=(?<code>\d+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>解析 --apply / --selftest 报告里的「域名 IP 延迟 状态 code」行。</summary>
    internal static IReadOnlyList<FastGithubNode> ParseNodeLines(string text)
    {
        var nodes = new List<FastGithubNode>();
        if (string.IsNullOrWhiteSpace(text)) return nodes;
        foreach (Match m in NodeLinePattern.Matches(text))
        {
            if (!IsIPv4(m.Groups["ip"].Value)) continue;
            nodes.Add(new FastGithubNode(
                m.Groups["host"].Value,
                m.Groups["ip"].Value,
                long.TryParse(m.Groups["ms"].Value, out var ms) ? ms : 0,
                m.Groups["state"].Value,
                int.TryParse(m.Groups["code"].Value, out var code) ? code : 0));
        }
        return nodes;
    }

    // ---------------- 小工具 ----------------

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return "?"; }
    }
}
