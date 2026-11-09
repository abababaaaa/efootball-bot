using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// GUI/Worker ↔ Rust vision-svc 的 JSON Lines 消息。
/// 与 <c>rust/vision-svc/src/proto.rs</c> 中的 <c>Msg</c> 字段一一对应（小写 JSON 键）。
/// 约定与现有 <c>Ipc.PipeProtocol.Msg</c> 一致：单条扁平结构 + 全字段可空。
/// </summary>
public sealed class VisionMsg
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // —— 命令（GUI → vision-svc）——
    [JsonPropertyName("cmd")] public string? Cmd { get; set; }
    [JsonPropertyName("id")] public uint? Id { get; set; }

    // —— 事件（vision-svc → GUI）——
    [JsonPropertyName("ev")] public string? Ev { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }

    // —— init / 帧环 ——
    [JsonPropertyName("shm")] public string? Shm { get; set; }
    [JsonPropertyName("slots")] public uint? Slots { get; set; }
    [JsonPropertyName("hwnd")] public long? Hwnd { get; set; }
    [JsonPropertyName("fps")] public uint? Fps { get; set; }
    /// <summary>models 目录（v6/*.onnx + yolo/*）</summary>
    [JsonPropertyName("models")] public string? Models { get; set; }
    /// <summary>帧来源："capture" = 直接用 sidecar 刚捕获的帧（跳过上传拷贝）</summary>
    [JsonPropertyName("src")] public string? Src { get; set; }
    /// <summary>上传帧的共享内存名（OCR/detect 用）</summary>
    [JsonPropertyName("up")] public string? Up { get; set; }
    /// <summary>上传帧序号</summary>
    [JsonPropertyName("up_seq")] public ulong? UpSeq { get; set; }
    /// <summary>捕获后端：auto / dxgi / wgc</summary>
    [JsonPropertyName("backend")] public string? Backend { get; set; }

    // —— 帧 / 推理结果 ——
    [JsonPropertyName("seq")] public ulong? Seq { get; set; }
    [JsonPropertyName("w")] public uint? W { get; set; }
    [JsonPropertyName("h")] public uint? H { get; set; }
    [JsonPropertyName("stride")] public uint? Stride { get; set; }
    [JsonPropertyName("slot")] public uint? Slot { get; set; }
    [JsonPropertyName("ts")] public ulong? Ts { get; set; }
    [JsonPropertyName("ms")] public double? Ms { get; set; }
    [JsonPropertyName("pid")] public uint? Pid { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    // —— OCR ——
    [JsonPropertyName("words")] public List<SidecarWord>? Words { get; set; }
    // —— YOLO ——
    [JsonPropertyName("boxes")] public List<SidecarBox>? Boxes { get; set; }
    // —— 区域（ocr/detect 请求，归一化 [x,y,w,h]）——
    [JsonPropertyName("region")] public float[]? Region { get; set; }
    /// <summary>诊断文本（捕获内部指标，排障用）。</summary>
    [JsonPropertyName("dbg")] public string? Dbg { get; set; }
    // —— 预览 ——
    [JsonPropertyName("on")] public bool On { get; set; }

    public static VisionMsg CmdOf(string cmd, uint? id = null) => new() { Cmd = cmd, Id = id };

    public override string ToString() =>
        $"{{ev={Ev} cmd={Cmd} id={Id} {(Text is null ? "" : "text=" + Text)}}}";
}

/// <summary>OCR 词块（坐标归一化到 0~1，语义与 <c>OcrWord</c> 一致）。</summary>
public sealed class SidecarWord
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("w")] public float W { get; set; }
    [JsonPropertyName("h")] public float H { get; set; }
    [JsonPropertyName("s")] public float S { get; set; }
}

/// <summary>检测框（坐标归一化到 0~1，语义与 <c>YoloDetection</c> 一致）。</summary>
public sealed class SidecarBox
{
    [JsonPropertyName("c")] public int C { get; set; }
    [JsonPropertyName("s")] public float S { get; set; }
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("w")] public float W { get; set; }
    [JsonPropertyName("h")] public float H { get; set; }
}
