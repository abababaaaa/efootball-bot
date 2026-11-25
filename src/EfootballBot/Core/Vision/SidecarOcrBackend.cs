using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision.Sidecar;

namespace EfootballBot.Core.Vision;

/// <summary>
/// Rust sidecar OCR 后端：帧经上传共享内存送给 vision-svc，识别在 Rust/ONNX Runtime 里跑。
/// 若侧车正在做捕获（<see cref="VisionSidecarRuntime"/>），自动复用同一个进程；
/// 识别"刚捕获的那一帧"时走 <c>src=capture</c>，跳过上传拷贝。
/// </summary>
internal sealed class SidecarOcrBackend : IOcrBackend
{
    private readonly VisionSidecarConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VisionSidecarClient? _client;
    private bool _owned;
    private bool _disposed;

    public SidecarOcrBackend(VisionSidecarConfig cfg)
    {
        _cfg = cfg;
        // 构造时校验 vision-svc.exe 是否存在，缺失立即抛异常以触发 SimdPaddle/RapidV6 回退
        if (VisionSidecarRuntime.SharedClient is null && VisionSidecarLocator.Locate(cfg.ExePath) is null)
            throw new global::System.IO.FileNotFoundException("未找到 vision-svc.exe", cfg.ExePath);
        Label = "vision-svc(ort)";
    }

    public string Label { get; }

    private async Task<VisionSidecarClient> EnsureClientAsync(CancellationToken ct)
    {
        var shared = VisionSidecarRuntime.SharedClient;
        if (shared is { IsDisconnected: false }) return shared;

        if (_client is { IsDisconnected: true })
        {
            if (_owned) { try { await _client.DisposeAsync().ConfigureAwait(false); } catch { } }
            _client = null;
            _owned = false;
        }
        if (_client is null)
        {
            _client = await VisionSidecarClient
                .StartAsync(_cfg, hwnd: 0, width: 1920, height: 1080, ct)
                .ConfigureAwait(false);
            _owned = true;
        }
        return _client;
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            var fromCapture = ReferenceEquals(client, VisionSidecarRuntime.SharedClient)
                              && VisionSidecarRuntime.MatchesCapturedFrame(frame);
            return await client.RecognizeAsync(frame, fromCapture, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_owned && _client is not null)
            {
                try { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
