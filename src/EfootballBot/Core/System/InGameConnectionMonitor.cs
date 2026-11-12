using System.Collections.Concurrent;
using System.Net;
using SharpPcap;
using SharpPcap.LibPcap;

namespace EfootballBot.Core.System;

/// <summary>一场对局结束时的网络小结（比赛收尾/停止监控时产出）。</summary>
public sealed record MatchSummary(
    DateTime StartedAt,
    string Mode,
    string Remote,
    string Region,
    int MinMs,
    int AvgMs,
    int MaxMs,
    int P95Ms,
    int JitterMs,
    double LossPct,
    int Samples,
    int DurationSec,
    string Grade,
    IReadOnlyList<int> Curve,
    int ServerRttMs = -1,
    double EstBars = -1)
{
    public string Line =>
        $"{StartedAt:HH:mm} {Mode} {Remote} {Region} · 延迟 {MinMs}/{AvgMs}/{MaxMs}ms"
        + (P95Ms > 0 ? $" · P95 {P95Ms}ms" : "")
        + (JitterMs >= 0 ? $" · 抖动 {JitterMs}ms" : "")
        + (LossPct >= 0 ? $" · 丢包 {LossPct:0.#}%" : " · 丢包 —")
        + $" · {Samples} 样本/{DurationSec}s · {Grade}";
}

/// <summary>游戏内连接监控的一次快照（每秒刷新）。</summary>
public sealed record ConnSnapshot(
    string State,            // 等待比赛 / 监控中 / 数据停止 / 未启动
    int DelayMs,             // -1=未知
    double LossPct,          // -1 = 不可测（对端不回 ICMP）
    string Remote,           // "ip:port"，未知时 "—"
    string Region,           // 中文地区或「未知区域」
    int MinMs,
    int AvgMs,
    int MaxMs,
    IReadOnlyList<int> Curve,
    string Mode = "—",       // 服务器 / P2P / 加速中继 / 测速中
    int JitterMs = -1,       // 对端回包间隔抖动（抓包实测，与 ICMP 无关）
    int UpPps = 0,
    int DownPps = 0,
    int UpKbps = 0,
    int DownKbps = 0,
    int Peers = 0,           // 同时活跃的游戏 UDP 对端数
    string Note = "",        // 归属/口径提示（加速器、未锁定进程、对端不回 ICMP…）
    int StunRttMs = -1,      // STUN 事务往返（游戏自己路径上的实测 RTT，拿得到才显示）
    bool ViaRelay = false,   // true = 对局流量走加速隧道（那么「延迟」只是你到节点的这一段）
    int ServerRttMs = -1,    // 直连基准：到对局地域服务器的实测 RTT（不开加速时的口径）
    string ServerLabel = "", // 该地域的中文名
    double EstBars = -1,     // 由 ServerRttMs 换算的图表格数（1~5）
    string PingStatus = "",  // 游戏内延迟读取状态（未启用/未定位偏移/游戏内 42ms…）
    int TcpingMs = -1,       // 服务器局：TCP 探测（xjbt 的 tcping 口径）
    string Direction = "",   // 最近 30 秒的双向探测史：'.'=上下行都有 '_'=只收到 X=只发出
    double BarsEstimate = -1,// 由实测延迟估算的天线格数
    string SpeedTests = "",  // 游戏自己测速包算出的各城市 RTT（"东京 42ms(38/46) 12/12"）
    string DelaySource = ""); // 这个延迟是哪来的：游戏内 / tcping / STUN / 链路ICMP

/// <summary>
/// 游戏内连接监控 v7.0.2：只统计「属于 eFootball.exe 的 UDP 流」，用抓包算速率/抖动，
/// ICMP 只作为路径延迟参考，并且只在这一次对局里对同一个远端持续探测。
///
/// v7.0.1 的三个实测错误（2026-11-12 用户现场：匹配谁都是 9~10ms、丢包 53.6%）：
/// ① 选流规则是「2~3 秒内包数最多的一条」，不认进程——机器上开着网易 UU 时，
///    accservice 自己到加速节点（113.47.5.84:18081）的 UDP 流被当成了对局连接，
///    于是「远端」是加速节点、区域查不到、延迟恒为节点延迟（实测 8~9ms，与阿里 DNS 同量级）。
/// ② 丢包 = ICMP 失败次数 / 累计探测次数，累计从监控启动那一刻算起，且远端失效后
///    不清空——比赛结束仍在 ping 已死对端 → 丢包率一路涨到 50%+ 并卡在那里。
/// ③ 模式判定只看远端端口（非 5735 一律 P2P），50000 一刀切算「测速中」。
/// 现在：GetExtendedUdpTable 按 PID 取游戏端口集合做白名单，按方向分别计包/计字节，
/// 每个对端的探测窗口独立且滚动 30 次，对端从不回 ICMP 时明确标「不可测」而不是算丢包。
/// Npcap 未安装/未提权时 Start 返回失败原因，页面提示，不影响挂机。
/// </summary>
public sealed class InGameConnectionMonitor : IDisposable
{
    /// <summary>服务器模式端口（5735 为服务器 UDP 端口，5736 为 TCP 侧）。</summary>
    private static readonly ushort[] ServerPorts = { 5735, 5736 };

    /// <summary>匹配前的测速端口（xjbt forceP2P 屏蔽的同一组）。5021/10000/30000 只在「同时连 3 个以上对端」时才算测速。</summary>
    private static readonly ushort[] SpeedTestPorts = { 5521, 10000, 30000, 50000 };

    private const int FlowIdleMs = 2500;      // 一个流超过这个时间没有包 → 不算活跃
    private const int RemoteStaleMs = 5000;   // 主连接超过这个时间没有包 → 清空远端，停止探测
    private const int SessionIdleMs = 10_000; // 远端 10 秒无流量视为本场结束
    private const int ProbeWindow = 30;       // 丢包率 = 最近 30 次探测
    private const int ProbeTimeoutMs = 700;

    private readonly List<ILiveDevice> _devices = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // ---------------- 抓包流表 ----------------
    private readonly ConcurrentDictionary<string, Flow> _flows = new();

