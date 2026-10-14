using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfootballBot.Ipc;

/// <summary>
/// GUI 壳 ↔ Worker 子进程 的命名管道协议（JSON Lines，一行一条 UTF-8 JSON）。
/// 命令方向 GUI→Worker：start/stop/pause/resume/preview/reloadConfig/ping
/// 事件方向 Worker→GUI：log/stats/screen/preview/pong/exited
/// </summary>
public static class PipeProtocol
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------- 消息模型 ----------------

    public sealed class Msg
    {
        // 命令（GUI→Worker）
        public string? Cmd { get; set; }          // start | stop | preview | reloadConfig | ping
        public string? Mode { get; set; }         // start: observe / cndaily / intl
        public bool? On { get; set; }             // preview: true/false

        // 事件（Worker→GUI）
        public string? Ev { get; set; }           // log | stats | screen | preview | pong | exited
        public string? Level { get; set; }        // log: Info/Success/Warn/Error/Debug
        public string? Text { get; set; }         // log.msg / exited.reason
        public string? Screen { get; set; }       // screen: 感知摘要文本
        public double Score { get; set; }         // screen.score
        public string? Evidence { get; set; }     // screen.evidence
        public string? Png { get; set; }          // preview: base64 PNG
        public int Matches { get; set; }          // stats
        public int KeyMatches { get; set; }
        public int PlayersMaxed { get; set; }  /// 满级提示次数（弃权场次已移除）
        public string? StartedAt { get; set; }    // stats ISO
        public string? LastError { get; set; }
    }

    // ---------------- 构造辅助 ----------------

    public static Msg CmdStart(string mode) => new() { Cmd = "start", Mode = mode };
    public static Msg CmdStop() => new() { Cmd = "stop" };
    public static Msg CmdPause(bool on) => new() { Cmd = on ? "pause" : "resume" };
    public static Msg CmdPreview(bool on) => new() { Cmd = "preview", On = on };
    public static Msg CmdReloadConfig() => new() { Cmd = "reloadConfig" };
    public static Msg CmdPing() => new() { Cmd = "ping" };

    public static Msg EvLog(string level, string msg) => new() { Ev = "log", Level = level, Text = msg };
    public static Msg EvScreen(string screen, double score, string? evidence)
        => new() { Ev = "screen", Screen = screen, Score = score, Evidence = evidence };
    public static Msg EvPreview(string pngBase64) => new() { Ev = "preview", Png = pngBase64 };
    public static Msg EvPong() => new() { Ev = "pong" };
    public static Msg EvExited(string reason) => new() { Ev = "exited", Text = reason };
    public static Msg EvStats(int matches, int keyMatches, int playersMaxed, DateTime? startedAt, string? lastError)
        => new()
        {
            Ev = "stats",
            Matches = matches,
            KeyMatches = keyMatches,
            PlayersMaxed = playersMaxed,
            StartedAt = startedAt?.ToString("o"),
            LastError = lastError,
        };

    // ---------------- JSON Lines 读写 ----------------

    public static string Serialize(Msg m) => JsonSerializer.Serialize(m, Opts);

    public static Msg? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize<Msg>(line, Opts); }
        catch { return null; }
    }

    /// <summary>向管道写入一条 JSON Lines 消息（自动加换行并 Flush）。</summary>
    public static async Task WriteLineAsync(Stream stream, Msg msg, CancellationToken ct = default)
    {
        var line = Serialize(msg) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>从管道持续读取 JSON Lines，逐条回调；断开时返回。</summary>
    public static Task ReadLoopAsync(Stream stream, Func<Msg, Task> onMsg, CancellationToken ct = default)
    {
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        return ReadLoopAsync(reader, onMsg, ct);
    }

    /// <summary>用已存在的 StreamReader 持续读取（握手阶段读 pong 后复用同一 reader，避免双 reader 抢缓冲）。</summary>
    public static async Task ReadLoopAsync(TextReader reader, Func<Msg, Task> onMsg, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { return; } // 管道断开
            if (line is null) return; // 对端关闭
            if (line.Length == 0) continue;
            var msg = TryDeserialize(line);
            if (msg is not null) await onMsg(msg);
        }
    }
}
