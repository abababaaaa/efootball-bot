using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Vision.Sidecar;

namespace EfootballBot.Core.Vision;

/// <summary>检测器抽象：进程内 YoloDetector 与 sidecar 版共用，PerceptionPipeline 只认这一个。</summary>
internal interface IDetector
{
    string Label { get; }
    Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct);
}

/// <summary>进程内 ONNX Runtime 检测（历史路径）。</summary>
internal sealed class InProcDetector : IDetector
{
    private readonly YoloDetector _yolo;
    public InProcDetector(YoloDetector yolo) => _yolo = yolo;
    public string Label => _yolo.ModelLabel;
    public Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct) =>
        Task.Run(() => _yolo.Detect(frame, ct), ct);
}

/// <summary>
/// sidecar 版 YOLO（M2）：模型在 vision-svc 里跑，主进程不加载 ONNX 检测模型。
/// 与 OCR 共用同一个侧车进程；识别刚捕获帧时同样走 <c>src=capture</c> 跳过上传。
/// </summary>
internal sealed class SidecarDetector : IDetector
{
    private readonly VisionSidecarConfig _cfg;
    private readonly string[] _classes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VisionSidecarClient? _client;
    private bool _owned;

    public SidecarDetector(VisionSidecarConfig cfg)
    {
        _cfg = cfg;
        var classesPath = AppPaths.FindBundledFile("models", "yolo", "classes.txt");
        // 注意：命名空间 EfootballBot.Core.System 会遮蔽 System.IO，必须写 global::
        _classes = classesPath is not null && global::System.IO.File.Exists(classesPath)
            ? global::System.IO.File.ReadAllLines(classesPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    public string Label => $"yolo26n（vision-svc，{_classes.Length} 类）";

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

    public async Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            var fromCapture = ReferenceEquals(client, VisionSidecarRuntime.SharedClient)
                              && VisionSidecarRuntime.MatchesCapturedFrame(frame);
            var raw = await client.DetectAsync(frame, fromCapture, ct).ConfigureAwait(false);
            return raw
                .Select(b => new YoloDetection(NameOf(b.C), b.C, b.S, b.X, b.Y, b.W, b.H))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string NameOf(int id) =>
        id >= 0 && id < _classes.Length ? _classes[id] : $"cls{id}";

    public void Dispose()
    {
        _gate.Dispose();
        if (_owned && _client is not null)
        {
            try { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }
}