    /// <summary>这条流是谁的：游戏自己发的 / 加速器隧道的 / 未知。</summary>
    private enum FlowRole { Game, Relay }

    private sealed class Flow
    {
        public string RemoteIp = "";
        public FlowRole Role = FlowRole.Game;
        public ushort LocalPort, RemotePort;
        public long UpPkts, DownPkts, UpBytes, DownBytes;
        public long LastUpTicks, LastDownTicks;
        public long PrevUpPkts, PrevDownPkts, PrevUpBytes, PrevDownBytes;
        public int UpPps, DownPps, UpKbps, DownKbps;
        public long LastInboundTicks;
        public int LastGapMs = -1;
        public readonly Queue<int> Gaps = new();
    }

    /// <summary>本地端口 → 游戏 PID（每 2 秒刷新一次）。</summary>
    private HashSet<ushort> _gamePorts = new();
    /// <summary>加速器（UU/迅游…）自己开的 UDP 端口：对局流量通常整条挂在这上面。</summary>
    private HashSet<ushort> _relayPorts = new();
    private List<string> _relayNames = new();
    private long _forceRefreshAt;
    private long _seenUdp;          // 抓到的 UDP 包总数（不看进程过滤）——诊断「抓包到底收没收到东西」
    private long _seenGameUdp;      // 其中落在游戏端口白名单里的
    private string _gameProcNote = "";
    private DateTime _portsAt = DateTime.MinValue;
    private HashSet<string> _localIps = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- 当前主连接 ----------------
    private string _remoteIp = "";
    private ushort _remotePort;
    private Flow? _primary;
    private string _peerKey = "";

    /// <summary>STUN 事务表：事务 ID → 发出时刻，用来算「游戏自己这条路径」的往返时延。</summary>
    private readonly ConcurrentDictionary<string, long> _stunPending = new();
    private readonly Queue<int> _stunRtts = new();
    private long _stunLastCleanup;

    private readonly Queue<int> _curve = new();          // 最近 60 个探测值（-1=失败）
    private readonly Queue<bool> _probeWindow = new();   // 最近 30 次探测结果
    private bool _peerRepliesIcmp;                       // 这个远端回不回 ICMP（回过一次就算回）
    private int _probeCount;
    private int _ok, fail, sum, max, _min = -1;

    private sealed class MatchSession
    {
        public DateTime StartedAt = DateTime.Now;
        public string Remote = "";
        public string Mode = "—";
        public long LastDataTicks;
        public int Min = -1, Max, Sum, Ok, Fail;
        /// <summary>本场每一次探测的延迟（ms，-1=失败）——结束时画曲线、算 P95 与抖动。</summary>
        public readonly List<int> Samples = new();
        public readonly List<int> Jitters = new();
        /// <summary>本场每分钟采到的「到该地域服务器」实测 RTT（真延迟）。</summary>
        public readonly List<int> ServerRtts = new();
        public int TcpingAvg = -1;
    }
    private MatchSession? _session;

    public bool Running { get; private set; }
    public event Action<ConnSnapshot>? Updated;

    /// <summary>一场对局结束（远端切换 / 数据停止 / 停止监控）时触发：延迟曲线小结。</summary>
    public event Action<MatchSummary>? MatchEnded;

    /// <summary>最近若干场小结（新的在前），界面重建时直接铺出来。</summary>
    public IReadOnlyList<MatchSummary> RecentMatches => _recent;
    private readonly List<MatchSummary> _recent = new();

    /// <summary>地区查询：IP → 中文地区名（找不到返回 null）。</summary>
    public Func<string, string?>? RegionLookup { get; set; }

    /// <summary>
    /// 候选服务器表（页面上的「游戏服」目标）：用来测「与游戏内天线格同一口径」的真延迟——
    /// 游戏自己也是每 10 分钟对 40 个服务器地域做一次测速，格子取自那份结果。我们测同一批目标，
    /// 才能给出「这条线大概是多少 ms」的答案。空表时不做这项测量。
    /// </summary>
    public Func<IReadOnlyList<(string Ip, string Label)>>? ServerTargetsProvider { get; set; }

    private readonly ConcurrentDictionary<string, int> _serverRtt = new();
    private long _serverProbeAt;
    private int _serverRttBest = -1;
    private string _serverBestLabel = "";

    /// <summary>游戏内延迟读取（读客户端内存里的 NetDriver ping）。这是唯一「真延迟」来源。</summary>
    private readonly GamePingReader _ping = new();

    // ---- xjbt 口径的三件套：tcping / 双向探测史 / 被动测速 ----
    private readonly Queue<char> _direction = new();          // 最近 30 秒：'.' '_' 'X'
    private int _tcpingMs = -1;
    private readonly ConcurrentDictionary<string, long> _probeSent = new();   // 服务器 IP → 发出时刻
    private readonly ConcurrentDictionary<string, (int Req, int Resp, int Best, int Sum, int N)> _speed = new();
    private HashSet<string> _knownServerIps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _knownIpsAt = DateTime.MinValue;
    private readonly Queue<char> _pingHistory = new();
    public GamePingReader Ping => _ping;
    /// <summary>服务器直连基准探测：默认关（用户 2026-11-12：假延迟不显示）。</summary>
    public bool EnableServerProbe { get; set; }

    /// <summary>最近一次快照（页面重建/悬浮窗新建时先拿这份，别显示空白）。</summary>
    public ConnSnapshot? Last { get; private set; }

