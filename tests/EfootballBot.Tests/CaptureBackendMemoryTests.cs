using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

public class CaptureBackendMemoryTests : IDisposable
{
    private readonly string _dir;

    public CaptureBackendMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "efb-tests", "cbm-" + Guid.NewGuid().ToString("N")[..8]);
        CaptureBackendMemory.FilePathOverride = () => Path.Combine(_dir, "capture-backend.json");
    }

    public void Dispose()
    {
        CaptureBackendMemory.FilePathOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        Assert.Null(CaptureBackendMemory.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsKindAndMeta()
    {
        CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "WGC/DXGI 捕获熔断降级");
        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.Equal(CaptureBackendKind.Gdi, entry!.Kind);
        Assert.Equal("WGC/DXGI 捕获熔断降级", entry.Reason);
        // 时间戳应是刚刚（容差 2 分钟，防慢机器时钟抖动误报）
        Assert.True((DateTime.Now - entry.At).Duration() < TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Save_OverwritesPreviousKind()
    {
        CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "熔断");
        CaptureBackendMemory.Save(CaptureBackendKind.Wgc, "硬件 WGC 出帧正常");
        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.Equal(CaptureBackendKind.Wgc, entry!.Kind);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "capture-backend.json"), "{ not valid json !!!");
        Assert.Null(CaptureBackendMemory.Load());
    }

    // ---- 衰减：GDI 判定只该加速启动，不该永久钉死后端（2026-11-19 反馈包 R6） ----

    private void WriteEntry(DateTime at, int hits, string kind = "gdi")
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "capture-backend.json"),
            $"{{\"kind\":\"{kind}\",\"reason\":\"测试\",\"at\":\"{at:O}\",\"hits\":{hits}}}");
    }

    [Fact]
    public void NewGdiEntry_IsNotReprobed()
    {
        CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "熔断降级");
        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.False(CaptureBackendMemory.ShouldReprobe(entry!));
    }

    [Fact]
    public void ExpiredGdiEntry_IsReprobed()
    {
        var old = CaptureBackendMemory.ReprobeAfter;
        try
        {
            CaptureBackendMemory.ReprobeAfter = TimeSpan.Zero;
            WriteEntry(DateTime.Now.AddMinutes(-5), hits: 0);
            var entry = CaptureBackendMemory.Load();
            Assert.NotNull(entry);
            Assert.True(CaptureBackendMemory.ShouldReprobe(entry!));
        }
        finally { CaptureBackendMemory.ReprobeAfter = old; }
    }

    [Fact]
    public void GdiEntryAtHitLimit_IsReprobed()
    {
        WriteEntry(DateTime.Now.AddMinutes(-1), hits: CaptureBackendMemory.MaxHits);
        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.True(CaptureBackendMemory.ShouldReprobe(entry!));
    }

    [Fact]
    public void WgcEntry_IsNeverReprobed()
    {
        // WGC/WARP 是已验证能出帧的后端，不参与衰减（否则每次启动都白探一遍）
        WriteEntry(DateTime.Now.AddYears(-1), hits: 999, kind: "wgc");
        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.False(CaptureBackendMemory.ShouldReprobe(entry!));
    }

    [Fact]
    public void Save_SameBackend_KeepsOriginalAtAndHits()
    {
        // ObservationService 每次出帧都会 Save 一次；若它刷新 At，衰减就永远不触发
        WriteEntry(DateTime.Now.AddDays(-3), hits: 7);
        CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "GDI 捕获出帧正常");

        var entry = CaptureBackendMemory.Load();
        Assert.NotNull(entry);
        Assert.Equal(7, entry!.Hits);
        Assert.True((DateTime.Now - entry.At).TotalDays > 2.9);
    }

    [Fact]
    public void NoteHit_IncrementsHits_AndKeepsOriginalAt()
    {
        WriteEntry(DateTime.Now.AddDays(-1), hits: 3);
        var before = CaptureBackendMemory.Load()!;

        CaptureBackendMemory.NoteHit();
        CaptureBackendMemory.NoteHit();

        var after = CaptureBackendMemory.Load();
        Assert.NotNull(after);
        Assert.Equal(5, after!.Hits);
        // 判定时间必须保留：借 Save 刷成「刚刚」会让衰减窗口永远差一点点，永远不触发
        Assert.Equal(before.At, after.At);
        Assert.Equal(CaptureBackendKind.Gdi, after.Kind);
    }
}
