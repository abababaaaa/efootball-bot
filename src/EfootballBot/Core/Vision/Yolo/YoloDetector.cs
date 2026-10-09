using System.IO;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EfootballBot.Core.Vision.Yolo;

/// <summary>
/// YOLO26n 目标检测器：OpenCV letterbox 预处理 → ONNX Runtime 推理 → NMS 后处理。
/// 自动兼容两种导出格式：
/// ① 端到端 NMS-free（YOLO26 官方导出）：输出 [1, N, 6]（cx,cy,w,h,score,cls）或转置；
/// ② 常规预测头（v8/11/26 未含 NMS）：输出 [1, 4+nc, N] 或 [1, N, 4+nc]，本地做 NMS。
/// 模型文件约定：models/yolo/yolo26n.onnx + classes.txt（每行一个类别名，顺序=类别 id）。
/// </summary>
public sealed class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _classes;
    private readonly int _inputW;
    private readonly int _inputH;
    private readonly float _scoreThreshold;
    private readonly float _nmsIouThreshold;
    private readonly string[] _inputNames;
    private readonly string _outputName;

    public bool Enabled => true;
    public string ModelLabel { get; }

    public YoloDetector(string modelPath, string? classesPath, OnnxSessionFactory factory,
        float scoreThreshold = 0.25f, float nmsIouThreshold = 0.45f)
    {
        _scoreThreshold = scoreThreshold;
        _nmsIouThreshold = nmsIouThreshold;

        _classes = LoadClasses(classesPath);
        _session = new InferenceSession(modelPath, factory.GetSessionOptions());
        _inputNames = _session.InputMetadata.Keys.ToArray();

        // 输入尺寸：优先从模型元数据读取，动态维或读不到时回退 640
        (_inputW, _inputH) = ResolveInputSize();
        _outputName = ResolveOutputName();

        ModelLabel = $"{Path.GetFileName(modelPath)} {_inputW}×{_inputH} {_classes.Length}类";
    }

    private static string[] LoadClasses(string? classesPath)
    {
        if (classesPath is not null && File.Exists(classesPath))
        {
            var lines = File.ReadAllLines(classesPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            if (lines.Length > 0) return lines;
        }
        return Array.Empty<string>();
    }

    private (int W, int H) ResolveInputSize()
    {
        try
        {
            var meta = _session.InputMetadata[_inputNames[0]];
            var dims = meta.Dimensions;
            // NCHW：dims[2]=H dims[3]=W
            if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0) return (dims[3], dims[2]);
        }
        catch { }
        return (640, 640);
    }

    private string ResolveOutputName()
    {
        // 优先 output0（Ultralytics 导出约定），否则取唯一输出
        if (_session.OutputMetadata.ContainsKey("output0")) return "output0";
        return _session.OutputMetadata.Keys.First();
    }

    /// <summary>对一帧 BGRA 画面做检测，返回归一化坐标结果。</summary>
    public IReadOnlyList<YoloDetection> Detect(FrameData frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var input = Letterbox(frame, out float scale, out int padX, out int padY);
        var tensor = ToCHWTensor(input);

        // InferenceSession.Run 走 IReadOnlyCollection<NamedOnnxValue> 重载（名称携带在 NamedOnnxValue 上）
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputNames[0], tensor),
        };
        using var results = _session.Run(inputs, new[] { _outputName });
        var output = results.First().AsTensor<float>();

        var boxes = ParseOutput(output, _classes.Length);
        return MapAndFilter(boxes, scale, padX, padY, frame.Width, frame.Height);
    }

    // ---------------- 预处理 ----------------

    /// <summary>BGRA 帧 → letterbox 缩放到模型输入尺寸（等比 + 灰边填充），输出 RGB Mat。</summary>
    internal Mat Letterbox(FrameData frame, out float scale, out int padX, out int padY)
    {
        scale = Math.Min((float)_inputW / frame.Width, (float)_inputH / frame.Height);
        int newW = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(frame.Height * scale));
        padX = (_inputW - newW) / 2;
        padY = (_inputH - newH) / 2;

        using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);

        var dst = new Mat(_inputH, _inputW, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(newW, newH));
        resized.CopyTo(dst[new Rect(padX, padY, newW, newH)]);
        Cv2.CvtColor(dst, dst, ColorConversionCodes.BGR2RGB);
        return dst;
    }

    /// <summary>RGB Mat → [1,3,H,W] 归一化 Tensor。</summary>
    internal DenseTensor<float> ToCHWTensor(Mat rgb)
    {
        var t = new DenseTensor<float>(new[] { 1, 3, _inputH, _inputW });
        float inv = 1f / 255f;
        unsafe
        {
            byte* basePtr = (byte*)rgb.DataPointer;
            int stride = (int)rgb.Step();
            for (int y = 0; y < _inputH; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < _inputW; x++)
                {
                    int p = x * 3;
                    t[0, 0, y, x] = row[p] * inv;
                    t[0, 1, y, x] = row[p + 1] * inv;
                    t[0, 2, y, x] = row[p + 2] * inv;
                }
            }
        }
        return t;
    }

    // ---------------- 输出解析 ----------------

    /// <summary>解析原始输出为像素坐标（letterbox 空间）候选框。</summary>
    internal List<(float Cx, float Cy, float W, float H, float Score, int ClassId)> ParseOutput(
        Tensor<float> output, int numClasses)
    {
        var list = new List<(float, float, float, float, float, int)>();
        var dims = output.Dimensions;
        if (dims.Length != 3) return list;

        int d0 = dims[0];
        if (d0 != 1) return list;
        int d1 = dims[1];
        int d2 = dims[2];

        // 端到端 NMS-free：[1, N, 6] 或 [1, 6, N]（cx,cy,w,h,score,cls）
        if (d1 == 6 || d2 == 6)
        {
            bool rowsFirst = d1 == 6 && d2 >= d1; // [1,6,N]
            int n = rowsFirst ? d2 : d1;
            for (int i = 0; i < n; i++)
            {
                float cx = rowsFirst ? output[0, 0, i] : output[0, i, 0];
                float cy = rowsFirst ? output[0, 1, i] : output[0, i, 1];
                float w = rowsFirst ? output[0, 2, i] : output[0, i, 2];
                float h = rowsFirst ? output[0, 3, i] : output[0, i, 3];
                float score = rowsFirst ? output[0, 4, i] : output[0, i, 4];
                int cls = (int)(rowsFirst ? output[0, 5, i] : output[0, i, 5]);
                if (score >= _scoreThreshold)
                    list.Add((cx, cy, w, h, score, cls));
            }
            return list;
        }

        // 常规预测头：[1, 4+nc, N]（Ultralytics 默认）或 [1, N, 4+nc]
        int attrs = 4 + numClasses;
        if (d1 == attrs || d2 == attrs)
        {
            bool chFirst = d1 == attrs; // [1, attrs, N]
            int n = chFirst ? d2 : d1;
            for (int i = 0; i < n; i++)
            {
                float cx = chFirst ? output[0, 0, i] : output[0, i, 0];
                float cy = chFirst ? output[0, 1, i] : output[0, i, 1];
                float w = chFirst ? output[0, 2, i] : output[0, i, 2];
                float h = chFirst ? output[0, 3, i] : output[0, i, 3];

                int best = -1;
                float bestScore = 0f;
                for (int c = 0; c < numClasses; c++)
                {
                    float s = chFirst ? output[0, 4 + c, i] : output[0, i, 4 + c];
                    if (s > bestScore) { bestScore = s; best = c; }
                }
                if (best >= 0 && bestScore >= _scoreThreshold)
                    list.Add((cx, cy, w, h, bestScore, best));
            }
        }
        return list;
    }

    /// <summary>letterbox 像素坐标 → 原帧归一化坐标 + NMS。</summary>
    internal IReadOnlyList<YoloDetection> MapAndFilter(
        List<(float Cx, float Cy, float W, float H, float Score, int ClassId)> boxes,
        float scale, int padX, int padY, int frameW, int frameH)
    {
        var mapped = boxes
            .Select(b => MapLetterboxToFrame(b.Cx, b.Cy, b.W, b.H, b.Score, b.ClassId, scale, padX, padY))
            .ToList();

        var kept = NonMaxSuppression(mapped, _nmsIouThreshold);
        var result = new List<YoloDetection>(kept.Count);
        foreach (var b in kept)
        {
            double bx = Math.Clamp(b.x0 / frameW, 0.0, 1.0);
            double by = Math.Clamp(b.y0 / frameH, 0.0, 1.0);
            double bw = Math.Clamp((b.x1 - b.x0) / frameW, 0.0, 1.0);
            double bh = Math.Clamp((b.y1 - b.y0) / frameH, 0.0, 1.0);
            string name = b.ClassId >= 0 && b.ClassId < _classes.Length ? _classes[b.ClassId] : $"class{b.ClassId}";
            result.Add(new YoloDetection(name, b.ClassId, b.Score, bx, by, bw, bh));
        }
        return result.OrderByDescending(d => d.Score).ToList();
    }

    /// <summary>letterbox 空间中心点框 → 原帧像素坐标角点框（去灰边、除缩放）。纯函数，可单测。</summary>
    internal static (float x0, float y0, float x1, float y1, float Score, int ClassId) MapLetterboxToFrame(
        float cx, float cy, float w, float h, float score, int classId,
        float scale, int padX, int padY)
    {
        float x0 = (cx - w / 2f - padX) / scale;
        float y0 = (cy - h / 2f - padY) / scale;
        float x1 = (cx + w / 2f - padX) / scale;
        float y1 = (cy + h / 2f - padY) / scale;
        return (x0, y0, x1, y1, score, classId);
    }

    /// <summary>IoU 非极大值抑制（纯 C#，可单测）。分数降序贪心保留。</summary>
    public static List<(float x0, float y0, float x1, float y1, float Score, int ClassId)> NonMaxSuppression(
        IReadOnlyList<(float x0, float y0, float x1, float y1, float Score, int ClassId)> boxes, float iouThreshold)
    {
        var order = boxes.Select((b, i) => (b, i)).OrderByDescending(t => t.b.Score).ToList();
        var kept = new List<(float, float, float, float, float, int)>();
        var suppressed = new HashSet<int>();
        foreach (var (b, i) in order)
        {
            if (suppressed.Contains(i)) continue;
            kept.Add(b);
            foreach (var (_, j) in order)
            {
                if (j == i || suppressed.Contains(j)) continue;
                if (IoU(b.x0, b.y0, b.x1, b.y1, boxes[j].x0, boxes[j].y0, boxes[j].x1, boxes[j].y1) > iouThreshold)
                    suppressed.Add(j);
            }
        }
        return kept;
    }

    public static float IoU(float ax0, float ay0, float ax1, float ay1, float bx0, float by0, float bx1, float by1)
    {
        float ix0 = Math.Max(ax0, bx0), iy0 = Math.Max(ay0, by0);
        float ix1 = Math.Min(ax1, bx1), iy1 = Math.Min(ay1, by1);
        float iw = Math.Max(0f, ix1 - ix0), ih = Math.Max(0f, iy1 - iy0);
        float inter = iw * ih;
        float areaA = Math.Max(0f, ax1 - ax0) * Math.Max(0f, ay1 - ay0);
        float areaB = Math.Max(0f, bx1 - bx0) * Math.Max(0f, by1 - by0);
        float union = areaA + areaB - inter;
        return union <= 0f ? 0f : inter / union;
    }

    public void Dispose() => _session.Dispose();
}
