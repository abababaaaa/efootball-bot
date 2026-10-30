using EfootballBot.Core.Capture;
using EfootballBot.Core.Observation;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 捕获自愈回归（2026-10-29 实机故障：游戏先卡成空画面 → WGC/WARP 全无帧 → 降级 GDI
// 报"已就绪"但 2 秒后又判无帧 → 12 秒内三次"重建"全烧在空操作上 → 终停，且把一次游戏卡死
// 误报成显卡问题）。本文件守住四个修复点：
//   ① GDI 必须等首帧才算就绪（不允许假 OK）
//   ② GDI 的失败必须可归因（LastError 有内容，不是静默 false）
//   ③ 已运行时的"重建"必须是真重启，不是空操作
//   ④ 无帧熔断的报错文本随窗口响应状态变化，不再一律甩给显卡
public class CaptureSelfHealTests
{
    // ---- ①② GdiWindowCapture：失败必须可归因，首帧验证不得假成功 ----

    [Fact]
    public void Gdi_StartWithInvalidHandle_ReportsFailureNotSuccess()
    {
        using var gdi = new GdiWindowCapture();
        // 无效句柄：GetWindowRect/PrintWindow 必然失败 → 首帧验证必须返回 false
        bool gotFrame = gdi.StartAndWaitFirstFrame(nint.Zero, 1280, 720, timeoutMs: 600);

        Assert.False(gotFrame);                    // 绝不能"报就绪"
        Assert.Null(gdi.TryGetLatest());           // 也绝不能有帧
        Assert.Equal(0, gdi.FrameCount);
        Assert.False(string.IsNullOrWhiteSpace(gdi.LastError));   // 必须有可读原因
    }

    [Fact]
    public void Gdi_FailureReason_IsActionable()
    {
        using var gdi = new GdiWindowCapture();
        gdi.StartAndWaitFirstFrame(nint.Zero, 1280, 720, timeoutMs: 400);

        Assert.NotNull(gdi.LastError);
        // 失败原因要能看出卡在哪一步（窗口/尺寸/DIB/PrintWindow/BitBlt），而不是空字符串
        Assert.True(
            gdi.LastError!.Contains("窗口", StringComparison.Ordinal) ||
            gdi.LastError.Contains("DIBSection", StringComparison.Ordinal) ||
            gdi.LastError.Contains("BitBlt", StringComparison.Ordinal) ||
            gdi.LastError.Contains("异常", StringComparison.Ordinal),
            $"LastError 应包含可归因信息，实际：{gdi.LastError}");
    }

    // ---- ③ 重建必须是真重启 ----

    [Fact]
    public void Gdi_StopThenStart_RestartsThread()
    {
        using var gdi = new GdiWindowCapture();
        gdi.Start(nint.Zero, 1280, 720);
        Assert.True(gdi.IsRunning);

        gdi.Stop();
        Assert.False(gdi.IsRunning);

        // 旧实现里已在运行时 Start 是 no-op，观察层的"重建"实际什么都没做，
        // 却照样消耗熔断预算 → 12 秒内三次把预算烧完。现在必须能真正重启。
        gdi.Start(nint.Zero, 1280, 720);
        Assert.True(gdi.IsRunning);
    }

    [Fact]
    public void Gdi_RestartActuallyRetriesCapture()
    {
        using var gdi = new GdiWindowCapture();
        gdi.StartAndWaitFirstFrame(nint.Zero, 1280, 720, timeoutMs: 300);
        Assert.NotNull(gdi.LastError);
        Assert.Equal(0, gdi.FrameCount);

        // 旧实现里"重建"在已运行时是空操作：Start 里 `if (_running) return`，抓帧线程原样继续跑、
        // 没人重新尝试，但观察层照样记一次无帧重建 → 12 秒内三次把熔断预算烧完就判死。
        // 现在 BotEngine 的重建路径是先 Stop 再 StartAndWaitFirstFrame（真重启），
        // 这里按同样的路径验证：重启后仍会给出（新的）失败原因 = 确实重新尝试过抓帧。
        gdi.Stop();
        bool restarted = gdi.StartAndWaitFirstFrame(nint.Zero, 1280, 720, timeoutMs: 400);
        Assert.False(restarted);             // 句柄无效，仍不应有帧
        Assert.NotNull(gdi.LastError);       // 但确实重新尝试过，并再次报出原因
    }

    // ---- ④ 窗口响应探测 ----

    [Fact]
    public void IsResponding_ZeroHandle_IsFalse()
    {
        Assert.False(GameWindow.IsResponding(nint.Zero));
    }

    [Fact]
    public void IsResponding_BogusHandle_IsFalse()
    {
        // 随机无效句柄：SendMessageTimeout 会失败/超时 → false（不抛异常）
        Assert.False(GameWindow.IsResponding(new nint(0x1)));
    }

    // ---- ④ 无帧熔断归因文本（纯函数） ----

    [Fact]
    public void ZeroFrameCause_NoWindow_SaysUnbound_NotGraphics()
    {
        string cause = ObservationService.BuildZeroFrameCause(nint.Zero, "WindowCapture");

        Assert.Contains("未绑定", cause);
        Assert.Contains("WindowCapture", cause);
        Assert.DoesNotContain("无显示输出的显卡", cause);
    }

    [Fact]
    public void ZeroFrameCause_HungWindow_PointsAtGameNotGraphics()
    {
        // 无效句柄 = 窗口无响应分支
        string cause = ObservationService.BuildZeroFrameCause(new nint(0x1), "GdiWindowCapture");

        Assert.Contains("无响应", cause);
        Assert.Contains("游戏", cause);
        Assert.Contains("GdiWindowCapture", cause);
        // 关键：不能在这个分支里继续甩锅显卡
        Assert.DoesNotContain("更可能是捕获环境问题", cause);
    }
}
