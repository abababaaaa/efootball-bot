using System.IO;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision.Sidecar;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// Rust vision-svc sidecar 的端到端契约测试：管道握手 → init 帧环 → ping → inject/grab → 共享内存读帧。
/// Rust 侧未构建（rust\bin\vision-svc.exe 不存在）时整组跳过，不阻塞日常 dotnet test。
/// 注意：Desktop Duplication 在虚拟显示器/远程会话机器上会产出全黑帧（本仓库早已记录该现象），
/// 因此链路正确性用 inject（合成图案）断言，DXGI 只是"能出帧"的冒烟断言。
/// </summary>
public class VisionSidecarTests
{
    private static string? FindExe() => VisionSidecarLocator.Locate(null);

    [Fact]
    public void Locator_returns_existing_path_or_null()
    {
        var exe = FindExe();
        Assert.True(exe is null || File.Exists(exe), $"locator 返回了不存在的路径：{exe}");
    }

    [Fact]
    public async Task Handshake_ping_init_and_ring_geometry()
    {
        var exe = FindExe();
        if (exe is null) return;

        var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 15, Verbose = true };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 1280, height: 720);

        Assert.NotNull(client.Ring);
        Assert.True(client.Ring.SlotCount >= 2);
        Assert.True(client.Ring.SlotBytes >= 1280 * 720 * 4);

        var pong = await client.SendAsync(VisionMsg.CmdOf("ping"), 5000);
        Assert.Equal("pong", pong.Ev);
    }

    [Fact]
    public async Task Injected_pattern_roundtrips_through_shared_memory()
    {
        var exe = FindExe();
        if (exe is null) return;

        var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 5 };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 640, height: 360);

        var inject = await client.SendAsync(
            new VisionMsg { Cmd = "inject", W = 640, H = 360 }, 5000);
        Assert.Equal("frame", inject.Ev);
        Assert.True(inject.Seq is > 0);

        FrameData? frame = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && frame is null)
        {
            if (client.Ring.TryReadLatest(out var f)) frame = f;
            else await Task.Delay(20);
        }

        Assert.NotNull(frame);
        Assert.Equal(640, frame!.Width);
        Assert.Equal(360, frame.Height);
        Assert.Equal(640 * 4 * 360, frame.Bgra.Length);

        // 图案是渐变 + 竖条：RGB 通道必须有多种取值，且不是全零
        var distinct = frame.Bgra.Where((_, i) => i % 4 != 3).Distinct().Count();
        Assert.True(distinct > 8, $"注入图案应当有多种取色，实际只有 {distinct} 种");
        Assert.Contains(frame.Bgra, b => b != 0);
    }

    [Fact]
    public async Task Dxgi_grab_produces_a_frame_of_requested_geometry()
    {
        var exe = FindExe();
        if (exe is null) return;

        var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 5 };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 1920, height: 1080);

        var grab = await client.SendAsync(new VisionMsg { Cmd = "grab", Hwnd = 0 }, 10000);
        // 桌面静止时可能 60ms×30 次都没有新帧，这时允许失败（环境相关）
        if (grab.Ev == "error")
        {
            Assert.Contains("没有新帧", grab.Text ?? "");
            return;
        }

        Assert.Equal("frame", grab.Ev);
        Assert.True(grab.W is > 0 && grab.H is > 0);
        Assert.True(grab.Seq is > 0);

        FrameData? frame = null;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && frame is null)
        {
            if (client.Ring.TryReadLatest(out var f)) frame = f;
            else await Task.Delay(20);
        }
        Assert.NotNull(frame);
        // DXGI 抓的是整块输出，帧几何可能与 init 请求的不同（槽位按桌面最大尺寸开）
        Assert.True(frame!.Width >= (int)grab.W!);
        Assert.True(frame.Height >= (int)grab.H!);
    }

    [Fact]
    public async Task Unknown_command_returns_error_event()
    {
        var exe = FindExe();
        if (exe is null) return;

        var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 5 };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 640, height: 360);
        var reply = await client.SendAsync(VisionMsg.CmdOf("nonexistent-cmd"), 3000);
        Assert.Equal("error", reply.Ev);
        Assert.Contains("未知命令", reply.Text ?? "");
    }
}
