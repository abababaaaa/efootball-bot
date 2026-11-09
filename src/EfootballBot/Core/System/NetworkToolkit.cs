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
    /// <summary>显示名（种子条目=国家/城市；域名目标=域名；手工添加=输入原文）。</summary>
    public string Label { get; set; } = "";
    /// <summary>类型：game / update / candidate / domain / manual。</summary>
    public string Kind { get; set; } = "game";
    /// <summary>国家代码（种子行；UPDATE=仅更新服）。</summary>
    public string Country { get; set; } = "";
    /// <summary>城市/区域英文名（种子行）。</summary>
    public string Region { get; set; } = "";
    /// <summary>域名（非空 = 每次测量前 DNS 解析展开成 IP；空 = 直接测 Ip）。</summary>
    public string? Domain { get; set; }
    /// <summary>最近一次解析/填写的 IP。</summary>
    public string Ip { get; set; } = "";
    /// <summary>端口（game=5735 走 ICMP 不通端口；update=443 走 TCP）。</summary>
    public int Port { get; set; } = 443;
    /// <summary>true = ICMP 回显（游戏服 UDP 端口 TCP 建连不通，与 ETK 一致用 ping）。</summary>
    public bool UseIcmp { get; set; }
    /// <summary>是否纳入实时监控（每秒探测）。</summary>
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
        Migrate();
        // 旧存档升级：把内嵌 eFootball 服务器表（中文名）重新灌进来（按 IP+端口去重）
        if (_targets.Count < 10) Seed();
        Save();
    }

    /// <summary>旧格式迁移：Port 缺省补 443、Kind 缺省按有无域名推断；游戏服 5735 补 ICMP 标记。</summary>
    private void Migrate()
    {
        bool touched = false;
        foreach (var t in _targets)
        {
            if (t.Port <= 0) { t.Port = t.Kind == "game" ? 5735 : 443; touched = true; }
            if (string.IsNullOrEmpty(t.Kind))
            { t.Kind = t.Domain is not null ? "domain" : "manual"; touched = true; }
            if (!t.UseIcmp && t.Port is not (443 or 80) && t.Domain is null)
            { t.UseIcmp = true; touched = true; }
        }
        if (touched) Save();
    }

    /// <summary>
    /// v3 迁移：种子表改中文名后，把旧存档里的种子行（按 IP+端口精确匹配）丢掉重灌，
    /// 用户手填的目标原样保留。存档版本号在 Load 里判断，这里只做「丢种子行」。
    /// </summary>
    private void DropOldSeedRows()
    {
        var seedKeys = LoadSeedRows()
            .Where(r => !string.IsNullOrWhiteSpace(r.ip))
            .Select(r => $"{r.ip}|{(r.port <= 0 ? 443 : r.port)}")
            .ToHashSet();
        if (seedKeys.Count == 0) return;
        _targets.RemoveAll(t => t.Domain is null && seedKeys.Contains($"{t.Ip}|{t.Port}"));
    }

    private static List<SeedRow> LoadSeedRows()
    {
        try
        {
            using var s = typeof(NetworkProbe).Assembly.GetManifestResourceStream("EfootballBot.efootball-servers.json");
            if (s is null) return new();
            return JsonSerializer.Deserialize<List<SeedRow>>(s, JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    // ---------------- 国家/城市中文化 ----------------

    private static readonly Dictionary<string, string> CountryZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AE"] = "阿联酋", ["AR"] = "阿根廷", ["AU"] = "澳大利亚", ["BE"] = "比利时", ["BR"] = "巴西",
        ["CA"] = "加拿大", ["CH"] = "瑞士", ["CL"] = "智利", ["DE"] = "德国", ["ES"] = "西班牙",
        ["FI"] = "芬兰", ["FR"] = "法国", ["GB"] = "英国", ["GR"] = "希腊", ["HK"] = "中国香港",
        ["ID"] = "印度尼西亚", ["IL"] = "以色列", ["IN"] = "印度", ["IT"] = "意大利", ["JP"] = "日本",
        ["KR"] = "韩国", ["MX"] = "墨西哥", ["MY"] = "马来西亚", ["NG"] = "尼日利亚", ["NL"] = "荷兰",
        ["OM"] = "阿曼", ["PE"] = "秘鲁", ["PL"] = "波兰", ["QA"] = "卡塔尔", ["SA"] = "沙特",
        ["SG"] = "新加坡", ["TH"] = "泰国", ["TR"] = "土耳其", ["TW"] = "中国台湾", ["US"] = "美国",
        ["ZA"] = "南非", ["UPDATE"] = "仅更新",
    };

    private static readonly Dictionary<string, string> CityZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dubai"] = "迪拜", ["Buenos Aires"] = "布宜诺斯艾利斯", ["Melbourne"] = "墨尔本", ["Sydney"] = "悉尼",
        ["Brussels"] = "布鲁塞尔", ["Sao Paulo"] = "圣保罗", ["Montreal"] = "蒙特利尔", ["Toronto"] = "多伦多",
        ["Zurich"] = "苏黎世", ["Santiago"] = "圣地亚哥", ["Berlin"] = "柏林",
        ["Frankfurt am Main"] = "法兰克福", ["Hamburg"] = "汉堡", ["Madrid"] = "马德里",
        ["Helsinki"] = "赫尔辛基", ["Paris"] = "巴黎", ["London"] = "伦敦", ["Athens"] = "雅典",
        ["Hong Kong"] = "香港", ["Jakarta"] = "雅加达", ["Petah Tikva"] = "佩塔提克瓦",
        ["Tel Aviv"] = "特拉维夫", ["Delhi"] = "德里", ["Kolkata"] = "加尔各答", ["Mumbai"] = "孟买",
        ["Milan"] = "米兰", ["Torino"] = "都灵", ["Osaka"] = "大阪", ["Tokyo"] = "东京",
        ["Seoul"] = "首尔", ["Queretaro"] = "克雷塔罗", ["Kuala Lumpur"] = "吉隆坡", ["Lagos"] = "拉各斯",
        ["Amsterdam"] = "阿姆斯特丹", ["Muscat"] = "马斯喀特", ["Lima"] = "利马", ["Warsaw"] = "华沙",
        ["Doha"] = "多哈", ["Dammam"] = "达曼", ["Singapore"] = "新加坡", ["Bangkok"] = "曼谷",
        ["Istanbul"] = "伊斯坦布尔", ["Taipei"] = "台北", ["Ashburn"] = "阿什本", ["Columbus"] = "哥伦布",
        ["Council Bluffs"] = "康瑟尔布拉夫斯", ["Dallas"] = "达拉斯", ["Las Vegas"] = "拉斯维加斯",
        ["Los Angeles"] = "洛杉矶", ["Moncks Corner"] = "蒙克斯科纳", ["Salt Lake City"] = "盐湖城",
        ["The Dalles"] = "达尔斯", ["Johannesburg"] = "约翰内斯堡", ["Anycast"] = "任意播",
    };

    /// <summary>种子行 → 中文显示名（"西班牙 · 马德里"）；未收录的代码/城市回退原文。</summary>
    private static string LocalizeLabel(string country, string region)
    {
        string c = CountryZh.TryGetValue(country ?? "", out var rc) ? rc : country ?? "";
        if (string.IsNullOrEmpty(region)) return c;
        string r = CityZh.TryGetValue(region, out var rr) ? rr : region;
        return string.IsNullOrEmpty(c) ? r : $"{c} · {r}";
    }

    private void Seed()
    {
        // ① eFootball 服务器种子表（内嵌，来源=ETK 本机实测清单）：游戏服走 ICMP、
        //    更新服/候选服按记录的方法或端口推断 TCP。
        try
        {
            using var s = typeof(NetworkProbe).Assembly.GetManifestResourceStream("EfootballBot.efootball-servers.json");
            if (s is not null)
            {
                var rows = JsonSerializer.Deserialize<List<SeedRow>>(s, JsonOpts) ?? new();
                foreach (var r in rows)
                {
                    if (string.IsNullOrWhiteSpace(r.ip)) continue;
                    int port = r.port <= 0 ? 443 : r.port;
                    if (_targets.Any(t => t.Ip == r.ip && t.Port == port)) continue; // 已有不重复
                    string kind = r.type switch
                    {
                        "update" => "update",
                        "game-candidate" => "candidate",
                        _ => "game",
                    };
                    bool tcp = (r.method ?? "").StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
                               || r.port is 443 or 80;
                    string label = LocalizeLabel(r.country ?? "", r.region ?? "");
                    if (kind == "update") label = "CloudFront 更新";
                    _targets.Add(new ProbeTarget
                    {
                        Id = _nextId++,
                        Label = string.IsNullOrEmpty(label) ? r.ip : label,
                        Kind = kind,
                        Country = r.country ?? "",
                        Region = r.region ?? "",
                        Ip = r.ip,
                        Port = port,
                        UseIcmp = !tcp,
                        Monitor = false,
                    });
                }
            }
        }
        catch { /* 种子表损坏：退回仅域名种子 */ }

        // ② GitHub 直连验证域（配合「更新/加速」排查）
        foreach (var d in SeedDomains)
        {
            if (_targets.Any(t => t.Domain == d)) continue;
            _targets.Add(new ProbeTarget
            {
                Id = _nextId++,
                Label = d,
                Kind = "domain",
                Domain = d,
                Ip = "",
                Port = 443,
                UseIcmp = false,
                Monitor = false,
            });
        }
        Save();
    }

    private sealed record SeedRow(string ip, int port, string type, string country, string region, string method);

    // ---------------- 目标表 ----------------

    public ProbeTarget Add(string input, bool monitor = false)
    {
        input = input.Trim();
        bool isDomain = Uri.CheckHostName(input) == UriHostNameType.Dns && !input.All(char.IsDigit) && input.Contains('.');
        var t = new ProbeTarget
        {
            Id = _nextId++,
            Label = input,
            Kind = isDomain ? "domain" : "manual",
            Domain = isDomain ? input : null,
            Ip = isDomain ? "" : input,
            Port = 443,
            UseIcmp = false,
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
            // v1 存档（只有域名种子、缺 Kind/Port/ICMP 字段）：丢旧域名种子、保留用户手填 IP
            if (doc.Version < 2)
                _targets = _targets.Where(t => !string.IsNullOrEmpty(t.Ip)).ToList();
            // v2 → v3：种子表改为中文名，旧种子行丢弃后由 Seed() 重灌（手填目标保留）
            if (doc.Version < 3)
                DropOldSeedRows();
            _nextId = _targets.Count == 0 ? 1 : _targets.Max(t => t.Id) + 1;
        }
        catch { _targets = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllBytes(StorePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Store(_targets, _nextId, 3), JsonOpts)));
        }
        catch { }
    }

    private sealed record Store(List<ProbeTarget> Targets, int NextId, int Version = 0);

    /// <summary>按 IP 反查中文地区：先精确匹配种子表，再退到同 /16 段的种子（AWS 同区域大段相邻）。</summary>
    public string? RegionFor(string ip)
    {
        var exact = _targets.FirstOrDefault(t => t.Ip == ip && t.Kind != "domain");
        if (exact is not null) return exact.Label;
        int dot = ip.LastIndexOf('.');
        if (dot <= 0) return null;
        string prefix = ip[..dot];
        var near = _targets.FirstOrDefault(t => t.Kind != "domain"
            && t.Ip.Length > prefix.Length && t.Ip.StartsWith(prefix + ".", StringComparison.Ordinal));
        return near is null ? null : near.Label + "（附近）";
    }

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

    /// <summary>统一探测：ICMP 目标走回显、TCP 目标走端口建连；返回毫秒，失败 -1。</summary>
    public static int Measure(string ip, bool useIcmp, int port, int timeoutMs = 1500)
        => useIcmp ? IcmpPing(ip, timeoutMs) : TcpLatency(ip, port, timeoutMs);

    // ---------------- ICMP（iphlpapi，无需管理员） ----------------

    [global::System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [global::System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho(IntPtr handle, uint destIp, byte[] data, ushort dataSize,
        IntPtr options, byte[] reply, uint replySize, uint timeout);

    [global::System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    /// <summary>ICMP 回显延迟（毫秒）；失败/超时返回 -1。游戏服 5735 是 UDP 端口，只能靠 ping 判活。</summary>
    public static int IcmpPing(string ip, int timeoutMs = 1500)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return -1;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = IcmpCreateFile();
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;
            var payload = new byte[32];
            var reply = new byte[128];
            uint n = IcmpSendEcho(h, BitConverter.ToUInt32(addr.GetAddressBytes(), 0), payload,
                (ushort)payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeoutMs);
            if (n == 0) return -1;
            // ICMP_ECHO_REPLY(64 位)：Address(0-3) + Status(4-7) + RoundTripTime(8-11) + ...
            if (BitConverter.ToUInt32(reply, 4) != 0) return -1;
            return (int)BitConverter.ToUInt32(reply, 8);
        }
        catch { return -1; }
        finally
        {
            if (h != IntPtr.Zero) { try { IcmpCloseHandle(h); } catch { } }
        }
    }

    /// <summary>
    /// 测量全部目标（并行，DNS 域名目标先解析展开，逐 IP 测，目标取最优值并回填 IP）。
    /// onResult 在工作线程回调；返回 false 表示被取消。
    /// </summary>
    public async Task<bool> MeasureAllAsync(CancellationToken ct, Action<ProbeTarget, int> onResult,
        Func<ProbeTarget, bool>? scope = null)
    {
        var snapshot = scope is null ? _targets.ToList() : _targets.Where(scope).ToList();
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
                    int ms = await Task.Run(() => Measure(ip, t.UseIcmp, t.Port), CancellationToken.None);
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
                    ms = Measure(ip, t.UseIcmp, t.Port);
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
