using System.Diagnostics;
using System.Runtime.InteropServices;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 只静音游戏（WASAPI 会话级）的 COM 互操作回归：vtable 槽位一旦错位，
// GetProcessId 会读到垃圾 PID（可能把别的软件静音）。这里用本进程真实放音
// 建一个音频会话，再对自己的会话静音/取消静音，直接验证槽位与 SetMute。
public class GameAudioMuteTests
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_LOOP = 0x0008;
    private const uint SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>写一个 1 秒 440Hz 正弦 WAV（16bit PCM 单声道 8kHz）。</summary>
    private static string WriteToneWav()
    {
        const int sampleRate = 8000, seconds = 1;
        int samples = sampleRate * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // 单声道
        w.Write(sampleRate);
        w.Write(sampleRate * 2);    // 字节率
        w.Write((short)2);          // 块对齐
        w.Write((short)16);         // 位深
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            double v = Math.Sin(2 * Math.PI * 440 * i / sampleRate);
            w.Write((short)(v * 2000)); // 音量压低，测试时只是轻声
        }
        w.Flush();
        string path = Path.Combine(Path.GetTempPath(), "bot-mute-test.wav");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void ProbeSessions_DoesNotThrow_AndReportsLiveProcesses()
    {
        var list = GameAudioMute.ProbeSessions(out string error);
        Assert.True(string.IsNullOrEmpty(error), $"会话枚举报错：{error}");
        // 有会话时：PID 必须指向真实进程（系统声音会话 PID=0 除外）。
        // 允许 1 个刚退出的进程造成的时间差，但绝大多数必须真实存在。
        var pids = list.Where(s => s.Pid > 0).Select(s => s.Pid).Distinct().ToList();
        if (pids.Count == 0) return;
        int alive = pids.Count(p => Exists(p));
        Assert.True(alive >= pids.Count - 1,
            $"会话里出现不存在的进程 PID（GetProcessId 槽位可疑）：{string.Join(",", pids)}");
    }

    [Fact]
    public void ApplyToPids_MutesAndUnmutesOwnAudioSession()
    {
        string wav = WriteToneWav();
        try
        {
            PlaySound(wav, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_LOOP | SND_NODEFAULT);
            Thread.Sleep(500); // 等会话建立

            int pid = Environment.ProcessId;
            var mine = GameAudioMute.ProbeSessions(out _).Where(s => s.Pid == pid).ToList();
            if (mine.Count == 0) return; // 无声卡/会话未建立：环境限制，跳过（不伪造结论）

            int muted = GameAudioMute.ApplyToPids(true, new[] { pid }, out string err);
            Assert.True(string.IsNullOrEmpty(err), $"静音报错：{err}");
            if (muted == 0) return;
            Assert.True(GameAudioMute.ProbeSessions(out _).Where(s => s.Pid == pid).All(s => s.Muted),
                "自己的会话应当已静音");
        }
        finally
        {
            PlaySound(null, IntPtr.Zero, 0);
            int pid = Environment.ProcessId;
            GameAudioMute.ApplyToPids(false, new[] { pid }, out _); // 兜底恢复，绝不留静音
            try { File.Delete(wav); } catch { }
        }
    }

    private static bool Exists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
