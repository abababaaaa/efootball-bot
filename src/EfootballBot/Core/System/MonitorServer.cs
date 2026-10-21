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

/// <summary>
/// 局域网监控页（默认关闭）：手机/平板与电脑同一 WiFi 时打开
/// <c>http://电脑IP:端口/?t=token</c> 即可看运行状态，并暂停/继续/停止/单步。
///
/// 设计约束：
/// - 只监听本机所有网卡，不向外网注册任何东西，不上传任何数据；
/// - 有 token 才能执行操作（无 token 时页面只读）；
/// - 起不来（端口占用 / 无 URL 权限）只记一条日志，绝不影响挂机。
/// </summary>
public sealed class MonitorServer : IDisposable
{
    private readonly MonitorConfig _cfg;
    private readonly Func<MonitorStatus> _status;
    private readonly Func<string, Task> _command;
    private readonly Action<string, LogLevel>? _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Url { get; private set; } = "";

    /// <param name="command">操作回调：pause / resume / stop / step。</param>
    public MonitorServer(MonitorConfig cfg, Func<MonitorStatus> status, Func<string, Task> command,
        Action<string, LogLevel>? log = null)
    {
        _cfg = cfg;
        _status = status;
        _command = command;
        _log = log;
    }

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
                        canOperate,
                    }));
                    return;
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
        <script>
          const T = q(); function q(){const u=new URL(location.href);return u.searchParams.get('t')||''}
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
          async function act(c){
            const r = await fetch('/api/'+c + (T ? '?t='+encodeURIComponent(T) : ''));
            if(!r.ok) document.getElementById('err').textContent = '操作被拒绝（token 或状态不允许）';
            tick();
          }
          tick(); setInterval(tick, 3000);
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
