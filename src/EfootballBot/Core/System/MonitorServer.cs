using System.IO;
using System.Net;
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
                    await WriteAsync(ctx, 200, "text/html; charset=utf-8", Html(token, canOperate));
                    return;
                case "/api/status":
                    var s = _status();
                    await WriteAsync(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new
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
                        lan = LanUrl(),
                        canOperate,
                    }));
                    return;
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
            _log?.Invoke($"监控页请求处理失败：{ex.Message}", LogLevel.Debug);
            try { ctx.Response.Abort(); } catch { }
        }
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

    private string Html(string token, bool canOperate)
    {
        string q = string.IsNullOrEmpty(token) ? "" : $"?t={Uri.EscapeDataString(token)}";
        string op = canOperate ? "" : "disabled";
        return $$"""
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>挂机监控</title>
        <style>
          body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;background:#12151c;color:#e6e9ef;margin:0;padding:18px}
          .card{background:#1b202b;border-radius:14px;padding:16px;margin-bottom:12px}
          h1{font-size:17px;margin:0 0 12px}
          .row{display:flex;justify-content:space-between;padding:5px 0;font-size:14px;border-bottom:1px solid #262c3a}
          .row:last-child{border:0}
          .k{color:#9aa4b2}.v{font-weight:600}
          .dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px}
          button{margin:6px 8px 0 0;padding:10px 16px;border-radius:10px;border:1px solid #2f3949;
                 background:#232b39;color:#e6e9ef;font-size:14px}
          button:disabled{opacity:.35}
          #err{color:#ff9a9a;font-size:13px;margin-top:6px}
          .muted{color:#7b8696;font-size:12px;margin-top:10px}
          #events{font-size:13px;max-height:260px;overflow-y:auto}
          #events .ev{padding:4px 0;border-bottom:1px solid #262c3a;line-height:1.45}
          #events .ev:last-child{border:0}
          #log{font-family:Consolas,Menlo,monospace;font-size:11.5px;line-height:1.55;
               max-height:340px;overflow-y:auto;white-space:pre-wrap;word-break:break-all}
          .lv-OK{color:#3ddc84}.lv-Warn{color:#ffd166}.lv-Error{color:#ff9a9a}
          .ev-比赛完成{color:#3ddc84}.ev-关键比赛{color:#6cb6ff}.ev-球员满级{color:#ffd166}
        </style></head><body>
        <div class="card"><h1>eFootball 挂机监控</h1>
          <div class="row"><span class="k">状态</span><span class="v" id="st">—</span></div>
          <div class="row"><span class="k">场景</span><span class="v" id="mode">—</span></div>
          <div class="row"><span class="k">场次</span><span class="v" id="m">—</span></div>
          <div class="row"><span class="k">关键场次</span><span class="v" id="km">—</span></div>
          <div class="row"><span class="k">用时</span><span class="v" id="el">—</span></div>
          <div class="row"><span class="k">感知</span><span class="v" id="sc">—</span></div>
        </div>
        <div class="card">
          <button id="bp" onclick="act('pause')" {{op}}>暂停</button>
          <button onclick="act('resume')" {{op}}>继续</button>
          <button onclick="act('step')" {{op}}>单步</button>
          <button onclick="act('stop')" {{op}}>停止</button>
          <div id="err"></div>
          <div class="muted">操作需带 token 打开本页；页面每 3 秒自动刷新。</div>
        </div>
        <div class="card"><h1>比赛 / 满级动态</h1>
          <div id="events">—</div>
          <div class="muted">比赛完成与球员满级事件（最近 50 条）。</div>
        </div>
        <div class="card"><h1>实时日志</h1>
          <div id="log">—</div>
          <div class="muted">最近 100 条运行日志，每 3 秒刷新。</div>
        </div>
        <script>
          const T = q(); function q(){const u=new URL(location.href);return u.searchParams.get('t')||''}
          function esc(s){const d=document.createElement('div');d.textContent=s==null?'':String(s);return d.innerHTML}
          async function tick(){
            try{
              const r = await fetch('/api/status' + (T ? '?t='+encodeURIComponent(T) : ''));
              const s = await r.json();
              document.getElementById('st').innerHTML =
                '<span class="dot" style="background:'+(s.running?(s.paused?'#ffd166':'#3ddc84'):'#777')+'"></span>'+
                (s.running?(s.paused?'已暂停':'运行中'):'未运行');
              document.getElementById('mode').textContent = s.mode || '—';
              document.getElementById('m').textContent = s.matches;
              document.getElementById('km').textContent = s.keyMatches;
              document.getElementById('el').textContent = s.elapsed || '—';
              document.getElementById('sc').textContent = s.lastScreen || '—';
              document.getElementById('err').textContent = s.lastError ? ('最近错误：'+s.lastError) : '';
            }catch(e){document.getElementById('st').textContent='连接失败'}
          }
          async function tickEvents(){
            try{
              const r = await fetch('/api/events' + (T ? '?t='+encodeURIComponent(T) : ''));
              const d = await r.json();
              document.getElementById('events').innerHTML = (d.rows||[]).map(e =>
                '<div class="ev"><span class="ev-'+esc(e.kind)+'">['+esc(e.t)+'] '+esc(e.kind)+'</span> '+esc(e.msg)+'</div>'
              ).join('') || '暂无动态';
            }catch(e){}
          }
          async function tickLog(){
            try{
              const r = await fetch('/api/log?n=100' + (T ? '&t='+encodeURIComponent(T) : ''));
              const d = await r.json();
              const el = document.getElementById('log');
              const nearBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 30;
              el.innerHTML = (d.rows||[]).map(l =>
                '<div class="lv-'+esc(l.lv)+'">['+esc(l.t)+'] '+esc(l.msg)+'</div>').join('');
              if (nearBottom) el.scrollTop = el.scrollHeight;
            }catch(e){}
          }
          async function act(c){
            const r = await fetch('/api/'+c + (T ? '?t='+encodeURIComponent(T) : ''));
            if(!r.ok) document.getElementById('err').textContent = '操作被拒绝（token 或状态不允许）';
            tick();
          }
          tick(); tickEvents(); tickLog();
          setInterval(tick, 3000); setInterval(tickEvents, 3000); setInterval(tickLog, 3000);
        </script></body></html>
        """;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
    }
}
