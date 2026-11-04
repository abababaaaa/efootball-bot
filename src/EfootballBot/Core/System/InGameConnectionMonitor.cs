using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using SharpPcap;
using SharpPcap.LibPcap;

namespace EfootballBot.Core.System;

/// <summary>游戏内连接监控的一次快照（每秒刷新）。</summary>
public sealed record ConnSnapshot(
    string State,            // 等待比赛 / 监控中 / 数据停止 / 未启动
    int DelayMs,             // -1=未知
    double LossPct,
    string Remote,           // "ip:port"，未知时 "—"
    string Region,           // 中文地区或「未知区域」
    int MinMs,
    int AvgMs,
    int MaxMs,
    IReadOnlyList<int> Curve,
    string Mode = "—");      // 对战模式：服务器 / P2P / 测速中（端口判定，与 ETK 一致）

/// <summary>
/// 游戏内连接监控：用 Npcap（SharpPcap）抓本机 UDP 流量，从所有流里认出击游戏服务器的那条
/// （优先游戏端口 5735/50000/5521/3478，其次 2 秒窗口内包数最多的一条），然后每秒 ICMP ping
/// 远端算出延迟与丢包。参考 ETK「监控」页的打法，但延迟值来自真实 ping 而不是抓包时间差。
/// Npcap 未安装/未提权时 Start 返回失败原因，页面提示，不影响挂机。
/// </summary>
public sealed class InGameConnectionMonitor : IDisposable
{
    private static readonly ushort[] GamePorts = { 5735, 50000, 5521, 3478, 3479, 5742 };

    /// <summary>服务器模式端口（5735 为服务器 UDP 端口，5736 为 TCP 侧）；其余游戏端口=P2P。</summary>
    private static readonly ushort[] ServerPorts = { 5735, 5736 };

    /// <summary>匹配前的测速端口（xjbt forceP2P 屏蔽的同一组）；出现这些说明还在测速/匹配。</summary>
    private static readonly ushort[] SpeedTestPorts = { 5521, 10000, 30000, 50000 };

    private const int SessionIdleMs = 10_000; // 远端 10 秒无流量视为本场结束

    private ILiveDevice? _device;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly ConcurrentDictionary<string, FlowStat> _flows = new();
    private sealed class FlowStat
    {
        public int Count;
        public long LastTicks;
        public ushort LocalPort, RemotePort;
    }

    private string _remoteIp = "";
    private ushort _remotePort;
    private readonly Queue<int> _curve = new();       // 最近 60 个 ping 值（-1=失败）
    private int _ok, fail;
    private int _min = -1, max, sum;

    /// <summary>当前这一场的统计（远端切换/长时间空闲时收尾写 matchlog.csv）。</summary>
    private sealed class MatchSession
    {
        public DateTime StartedAt = DateTime.Now;
        public string Remote = "";
        public string Mode = "—";
        public long LastDataTicks;
        public int Min = -1, Max, Sum, Ok, Fail;
    }
    private MatchSession? _session;

    public bool Running { get; private set; }
    public event Action<ConnSnapshot>? Updated;

    /// <summary>地区查询：IP → 中文地区名（找不到返回 null）。</summary>
    public Func<string, string?>? RegionLookup { get; set; }

    /// <summary>启动抓包 + 每秒测速。count 个网卡里自动选第一个非回环 IPv4 网卡。</summary>
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
            ILiveDevice? pick = null;
            // 优先选承载本机出口 IP 的网卡（多网卡 / 虚拟显示器 / 加速器 TUN 环境下才不会抓空）
            string localIp = LocalIp();
            foreach (var d in devs.OfType<LibPcapLiveDevice>())
            {
                if (d.Loopback) continue;
                if (d.Addresses.Any(a => a.Addr?.ipAddress?.ToString() == localIp)) { pick = d; break; }
            }
            if (pick is null)
            {
                foreach (var d in devs.OfType<LibPcapLiveDevice>())
                {
                    if (d.Loopback || d.Addresses.Count == 0) continue;
                    if (d.Addresses.Any(a => a.Addr?.ipAddress?.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork))
                    { pick = d; break; }
                }
            }
            pick ??= devs.FirstOrDefault(d => !(d is LibPcapLiveDevice l) || !l.Loopback);
            if (pick is null) { error = "没有可用的 IPv4 抓包网卡"; return false; }

