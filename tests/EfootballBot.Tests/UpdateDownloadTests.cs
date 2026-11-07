using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>更新下载候选链：纯函数（候选构建/SHA256 解析）+ 真 HTTP 环回行为
/// （TcpListener 手写最小响应，无 urlacl/管理员依赖）。覆盖：候选失败自动切换下一通道、
/// 失败后文件回卷（半截数据不得污染下一候选）、SHA256 匹配放行/不匹配弃用候选、
/// Content-Length 与实际不符的假 200 拦截。</summary>
public class UpdateDownloadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _target;

    public UpdateDownloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "updtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _target = Path.Combine(_dir, "download.bin");
        // 程序内置了真实主源地址（BuiltInPrimaryBaseUrl），测试要从「未配置」这个干净状态起步，
        // 否则候选链会多出主源一项、把「镜像 → GitHub」的断言弄错。需要主源的用例自己赋值。
        Updater.R2PublicBaseUrl = "";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable(Updater.PrimaryBaseUrlEnvVar, null);
        Updater.Configure(null); // 静态状态复位（主源 + 版本检查缓存），避免泄漏到其他用例
    }

    // ---------------- 候选构建（纯函数） ----------------

    [Fact]
    public void BuildDownloadUrls_镜像前缀在前_GitHub直连恒最后()
    {
        var urls = Updater.BuildDownloadUrls(new Version(2, 0, 7));
        string gh = "https://github.com/abababaaaa/efootball-bot/releases/download/v2.0.7/EfootballBot.exe";

        Assert.Equal(Updater.DownloadMirrorPrefixes.Length + 1, urls.Count);
        Assert.Equal(gh, urls[^1]); // 直连兜底必须最后
        for (int i = 0; i < Updater.DownloadMirrorPrefixes.Length; i++)
            Assert.Equal(Updater.DownloadMirrorPrefixes[i] + gh, urls[i]);
    }

    [Fact]
    public void BuildDownloadUrls_配置R2后_R2排第一_镜像居中_直连兜底()
    {
        Updater.R2PublicBaseUrl = "https://pub-abc123.r2.dev";
        var urls = Updater.BuildDownloadUrls(new Version(2, 0, 7, 1));
        string gh = "https://github.com/abababaaaa/efootball-bot/releases/download/v2.0.7.1/EfootballBot.exe";

        Assert.Equal("https://pub-abc123.r2.dev/EfootballBot.exe", urls[0]);
        Assert.Equal(gh, urls[^1]);
        Assert.Equal(Updater.DownloadMirrorPrefixes.Length + 2, urls.Count);
    }

    [Fact]
    public void BuildDownloadUrls_zip目标不带R2候选_R2只放exe()
    {
        Updater.R2PublicBaseUrl = "https://pub-abc123.r2.dev";
        var urls = Updater.BuildDownloadUrls(new Version(2, 0, 7), "efootball-bot-win-x64.zip");
        Assert.DoesNotContain(urls, u => u.Contains("r2.dev"));
    }

    // ---------------- 主源基址（配置化：环境变量 > config.json > 内置常量） ----------------

    [Theory]
    [InlineData("https://pub-abc123.r2.dev/", "https://pub-abc123.r2.dev")]
    [InlineData("  https://cdn.example.com/upd//  ", "https://cdn.example.com/upd")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("ftp://files.example.com", "")] // 非 http(s)：宁可不用也不要拼出打不开的地址
    [InlineData("pub-abc123.r2.dev", "")]       // 裸域名：缺协议，Uri 判为相对地址
    [InlineData("not a url", "")]
    public void 主源基址_归一化(string raw, string expected)
        => Assert.Equal(expected, Updater.NormalizeBaseUrl(raw));

    [Fact]
    public void 主源基址_环境变量优先于配置_配置优先于内置()
    {
        Environment.SetEnvironmentVariable(Updater.PrimaryBaseUrlEnvVar, null);

        Updater.Configure("https://cfg.example.com/");
        Assert.Equal("https://cfg.example.com", Updater.PrimaryBaseUrl);

        Updater.Configure(null); // 未配置 + 未内置 → 该通道关闭
        Assert.Equal(Updater.NormalizeBaseUrl(Updater.BuiltInPrimaryBaseUrl), Updater.PrimaryBaseUrl);

        Environment.SetEnvironmentVariable(Updater.PrimaryBaseUrlEnvVar, "https://env.example.com");
        Updater.Configure("https://cfg.example.com");
        Assert.Equal("https://env.example.com", Updater.PrimaryBaseUrl);
    }

    [Fact]
    public void 主源基址_通道描述展示真实生效域名()
    {
        Updater.Configure("https://pub-abc123.r2.dev");
        Assert.Equal("主源 pub-abc123.r2.dev → 加速镜像 → GitHub 直连", Updater.DescribeChannel());
    }

    [Theory]
    [InlineData("EfootballBot-6.1.2.exe", "EfootballBot-6.1.2.exe")]
    [InlineData(null, "EfootballBot.exe")]
    [InlineData("   ", "EfootballBot.exe")]
    [InlineData("../../evil.exe", "EfootballBot.exe")]   // 上跳路径
    [InlineData("sub/EfootballBot.exe", "EfootballBot.exe")] // 带目录
    [InlineData("https://evil.example.com/x.exe", "EfootballBot.exe")] // 整条 URL
    [InlineData("EfootballBot.zip", "EfootballBot.exe")] // 只认 exe
    public void 清单文件名_只接受纯文件名(string? raw, string expected)
        => Assert.Equal(expected, Updater.ResolveManifestFileName(raw));

    [Fact]
    public void 主源下载地址_用清单文件名而非固定名()
    {
        Updater.R2PublicBaseUrl = "https://pub-abc.r2.dev";
        var urls = Updater.BuildDownloadUrls(new Version(6, 1, 2), "EfootballBot-6.1.2.exe");

        Assert.Equal("https://pub-abc.r2.dev/EfootballBot-6.1.2.exe", urls[0]);
        Assert.EndsWith("releases/download/v6.1.2/EfootballBot-6.1.2.exe", urls[^1]);
    }

    [Fact]
    public async Task 主源清单说已最新_不再回退GitHub()
    {
        // 环回桩同时充当「主源清单」：清单版本 = 本机版本 → 结论必须是「已是最新」。
        // 若实现仍去问 GitHub，本机无外网/被墙时这里会超时或返回错误，测试即失败。
        string json = $"{{\"version\":\"{Updater.CurrentVersion}\",\"size\":1}}";
        var (url, loop) = StartStub(Encoding.UTF8.GetBytes(json));
        Updater.Configure(url[..url.IndexOf("/EfootballBot.exe", StringComparison.Ordinal)]);

        var result = await Updater.CheckAsync(includePrerelease: false, force: true);

        Assert.True(result.UpToDate);
        Assert.False(result.IsError);
        await loop;
    }

    // ---------------- SHA256 解析（纯函数，CI 追加到 Release 正文的行） ----------------

    [Fact]
    public void ParseExpectedSha256_正文含哈希行_按文件名取值并转小写()
    {
        string exeHash = new string('A', 64);
        string zipHash = new string('0', 63) + "f";
        string body = $"更新内容\r\n\r\n- 修复若干\r\n\r\nSHA256 EfootballBot.exe: {exeHash}\r\nSHA256 efootball-bot-win-x64.zip: {zipHash}\r\n";

        Assert.Equal(new string('a', 64), Updater.ParseExpectedSha256(body, "EfootballBot.exe"));
        Assert.Equal(new string('0', 63) + "f", Updater.ParseExpectedSha256(body, "efootball-bot-win-x64.zip"));
    }

    [Fact]
    public void ParseExpectedSha256_无哈希行或空正文_返回null()
    {
        Assert.Null(Updater.ParseExpectedSha256("旧的发行版正文，没有哈希行", "EfootballBot.exe"));
        Assert.Null(Updater.ParseExpectedSha256(null, "EfootballBot.exe"));
        Assert.Null(Updater.ParseExpectedSha256("SHA256 EfootballBot.exe: 短了", "EfootballBot.exe"));
    }

    // ---------------- 下载行为（真 HTTP 环回） ----------------

    /// <summary>手写最小 HTTP 响应的环回桩。maxRequests=预期请求数（服务完即退出，
    /// 测试才能 await Loop 收尾）；truncateLength < payload.Length 时谎报 Content-Length
    /// 但少发字节（模拟反代吐半截/错误页）；Accept 带 30 秒超时防任何路径挂死。</summary>
    private static (string Url, Task Loop) StartStub(byte[] payload, int maxRequests = 1, int? truncateLength = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var loop = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < maxRequests; i++)
                {
                    Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                    var done = await Task.WhenAny(accept, Task.Delay(30_000));
                    if (done != accept) break;
                    using TcpClient client = await accept;
                    using NetworkStream s = client.GetStream();
                    var rb = new byte[8192];
                    int total = 0;
                    while (total < rb.Length)
                    {
                        int n = await s.ReadAsync(rb.AsMemory(total));
                        if (n == 0) break;
                        total += n;
                        if (Encoding.ASCII.GetString(rb, 0, total).Contains("\r\n\r\n")) break;
                    }
                    string header = $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await s.WriteAsync(payload.AsMemory(0, truncateLength ?? payload.Length));
                    await s.FlushAsync();
                }
            }
            catch { /* 测试结束后的中断属正常 */ }
            finally { try { listener.Stop(); } catch { } }
        });
        return ($"http://127.0.0.1:{port}/EfootballBot.exe", loop);
    }

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    [Fact]
    public async Task 下载_首个候选连不上_自动切换下一候选成功()
    {
        byte[] payload = Payload(100_000);
        var (url, loop) = StartStub(payload);
        string dead = "http://127.0.0.1:1/EfootballBot.exe"; // 保留端口 1，连接立即被拒

        long n = await UpdateApplier.DownloadAsync(new[] { dead, url }, _target, payload.Length, null);

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, await File.ReadAllBytesAsync(_target));
        await loop;
    }

    [Fact]
    public async Task 下载_SHA256匹配_放行()
    {
        byte[] payload = Payload(50_000);
        var (url, loop) = StartStub(payload);
        string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        long n = await UpdateApplier.DownloadAsync(new[] { url }, _target, payload.Length, null, expected);

        Assert.Equal(payload.Length, n);
        await loop;
    }

    [Fact]
    public async Task 下载_SHA256不匹配_弃用候选_全部耗尽后抛校验失败()
    {
        byte[] payload = Payload(50_000);
        var (u1, l1) = StartStub(payload);
        var (u2, l2) = StartStub(payload);
        string wrongHash = new string('a', 64);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => UpdateApplier.DownloadAsync(new[] { u1, u2 }, _target, payload.Length, null, wrongHash));

        Assert.Contains("SHA256", ex.Message);
        Assert.Equal(0, new FileInfo(_target).Length); // 失败候选已回卷，不留半截数据
        await l1; await l2;
    }

    [Fact]
    public async Task 下载_假200少发字节_ContentLength兜底拦截()
    {
        byte[] payload = Payload(80_000);
        var (bad, loopBad) = StartStub(payload, truncateLength: payload.Length - 10);
        var (good, loopGood) = StartStub(payload);

        long n = await UpdateApplier.DownloadAsync(new[] { bad, good }, _target, payload.Length, null);

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, await File.ReadAllBytesAsync(_target));
        await loopBad; await loopGood;
    }
}
