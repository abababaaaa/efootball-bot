using EfootballBot.Core.Config;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// Worker 进程级共享推理资源（P0-10）：ONNX 会话工厂 + OCR 引擎 + YOLO 感知管线。
///
/// 为什么需要：Worker 进程本来就常驻（停止后等 GUI 重连），但每次「开始挂机」都会
/// <c>new BotEngine(...)</c>，构造函数里重新 <c>new OcrRecognizer</c>（立即加载 PP-OCRv6
/// det+rec+cls 三个模型）再在 StartAsync 里重建 YOLO 会话——一次 1~3 秒，纯白给。
/// 这里按配置指纹缓存实例：配置没变就复用，配置变了才重建（旧实例 Dispose）。
///
/// 线程模型：只有 Worker 的 start 命令线程会调用 Acquire，全程单线程串行；
/// OCR 推理本身由观察循环单线程调用，跨引擎复用不会并发（同一时刻只有一个引擎）。
/// 生命周期：进程级，不随 BotEngine.Dispose 释放（BotEngine 只"归还"，不"销毁"）。
/// </summary>
public static class EngineServices
{
    private static readonly object Gate = new();

    private static OnnxSessionFactory? _factory;
    private static string _factoryKey = "";

    private static OcrRecognizer? _ocr;
    private static string _ocrKey = "";

    private static PerceptionPipeline? _perception;
    private static string _perceptionKey = "";

    /// <summary>拿共享的 ONNX 会话工厂（EP 探测只做一次）。</summary>
    public static OnnxSessionFactory AcquireFactory(AppConfig cfg, Action<string, LogLevel> log)
    {
        lock (Gate)
        {
            string key = cfg.Inference.PreferredProvider ?? "";
            if (_factory is not null && key == _factoryKey) return _factory;

            // OnnxSessionFactory 不实现 IDisposable（内部 SessionOptions 由进程回收），直接换新实例
            _factory = new OnnxSessionFactory(cfg.Inference.PreferredProvider, log);
            _factoryKey = key;
            return _factory;
        }
    }

    /// <summary>
    /// 拿共享 OCR 引擎。reused=true 表示直接复用了上次加载好的实例（本次没有模型加载耗时）。
    /// 配置指纹含引擎种类、外部服务地址、以及性能档（检测长边 / 行级重读）——
    /// 这些都会改变 OcrRecognizer 的构造参数，变了必须重建。
    /// </summary>
    public static OcrRecognizer AcquireOcr(AppConfig cfg, Action<string, LogLevel> log, out bool reused)
    {
        lock (Gate)
        {
            string key = OcrKey(cfg);
            if (_ocr is not null && key == _ocrKey)
            {
                reused = true;
                return _ocr;
            }

            var created = new OcrRecognizer(cfg.OcrEngine, unlimitedOcr: cfg.UnlimitedOcr, tuning: BuildTuning(cfg));
            var old = _ocr;
            _ocr = created;
            _ocrKey = key;
            reused = false;
            Retire(old);
            return created;
        }
    }

    /// <summary>拿共享感知管线（YOLO；模型缺失时返回仅 OCR 的管线）。</summary>
    public static PerceptionPipeline AcquirePerception(AppConfig cfg, OnnxSessionFactory factory,
        Action<string, LogLevel> log, out bool reused)
    {
        lock (Gate)
        {
            string key = PerceptionKey(cfg);
            if (_perception is not null && key == _perceptionKey)
            {
                reused = true;
                return _perception;
            }

            var created = PerceptionPipeline.Create(cfg, factory, log);
            var old = _perception;
            _perception = created;
            _perceptionKey = key;
            reused = false;
            Retire(old);
            return created;
        }
    }

    /// <summary>
    /// 延迟释放旧实例：配置变更（换 OCR 引擎/开性能档）时要换新实例，但旧引擎可能还有一次
    /// 推理在飞（OCR 单帧 ~1s）。立刻 Dispose 会让原生推理会话在调用中被销毁——那是不可捕获的
    /// 崩溃。这里统一延迟 5 秒再释放，覆盖任何在途调用；进程退出时由系统回收。
    /// </summary>
    private static void Retire(IDisposable? old)
    {
        if (old is null) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(5000).ConfigureAwait(false); } catch { }
            try { old.Dispose(); } catch { /* 释放失败不影响新实例 */ }
        });
    }

    /// <summary>进程退出前释放（正常流程由操作系统回收，这里只是给 --console 调试路径留口子）。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _perception?.Dispose(); } catch { }
            try { _ocr?.Dispose(); } catch { }
            _perception = null; _ocr = null; _factory = null;
            _perceptionKey = _ocrKey = _factoryKey = "";
        }
    }

    private static string OcrKey(AppConfig cfg)
        => $"{cfg.OcrEngine}|{cfg.OcrPerf.DetLimitSideLength}|{cfg.OcrPerf.LineReread}|" +
           $"{cfg.UnlimitedOcr.Endpoint}|{cfg.UnlimitedOcr.Model}|{cfg.UnlimitedOcr.TimeoutSeconds}";

    private static string PerceptionKey(AppConfig cfg)
        => $"{cfg.Yolo.Enabled}|{cfg.Yolo.ScoreThreshold:F3}|{cfg.Yolo.NmsIou:F3}";

    /// <summary>把配置里的性能档翻译成 OcrTuning（全关时返回 Off，与历史行为逐字节一致）。</summary>
    private static OcrTuning BuildTuning(AppConfig cfg)
    {
        int det = cfg.OcrPerf.DetLimitSideLength;
        bool reread = cfg.OcrPerf.LineReread;
        if (det <= 0 && !reread) return OcrTuning.Off;
        return new OcrTuning
        {
            DetLimitSideLength = det > 0 ? det : null,
            LineReread = reread,
        };
    }
}
