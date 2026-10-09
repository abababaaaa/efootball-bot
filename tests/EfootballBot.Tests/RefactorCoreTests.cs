using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// ---------------- EP 降级顺序（纯函数） ----------------

public class OnnxProviderSelectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("nonsense")]
    public void BuildOrder_Auto_IsCudaThenDmlThenCpu(string? preferred)
    {
        var order = OnnxProviderSelector.BuildOrder(preferred);
        Assert.Equal(
            new[] { OnnxProvider.Cuda, OnnxProvider.DirectML, OnnxProvider.Cpu },
            order);
    }

    [Fact]
    public void BuildOrder_Cuda_KeepsDmlFallback()
    {
        Assert.Equal(
            new[] { OnnxProvider.Cuda, OnnxProvider.DirectML, OnnxProvider.Cpu },
            OnnxProviderSelector.BuildOrder("CUDA"));
    }

    [Theory]
    [InlineData("DML")]
    [InlineData("DirectML")]
    [InlineData("directml")]
    public void BuildOrder_Dml_SkipsCuda(string preferred)
    {
        Assert.Equal(
            new[] { OnnxProvider.DirectML, OnnxProvider.Cpu },
            OnnxProviderSelector.BuildOrder(preferred));
    }

    [Fact]
    public void BuildOrder_Cpu_OnlyCpu()
    {
        Assert.Equal(new[] { OnnxProvider.Cpu }, OnnxProviderSelector.BuildOrder("CPU"));
    }
}

// ---------------- YOLO 后处理（NMS / IoU / letterbox 坐标还原） ----------------

public class YoloPostProcessTests
{
    [Fact]
    public void IoU_IdenticalBoxes_IsOne()
    {
        float iou = YoloDetector.IoU(0, 0, 10, 10, 0, 0, 10, 10);
        Assert.Equal(1f, iou, 5);
    }

    [Fact]
    public void IoU_DisjointBoxes_IsZero()
    {
        Assert.Equal(0f, YoloDetector.IoU(0, 0, 10, 10, 20, 20, 30, 30), 5);
    }

    [Fact]
    public void IoU_HalfOverlappingBoxes_IsKnownValue()
    {
        // A=[0,0,10,10] B=[5,0,15,10]：交集 50，并集 150 → 1/3
        float iou = YoloDetector.IoU(0, 0, 10, 10, 5, 0, 15, 10);
        Assert.Equal(1f / 3f, iou, 4);
    }

    [Fact]
    public void NonMaxSuppression_KeepsHighestScore_SuppressesOverlap()
    {
        var boxes = new List<(float, float, float, float, float, int)>
        {
            (0, 0, 10, 10, 0.5f, 0),   // 与高分框重叠 → 被抑制
            (0, 0, 10, 10, 0.9f, 0),   // 最高分
            (100, 100, 110, 110, 0.7f, 1), // 不重叠 → 保留
        };
        var kept = YoloDetector.NonMaxSuppression(boxes, 0.45f);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0.9f, kept[0].Score, 5);
        Assert.Equal(0.7f, kept[1].Score, 5);
    }

    [Fact]
    public void NonMaxSuppression_SameClassSeparated_BothKept()
    {
        var boxes = new List<(float, float, float, float, float, int)>
        {
            (0, 0, 10, 10, 0.9f, 0),
            (100, 0, 110, 10, 0.8f, 0),
        };
        Assert.Equal(2, YoloDetector.NonMaxSuppression(boxes, 0.45f).Count);
    }

    [Fact]
    public void MapLetterboxToFrame_RestoresOriginalPixelCoords()
    {
        // 帧名 1296×759 → letterbox 640×640：scale = 640/1296 ≈ 0.49383，
        // padY = (640 - 759*scale)/2 ≈ 112.6 → 取整数版 113（与 Letterbox 取整一致量级）
        float scale = 640f / 1296f;
        int padX = 0;
        int padY = (640 - (int)Math.Round(759 * scale)) / 2;

        // 原帧像素 (400, 300) 的 20×20 框，letterbox 空间坐标：
        float cx = 400 * scale + padX;
        float cy = 300 * scale + padY;

        var (x0, y0, x1, y1, _, _) = YoloDetector.MapLetterboxToFrame(
            cx, cy, 20 * scale, 20 * scale, 0.9f, 0, scale, padX, padY);

        Assert.Equal(390f, x0, 2);
        Assert.Equal(290f, y0, 2);
        Assert.Equal(410f, x1, 2);
        Assert.Equal(310f, y1, 2);
    }
}

// ---------------- 官方 yolo26n 模型加载冒烟（模型未分发时自动跳过） ----------------

public class YoloModelSmokeTests
{
    [Fact]
    public void OfficialYolo26n_LoadsAndRunsForward()
    {
        string modelPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx");
        if (!File.Exists(modelPath)) return; // 模型未随测试输出分发时跳过

        string classesPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt");
        var factory = new OnnxSessionFactory("CPU");
        using var detector = new YoloDetector(modelPath, classesPath, factory);

        Assert.Equal("Cpu", factory.ActiveProvider);
        Assert.Contains("640×640", detector.ModelLabel);
        Assert.Contains("80类", detector.ModelLabel);

        // 全零帧跑一次前向：验证 letterbox → 推理 → 输出解析全链路不抛异常
        var frame = new FrameData(new byte[1280 * 720 * 4], 1280, 720, 0);
        var dets = detector.Detect(frame);
        Assert.NotNull(dets);
        Assert.Empty(dets);
    }
}

// ---------------- 配置往返（Inference / Yolo 新段） ----------------

public class AppConfigInferenceRoundTripTests
{
    private static string TempConfigPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "efootballbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    [Fact]
    public void InferenceAndYolo_RoundTrip()
    {
        var cfg = new AppConfig
        {
            Inference = new InferenceConfig { PreferredProvider = "DML" },
            Yolo = new YoloConfig { Enabled = false, ScoreThreshold = 0.4, NmsIou = 0.6 },
        };
        string path = TempConfigPath();
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(cfg));

            var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            Assert.NotNull(back);
            Assert.Equal("DML", back!.Inference.PreferredProvider);
            Assert.False(back.Yolo.Enabled);
            Assert.Equal(0.4, back.Yolo.ScoreThreshold);
            Assert.Equal(0.6, back.Yolo.NmsIou);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    [Fact]
    public void MissingSections_FallBackToDefaults()
    {
        // 旧配置文件没有 Inference/Yolo 段 → 反序列化应保留默认值
        const string json = """{"Theme":"Dark"}""";
        var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
        Assert.NotNull(back);
        Assert.Equal("Auto", back!.Inference.PreferredProvider);
        Assert.True(back.Yolo.Enabled);
        Assert.Equal(0.25, back.Yolo.ScoreThreshold);
        Assert.Equal(0.45, back.Yolo.NmsIou);
    }
}
