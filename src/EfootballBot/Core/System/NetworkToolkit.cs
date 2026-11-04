using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot;

namespace EfootballBot.Core.System;

/// <summary>网络工具页里的一个探测目标（域名目标每次测量前实时解析，IP 目标直测）。</summary>
public sealed class ProbeTarget
{
    public int Id { get; set; }
    /// <summary>显示名（域名目标的域名 / 手工添加的备注）。</summary>
    public string Label { get; set; } = "";
    /// <summary>域名（非空 = 每次测量前 DNS 解析展开成 IP；空 = 直接测 Ip）。</summary>
    public string? Domain { get; set; }
    /// <summary>最近一次解析/填写的 IP。</summary>
    public string Ip { get; set; } = "";
    public string Provider { get; set; } = "";
    /// <summary>是否纳入实时监控（每秒 tcping）。</summary>
    public bool Monitor { get; set; }
}

/// <summary>
/// 服务器延迟测试 + 实时监控引擎（参考 xjbt 监控工具的 tcping 思路，纯 C# Socket 实现，
/// 不依赖 npcap）：目标表持久化在 data\network\servers.json；测量全部=并行 TCP/443 建连测毫秒；
/// 实时监控=对勾选目标每秒 tcping，记录丢包率与最近样本环形缓冲，可写 CSV。
/// </summary>
public sealed class NetworkProbe
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(AppPaths.DataDir, "network", "servers.json");
    private static string CsvDir => Path.Combine(AppPaths.DataDir, "network");

    /// <summary>内置域名种子：eFootball 官方域 + GitHub 直连验证域（解析失败自动跳过，可手工加 IP）。</summary>
    public static readonly string[] SeedDomains =
        { "konami.net", "efootball.konami.net", "github.com", "api.github.com", "raw.githubusercontent.com" };

    private List<ProbeTarget> _targets = new();
    private int _nextId = 1;

    public IReadOnlyList<ProbeTarget> Targets => _targets;

    public NetworkProbe()
    {
        Load();
        if (_targets.Count == 0) Seed();
    }

    private void Seed()
    {
        foreach (var d in SeedDomains)
            _targets.Add(new ProbeTarget
            {
                Id = _nextId++,
                Label = d,
                Domain = d,
                Ip = "",
                Provider = GuessProvider(d),
                Monitor = false,
            });
        Save();
    }

    private static string GuessProvider(string s)
    {
        if (s.Contains("github")) return "GitHub";
        if (s.Contains("konami")) return "Konami";
        return "-";
    }

    // ---------------- 目标表 ----------------

    public ProbeTarget Add(string input, bool monitor = false)
    {
        input = input.Trim();
        bool isDomain = Uri.CheckHostName(input) == UriHostNameType.Dns && !input.All(char.IsDigit) && input.Contains('.');
        var t = new ProbeTarget
        {
            Id = _nextId++,
            Label = input,
            Domain = isDomain ? input : null,
            Ip = isDomain ? "" : input,
            Provider = isDomain ? GuessProvider(input) : "-",
            Monitor = monitor,
        };
        _targets.Add(t);
        Save();
        return t;
    }

    public bool Remove(int id)
    {
        var n = _targets.RemoveAll(t => t.Id == id);
        if (n > 0) Save();
        return n > 0;
    }

    public void SetMonitor(int id, bool on)
    {
        var t = _targets.FirstOrDefault(x => x.Id == id);
        if (t is null) return;
        t.Monitor = on;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var doc = JsonSerializer.Deserialize<Store>(File.ReadAllBytes(StorePath).AsSpan(), JsonOpts);
            if (doc?.Targets is null) return;
            _targets = doc.Targets;
            _nextId = _targets.Count == 0 ? 1 : _targets.Max(t => t.Id) + 1;
        }
        catch { _targets = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllBytes(StorePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Store(_targets, _nextId), JsonOpts)));
        }
        catch { }
    }

    private sealed record Store(List<ProbeTarget> Targets, int NextId);

    // ---------------- TCP 测速 ----------------

    /// <summary>TCP 建连延迟（毫秒）；失败/超时返回 -1。</summary>
    public static int TcpLatency(string ip, int port, int timeoutMs = 1200)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect(ip, port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                c.EndConnect(ar);
                sw.Stop();
                return (int)sw.ElapsedMilliseconds;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>域名实时解析（只取 IPv4，最多 6 个）；失败返回空。</summary>
    public static List<string> ResolveDomain(string domain)
    {
        var list = new List<string>();
        try
        {
            foreach (var a in Dns.GetHostAddresses(domain))
                if (a.AddressFamily == AddressFamily.InterNetwork && list.Count < 6)
                    list.Add(a.ToString());
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 测量全部目标（并行，DNS 域名目标先解析展开，逐 IP 测，目标取最优值并回填 IP）。
    /// onResult 在工作线程回调；返回 false 表示被取消。
    /// </summary>
    public async Task<bool> MeasureAllAsync(CancellationToken ct, Action<ProbeTarget, int> onResult)
    {
        var snapshot = _targets.ToList();
        using var gate = new SemaphoreSlim(16);
        var tasks = snapshot.Select(async t =>
        {
            if (ct.IsCancellationRequested) return;
            await gate.WaitAsync(ct);
            try
            {
                int best = -1;
                string bestIp = t.Ip;
                var ips = t.Domain is not null ? ResolveDomain(t.Domain) : new List<string> { t.Ip };
                if (ips.Count == 0 && t.Domain is not null) { onResult(t, -1); return; }
                foreach (var ip in ips)
                {
                    ct.ThrowIfCancellationRequested();
                    int ms = await Task.Run(() => TcpLatency(ip, 443), CancellationToken.None);
                    if (ms >= 0 && (best < 0 || ms < best)) { best = ms; bestIp = ip; }
                }
                if (best >= 0) t.Ip = bestIp;
                onResult(t, best);
            }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { return false; }
        return !ct.IsCancellationRequested;
    }

    // ---------------- 实时监控 ----------------

    /// <summary>单个目标的实时监控统计。</summary>
    public sealed class MonitorStats
    {
        public int Current = -1;          // 最近一次延迟（-1=失败）
        public readonly Queue<int> Samples = new(); // 最近样本（含 -1），上限 180
        public int OkCount, FailCount;
        public int Min = -1, Max = -1;
        public double Avg = -1;
        public double LossPct => OkCount + FailCount == 0 ? 0 : 100.0 * FailCount / (OkCount + FailCount);
    }

    /// <summary>监控样本事件（工作线程触发，UI 侧自行调度）。</summary>
    public event Action<int, int>? Sample; // (targetId, ms or -1)

    public bool Monitoring { get; private set; }
    private CancellationTokenSource? _monCts;
    private Task? _monTask;
    private StreamWriter? _csv;
    private readonly ConcurrentDictionary<int, MonitorStats> _stats = new();

    public MonitorStats StatsFor(int id) => _stats.GetOrAdd(id, _ => new MonitorStats());

    /// <summary>启动实时监控（对勾选目标每秒 tcping/443）。csv=true 时把每秒结果追加写 data\network\monitor.csv。</summary>
    public void StartMonitoring(bool csv)
    {
        if (Monitoring) return;
        _stats.Clear();
        var monitored = _targets.Where(t => t.Monitor).ToList();
        _monCts = new CancellationTokenSource();
        _csv = null;
        if (csv)
        {
            try
            {
                Directory.CreateDirectory(CsvDir);
                _csv = new StreamWriter(Path.Combine(CsvDir, "monitor.csv"), append: true, Encoding.UTF8) { AutoFlush = true };
                _csv.WriteLine(string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "time", "label", "ip", "ms"));
            }
            catch { _csv = null; }
        }
        Monitoring = true;
        _monTask = Task.Run(() => MonitorLoopAsync(monitored, _monCts.Token));
    }

    public async Task StopMonitoringAsync()
    {
        if (!Monitoring) return;
        Monitoring = false;
        try { _monCts?.Cancel(); } catch { }
        try { if (_monTask is not null) await _monTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _monCts?.Dispose(); _monCts = null; _monTask = null;
        try { _csv?.Dispose(); } catch { }
        _csv = null;
    }

    private async Task MonitorLoopAsync(List<ProbeTarget> monitored, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 目标表可能被增删/勾选变化：每轮取最新勾选集
            var round = _targets.Where(t => t.Monitor).ToList();
            if (round.Count == 0) { try { await Task.Delay(1000, ct); } catch { return; } continue; }

            var jobs = round.Select(async t =>
            {
                var ips = t.Domain is not null ? ResolveDomain(t.Domain) : new List<string> { t.Ip };
                if (ips.Count == 0) ips.Add(t.Ip.Length > 0 ? t.Ip : "0.0.0.0");
                int ms = -1;
                foreach (var ip in ips)
                {
                    ms = TcpLatency(ip, 443, 1500);
                    if (ms >= 0) { if (t.Ip != ip) t.Ip = ip; break; }
                }
                Record(t, ms);
                Sample?.Invoke(t.Id, ms);
            }).ToList();
            await Task.WhenAll(jobs);
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    private void Record(ProbeTarget t, int ms)
    {
        var s = StatsFor(t.Id);
        s.Current = ms;
        s.Samples.Enqueue(ms);
        while (s.Samples.Count > 180) s.Samples.Dequeue();
        if (ms >= 0)
        {
            s.OkCount++;
            s.Min = s.Min < 0 ? ms : Math.Min(s.Min, ms);
            s.Max = Math.Max(s.Max, ms);
            s.Avg = s.Avg < 0 ? ms : Math.Round((s.Avg * (s.OkCount - 1) + ms) / s.OkCount, 1);
        }
        else s.FailCount++;
        _csv?.WriteLine(string.Join(",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTime.Now.ToString("HH:mm:ss"), t.Label, t.Ip, ms));
    }
}

/// <summary>
/// GitPort GitHub 加速内核管理：按需下载 zhidian-cmd/gitport-github 的 Release exe 到
/// data\gitport\，以 -headless 启动本地代理(127.0.0.1:7810)+工作台(127.0.0.1:7811)。
/// 系统代理接管/PAC/MITM 的开关都在它的工作台页面里完成，本类只负责内核的下载与进程生命周期。
/// </summary>
public static class GitPortManager
{
    public const string Repo = "zhidian-cmd/gitport-github";
    public const string Version = "v0.5.3";
    public const string ExeName = "gitport-github.exe";
    public const int ProxyPort = 7810;
    public const int WebUiPort = 7811;
    public const string WorkbenchUrl = "http://127.0.0.1:7811";

    private static string Dir => Path.Combine(AppPaths.DataDir, "gitport");
    private static string ExePath => Path.Combine(Dir, ExeName);
    private static Process? _proc;
    private static readonly object _lock = new();

    public static bool IsInstalled => File.Exists(ExePath);

    public static string VersionInfo =>
        IsInstalled ? FileVersionInfo.GetVersionInfo(ExePath).FileVersion ?? Version : "";

    /// <summary>内核进程是否存活（含上次异常退出残留的同名进程）。</summary>
    public static bool IsRunning =>
        _proc is { HasExited: false } ||
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length > 0;

    /// <summary>工作台端口是否有响应（比进程名更接近真实可用态）。</summary>
    public static bool IsWorkbenchUp => NetworkProbe.TcpLatency("127.0.0.1", WebUiPort, 400) >= 0;

    /// <summary>下载内核（镜像链 → GitHub 直连）。progress 收到形如 "已下载 3.2/8.0 MB" 的文本。</summary>
    public static async Task DownloadAsync(Action<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string url = $"https://github.com/{Repo}/releases/download/{Version}/{ExeName}";
        var urls = Updater.DownloadMirrorPrefixes.Select(p => p + url).Append(url).ToList();
        string tmp = ExePath + ".download";
        Exception? last = null;
        foreach (var u in urls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EfootballBot");
                using var resp = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 8_353_280;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                var buf = new byte[81920];
                long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress?.Invoke($"已下载 {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");
                }
                if (done < 1_000_000) throw new IOException($"文件不完整（{done} 字节）");
                File.Move(tmp, ExePath, true);
                return;
            }
            catch (OperationCanceledException) { try { File.Delete(tmp); } catch { } throw; }
            catch (Exception ex) { last = ex; try { File.Delete(tmp); } catch { } }
        }
        throw new InvalidOperationException($"所有下载源均失败：{last?.Message}");
    }

    /// <summary>启动内核（-headless）。already-running 幂等。返回 false=启动失败。</summary>
    public static bool Start(Action<string, LogLevel>? log = null)
    {
        lock (_lock)
        {
            if (IsRunning) { log?.Invoke("GitPort 内核已在运行", LogLevel.Debug); return IsWorkbenchUp || WaitWorkbench(10); }
            if (!IsInstalled) { log?.Invoke("GitPort 内核未下载", LogLevel.Warn); return false; }
            try
            {
                _proc = Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = "-headless",
                    WorkingDirectory = Dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                bool up = WaitWorkbench(20);
                log?.Invoke(up
                    ? $"GitPort 已启动：代理 127.0.0.1:{ProxyPort}，工作台 {WorkbenchUrl}"
                    : "GitPort 进程已拉起，但工作台端口暂未就绪（可稍后重试打开）",
                    up ? LogLevel.Success : LogLevel.Warn);
                return up;
            }
            catch (Exception ex)
            {
                log?.Invoke($"GitPort 启动失败：{ex.Message}", LogLevel.Error);
                return false;
            }
        }
    }

    private static bool WaitWorkbench(int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            if (IsWorkbenchUp) return true;
            Thread.Sleep(500);
        }
        return IsWorkbenchUp;
    }

    /// <summary>停止内核：先停本进程持有的实例，再兜底清理同名残留（强杀后其自愈逻辑负责还原系统代理）。</summary>
    public static void Stop(Action<string, LogLevel>? log = null)
    {
        lock (_lock)
        {
            try
            {
                if (_proc is { HasExited: false }) { _proc.Kill(true); _proc.WaitForExit(5000); }
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
                {
                    try { p.Kill(true); p.WaitForExit(3000); } catch { }
                    p.Dispose();
                }
                _proc = null;
                log?.Invoke("GitPort 已停止", LogLevel.Info);
            }
            catch (Exception ex) { log?.Invoke($"GitPort 停止失败：{ex.Message}", LogLevel.Warn); }
        }
    }

    /// <summary>用系统默认浏览器打开工作台（分流开关/PAC/MITM/镜像链都在这里配置）。</summary>
    public static void OpenWorkbench()
    {
        try
        {
            Process.Start(new ProcessStartInfo(WorkbenchUrl) { UseShellExecute = true });
        }
        catch { }
    }
}
