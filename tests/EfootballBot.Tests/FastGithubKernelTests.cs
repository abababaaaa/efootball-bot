using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>FastGithub 内核的纯函数部分：hosts 加速段的读写往返、内核报告解析、镜像链与链接转换。
/// 这些都是「加速生效 / 还原干净 / 下载走对通道」的底层不变量，不需要内核 exe 也不需要网络。</summary>
public class FastGithubKernelTests
{
    // ---------------- hosts 加速段 ----------------

    [Fact]
    public void 加速段_生成后能被解析回原映射()
    {
        var map = new Dictionary<string, string>
        {
            ["github.com"] = "20.27.177.115",
            ["api.github.com"] = "20.205.243.168",
            ["raw.githubusercontent.com"] = "185.199.110.133",
        };

        string block = FastGithubKernel.BuildBlock(map);

        Assert.StartsWith(FastGithubKernel.MarkBegin, block);
        Assert.EndsWith(FastGithubKernel.MarkEnd + "\r\n", block);
        Assert.True(FastGithubKernel.HasBlock(block));

        var parsed = FastGithubKernel.ParseBlock(block);
        Assert.Equal(map.Count, parsed.Count);
        foreach (var kv in map) Assert.Equal(kv.Value, parsed[kv.Key]);
    }

    [Fact]
    public void 删块_只删加速段_其它行一字不动()
    {
        string original = "127.0.0.1 localhost\r\n# 用户自己的注释\r\n10.0.0.2 nas.local\r\n";
        string withBlock = original + "\r\n" + FastGithubKernel.BuildBlock(new Dictionary<string, string>
        {
            ["github.com"] = "20.27.177.115",
        });

        string stripped = FastGithubKernel.StripBlock(withBlock);

        Assert.DoesNotContain(FastGithubKernel.MarkBegin, stripped);
        Assert.DoesNotContain("20.27.177.115", stripped);
        Assert.Contains("127.0.0.1 localhost", stripped);
        Assert.Contains("10.0.0.2 nas.local", stripped);
        Assert.Contains("# 用户自己的注释", stripped);
    }

    [Fact]
    public void 还原_去掉内核给第三方加速器加的注释前缀()
    {
        // 内核把 Steam++ 之类指到 127.0.0.1 的行临时注释成 "#FG# "，还原时必须原样还回去
        string text = "#FG# 127.0.0.1 github.com\r\n127.0.0.1 localhost\r\n";
        string restored = FastGithubKernel.RestoreOverrides(text);
        Assert.Contains("127.0.0.1 github.com", restored);
        Assert.DoesNotContain("#FG# ", restored);
    }

    [Fact]
    public void 加速段_解析时忽略注释行与非法IP()
    {
        string block = FastGithubKernel.MarkBegin + "\r\n" +
                       "# 由 FastGithub 生成\r\n" +
                       "20.27.177.115\tgithub.com\r\n" +
                       "999.1.1.1\tbad.example.com\r\n" +
                       FastGithubKernel.MarkEnd + "\r\n";

        var parsed = FastGithubKernel.ParseBlock(block);

        Assert.Single(parsed);
        Assert.Equal("20.27.177.115", parsed["github.com"]);
        Assert.False(parsed.ContainsKey("bad.example.com"));
    }

    // ---------------- 内核报告解析 ----------------

    [Fact]
    public void 报告解析_兼容apply缩进排版与selftest对齐排版()
    {
        string report =
            "FastGithub CLI apply 2026-11-09 12:00:00\r\n" +
            "  github.com                               20.27.177.115     416ms  WEAK  code=404\r\n" +
            "  api.github.com                           20.205.243.168    751ms  GOOD  code=200\r\n" +
            "raw.githubusercontent.com                 185.199.110.133     81ms  FAIL  code=0\r\n" +
            "result=True  ok=2  conflicts=0\r\n";

        var nodes = FastGithubKernel.ParseNodeLines(report);

        Assert.Equal(3, nodes.Count);
        Assert.Equal("github.com", nodes[0].Host);
        Assert.Equal("20.27.177.115", nodes[0].Ip);
        Assert.Equal(416, nodes[0].Ms);
        Assert.True(nodes[0].Usable);            // WEAK 也算可用（403/404 只是路径探测被拒）
        Assert.Equal("受限", nodes[0].StateText);
        Assert.True(nodes[1].Usable);
        Assert.False(nodes[2].Usable);           // FAIL 不可用
        Assert.Equal(0, nodes[2].Code);
    }

