using EfootballBot.Core.Config;
using EfootballBot.Core.Vision.Sidecar;

namespace EfootballBot.Core.Capture;

/// <summary>
/// 捕获宿主：默认走 WGC（<see cref="WindowCapture"/>），可切换到 Rust sidecar（<see cref="SidecarCaptureProvider"/>）。
/// 存在的意义：BotEngine 只持有一个稳定句柄，后端切换（含运行期失败回退）不用改调用点。
/// </summary>
public sealed class CaptureHost : ICaptureProvider
{
    private readonly WindowCapture _wgc = new();
    private SidecarCaptureProvider? _sidecar;

    public CaptureHost()
    {
        _wgc.FrameUpdated += (s, e) => FrameUpdated?.Invoke(s, e);
    }

    public event EventHandler? FrameUpdated;
    public event EventHandler<string>? CaptureClosed
    {
        add => _wgc.CaptureClosed += value;
        remove => _wgc.CaptureClosed -= value;
    }
    public event Action<string>? StepLog
    {
        add => _wgc.StepLog += value;
        remove => _wgc.StepLog -= value;
    }
    /// <summary>sidecar 侧日志（级别, 文本）。</summary>
    public event Action<string, string>? SidecarLog;

    /// <summary>WGC 专用：强制 WARP 软渲染设备建帧池（虚拟显示器/远程会话机器）。</summary>
    public bool ForceWarpDevice
    {
        get => _wgc.ForceWarpDevice;
        set => _wgc.ForceWarpDevice = value;
    }

    public bool SidecarActive => _sidecar is not null;
    private ICaptureProvider Active => (ICaptureProvider?)_sidecar ?? _wgc;

    public bool HasFrame => Active.TryGetLatest() is not null;
    public bool IsRunning => Active.IsRunning;
    public nint Hwnd => Active.Hwnd;
    public DateTime LastFrameAt => Active.LastFrameAt;

    /// <summary>切到 sidecar 捕获（可在 Start 之前调用）。</summary>
    public SidecarCaptureProvider EnableSidecar(VisionSidecarConfig cfg)
    {
        DisableSidecar();
        var provider = new SidecarCaptureProvider(cfg);
        provider.FrameUpdated += (s, e) => FrameUpdated?.Invoke(s, e);
        provider.SidecarLog += (level, text) => SidecarLog?.Invoke(level, text);
        _sidecar = provider;
        return provider;
    }

    /// <summary>回退到 WGC（sidecar 启不来/运行中崩了时用）。</summary>
    public void DisableSidecar()
    {
        if (_sidecar is null) return;
        try { _sidecar.Stop(); } catch { }
        try { _sidecar.Dispose(); } catch { }
        _sidecar = null;
    }

    public void Start(nint hwnd, int clientW, int clientH) => Active.Start(hwnd, clientW, clientH);

    public void Stop()
    {
        try { _sidecar?.Stop(); } catch { }
        try { _wgc.Stop(); } catch { }
    }

    public FrameData? TryGetLatest() => Active.TryGetLatest();

    public void SetMinFrameInterval(int ms) => Active.SetMinFrameInterval(ms);

    public void Dispose()
    {
        DisableSidecar();
        _wgc.Dispose();
    }
}
