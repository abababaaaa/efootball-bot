using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Inference;

namespace EfootballBot.Core.Vision;

/// <summary>一帧的完整感知结果：画面 + OCR + YOLO 检测。</summary>
public sealed record Perception(
    FrameData Frame,
    OcrResult Ocr,
    IReadOnlyList<Yolo.YoloDetection> Detections)
{
    public static readonly Perception Empty = new(
        new FrameData(Array.Empty<byte>(), 0, 0, 0), OcrResult.Empty, Array.Empty<Yolo.YoloDetection>());
}

/// <summary>
/// 感知管线：帧 →（可选）YOLO 检测 + OCR → Perception。
/// YOLO 模型缺失（models/yolo/ 无 onnx）时构造返回 null，仅 OCR 观察。
/// </summary>
public sealed class PerceptionPipeline : IDisposable
{
    private readonly Yolo.YoloDetector? _yolo;

    /// <summary>YOLO 是否启用（模型就位且配置开启）。</summary>
    public bool YoloEnabled => _yolo is not null;
    public string? YoloLabel => _yolo?.ModelLabel;

    private PerceptionPipeline(Yolo.YoloDetector? yolo) => _yolo = yolo;

    /// <summary>按配置构造管线。模型缺失时返回仅 OCR 的管线（不抛异常）。</summary>
    public static PerceptionPipeline Create(AppConfig cfg, Inference.OnnxSessionFactory factory, Action<string, LogLevel> log)
    {
        Yolo.YoloDetector? yolo = null;
        if (cfg.Yolo.Enabled)
        {
            var modelPath = System.AppPaths.FindBundledFile("models", "yolo", "yolo26n.onnx");
            if (modelPath is null)
            {
                log("未找到 YOLO 模型（models/yolo/yolo26n.onnx），目标检测停用，仅 OCR 观察", LogLevel.Warn);
            }
            else
            {
                try
                {
                    var classesPath = System.AppPaths.FindBundledFile("models", "yolo", "classes.txt");
                    yolo = new Yolo.YoloDetector(modelPath, classesPath, factory,
                        (float)cfg.Yolo.ScoreThreshold, (float)cfg.Yolo.NmsIou);
                    log($"目标检测已启用：{yolo.ModelLabel}（EP={factory.ActiveProvider}）", LogLevel.Success);
                }
                catch (Exception ex)
                {
                    log($"YOLO 检测器初始化失败，目标检测停用：{ex.Message}", LogLevel.Error);
                    yolo = null;
                }
            }
        }
        else
        {
            log("目标检测已在设置中关闭，仅 OCR 观察", LogLevel.Info);
        }
        return new PerceptionPipeline(yolo);
    }

    /// <summary>对一帧执行 YOLO 检测（OCR 由 ObservationService 节流完成，此处只补检测）。</summary>
    public async Task<IReadOnlyList<Yolo.YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct)
    {
        if (_yolo is null) return Array.Empty<Yolo.YoloDetection>();
        return await Task.Run(() => _yolo.Detect(frame, ct), ct);
    }

    public void Dispose() => _yolo?.Dispose();
}