    /// <summary>启动抓包 + 每秒采样。网卡选承载本机出口 IP 的那块，另加 TAP/TUN（加速器/虚拟网卡）。</summary>
    public bool Start(out string error)
    {
        error = "";
        if (Running) return true;
        try
        {
            var devs = CaptureDeviceList.Instance;
            if (devs.Count == 0)
            {
                error = "未找到任何抓包网卡（Npcap 未安装或未启动）";
                return false;
            }

            _localIps = UdpSocketMap.LocalIPv4();
            string localIp = PrimaryLocalIp();

            ILiveDevice? primary = null;
            foreach (var d in devs.OfType<LibPcapLiveDevice>())
            {
                if (d.Loopback) continue;
                if (d.Addresses.Any(a => a.Addr?.ipAddress?.ToString() == localIp)) { primary = d; break; }
            }
            if (primary is null)
            {
                foreach (var d in devs.OfType<LibPcapLiveDevice>())
                {
                    if (d.Loopback || d.Addresses.Count == 0) continue;
                    if (d.Addresses.Any(a => a.Addr?.ipAddress?.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork))
                    { primary = d; break; }
                }
            }
            primary ??= devs.FirstOrDefault(d => !(d is LibPcapLiveDevice l) || !l.Loopback);
            if (primary is null) { error = "没有可用的 IPv4 抓包网卡"; return false; }

            var picked = new List<ILiveDevice> { primary };
            // 加速器/虚拟网卡会把游戏流量放进自己的 TAP/TUN：一并抓上，否则只看到「无流量」
            foreach (var d in devs.OfType<LibPcapLiveDevice>())
            {
                if (picked.Contains(d) || d.Loopback) continue;
                string desc = (d.Description ?? "") + " " + (d.Name ?? "");
                if (desc.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("vpn", StringComparison.OrdinalIgnoreCase))
                    picked.Add(d);
                if (picked.Count >= 4) break;
            }

            foreach (var d in picked)
            {
                try
                {
                    d.Open(new DeviceConfiguration
                    {
                        Mode = DeviceModes.Promiscuous,
                        Snaplen = 65535,
                        ReadTimeout = 500,
                    });
                    // 系统噪音在驱动层就丢掉，抓包回调里只处理游戏可能用的 UDP
                    d.Filter = "udp and not port 53 and not port 123 and not port 5353 and not port 1900 and not port 3702";
                    d.OnPacketArrival += OnPacket;
                    d.StartCapture();
                    _devices.Add(d);
                }
                catch { try { d.Close(); } catch { } }
            }
            if (_devices.Count == 0) { error = "抓包启动失败：Npcap 驱动打不开（需要管理员运行本程序）"; return false; }

            RefreshGamePorts(force: true);

            _cts = new CancellationTokenSource();
            Running = true;
            _loop = Task.Run(() => SampleLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            error = $"抓包启动失败：{ex.Message}（Npcap 需要以管理员身份运行本程序）";
            CloseDevices();
            return false;
        }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(1500); } catch { }
        FinishSession();
        CloseDevices();
        _cts?.Dispose(); _cts = null; _loop = null;
        _flows.Clear();
        ResetRemote();
        _curve.Clear();
        _probeWindow.Clear();
        _ok = fail = sum = max = 0; _min = -1;
        _probeCount = 0; _peerRepliesIcmp = false;
        Last = null;
    }

    private void CloseDevices()
    {
        foreach (var d in _devices)
        {
            try
            {
                d.OnPacketArrival -= OnPacket;
                d.StopCapture();
                d.Close();
            }
            catch { }
        }
        _devices.Clear();
    }

    /// <summary>
    /// 出口 IP（= 走默认路由的那块网卡）。必须按路由问，不能拿「本机地址集合的第一个」：
    /// 这台机器同时有 Radmin VPN 26.182.30.85 与 106.54.214.132 等虚拟地址，按集合顺序取会
    /// 挑到 Radmin 网卡 → 抓包成功但一包都收不到（v7.0.2 自测实测，监控永远「等待比赛」）。
    /// </summary>
    private string PrimaryLocalIp()
    {
        foreach (var probe in new[] { "8.8.8.8", "223.5.5.5", "114.114.114.114" })
        {
            try
            {
                using var s = new global::System.Net.Sockets.Socket(
                    global::System.Net.Sockets.AddressFamily.InterNetwork,
                    global::System.Net.Sockets.SocketType.Dgram, global::System.Net.Sockets.ProtocolType.Udp);
                s.Connect(probe, 65530);
                if (s.LocalEndPoint is IPEndPoint ep && ep.Address.ToString() is var ip
                    && ip != "127.0.0.1" && !ip.StartsWith("169.254.", StringComparison.Ordinal))
                    return ip;
            }
            catch { }
        }
        foreach (var ip in _localIps)
            if (ip != "127.0.0.1" && !ip.StartsWith("169.254.", StringComparison.Ordinal)) return ip;
        return "127.0.0.1";
    }

    // ---------------- 抓包 ----------------

