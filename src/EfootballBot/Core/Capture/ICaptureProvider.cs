namespace EfootballBot.Core.Capture;

/// <summary>画面捕获提供者抽象。DXGI Desktop Duplication（默认）和 WGC（备用）均实现此接口。</summary>
public interface ICaptureProvider : IDisposable
{
    void Start(IntPtr hwnd, int clientW, int clientH);
    void Stop();
    FrameData? TryGetLatest();
    event EventHandler? FrameUpdated;
    void SetMinFrameInterval(int ms);
    bool IsRunning { get; }
    IntPtr Hwnd { get; }
    DateTime LastFrameAt { get; }
}
