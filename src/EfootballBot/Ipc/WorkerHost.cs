using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Ipc;

/// <summary>
/// GUI 侧 Worker 管理器：启动本 exe 的 --worker 子进程，通过命名管道通信。
/// 崩溃/断开时触发 <see cref="Disconnected"/> 事件。
/// </summary>
public sealed class WorkerHost : IDisposable
{
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;
    private BotStats _lastStats = new();

    public event Action<string, string>? Log;           // level, msg
    public event Action<BotStats>? StatsChanged;
    public event Action<string, double, string?>? ScreenObserved; // screenName, score, evidence
    public event Action<string>? ProbeUpdated;          // top-5 candidates JSON
    public event Action<string, string>? Navigated;     // from, to
    public event Action<string>? PreviewReceived;       // pngBase64
    public event Action<string>? Exited;                // reason
    public event Action? Disconnected;                  // 管道断开 / 进程退出

    public bool IsRunning => _process is { HasExited: false };
    public BotStats LastStats => _lastStats;

    /// <summary>启动 Worker 子进程并建立管道连接。</summary>
    public async Task StartAsync(string mode)
    {
        if (_process is not null && !_process.HasExited)
        {
            // Worker 已在跑，直接发 start 命令
            await SendAsync(PipeProtocol.CmdStart(mode));
            return;
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法获取当前 exe 路径");

        // 管道名用 GUID 并通过命令行传给 Worker，避免猜测子进程 PID
        string pipeName = $"EfootballBot-Worker-{Guid.NewGuid():N}";

        var psi = new ProcessStartInfo(exePath, $"--worker {mode} --pipe {pipeName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Worker 子进程");

        // 等待 Worker 建立管道服务器。单文件自包含 exe 冷启动需解压（可能 10s+），给足 30s。
        // 关键：超时的 ConnectAsync 残留底层 CreateFile，可能被服务器"幽灵授予"，
        // 造成双方都 IsConnected 但数据永远到不了。故每次尝试用全新 client + ping/pong 握手验真。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        StreamReader? reader = null;
        NamedPipeClientStream? connectedPipe = null;
        Exception? lastConnectError = null;

        while (sw.ElapsedMilliseconds < 30000)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Worker 进程已退出（代码 {_process.ExitCode}）。{ReadWorkerError()}");

            if (!PipeExists(pipeName))
            {
                await Task.Delay(150);
                continue;
            }

            NamedPipeClientStream cand;
            try
            {
                cand = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await cand.ConnectAsync(500);
            }
            catch (Exception ex)
            {
                lastConnectError = ex;
                await Task.Delay(150);
                continue;
            }

            // 握手：发 ping，3s 内必须收到 pong，证明双向链路真实可用
            try
            {
                await PipeProtocol.WriteLineAsync(cand, PipeProtocol.CmdPing());
                var hr = new StreamReader(cand, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
                string? line;
                using (var pongCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    line = await hr.ReadLineAsync(pongCts.Token);
                var pong = line is null ? null : PipeProtocol.TryDeserialize(line);
                if (pong?.Ev != "pong")
                    throw new IOException($"握手响应异常：{line ?? "<null>"}");
                // 握手通过，reader 复用给正式读循环（不可新建第二个 StreamReader）
                connectedPipe = cand;
                reader = hr;
                break;
            }
            catch (Exception ex)
            {
                lastConnectError = ex;
                try { cand.Dispose(); } catch { }
                await Task.Delay(300);
            }
        }

        if (connectedPipe is null || reader is null)
            throw new InvalidOperationException(
                $"无法连接到 Worker 命名管道（30s 超时，管道名 {pipeName}，最后错误：{lastConnectError?.Message}）。{ReadWorkerError()}");

        _pipe = connectedPipe;
        Log?.Invoke("Info", $"Worker 管道已连接（耗时 {sw.ElapsedMilliseconds}ms），发送启动命令…");

        _readCts = new CancellationTokenSource();

        // 读循环：复用握手 reader 接收 Worker 事件
        _ = PipeProtocol.ReadLoopAsync(reader, async msg =>
        {
            try
            {
                switch (msg.Ev)
                {
                    case "log":
                        Log?.Invoke(msg.Level ?? "Info", msg.Text ?? "");
                        break;
                    case "stats":
                        _lastStats.MatchesDone = msg.Matches;
                        _lastStats.KeyMatches = msg.KeyMatches;
                        _lastStats.Forfeited = msg.Forfeited;
                        _lastStats.StartedAt = string.IsNullOrEmpty(msg.StartedAt)
                            ? null : DateTime.Parse(msg.StartedAt);
                        _lastStats.LastError = msg.LastError ?? "";
                        StatsChanged?.Invoke(_lastStats);
                        break;
                    case "screen":
                        ScreenObserved?.Invoke(msg.Screen ?? "未知", msg.Score, msg.Evidence);
                        break;
                    case "probe":
                        if (!string.IsNullOrEmpty(msg.Probe))
                            ProbeUpdated?.Invoke(msg.Probe);
                        break;
                    case "nav":
                        Navigated?.Invoke(msg.Text ?? "", msg.Screen ?? "");
                        break;
                    case "preview":
                        if (!string.IsNullOrEmpty(msg.Png))
                            PreviewReceived?.Invoke(msg.Png);
                        break;
                    case "exited":
                        Exited?.Invoke(msg.Text ?? "");
                        break;
                }
            }
            catch { /* 事件回调异常不中断读循环 */ }
        }, _readCts.Token);

        // 看门狗：监控进程退出
        _ = Task.Run(async () =>
        {
            try
            {
                if (_process is not null)
                    await _process.WaitForExitAsync();
            }
            catch { }
            finally
            {
                _readCts?.Cancel();
                Disconnected?.Invoke();
            }
        });

        // 发送 start 命令
        await SendAsync(PipeProtocol.CmdStart(mode));
    }

    /// <summary>发送 stop 命令，等待 5s 后若仍在跑则 Kill。</summary>
    public async Task StopAsync()
    {
        if (_pipe is { IsConnected: true })
        {
            try { await SendAsync(PipeProtocol.CmdStop()); }
            catch { }
        }

        if (_process is not null && !_process.HasExited)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { _process.Kill(); } catch { }
            }
        }

        _readCts?.Cancel();
        Cleanup();
    }

