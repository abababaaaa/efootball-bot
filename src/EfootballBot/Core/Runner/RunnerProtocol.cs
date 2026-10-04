using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfootballBot.Core.Runner;

// ---------- 上行：子进程 → 壳 ----------
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UpLog), "log")]
[JsonDerivedType(typeof(UpStats), "stats")]
[JsonDerivedType(typeof(UpScreen), "screen")]
[JsonDerivedType(typeof(UpPreview), "preview")]
[JsonDerivedType(typeof(UpHeartbeat), "hb")]
public abstract record UpMsg;

public sealed record UpLog(string Text, string Level) : UpMsg;
public sealed record UpStats(int MatchesDone, int KeyMatches, int NormalMatches, int Forfeited,
    string Scenario, string Screen, string Evidence, string LastError) : UpMsg;
public sealed record UpScreen(string Screen, double Score) : UpMsg;
public sealed record UpPreview(string JpegBase64, int Width, int Height) : UpMsg;
public sealed record UpHeartbeat(long Tick) : UpMsg;

// ---------- 下行：壳 → 子进程 ----------
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DownStop), "stop")]
public abstract record DownMsg;

public sealed record DownStop : DownMsg;

public static class RunnerProtocol
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 必须以基类为声明类型序列化，多态转换器才会写出 "type" 鉴别符；
    // 若传 msg.GetType()（具体派生类型），鉴别符不输出，DeserializeUp/Down 无法识别类型。
    public static string SerializeUp(UpMsg msg) => JsonSerializer.Serialize<UpMsg>(msg, Opts);
    public static string SerializeDown(DownMsg msg) => JsonSerializer.Serialize<DownMsg>(msg, Opts);

    public static UpMsg? DeserializeUp(string line)
    {
        try { return JsonSerializer.Deserialize<UpMsg>(line, Opts); }
        catch { return null; }
    }

    public static DownMsg? DeserializeDown(string line)
    {
        try { return JsonSerializer.Deserialize<DownMsg>(line, Opts); }
        catch { return null; }
    }
}
