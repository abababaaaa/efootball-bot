using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.System;

/// <summary>监控页对外暴露的运行状态快照（由 GUI 侧填充）。</summary>
public sealed record MonitorStatus(
    bool Running,
    bool Paused,
    string Mode,
    int Matches,
    int KeyMatches,
    int PlayersMaxed,
    string Elapsed,
    string LastScreen,
    string LastError,
    DateTime At);

/// <summary>监控页「实时日志」行（由 GUI 侧回放缓存填充）。</summary>
public sealed record MonitorLogRow(string Time, string Level, string Text);

/// <summary>监控页「比赛/满级动态」行（由 GUI 侧按日志关键词累积）。</summary>
public sealed record MonitorEventRow(DateTime At, string Kind, string Text);

/// <summary>
/// 局域网监控页（默认关闭）：手机/平板与电脑同一 WiFi 时打开
/// <c>http://电脑IP:端口/?t=token</c> 即可看运行状态与实时日志、比赛/满级动态，
/// 并暂停/继续/停止/单步。微信内置浏览器直接打开本页即可（手机与电脑同一 WiFi 时）。
///
/// 页面本体是嵌入资源（Assets\web\monitor.html / .css / .js），不再在 C# 里拼字符串：
/// - 静态资源带 gzip + ETag + 304，重复打开只走一次条件请求；
/// - /api/stream 是 SSE 长连接（1 秒一帧快照），比三路 3 秒轮询省电、实时性更好；
/// - SSE 不通时页面自动回落原来的 /api/status|log|events 轮询，行为与旧版一致。
///
/// 设计约束：
/// - 只监听本机所有网卡，不向外网注册任何东西，不上传任何数据；
/// - 有 token 才能执行操作（无 token 时页面只读；日志/动态与状态一样只读）；
/// - 起不来（端口占用 / 无 URL 权限）只记一条日志，绝不影响挂机。
/// </summary>
public sealed class MonitorServer : IDisposable
{
    private readonly MonitorConfig _cfg;
    private readonly Func<MonitorStatus> _status;
    private readonly Func<string, Task> _command;
    private readonly Func<int, IReadOnlyList<MonitorLogRow>>? _logProvider;
    private readonly Func<IReadOnlyList<MonitorEventRow>>? _eventsProvider;
    private readonly Action<string, LogLevel>? _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, Stream> _streams = new();

    /// <summary>SSE 推送间隔：1 秒（原来三路轮询是 3 秒，这里更实时且只占一条连接）。</summary>
    private static readonly TimeSpan StreamInterval = TimeSpan.FromSeconds(1);

    public string Url { get; private set; } = "";

    /// <param name="command">操作回调：pause / resume / stop / step。</param>
    /// <param name="logProvider">按条数取最近日志（GUI 回放缓存）；空 = 页面不提供日志区。</param>
    /// <param name="eventsProvider">取比赛/满级动态列表；空 = 页面不提供动态区。</param>
    public MonitorServer(MonitorConfig cfg, Func<MonitorStatus> status, Func<string, Task> command,
        Action<string, LogLevel>? log = null,
        Func<int, IReadOnlyList<MonitorLogRow>>? logProvider = null,
        Func<IReadOnlyList<MonitorEventRow>>? eventsProvider = null)
    {
        _cfg = cfg;
        _status = status;
        _command = command;
        _log = log;
        _logProvider = logProvider;
        _eventsProvider = eventsProvider;
    }

    private bool _allInterfaces;