    private void OnPacket(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var span = raw.Data.AsSpan();
            int off = LinkHeaderOffset(span);
            if (off < 0 || span.Length < off + 28) return;
            if ((span[off] >> 4) != 4) return;                 // 只认 IPv4
            int ihl = (span[off] & 0x0F) * 4;
            if (ihl < 20 || span.Length < off + ihl + 8) return;
            if (span[off + 9] != 17) return;                   // UDP
            var src = new IPAddress(span.Slice(off + 12, 4).ToArray()).ToString();
            var dst = new IPAddress(span.Slice(off + 16, 4).ToArray()).ToString();
            int u = off + ihl;
            ushort sp = (ushort)((span[u] << 8) | span[u + 1]);
            ushort dp = (ushort)((span[u + 2] << 8) | span[u + 3]);
            int payload = ((span[off + 2] << 8) | span[off + 3]) - ihl - 8;   // IP 总长 - IP头 - UDP头
            if (payload <= 0) payload = 8;                     // 某些驱动不给总长，按最小算
            int bytes = payload + 8 + 20;                      // 估算线上字节数（IP+UDP+载荷）
            TryStun(span.Slice(u + 8), payload);               // STUN 事务（3478）→ 游戏自己路径上的 RTT

            Interlocked.Increment(ref _seenUdp);
            bool srcLocal = _localIps.Contains(src);
            bool dstLocal = _localIps.Contains(dst);
            bool outbound;
            ushort localPort, remotePort;
            string remoteIp;
            if (srcLocal && !dstLocal) { outbound = true; remoteIp = dst; localPort = sp; remotePort = dp; }
            else if (dstLocal && !srcLocal) { outbound = false; remoteIp = src; localPort = dp; remotePort = sp; }
            else
            {
                // 网卡地址不在本机表里（加速器 TAP / NAT）：用「哪一端是游戏端口」判方向
                if (_gamePorts.Contains(sp)) { outbound = true; remoteIp = dst; localPort = sp; remotePort = dp; }
                else if (_gamePorts.Contains(dp)) { outbound = false; remoteIp = src; localPort = dp; remotePort = sp; }
                else return;
            }
            if (remoteIp == "0.0.0.0" || remoteIp.StartsWith("127.", StringComparison.Ordinal)) return;
            if (remotePort is 53 or 123 or 5353 or 1900) return;

            // 进程归属：本地端口要么属于 eFootball.exe（直连/驱动代理），
            // 要么属于加速器进程（UU/迅游的隧道——本机实测对局流量全在这条上）。
            FlowRole role;
            if (_gamePorts.Contains(localPort)) role = FlowRole.Game;
            else if (_relayPorts.Contains(localPort)) role = FlowRole.Relay;
            else
            {
                // 端口表 2 秒刷新一次，新开局的端口可能还没进表 → 命中不了就立刻补刷一次（限 300ms 一次）
                long t = Environment.TickCount64;
                if (t - _forceRefreshAt > 300)
                {
                    _forceRefreshAt = t;
                    RefreshGamePorts(force: true);
                }
                if (_gamePorts.Contains(localPort)) role = FlowRole.Game;
                else if (_relayPorts.Contains(localPort)) role = FlowRole.Relay;
                else return;
            }
            Interlocked.Increment(ref _seenGameUdp);

            string key = remoteIp + "|" + remotePort + "|" + localPort;
            var f = _flows.GetOrAdd(key, _ => new Flow { RemoteIp = remoteIp, LocalPort = localPort, RemotePort = remotePort, Role = role });
            long now = Environment.TickCount64;
            if (outbound)
            {
                Interlocked.Increment(ref f.UpPkts);
                Interlocked.Add(ref f.UpBytes, bytes);
                f.LastUpTicks = now;
                TrackProbeSend(remoteIp, now);
            }
            else
            {
                Interlocked.Increment(ref f.DownPkts);
                Interlocked.Add(ref f.DownBytes, bytes);
                f.LastDownTicks = now;
                TrackProbeReply(remoteIp, now);
                // 抖动只看对端回包间隔（游戏 20~60Hz，间隔差就是网络抖动）
                long prev = f.LastInboundTicks;
                if (prev > 0)
                {
                    int gap = (int)Math.Min(1000, now - prev);
                    if (gap > 0 && gap < 500)
                    {
                        lock (f.Gaps)
                        {
                            f.Gaps.Enqueue(gap);
                            while (f.Gaps.Count > 16) f.Gaps.Dequeue();
                        }
                        f.LastGapMs = gap;
                    }
                }
                f.LastInboundTicks = now;
            }
        }
        catch { }
    }

    /// <summary>
    /// 被动测速（对应 xjbt 的 speedtest()）：游戏自己在后台向各城市服务器发探测包，
    /// 我们把「发出 → 该 IP 的回包」配对，差就是这条路径的往返（最快/平均）。
    /// 不认协议、不构造包，只认「同一 IP 的出包与回包时间差」，所以 Konami 改包格式也不用改代码。
    /// </summary>
    private void TrackProbeSend(string ip, long now)
    {
        if (!_knownServerIps.Contains(ip)) return;
        _probeSent[ip] = now;
        var st = _speed.GetOrAdd(ip, _ => (0, 0, int.MaxValue, 0, 0));
        _speed[ip] = (st.Req + 1, st.Resp, st.Best, st.Sum, st.N);
    }

    private void TrackProbeReply(string ip, long now)
    {
        if (!_knownServerIps.Contains(ip)) return;
        if (!_probeSent.TryRemove(ip, out long sent)) return;
        int rtt = (int)(now - sent);
        if (rtt < 1 || rtt > 1500) return;
        var st = _speed.GetOrAdd(ip, _ => (0, 0, int.MaxValue, 0, 0));
        _speed[ip] = (st.Req, st.Resp + 1,
                      Math.Min(st.Best == 0 ? int.MaxValue : st.Best, rtt),
                      st.Sum + rtt, st.N + 1);
    }

    /// <summary>刷新已知服务器 IP 集合（被动测速与直连基准都用它判「这是不是游戏服的包」）。</summary>
    private void RefreshKnownServers()
    {
        if ((DateTime.Now - _knownIpsAt).TotalSeconds < 15) return;
        _knownIpsAt = DateTime.Now;
        try
        {
            var list = ServerTargetsProvider?.Invoke();
            if (list is null) return;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ip, _) in list) if (!string.IsNullOrEmpty(ip)) set.Add(ip);
            _knownServerIps = set;
        }
        catch { }
    }

    /// <summary>被动测速结果 → 一行文本（最快的三台）。</summary>
    private string SpeedTestText()
    {
        if (_speed.Count == 0) return "";
        return string.Join("  ",
            _speed.Where(kv => kv.Value.Resp > 0)
                  .OrderBy(kv => kv.Value.Best == 0 ? int.MaxValue : kv.Value.Best)
                  .Take(3)
                  .Select(kv =>
                  {
                      string city = SafeRegion(kv.Key) ?? kv.Key;
                      int best = kv.Value.Best == 0 ? -1 : kv.Value.Best;
                      int avg = kv.Value.N == 0 ? -1 : (int)Math.Round((double)kv.Value.Sum / kv.Value.N);
                      return $"{city} {best}ms({best}/{avg}) {kv.Value.Resp}/{kv.Value.Req}";
                  }));
    }

    private static int LinkHeaderOffset(ReadOnlySpan<byte> s)
    {
        if (s.Length >= 18 && (s[12] << 8 | s[13]) is 0x8100)                     // VLAN 标签
            return (s.Length >= 18 && (s[16] << 8 | s[17]) is 0x0800) ? 18 : -1;
        if (s.Length >= 14 && (s[12] << 8 | s[13]) is 0x0800) return 14;          // Ethernet IPv4
        if (s.Length >= 4 && s[0] == 2) return 4;                                 // Null/Loopback
        if (s.Length >= 24 && (s[21] << 8 | s[22]) == 0x0800) return 24;          // Linux cooked
        if (s.Length >= 1 && (s[0] >> 4) == 4) return 0;                          // 裸 IP
        return -1;
    }

    /// <summary>刷新「游戏进程的 UDP 端口白名单」。游戏没开时集合为空 → 退回不做进程过滤，但界面标注。</summary>
    private void RefreshGamePorts(bool force = false)
    {
        if (!force && (DateTime.Now - _portsAt).TotalSeconds < 2) return;
        _portsAt = DateTime.Now;
        try
        {
            var ports = UdpSocketMap.GamePorts(out var pids);
            _gamePorts = ports;
            _relayPorts = UdpSocketMap.AcceleratorPorts(out var owners);
            _relayNames = owners.Select(o => o.Name).Distinct().ToList();
            _gameProcNote = pids.Count == 0
                ? "未发现 eFootball.exe：当前不按进程过滤，可能混入其他软件流量"
                : ports.Count == 0
                    ? "已连上 eFootball.exe，但还没开出 UDP 端口（等它连服务器）"
                    : "";
        }
        catch { }
    }

    // ---------------- STUN 事务往返（游戏自己路径上的实测 RTT） ----------------

    /// <summary>
    /// eFootball 的联机握手走 STUN（UDP 3478 一类端口）：Binding Request 的 12 字节事务 ID 会原样出现在
    /// Binding Success Response 里。抓包把两边对上，就得到**游戏自己这条路径**（含加速器隧道）的往返时延——
    /// 这才是最接近客户端内部那个 ping 数字的量，和 ICMP 完全无关。拿不到就是 -1，界面不显示。
    /// </summary>
    private void TryStun(ReadOnlySpan<byte> p, int len)
    {
        if (len < 20 || p.Length < 20) return;
        if (p[4] != 0x21 || p[5] != 0x12 || p[6] != 0xA4 || p[7] != 0x42) return;   // magic cookie
        ushort type = (ushort)((p[0] << 8) | p[1]);
        string tx = Convert.ToHexString(p.Slice(8, 12));
        long now = Environment.TickCount64;
        if (type is 0x0001 or 0x0003)                                             // Binding Request / Indication
        {
            _stunPending[tx] = now;
            if (now - _stunLastCleanup > 5000)
            {
                _stunLastCleanup = now;
                foreach (var kv in _stunPending)
                    if (now - kv.Value > 3000) _stunPending.TryRemove(kv.Key, out _);
            }
            return;
        }
        if (type is 0x0101 && _stunPending.TryRemove(tx, out long sent))          // Binding Success Response
        {
            int rtt = (int)Math.Min(3000, now - sent);
            if (rtt < 0) return;
            lock (_stunRtts)
            {
                _stunRtts.Enqueue(rtt);
                while (_stunRtts.Count > 8) _stunRtts.Dequeue();
            }
        }
    }

    private int StunRttOf()
    {
        lock (_stunRtts)
        {
            if (_stunRtts.Count == 0) return -1;
            var arr = _stunRtts.ToArray();
            Array.Sort(arr);
            return arr[arr.Length / 2];
        }
    }

    /// <summary>算每秒速率 + 抖动，挑主连接（游戏端口内的活跃流里包最多的一条）。</summary>
    private void SampleFlows(int elapsedMs)
    {
        double sec = Math.Max(0.2, elapsedMs / 1000.0);
        long now = Environment.TickCount64;
        Flow? best = null;
        Flow? bestGame = null;
        long bestScore = -1, bestGameScore = -1;
        int peers = 0;

        foreach (var kv in _flows)
        {
            var f = kv.Value;
            long up = Interlocked.Read(ref f.UpPkts), down = Interlocked.Read(ref f.DownPkts);
            long upB = Interlocked.Read(ref f.UpBytes), downB = Interlocked.Read(ref f.DownBytes);
            f.UpPps = (int)Math.Max(0, (up - f.PrevUpPkts) / sec);
            f.DownPps = (int)Math.Max(0, (down - f.PrevDownPkts) / sec);
            f.UpKbps = (int)Math.Max(0, (upB - f.PrevUpBytes) * 8 / 1000 / sec);
            f.DownKbps = (int)Math.Max(0, (downB - f.PrevDownBytes) * 8 / 1000 / sec);
            f.PrevUpPkts = up; f.PrevDownPkts = down; f.PrevUpBytes = upB; f.PrevDownBytes = downB;

            bool active = Math.Max(f.LastUpTicks, f.LastDownTicks) is var last && now - last < FlowIdleMs;
            if (!active || f.UpPkts == 0) continue;
            long score = f.UpPps + f.DownPps;

            if (f.Role == FlowRole.Game)
            {
                peers++;
                if (score > bestGameScore || bestGame is null) { bestGame = f; bestGameScore = score; }
            }
            else
            {
                // 隧道流：只有「对局量级」的才算（实测对局上下行各 ~150pps；
                // 加速器的控制/心跳流只有几 pps，认进来就会刷出一堆假的「对局」）
                if (score < 30) continue;
                peers++;
                if (score > bestScore || best is null) { best = f; bestScore = score; }
            }
        }
        // 直连的游戏流优先；没有直连流 → 用加速隧道
        if (bestGame is not null) { best = bestGame; bestScore = bestGameScore; }

        // 长时间没有活跃流：把流表清掉，避免越积越多（特别是加速器/后台软件的临时端口）
        if (now % 30 < 1)
            foreach (var kv in _flows)
                if (now - Math.Max(kv.Value.LastUpTicks, kv.Value.LastDownTicks) > 60_000)
                    _flows.TryRemove(kv.Key, out _);

        Peers = peers;
        if (best is null) return;
        string key = best.RemoteIp + ":" + best.RemotePort;
        if (key != _peerKey)
        {
            _peerKey = key;
            _remoteIp = best.RemoteIp;
            _remotePort = best.RemotePort;
            _primary = best;
            _primaryRole = best.Role;
            _matchPpsWarm = 0;
            // 换对手 = 换一场：延迟统计与曲线都从零开始，
            // 否则「最小/平均/最大」横跨好几个对手，看起来永远差不多（v7.0.1 的体感来源之一）
            _probeWindow.Clear();
            _probeCount = 0;
            _peerRepliesIcmp = false;
            _ok = fail = sum = max = 0;
            _min = -1;
            _curve.Clear();
            lock (_stunRtts) _stunRtts.Clear();
            _stunPending.Clear();
        }
        else _primary = best;
    }

    private int Peers { get; set; }
    private FlowRole _primaryRole = FlowRole.Game;
    /// <summary>连续几秒达到「对局量级」的速率——不足 3 秒的不算一场（连接检查/心跳会把列表刷满）。</summary>
    private int _matchPpsWarm;

    private static int JitterOf(Flow? f)
    {
        if (f is null) return -1;
        lock (f.Gaps)
        {
            if (f.Gaps.Count < 4) return -1;
            var list = f.Gaps.ToArray();
            int sum = 0, n = 0;
            for (int i = 1; i < list.Length; i++)
            {
                sum += Math.Abs(list[i] - list[i - 1]);
                n++;
            }
            return n == 0 ? -1 : sum / n;
        }
    }

    private void ResetRemote()
    {
        _remoteIp = "";
        _remotePort = 0;
        _primary = null;
        _peerKey = "";
        _probeWindow.Clear();
        _probeCount = 0;
        _peerRepliesIcmp = false;
    }

    /// <summary>按「端口 + 已知服务器 IP + 是否经加速器」判对战模式。</summary>
    private string ModeFor(string ip, ushort port, int peers, bool accel, FlowRole role)
    {
        if (port == 0) return "—";
        // 加速器隧道：报的是「经加速」，别把它当对手/服务器
        if (role == FlowRole.Relay) return "加速隧道";
        string baseMode;
        if (ServerPorts.Contains(port)) baseMode = "服务器";
        else if (SpeedTestPorts.Contains(port) && peers >= 3) baseMode = "测速中";
        else
        {
            string? region = SafeRegion(ip);
            baseMode = region is not null && !region.Contains("附近", StringComparison.Ordinal) ? "服务器" : "P2P";
        }
        return accel && baseMode != "测速中" ? baseMode + "（经加速）" : baseMode;
    }

    private string? SafeRegion(string ip)
    {
        try { return RegionLookup?.Invoke(ip); } catch { return null; }
    }

    /// <summary>
    /// 收尾一场：算最小/平均/最大/P95/抖动/丢包/时长 → 写 matchlog.csv、推一条小结给界面。
    /// 探测样本不足 5 个（刚连上就断/只是测速）不当作一场，直接丢掉，避免刷出一堆 0 秒对局。
    /// </summary>
    private void FinishSession()
    {
        var s = _session;
        _session = null;
        if (s is null || s.Ok + s.Fail == 0) return;
        // 至少 5 个样本且至少 5 秒：STUN 连接检查、加速器心跳这种短流不进「最近对局」
        if (s.Samples.Count < 5 || (DateTime.Now - s.StartedAt).TotalSeconds < 5) return;
        if (s.Ok == 0) return;   // 一局里没拿到任何有效延迟样本：不写「延迟 -1/丢包 100%」这种垃圾行

        int duration = (int)Math.Max(0, (DateTime.Now - s.StartedAt).TotalSeconds);
        int avg = s.Ok == 0 ? -1 : (int)Math.Round((double)s.Sum / s.Ok);
        var okSamples = s.Samples.Where(v => v >= 0).OrderBy(v => v).ToArray();
        int p95 = okSamples.Length == 0 ? -1 : okSamples[(int)Math.Min(okSamples.Length - 1, Math.Floor(okSamples.Length * 0.95))];
        int jitter = s.Jitters.Count == 0 ? -1 : (int)Math.Round(s.Jitters.Average());
        double loss = 100.0 * s.Fail / (s.Ok + s.Fail);
        string region = SafeRegion(s.Remote.Split(':')[0]) ?? "未知区域";
        string grade = GradeOf(avg, jitter, loss, s.Fail, s.Ok);

        int serverRtt = s.ServerRtts.Count == 0 ? -1 : (int)Math.Round(s.ServerRtts.Average());
        var summary = new MatchSummary(s.StartedAt, s.Mode, s.Remote, region,
            s.Min, avg, s.Max, p95, jitter, loss, s.Samples.Count, duration, grade, s.Samples.ToArray(),
            serverRtt, BarsFromMs(serverRtt));

        try
        {
            MatchLog.Append(s.StartedAt, s.Mode, s.Remote, region, s.Min, avg, s.Max, loss, duration,
                p95, jitter, s.Samples.Count, grade, BarsFromMs(avg), s.TcpingAvg);
        }
        catch { }

        lock (_recent)
        {
            _recent.Insert(0, summary);
            while (_recent.Count > 10) _recent.RemoveAt(_recent.Count - 1);
        }
        try { MatchEnded?.Invoke(summary); } catch { }
    }

    /// <summary>一句话评级：先看丢包，再看平均延迟，最后看抖动（挂机党最怕的是突然的尖峰）。</summary>
    private static string GradeOf(int avg, int jitter, double loss, int fail, int ok)
    {
        if (ok == 0) return "无有效样本";
        if (loss >= 10) return "很差（严重丢包）";
        if (avg < 0) return "无延迟数据";
        if (loss >= 3) return "较差（有丢包）";
        if (jitter >= 40) return "抖动大";
        if (avg <= 60 && jitter <= 10) return "优秀";
        if (avg <= 90 && jitter <= 18) return "良好";
        if (avg <= 130) return "一般";
        return "较差";
    }

    // ---------------- 真延迟：到对局地域服务器的实测 RTT ----------------

    /// <summary>
    /// 并行 ping 候选服务器（ICMP，600ms 超时），取最快的那台当作「你这条线到该地域的延迟」。
    /// 这是与游戏内天线格最接近的可测量：格子本身取自游戏对 40 个地域的测速结果，
    /// 我们测同一批 IP、同一秒级节奏，换算成本机对应的格数。
    /// 注意：加速器只接管游戏进程的流量，这里的 ICMP 走的是系统直连——开加速时这是「关掉加速后你会得到的数」，
    /// 界面会同时列出加速节点那段（DelayMs），两者别混。
    /// </summary>
    private async Task ProbeServersAsync(CancellationToken ct)
    {
        List<(string Ip, string Label)> targets;
        try { targets = ServerTargetsProvider?.Invoke()?.ToList() ?? new(); }
        catch { targets = new(); }

        // 直连对局时，对局服务器的 IP 本身最权威，优先放进候选
        if (_primaryRole == FlowRole.Game && _remoteIp.Length > 0 &&
            !targets.Any(t => t.Ip == _remoteIp))
            targets.Insert(0, (_remoteIp, "对局服务器"));

        if (targets.Count == 0) { _serverRttBest = -1; _serverBestLabel = ""; return; }

        var jobs = targets.Take(16).Select(t => Task.Run(() =>
        {
            int ms = NetworkProbe.IcmpPing(t.Ip, 600);
            if (ms >= 0) _serverRtt[t.Ip] = ms;
            else _serverRtt.TryRemove(t.Ip, out _);
            return (t.Ip, t.Label, ms);
        }, ct)).ToArray();

        var results = await Task.WhenAll(jobs);
        var best = results.Where(r => r.ms >= 0).OrderBy(r => r.ms).FirstOrDefault();
        if (best.Ip is not null) { _serverRttBest = best.ms; _serverBestLabel = best.Label; }
        else if (_serverRttBest < 0) { _serverBestLabel = ""; }
    }

    /// <summary>
    /// 延迟 → 图表格数（社区实测锚点：到 大阪 70ms ≈ 3 格；5 格约 ≤40ms）。分段线性，够用且可解释。
    /// </summary>
    private static double BarsFromMs(int ms) => ms < 0 ? -1 : ms switch
    {
        <= 25 => 5.0,
        <= 40 => 4.8,
        <= 55 => 4.5,
        <= 70 => 4.0,
        <= 90 => 3.5,
        <= 110 => 3.0,
        <= 150 => 2.0,
        <= 220 => 1.5,
        _ => 1.0,
    };

    // ---------------- 每秒采样 ----------------

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        long prevTick = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RefreshGamePorts();
                RefreshKnownServers();
                long now = Environment.TickCount64;
                // 真延迟（服务器实测）每 3 秒刷一次：ICMP 十几个目标，串行会拖慢 1Hz 的主循环
                if (EnableServerProbe && now - _serverProbeAt > 3000)
                {
                    _serverProbeAt = now;
                    try { await ProbeServersAsync(ct); } catch { }
                }
                int elapsed = (int)(now - prevTick);
                prevTick = now;
                SampleFlows(elapsed);

                // 主连接 5 秒没有包 → 这场比赛结束了，别再 ping 已死的对端（v7.0.1 丢包率虚高的根因）
                if (_remoteIp.Length > 0 && _primary is not null &&
                    now - Math.Max(_primary.LastUpTicks, _primary.LastDownTicks) > RemoteStaleMs)
                {
                    if (_session is not null) FinishSession();
                    ResetRemote();
                }

                var accelerators = UdpSocketMap.RunningAccelerators();
                bool accel = accelerators.Count > 0;
                int jitter = JitterOf(_primary);
                bool alive = _remoteIp.Length > 0;
                // 延迟只认「游戏自己算的那个数」（读客户端内存）。拿不到就是 -1 → 界面显示「—」，
                // 绝不再拿加速节点/ICMP 直连的数冒充（用户 2026-11-12 明确要求）。
                // 延迟阶梯：游戏客户端自己的数（读内存）→ tcping → STUN → 链路 ICMP。
                // 拿不到游戏内就退回真实可测的下一档，并在 DelaySource 里写清是哪一档（界面据此标注）。
                int ms = await Task.Run(() => _ping.Sample(), ct);
                string src = ms >= 0 ? "游戏内" : "";
                if (ms < 0 && _tcpingMs >= 0) { ms = _tcpingMs; src = "tcping"; }
                if (ms < 0 && alive)
                {
                    int stun = StunRttOf();
                    if (stun >= 0) { ms = stun; src = "STUN"; }
                }
                if (ms < 0 && alive)
                {
                    string ip = _remoteIp;
                    int icmp = await Task.Run(() => NetworkProbe.IcmpPing(ip, ProbeTimeoutMs), ct);
                    if (icmp >= 0) { ms = icmp; src = _primaryRole == FlowRole.Relay ? "链路(节点)" : "链路ICMP"; }
                }
                if (ms >= 0) PushProbe(ms);

                // 双向探测史（xjbt 的 '.'/'X'/'_'）：这一秒里上行/下行有没有包
                if (alive && _primary is not null)
                {
                    bool up = _primary.UpPps > 0, down = _primary.DownPps > 0;
                    _direction.Enqueue(up && down ? '.' : up ? 'X' : down ? '_' : ' ');
                    while (_direction.Count > 30) _direction.Dequeue();
                }

                // 服务器局补一路 TCP 探测（tcping）：ICMP 常被对端丢弃，TCP 建连更能反映「你→服务器」
                if (alive && _primaryRole == FlowRole.Game && ServerPorts.Contains(_remotePort) && now % 2 == 0)
                {
                    string ip = _remoteIp;
                    ushort port = _remotePort == 5736 ? (ushort)5736 : (ushort)5736;
                    _tcpingMs = await Task.Run(() => NetworkProbe.TcpLatency(ip, port, 500), ct);
                    if (_tcpingMs < 0)
                        _tcpingMs = await Task.Run(() => NetworkProbe.TcpLatency(ip, 443, 500), ct);
                }
                else if (!alive) _tcpingMs = -1;

                string mode = ModeFor(_remoteIp, _remotePort, Peers, accel, _primaryRole);
                string remoteKey = alive ? $"{_remoteIp}:{_remotePort}" : "";

                // 场次切分：远端换成另一个，或本场 10 秒没流量 → 收尾写 matchlog
                if (_session is not null)
                {
                    bool switched = remoteKey.Length > 0 && _session.Remote != remoteKey;
                    bool idle = now - _session.LastDataTicks > SessionIdleMs;
                    if (switched || idle) FinishSession();
                }
                // 只有「持续 3 秒、每秒 ≥8 包」的流量才算一场对局：
                // 否则连接检查的 STUN（3478）、加速器心跳这些都会变成一条假的「对局小结」
                int pps = (_primary?.UpPps ?? 0) + (_primary?.DownPps ?? 0);
                _matchPpsWarm = alive && pps >= 8 ? _matchPpsWarm + 1 : 0;
                if (_session is null && alive && ms >= 0 && _matchPpsWarm >= 3 && mode is not ("测速中" or "—"))
                    _session = new MatchSession { Remote = remoteKey, Mode = mode, LastDataTicks = now };

                if (alive && ms >= 0)
                {
                    _ok++;
                    _min = _min < 0 ? ms : Math.Min(_min, ms);
                    max = Math.Max(max, ms);
                    sum += ms;
                    _curve.Enqueue(ms);
                }
                else if (alive) _curve.Enqueue(-1);
                while (_curve.Count > 60) _curve.Dequeue();

                if (_session is not null)
                {
                    _session.LastDataTicks = alive ? now : _session.LastDataTicks;
                    if (ms >= 0)
                    {
                        _session.Ok++;
                        _session.Sum += ms;
                        _session.Min = _session.Min < 0 ? ms : Math.Min(_session.Min, ms);
                        _session.Max = Math.Max(_session.Max, ms);
                    }
                    else _session.Fail++;
                    if (_session.Samples.Count < 3600)   // 最多记 1 小时（1Hz），防止挂一整天把内存吃掉
                    {
                        _session.Samples.Add(ms);
                        if (jitter >= 0) _session.Jitters.Add(jitter);
                        if (_serverRttBest >= 0 && _session.ServerRtts.Count < 3600)
                            _session.ServerRtts.Add(_serverRttBest);
                        if (_tcpingMs >= 0) _session.TcpingAvg = _tcpingMs;
                    }
                }

                double loss = LossOf();
                // 没有游戏内数据时，「丢包」用双向探测史算：只发不收（X）占比 = 你→服务器方向断流率
                if (loss < 0 && _direction.Count >= 5)
                {
                    int x = _direction.Count(c => c == 'X');
                    loss = 100.0 * x / _direction.Count;
                }
                string note = NoteFor(accel, accelerators, alive);
                string state = !alive ? "等待比赛（未发现游戏 UDP 连接）"
                    : mode == "测速中" ? "匹配前测速（未进比赛）"
                    : "监控中 · " + mode;

                var snap = new ConnSnapshot(
                    state,
                    ms,
                    loss,
                    alive ? $"{_remoteIp}:{_remotePort}" : "—",
                    alive ? SafeRegion(_remoteIp) ?? "未知区域" : "—",
                    _min < 0 ? -1 : _min,
                    _ok == 0 ? -1 : (int)((double)sum / _ok),
                    max,
                    _curve.ToArray(),
                    mode,
                    jitter,
                    _primary?.UpPps ?? 0,
                    _primary?.DownPps ?? 0,
                    _primary?.UpKbps ?? 0,
                    _primary?.DownKbps ?? 0,
                    Peers,
                    note,
                    StunRttOf(),
                    _primaryRole == FlowRole.Relay,
                    _serverRttBest,
                    _serverBestLabel,
                    BarsFromMs(_serverRttBest),
                    _ping.Status,
                    _tcpingMs,
                    new string(_direction.ToArray()),
                    BarsFromMs(ms),
                    SpeedTestText(),
                    src);
                Last = snap;
                Updated?.Invoke(snap);
            }
            catch { }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    /// <summary>
    /// 记录一次探测并返回滚动丢包率。-1 = 不可测：
    /// 该远端连一次 ICMP 都没回过（P2P 对手/NAT 后面很常见），这时算「丢包」纯属误导。
    /// </summary>
    private void PushProbe(int ms)
    {
        _probeCount++;
        if (ms >= 0) _peerRepliesIcmp = true;
        _probeWindow.Enqueue(ms >= 0);
        while (_probeWindow.Count > ProbeWindow) _probeWindow.Dequeue();
    }

    private double LossOf()
    {
        if (_probeWindow.Count == 0) return -1;
        if (!_peerRepliesIcmp) return -1;   // 从来没回过 → 不可测，不是 100% 丢包
        int bad = _probeWindow.Count(v => !v);
        return 100.0 * bad / _probeWindow.Count;
    }

    private string NoteFor(bool accel, List<string> accelerators, bool alive)
    {
        if (_primaryRole == FlowRole.Relay)
            return $"对局流量走加速隧道（{string.Join("/", _relayNames.Count > 0 ? _relayNames : accelerators)}）：抓到的远端是加速节点，"
                 + "延迟只是「你到节点」这一段，不是对手延迟；判卡不卡看抖动与上下行 pps。想要真实对局延迟就关掉加速让流量走直连。";
        if (accel)
            return $"检测到加速器（{string.Join("/", accelerators)}）：游戏流量由加速驱动接管，抓到的对端地址不一定是真实落地节点，"
                 + "ICMP 延迟走的是运营商直连路径、和游戏内实际延迟可能不是一回事——要判断卡不卡，看下面的抖动与上下行 pps，"
                 + "或者对着加速器里的节点延迟看。";
        if (Interlocked.Read(ref _seenUdp) == 0)
            return "抓包没有收到任何 UDP：网卡可能选错了（加速器/VPN 虚拟网卡）或需要以管理员运行本程序。";
        if (Interlocked.Read(ref _seenGameUdp) == 0 && _gamePorts.Count > 0)
            return "抓到了 UDP，但没有一包属于 eFootball.exe 的端口——游戏还没开始联网，或流量全走加速通道。";
        if (_gameProcNote.Length > 0) return _gameProcNote;
        if (alive && !_peerRepliesIcmp && _probeCount >= 5)
            return "对端不回 ICMP（P2P 对手常见的路由器设置）：延迟取不到，丢包率不可测——改看下面的上下行 pps 与抖动。";
        return "";
    }

    public void Dispose() => Stop();
}