            pick.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.Promiscuous,
                Snaplen = 65535,
                ReadTimeout = 500,
            });
            pick.Filter = "udp";
            pick.OnPacketArrival += OnPacket;
            _device = pick;
            pick.StartCapture();

            _cts = new CancellationTokenSource();
            Running = true;
            _loop = Task.Run(() => PingLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            error = $"抓包启动失败：{ex.Message}（Npcap 需要以管理员身份运行本程序）";
            try { _device?.Close(); } catch { }
            _device = null;
            return false;
        }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(1500); } catch { }
        FinishSession();   // 停止时把最后一局也记进 matchlog
        try
        {
            if (_device is not null)
            {
                _device.OnPacketArrival -= OnPacket;
                _device.StopCapture();
                _device.Close();
            }
        }
        catch { }
        _device = null;
        _cts?.Dispose(); _cts = null; _loop = null;
        _flows.Clear();
        _remoteIp = "";
        _curve.Clear();
        _ok = fail = sum = max = 0; _min = -1;
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
            if ((span[off] >> 4) != 4) return;            // 只认 IPv4
            int ihl = (span[off] & 0x0F) * 4;
            if (ihl < 20 || span.Length < off + ihl + 8) return;
            if (span[off + 9] != 17) return;              // UDP
            var src = new IPAddress(span.Slice(off + 12, 4).ToArray()).ToString();
            var dst = new IPAddress(span.Slice(off + 16, 4).ToArray()).ToString();
            int u = off + ihl;
            ushort sp = (ushort)((span[u] << 8) | span[u + 1]);
            ushort dp = (ushort)((span[u + 2] << 8) | span[u + 3]);

            string localIp = LocalIp();
            string remoteIp; ushort localPort, remotePort;
            if (dst == localIp) { remoteIp = src; localPort = dp; remotePort = sp; }
            else { remoteIp = dst; localPort = sp; remotePort = dp; }
            if (remoteIp == localIp) return;
            if (localPort is 53 or 123 or 443 or 80) return; // 排除系统流量
            if (remotePort is 53 or 123) return;

            var key = remoteIp + ":" + remotePort;
            var f = _flows.GetOrAdd(key, _ => new FlowStat());
            Interlocked.Increment(ref f.Count);
            f.LastTicks = Environment.TickCount64;
            f.LocalPort = localPort;
            f.RemotePort = remotePort;
        }
        catch { }
    }

    private static int LinkHeaderOffset(ReadOnlySpan<byte> s)
    {
        // 以太网(14) / Linux cooked 不常见；回环 Null 头(4)。按首字节猜测：
        if (s.Length >= 14 && (s[12] << 8 | s[13]) is 0x0800) return 14;     // Ethernet IPv4
        if (s.Length >= 4 && s[0] == 2) return 4;                            // Null/Loopback
        if (s.Length >= 24 && (s[21] << 8 | s[22]) == 0x0800) return 24;     // raw IPv4 之类兜底
        if (s.Length >= 1 && (s[0] >> 4) == 4) return 0;                     // 裸 IP
        return -1;
    }

    private string? _localIp;
    private string LocalIp()
    {
        if (_localIp is not null) return _localIp;
        try
        {
            using var s = new global::System.Net.Sockets.Socket(
                global::System.Net.Sockets.AddressFamily.InterNetwork,
                global::System.Net.Sockets.SocketType.Dgram, global::System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            _localIp = ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { _localIp = "127.0.0.1"; }
        return _localIp;
    }

    /// <summary>选主连接：游戏端口优先，其次 3 秒窗口内包数最多。</summary>
    private void PickFlow()
    {
        long now = Environment.TickCount64;
        var candidates = _flows.Where(kv => now - kv.Value.LastTicks < 3000).ToList();
        if (candidates.Count == 0) return;

        var best = candidates
            .OrderByDescending(kv => GamePorts.Contains(kv.Value.RemotePort) ? 1 : 0)
            .ThenByDescending(kv => kv.Value.Count)
            .First();
        var (key, _) = (best.Key, best.Value);
        int idx = key.LastIndexOf(':');
        if (idx <= 0) return;
        _remoteIp = key[..idx];
        _remotePort = ushort.TryParse(key[(idx + 1)..], out var p) ? p : (ushort)0;
    }

    /// <summary>按远端端口判断对战模式（与 ETK/forceP2P 的端口口径一致）。</summary>
    private static string ModeFor(ushort port)
        => port == 0 ? "—"
         : ServerPorts.Contains(port) ? "服务器"
         : SpeedTestPorts.Contains(port) ? "测速中"
         : "P2P";

    /// <summary>把当前场次写进 matchlog.csv 并清空（会话结束/停止监控时调用）。</summary>
    private void FinishSession()
    {
        var s = _session;
        _session = null;
        if (s is null || s.Ok + s.Fail == 0) return;
        int duration = (int)Math.Max(0, (DateTime.Now - s.StartedAt).TotalSeconds);
        double loss = 100.0 * s.Fail / (s.Ok + s.Fail);
        MatchLog.Append(s.StartedAt, s.Mode, s.Remote,
            RegionLookup?.Invoke(s.Remote.Split(':')[0]) ?? "未知区域",
            s.Min, s.Ok == 0 ? -1 : (int)((double)s.Sum / s.Ok), s.Max, loss, duration);
    }

    // ---------------- 每秒测速 ----------------

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PickFlow();
                int ms = _remoteIp.Length > 0 ? NetworkProbe.IcmpPing(_remoteIp, 1000) : -1;
                bool alive = _remoteIp.Length > 0;
                long now = Environment.TickCount64;
                bool recent = _flows.Any(kv => now - kv.Value.LastTicks < 2000);
                string mode = ModeFor(_remotePort);
                string remoteKey = alive ? $"{_remoteIp}:{_remotePort}" : "";

                // 场次切分：远端换成另一个，或本场 10 秒没流量 → 收尾写 matchlog
                if (_session is not null)
                {
                    bool switched = remoteKey.Length > 0 && _session.Remote != remoteKey;
                    bool idle = now - _session.LastDataTicks > SessionIdleMs;
                    if (switched || idle) FinishSession();
                }
                if (_session is null && recent && remoteKey.Length > 0 && mode != "测速中")
                    _session = new MatchSession { Remote = remoteKey, Mode = mode, LastDataTicks = now };

                if (alive)
                {
                    _curve.Enqueue(ms);
                    if (_curve.Count > 60) _curve.Dequeue();
                    if (ms >= 0)
                    {
                        _ok++;
                        _min = _min < 0 ? ms : Math.Min(_min, ms);
                        max = Math.Max(max, ms);
                        sum += ms;
                    }
                    else fail++;
                }

                if (_session is not null)
                {
                    _session.LastDataTicks = recent ? now : _session.LastDataTicks;
                    if (ms >= 0)
                    {
                        _session.Ok++;
                        _session.Sum += ms;
                        _session.Min = _session.Min < 0 ? ms : Math.Min(_session.Min, ms);
                        _session.Max = Math.Max(_session.Max, ms);
                    }
                    else _session.Fail++;
                }

                string state = !alive ? "等待比赛（未发现游戏 UDP 连接）"
                    : !recent ? "连接空闲（数据停止）"
                    : mode == "测速中" ? "匹配前测速（未进比赛）"
                    : "监控中 · " + mode;

                double loss = _ok + fail == 0 ? 0 : 100.0 * fail / (_ok + fail);
                var snap = new ConnSnapshot(
                    state,
                    ms,
                    loss,
                    alive ? $"{_remoteIp}:{_remotePort}" : "—",
                    alive ? RegionLookup?.Invoke(_remoteIp) ?? "未知区域" : "—",
                    _min < 0 ? -1 : _min,
                    _ok == 0 ? -1 : (int)((double)sum / _ok),
                    max,
                    _curve.ToArray(),
                    mode);
                Updated?.Invoke(snap);
            }
            catch { }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    public void Dispose() => Stop();
}
