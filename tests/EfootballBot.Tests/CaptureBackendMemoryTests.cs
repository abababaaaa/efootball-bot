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
}