    public void Start()
    {
        if (_listener is not null) return;
        int port = _cfg.Port is > 0 and < 65536 ? _cfg.Port : 18321;
        var listener = new HttpListener();
        // 先试全网卡（需要管理员/URL ACL），失败退回仅本机
        string[] prefixes = { $"http://+:{port}/", $"http://localhost:{port}/" };
        foreach (var prefix in prefixes)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                Url = prefix.Replace("+", Environment.MachineName);
                _allInterfaces = prefix.StartsWith("http://+:");
                _listener = listener;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));
                _log?.Invoke($"监控页已启动：{Url}（局域网同网段可访问；操作需 token）", LogLevel.Success);
                return;
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                _log?.Invoke($"监控页监听 {prefix} 失败：{ex.Message}", LogLevel.Debug);
            }
        }
        _log?.Invoke("监控页启动失败（端口被占用或缺少 URL 权限），已跳过——不影响挂机", LogLevel.Warn);
    }

    /// <summary>本机局域网访问地址（UDP connect 探本机出口 IP，不发任何包）；仅全网卡监听时有意义。</summary>
    private string LanUrl()
    {
        if (!_allInterfaces) return "";
        int port = _cfg.Port is > 0 and < 65536 ? _cfg.Port : 18321;
        try
        {
            using var s = new global::System.Net.Sockets.Socket(
                global::System.Net.Sockets.AddressFamily.InterNetwork,
                global::System.Net.Sockets.SocketType.Dgram, global::System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            if (s.LocalEndPoint is IPEndPoint ep && ep.Address.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork)
                return $"http://{ep.Address}:{port}/";
        }
        catch { }
        return "";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) { return; }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string token = ctx.Request.QueryString["t"] ?? ctx.Request.Headers["X-Token"] ?? "";
            bool canOperate = _cfg.Token.Length == 0 || token == _cfg.Token;

            switch (path)
            {
                case "/":
                    await AssetAsync(ctx, "monitor.html", "text/html; charset=utf-8", cacheSeconds: 0);
                    return;
                case "/assets/monitor.css":
                    await AssetAsync(ctx, "monitor.css", "text/css; charset=utf-8");
                    return;
                case "/assets/monitor.js":
                    await AssetAsync(ctx, "monitor.js", "application/javascript; charset=utf-8");
                    return;
                case "/api/stream":
                    await StreamAsync(ctx, canOperate);
                    return;
                case "/api/status":
                {
                    var s0 = _status();
                    await WriteAsync(ctx, 200, "application/json; charset=utf-8",
                        JsonSerializer.Serialize(new
                        {
                            running = s0.Running,
                            paused = s0.Paused,
                            mode = s0.Mode,
                            matches = s0.Matches,
                            keyMatches = s0.KeyMatches,
                            playersMaxed = s0.PlayersMaxed,
                            elapsed = s0.Elapsed,
                            lastScreen = s0.LastScreen,
                            lastError = s0.LastError,
                            at = s0.At.ToString("yyyy-MM-dd HH:mm:ss"),
                            lan = LanUrl(),
                            canOperate,
                        }));
                    return;
                }
                case "/api/log":
                {
                    int n = int.TryParse(ctx.Request.QueryString["n"], out var reqN)
                        ? Math.Clamp(reqN, 1, 300) : 100;
                    var rows = _logProvider?.Invoke(n) ?? Array.Empty<MonitorLogRow>();
                    await WriteAsync(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new
                    {
                        rows = rows.Select(r => new { t = r.Time, lv = r.Level, msg = r.Text }),
                    }));
                    return;
                }
                case "/api/events":
                {
                    var rows = _eventsProvider?.Invoke() ?? Array.Empty<MonitorEventRow>();
                    await WriteAsync(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new
                    {
                        rows = rows.Select(r => new
                        {
                            t = r.At.ToString("HH:mm:ss"),
                            kind = r.Kind,
                            msg = r.Text,
                        }),
                    }));
                    return;
                }
                case "/api/pause":
                case "/api/resume":
                case "/api/stop":
                case "/api/step":
                    if (!canOperate)
                    {
                        await WriteAsync(ctx, 403, "text/plain; charset=utf-8", "需要 token");
                        return;
                    }
                    string cmd = path[5..];
                    await _command(cmd);
                    await WriteAsync(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                    return;
                default:
                    await WriteAsync(ctx, 404, "text/plain", "not found");
                    return;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"监控页请求处理失败（{ctx.Request.Url?.AbsolutePath}）：{ex.Message}", LogLevel.Debug);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // ---------------- SSE ----------------

    /// <summary>一帧快照（状态 + 最近 20 条动态 + 最近 100 条日志），整包 ≤ 数十 KB。</summary>
    private string Snapshot(bool canOperate)
    {
        var s = _status();
        var events = _eventsProvider?.Invoke() ?? Array.Empty<MonitorEventRow>();
        var logs = _logProvider?.Invoke(100) ?? Array.Empty<MonitorLogRow>();
        return JsonSerializer.Serialize(new
        {
            status = new
            {
                running = s.Running,
                paused = s.Paused,
                mode = s.Mode,
                matches = s.Matches,
                keyMatches = s.KeyMatches,
                playersMaxed = s.PlayersMaxed,
                elapsed = s.Elapsed,
                lastScreen = s.LastScreen,
                lastError = s.LastError,
                at = s.At.ToString("yyyy-MM-dd HH:mm:ss"),
                canOperate,
            },
            events = events.Take(20).Select(r => new { t = r.At.ToString("HH:mm:ss"), kind = r.Kind, msg = r.Text }),
            logs = logs.Select(r => new { t = r.Time, lv = r.Level, msg = r.Text }),
        });
    }

    private async Task StreamAsync(HttpListenerContext ctx, bool canOperate)
    {
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache, no-transform";
        res.SendChunked = true;

        var id = Guid.NewGuid();
        var stream = res.OutputStream;
        _streams[id] = stream;
        try
        {
            await WriteEventAsync(stream, Snapshot(canOperate));
            var ct = _cts?.Token ?? CancellationToken.None;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(StreamInterval, ct);
                await WriteEventAsync(stream, Snapshot(canOperate));
            }
        }
        catch
        {
            // 客户端断开（切后台/锁屏/关页面）走这里：正常路径，不记错误
        }
        finally
        {
            _streams.TryRemove(id, out _);
            try { res.Close(); } catch { }
        }
    }

    private static async Task WriteEventAsync(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    // ---------------- 静态资源 ----------------

    private sealed record Asset(byte[] Raw, byte[] Gz, string ETag);

    private static readonly ConcurrentDictionary<string, Asset> _assets = new();

    private static Asset LoadAsset(string name)
    {
        return _assets.GetOrAdd(name, key =>
        {
            var asm = Assembly.GetExecutingAssembly();
            using var res = asm.GetManifestResourceStream("EfootballBot.web." + key)
                      ?? throw new InvalidOperationException($"嵌入资源缺失：EfootballBot.web.{key}");
            using var rawMs = new MemoryStream();
            res.CopyTo(rawMs);
            var raw = rawMs.ToArray();
            using var gzMs = new MemoryStream();
            using (var gz = new GZipStream(gzMs, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            var etag = "\"" + Convert.ToHexString(SHA1.HashData(raw)).ToLowerInvariant()[..16] + "\"";
            return new Asset(raw, gzMs.ToArray(), etag);
        });
    }

    private static async Task AssetAsync(HttpListenerContext ctx, string name, string contentType, int cacheSeconds = 86400)
    {
        var asset = LoadAsset(name);

        if (ETagMatches(ctx.Request.Headers["If-None-Match"], asset.ETag))
        {
            ctx.Response.StatusCode = 304;
            ctx.Response.Headers["ETag"] = asset.ETag;
            ctx.Response.Close();
            return;
        }

        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = contentType;
        res.Headers["ETag"] = asset.ETag;
        res.Headers["Vary"] = "Accept-Encoding";
        res.Headers["Cache-Control"] = cacheSeconds > 0
            ? $"public, max-age={cacheSeconds}"
            : "no-cache";
        // 只对声明支持 gzip 的客户端压缩；其它（几乎只有 curl/脚本）拿原文
        var gzipOk = (ctx.Request.Headers["Accept-Encoding"] ?? "")
            .Contains("gzip", StringComparison.OrdinalIgnoreCase);
        var body = gzipOk ? asset.Gz : asset.Raw;
        if (gzipOk) res.Headers["Content-Encoding"] = "gzip";
        res.ContentLength64 = body.Length;
        await res.OutputStream.WriteAsync(body);
        res.OutputStream.Close();
    }

    /// <summary>
    /// If-None-Match 宽松比较：容忍 W/ 弱前缀、引号被中间层剥掉（curl/部分代理会）、多值列表。
    /// </summary>
    private static bool ETagMatches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrEmpty(ifNoneMatch)) return false;
        var target = etag.Trim('"');
        foreach (var part in ifNoneMatch.Split(','))
        {
            var t = part.Trim();
            if (t.StartsWith("W/", StringComparison.Ordinal)) t = t[2..];
            if (t.Trim('"') == target) return true;
        }
        return false;
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int code, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        foreach (var s in _streams.Values)
        {
            try { s.Close(); } catch { }
        }
        _streams.Clear();
        _listener = null;
    }
}