    [Fact]
    public void 报告解析_空文本返回空表()
    {
        Assert.Empty(FastGithubKernel.ParseNodeLines(""));
        Assert.Empty(FastGithubKernel.ParseNodeLines("--- hosts 状态 ---\r\nhasBlock=False\r\n"));
    }

    // ---------------- 加速段「真实可用性」验收 ----------------

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(301, true)]
    [InlineData(302, true)]
    [InlineData(403, false)]   // 内核把它当 WEAK 可用，但对用户就是打不开
    [InlineData(404, false)]   // 用户实测的那个「GitHub Pages 404 页」节点
    [InlineData(0, false)]     // 连接失败/超时
    public void 验收判定_只认2xx与3xx_404与失败一律不可用(int status, bool ok)
    {
        var check = new FastGithubKernel.HostCheck("github.com", "20.27.177.115", status, 100);
        Assert.Equal(ok, check.Ok);
    }

    // ---------------- 镜像链 ----------------

    [Fact]
    public void 镜像前缀_空或非法落回首选_并补齐末尾斜杠()
    {
        string first = FastGithubKernel.MirrorPrefixes[0];
        Assert.Equal(first, FastGithubKernel.NormalizeMirror(""));
        Assert.Equal(first, FastGithubKernel.NormalizeMirror(null));
        Assert.Equal(first, FastGithubKernel.NormalizeMirror("ghfast.top"));       // 裸域名不算合法前缀
        Assert.Equal(first, FastGithubKernel.NormalizeMirror("ftp://ghfast.top")); // 只认 http(s)
        Assert.Equal("https://example.com/proxy/", FastGithubKernel.NormalizeMirror(" https://example.com/proxy "));
        Assert.Equal("https://example.com/proxy/", FastGithubKernel.NormalizeMirror("https://example.com/proxy/"));
    }

    [Fact]
    public void 链接转换_套前缀与剥前缀互为逆运算()
    {
        string raw = "https://github.com/abababaaaa/efootball-bot/releases/download/v6.4.0/EfootballBot.exe";

        string accelerated = FastGithubKernel.AccelerateUrl(raw, "https://ghfast.top/");
        Assert.Equal("https://ghfast.top/" + raw, accelerated);

        Assert.Equal(raw, FastGithubKernel.StripUrl(accelerated));
        // 已经是镜像链接再套一次不会套娃
        Assert.Equal(accelerated, FastGithubKernel.AccelerateUrl(accelerated, "https://ghfast.top/"));
        // 换镜像时先把旧前缀剥掉
        Assert.Equal("https://ghproxy.net/" + raw, FastGithubKernel.AccelerateUrl(accelerated, "https://ghproxy.net/"));
        Assert.Equal("", FastGithubKernel.AccelerateUrl(""));
    }

    [Fact]
    public void 候选链_默认镜像第一_预设去重_直连恒最后()
    {
        string url = "https://github.com/owner/repo/releases/download/v1.0.0/app.exe";
        var chain = FastGithubKernel.WithMirrors(url);

        Assert.Equal(url, chain[^1]);
        Assert.Equal(FastGithubKernel.DefaultMirror + url, chain[0]);
        Assert.Contains(url, chain);
        Assert.Equal(chain.Count, chain.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var prefix in FastGithubKernel.MirrorPrefixes)
            Assert.Contains(prefix + url, chain);
    }
}
