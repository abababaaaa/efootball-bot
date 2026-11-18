using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot;

/// <summary>一次观察结果：帧 + OCR + YOLO 检测。</summary>
public sealed record Observation(FrameData Frame, OcrResult Ocr, IReadOnlyList<YoloDetection> Detections)
{
    public static readonly Observation Empty = new(
        new FrameData(Array.Empty<byte>(), 0, 0, 0), OcrResult.Empty, Array.Empty<YoloDetection>());
}

// 日志级别已提到 EfootballBot.Core.LogLevel（工具层也要用，不能挂在挂机模块下）
