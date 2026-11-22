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
        /// <summary>这份 GDI 判定被直接复用（消费）过多少次。</summary>
        [JsonPropertyName("hits")] public int Hits { get; set; }
    }

    public sealed record Entry(CaptureBackendKind Kind, DateTime At, string? Reason, int Hits = 0);

    /// <summary>
    /// GDI 判定的有效期：超过这个时间、或已被复用 <see cref="MaxHits"/> 次，启动时强制重新探测一次。
    /// 理由（2026-11-19 实机反馈）：降级判定一旦落盘就永久生效——显卡驱动修好、远程会话结束后，
    /// 机器仍被一直锁在 GDI PrintWindow 上。而 GDI 在全屏 D3D 游戏里会成片返回陈旧帧，
    /// 一旦画面停更，下游的「画面变没变」判断（输入活性、页面识别）全部失效且无从察觉。
    /// 记忆只该加速启动，不该永久钉死后端。
    /// </summary>
    public static TimeSpan ReprobeAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>同一个 GDI 判定最多直接复用多少次（老机器不会永久跳过探测）。</summary>
    public const int MaxHits = 20;

    /// <summary>这条判定是否该作废重探。只对 GDI 生效：WGC/WARP 是已验证能出帧的后端，不需衰减。</summary>
    public static bool ShouldReprobe(Entry entry)
        => entry.Kind == CaptureBackendKind.Gdi
           && ((DateTime.Now - entry.At) > ReprobeAfter || entry.Hits >= MaxHits);

    /// <summary>消费一次记忆（仅 GDI 计数；写回失败静默——记忆只是加速，不是正确性依赖）。</summary>
    public static void NoteHit()
    {
        var entry = Load();
        if (entry is null || entry.Kind != CaptureBackendKind.Gdi) return;
        SaveState(CaptureBackendKind.Gdi, entry.Reason ?? "", entry.At, entry.Hits + 1);
    }

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
                "wgc" => new Entry(CaptureBackendKind.Wgc, state.At, state.Reason, state.Hits),
                "warp" => new Entry(CaptureBackendKind.Warp, state.At, state.Reason, state.Hits),
                "gdi" => new Entry(CaptureBackendKind.Gdi, state.At, state.Reason, state.Hits),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>写入判定（原子写：tmp + overwrite move）。失败静默——记忆只是加速，不是正确性依赖。</summary>
    public static void Save(CaptureBackendKind kind, string reason)
    {
        // 同一后端的再次确认**不刷新**判定时间与命中数：ObservationService 每次出帧成功都会
        // Save 一次，若这里刷成「刚刚」，衰减（ReprobeAfter / MaxHits）就永远差一点点、永不触发。
        // 判定时间代表「这个后端是唯一可用结论」第一次成立的时刻，那才是该过期的东西。
        var prev = Load();
        bool sameBackend = prev is not null && prev.Kind == kind;
        SaveState(kind, reason,
            sameBackend ? prev!.At : DateTime.Now,
            sameBackend ? prev!.Hits : 0);
    }

    /// <summary>按指定判定时间与消费次数落盘（NoteHit 专用：不能借 Save 把时间刷成“刚刚”，否则衰减永不触发）。</summary>
    private static void SaveState(CaptureBackendKind kind, string reason, DateTime at, int hits)
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
                At = at,
                Hits = hits,
            };
            string tmp = fp + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Opts));
            File.Move(tmp, fp, overwrite: true);
        }
        catch { }
    }
}
