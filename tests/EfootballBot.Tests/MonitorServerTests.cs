using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 局域网监控页回归：静态资源（gzip/ETag/304）、SSE 首帧、token 权限。
/// 这些都是「手机端能不能用」的关键路径，改页面时最容易被无声弄坏。
/// </summary>
public sealed class MonitorServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static MonitorServer Start(string token, out string baseUrl)
    {
        int port = FreePort();
        var cfg = new MonitorConfig { Enabled = true, Port = port, Token = token };
        var logs = new List<MonitorLogRow> { new("21:12:04", "OK", "结算页识别：比赛完成") };
        var events = new List<MonitorEventRow> { new(DateTime.Now, "比赛完成", "第 13 轮 2-0 胜") };

        var server = new MonitorServer(
            cfg,
            () => new MonitorStatus(true, false, "我的联赛 · 第 14 轮", 37, 12, 3, "2:41:07", "赛后结算页", "", DateTime.Now),
            _ => Task.CompletedTask,
            (_, _) => { },
            _ => logs,
            () => events);
        server.Start();
        baseUrl = $"http://localhost:{port}/";
        return server;
    }

    private static HttpClient Client()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    [Fact]
    public async Task Page_Is_Gzip_Etagged_And_Revalidates_With_304()
    {
        using var server = Start("t0ken", out var url);
        // 不走自动解压：要亲眼确认服务器真的发的是 gzip（自动解压会把 Content-Encoding 摘掉）
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        using var gzReq = new HttpRequestMessage(HttpMethod.Get, url);
        gzReq.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var first = await http.SendAsync(gzReq);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("gzip", first.Content.Headers.ContentEncoding.Single());

        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var raw = await first.Content.ReadAsByteArrayAsync();
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        Assert.Contains("挂机监控", html);
        // 页面本体来自嵌入资源，脚本走 /assets：旧版内联字符串里的东西不该再出现
        Assert.Contains("/assets/monitor.js", html);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var second = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Theory]
    [InlineData("monitor.css", "text/css")]
    [InlineData("monitor.js", "javascript")]
    public async Task Assets_Are_Served_With_Correct_Type(string file, string expectedType)
    {
        using var server = Start("t0ken", out var url);
        using var http = Client();

        using var res = await http.GetAsync(url + "assets/" + file);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(expectedType, res.Content.Headers.ContentType!.MediaType);
        Assert.True(res.Headers.ETag is not null);

        var body = await res.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public async Task Stream_Pushes_Snapshot_Immediately()
    {
        using var server = Start("t0ken", out var url);
        using var http = Client();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var res = await http.GetAsync(url + "api/stream?t=t0ken", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);

        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.True(read > 0, "SSE 首帧没有数据");

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        Assert.StartsWith("data: ", text);
        using var doc = JsonDocument.Parse(text[6..].Trim());
        Assert.True(doc.RootElement.GetProperty("status").GetProperty("running").GetBoolean());
        Assert.Equal("我的联赛 · 第 14 轮", doc.RootElement.GetProperty("status").GetProperty("mode").GetString());
        Assert.True(doc.RootElement.GetProperty("logs").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Commands_Require_Token()
    {
        using var server = Start("t0ken", out var url);
        using var http = Client();

        using var denied = await http.GetAsync(url + "api/pause");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var allowed = await http.GetAsync(url + "api/pause?t=t0ken");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }
}