    /// <summary>发送预览开关命令。</summary>
    public Task SendPreviewAsync(bool on) => SendAsync(PipeProtocol.CmdPreview(on));

    /// <summary>枚举 \\.\pipe\ 判断 Worker 管道是否已存在（避免在服务器未监听时空连产生幽灵句柄）。</summary>
    private static bool PipeExists(string pipeName)
    {
        try
        {
            string prefix = @"\\.\pipe\" + pipeName;
            foreach (var p in Directory.EnumerateFiles(@"\\.\pipe\"))
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>读取 Worker 崩溃日志（%APPDATA%\EfootballBot\worker-error.log），供错误消息附带。</summary>
    private static string ReadWorkerError()
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EfootballBot", "worker-error.log");
            if (!File.Exists(p)) return "";
            // 只取最近的崩溃（文件可能被旧崩溃占着，超过 10 分钟前的忽略）
            if (DateTime.Now - File.GetLastWriteTime(p) > TimeSpan.FromMinutes(10)) return "";
            string text = File.ReadAllText(p);
            return $"Worker 错误：{(text.Length > 500 ? text[..500] : text)}";
        }
        catch { return ""; }
    }

    private async Task SendAsync(PipeProtocol.Msg msg)
    {
        if (_pipe is null || !_pipe.IsConnected)
            throw new InvalidOperationException("Worker 管道未连接");
        await PipeProtocol.WriteLineAsync(_pipe, msg);
    }

    private void Cleanup()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _process?.Kill(); } catch { }
        Cleanup();
    }
}
