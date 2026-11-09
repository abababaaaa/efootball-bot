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

    public SidecarOcrBackend(VisionSidecarConfig cfg)
    {
        _cfg = cfg;
        Label = "vision-svc(ort)";
    }

    public string Label { get; }

    private VisionSidecarClient EnsureClient()
    {
        var shared = VisionSidecarRuntime.SharedClient;
        if (shared is not null) return shared;

        lock (_gate)
        {
            _client ??= VisionSidecarClient
                .StartAsync(_cfg, hwnd: 0, width: 1920, height: 1080)
                .GetAwaiter().GetResult();
            _owned = true;
            return _client;
        }
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
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
        _gate.Dispose();
        if (_owned && _client is not null)
        {
            try { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }
}
