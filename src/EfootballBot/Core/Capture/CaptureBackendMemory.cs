using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfootballBot.Core.Capture;

/// <summary>捕获后端种类（与 provider 类型/WARP 开关一一对应）。</summary>
public enum CaptureBackendKind
{
    /// <summary>硬件设备 WGC（大多数机器的最优解）。</summary>
    Wgc,
    /// <summary>WARP 软渲染设备 WGC（虚拟显示器/远程会话兼容模式）。</summary>
    Warp,
    /// <summary>GDI PrintWindow 兜底（无 D3D 依赖）。</summary>
    Gdi,
}

/// <summary>
/// 记住本机上一次验证可用的捕获后端（data\capture-backend.json，原子写）。
/// 背景：虚拟显示器/无头显卡/远程会话机器上，硬件 WGC 会话建立后永远无帧，旧流程每次
/// 启动都要把「重建×6 → WARP×2 → 熔断」的探测链完整撞一遍（实测约 64s）才轮到 GDI。
/// 熔断判定结果落盘后，下次启动直接用已验证的后端，启动耗时从 1 分钟降到秒级。
/// 机器修复后（换显卡/修驱动）删除该文件即可恢复自动探测；运行中若更高的后端出帧成功，
/// 会自动覆盖回写实现自愈。
/// </summary>
public static class CaptureBackendMemory
{
    private sealed class State
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "wgc";
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("at")] public DateTime At { get; set; }
    }

    public sealed record Entry(CaptureBackendKind Kind, DateTime At, string? Reason);

    /// <summary>测试注入点：null=默认 exe 旁 data\capture-backend.json。</summary>
    internal static Func<string>? FilePathOverride;

    private static string FilePath => FilePathOverride?.Invoke()
        ?? Path.Combine(EfootballBot.Core.System.AppPaths.DataDir, "capture-backend.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读取上次判定。文件缺失/损坏/内容非法返回 null（走完整探测链）。</summary>
    public static Entry? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
            if (state is null) return null;
            return state.Kind switch
            {
                "wgc" => new Entry(CaptureBackendKind.Wgc, state.At, state.Reason),
                "warp" => new Entry(CaptureBackendKind.Warp, state.At, state.Reason),
                "gdi" => new Entry(CaptureBackendKind.Gdi, state.At, state.Reason),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>写入判定（原子写：tmp + overwrite move）。失败静默——记忆只是加速，不是正确性依赖。</summary>
    public static void Save(CaptureBackendKind kind, string reason)
    {
        try
        {
            string fp = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            var state = new State
            {
                Kind = kind switch
                {
                    CaptureBackendKind.Warp => "warp",
                    CaptureBackendKind.Gdi => "gdi",
                    _ => "wgc",
                },
                Reason = reason,
                At = DateTime.Now,
            };
            string tmp = fp + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Opts));
            File.Move(tmp, fp, overwrite: true);
        }
        catch { }
    }
}
