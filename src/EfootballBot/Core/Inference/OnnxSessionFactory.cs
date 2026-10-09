using EfootballBot.Core.Bot;
using Microsoft.ML.OnnxRuntime;

namespace EfootballBot.Core.Inference;

/// <summary>ONNX Runtime 执行提供者（EP）。</summary>
public enum OnnxProvider
{
    /// <summary>NVIDIA CUDA（需 N 卡 + CUDA 原生库；未随包分发，尝试必失败后降级）。</summary>
    Cuda,
    /// <summary>DirectML（任意 D3D12 GPU：AMD/Intel/NVIDIA，本项目的实际加速路径）。</summary>
    DirectML,
    /// <summary>CPU（兜底）。</summary>
    Cpu,
}

/// <summary>
/// EP 优先级选择（纯函数，可单测）：
/// Auto = CUDA → DirectML → CPU；可按配置强制单一提供者（失败仍降级到 CPU 兜底）。
/// </summary>
public static class OnnxProviderSelector
{
    public static IReadOnlyList<OnnxProvider> BuildOrder(string? preferred) => preferred?.Trim().ToUpperInvariant() switch
    {
        "CUDA" => new[] { OnnxProvider.Cuda, OnnxProvider.DirectML, OnnxProvider.Cpu },
        "DIRECTML" or "DML" => new[] { OnnxProvider.DirectML, OnnxProvider.Cpu },
        "CPU" => new[] { OnnxProvider.Cpu },
        _ => new[] { OnnxProvider.Cuda, OnnxProvider.DirectML, OnnxProvider.Cpu },
    };
}

/// <summary>
/// ONNX Runtime SessionOptions 工厂：CUDA → DirectML → CPU 三级降级。
/// 进程内共享同一份 SessionOptions（OCR 与 YOLO 共用同一原生 onnxruntime.dll，
/// EP 注册一次、多个 InferenceSession 复用），实际生效的 EP 通过日志输出。
/// </summary>
public sealed class OnnxSessionFactory
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<OnnxProvider> _order;
    private readonly Action<string, LogLevel>? _log;
    private SessionOptions? _options;

    /// <summary>实际生效的 EP 名称（首次创建 SessionOptions 后有值）。</summary>
    public string? ActiveProvider { get; private set; }

    public OnnxSessionFactory(string? preferredProvider, Action<string, LogLevel>? log = null)
    {
        _order = OnnxProviderSelector.BuildOrder(preferredProvider);
        _log = log;
    }

    /// <summary>获取共享 SessionOptions（懒创建，进程内只做一次 EP 探测）。</summary>
    public SessionOptions GetSessionOptions()
    {
        lock (_gate)
        {
            if (_options is not null) return _options;
            _options = BuildSessionOptions(_order, s =>
            {
                ActiveProvider = s;
                _log?.Invoke($"ONNX Runtime 执行提供者 = {s}", LogLevel.Info);
            }, m => _log?.Invoke(m, LogLevel.Debug));
            return _options;
        }
    }

    /// <summary>
    /// 按顺序注册 EP：GPU EP 优先（失败/无原生库则异常降级），CPU 永远最后追加
    /// 承接 GPU 不支持的算子分区。全部失败时仅剩 CPU。
    /// </summary>
    internal static SessionOptions BuildSessionOptions(
        IReadOnlyList<OnnxProvider> order,
        Action<string>? onEngaged,
        Action<string>? onSkip = null)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        var failures = new List<string>();
        OnnxProvider engaged = OnnxProvider.Cpu;

        foreach (var p in order)
        {
            if (p == OnnxProvider.Cpu) continue; // CPU 统一最后追加
            try
            {
                switch (p)
                {
                    case OnnxProvider.Cuda:
                        opts.AppendExecutionProvider_CUDA(0);
                        break;
                    case OnnxProvider.DirectML:
                        opts.AppendExecutionProvider_DML(0);
                        break;
                }
                engaged = p;
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{p}: {ex.Message}");
                onSkip?.Invoke($"EP {p} 不可用，降级：{ex.Message}");
            }
        }

        try { opts.AppendExecutionProvider_CPU(1); }
        catch { /* 已注册过则忽略 */ }

        onEngaged?.Invoke(engaged.ToString());
        if (failures.Count > 0 && onSkip is not null)
            onSkip($"EP 降级记录：{string.Join("；", failures)}");
        return opts;
    }
}
