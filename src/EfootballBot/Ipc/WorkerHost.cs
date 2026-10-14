using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;

namespace EfootballBot.Ipc;

/// <summary>
/// GUI 侧 Worker 管理器：启动本 exe 的 --worker 子进程，通过命名管道通信。
/// 崩溃/断开时触发 <see cref="Disconnected"/> 事件。
/// 所有启停操作由 <see cref="_lifecycle"/> 串行化，快速连续点击开始/停止不会竞态。
/// </summary>
public sealed class WorkerHost : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;
    private BotStats _lastStats = new();
    private bool _disposed;

    public event Action<string, string>? Log;           // level, msg
    public event Action<BotStats>? StatsChanged;
    public event Action<string, double, string?>? ScreenObserved; // 感知摘要文本, 评分, 证据
    public event Action<string>? PreviewReceived;       // pngBase64
    public event Action<string>? Exited;                // reason
    public event Action? Disconnected;                  // 管道断开 / 进程退出

    public bool IsRunning => _process is { HasExited: false };
    public BotStats LastStats => _lastStats;

    /// <summary>启动 Worker 子进程并建立管道连接（与 StopAsync 互斥，重入时排队等待）。</summary>
    public async Task StartAsync(string mode)
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StartInternalAsync(mode);
        }
        catch
        {
            // 任何失败（连接超时/握手失败/NRE）都必须回收残留子进程与句柄，
            // 否则孤儿 Worker 会让下一次点击走进"进程还在但管道没连上"的死分支
            await TeardownAsync(killIfAlive: true);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StartInternalAsync(string mode)
    {
        // 已在跑且管道健康：直接发 start。管道为空说明是上次失败残留的孤儿，走下方重建
        if (_process is { HasExited: false } && _pipe is { IsConnected: true })
        {
            await SendAsync(PipeProtocol.CmdStart(mode));
            return;
        }
        if (_process is not null)
            await TeardownAsync(killIfAlive: true);

        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法获取当前 exe 路径");

        // 管道名用 GUID 并通过命令行传给 Worker，避免猜测子进程 PID
        string pipeName = $"EfootballBot-Worker-{Guid.NewGuid():N}";

        var psi = new ProcessStartInfo(exePath, $"--worker {mode} --pipe {pipeName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Worker 子进程");
        _process = process;

        // 等待 Worker 建立管道服务器。单文件自包含 exe 冷启动需解压（可能 10s+），给足 30s。
        // 关键：超时的 ConnectAsync 残留底层 CreateFile，可能被服务器"幽灵授予"，
        // 造成双方都 IsConnected 但数据永远到不了。故每次尝试用全新 client + ping/pong 握手验真。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        StreamReader? reader = null;
        NamedPipeClientStream? connectedPipe = null;
        Exception? lastConnectError = null;

        while (sw.ElapsedMilliseconds < 30000)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Worker 进程已退出（代码 {process.ExitCode}）。{ReadWorkerError()}");

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
        if (process.HasExited)
            throw new InvalidOperationException(
                $"握手后 Worker 进程已退出（代码 {process.ExitCode}）。{ReadWorkerError()}");

        _pipe = connectedPipe;
        var readCts = new CancellationTokenSource();
        _readCts = readCts;
        EmitLog("Info", $"Worker 管道已连接（耗时 {sw.ElapsedMilliseconds}ms），发送启动命令…");

        // 读循环：复用握手 reader 接收 Worker 事件。回调内任何异常吞掉，绝不让它冒到 StartAsync
        _ = PipeProtocol.ReadLoopAsync(reader, msg =>
        {
            try
            {
                switch (msg.Ev)
                {
                    case "log":
                        EmitLog(msg.Level ?? "Info", msg.Text ?? "");
                        break;
                    case "stats":
                        _lastStats.MatchesDone = msg.Matches;
                        _lastStats.KeyMatches = msg.KeyMatches;
                        _lastStats.PlayersMaxed = msg.PlayersMaxed;
                        _lastStats.StartedAt = string.IsNullOrEmpty(msg.StartedAt)
                            ? null : DateTime.Parse(msg.StartedAt);
                        _lastStats.LastError = msg.LastError ?? "";
                        StatsChanged?.Invoke(_lastStats);
                        break;
                    case "screen":
                        ScreenObserved?.Invoke(msg.Screen ?? "未知", msg.Score, msg.Evidence);
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
            catch { /* 事件回调异常不中断读循环，也不冒出调用方 */ }
            return Task.CompletedTask;
        }, readCts.Token);

        // 看门狗：局部捕获 process，Stop/Cleanup 置空字段也不会 NRE
        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync();
            }
            catch { }
            try { readCts.Cancel(); } catch { }
            try { Disconnected?.Invoke(); } catch { }
        });

        // 发送 start 命令
        await SendAsync(PipeProtocol.CmdStart(mode));
    }

    /// <summary>发送 stop 命令，等待 5s 后若仍在跑则 Kill。</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await TeardownAsync(killIfAlive: true);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>回收当前会话：发 stop → 等退出（5s）→ Kill 兜底 → 取消读循环 → 释放句柄。幂等。</summary>
    private async Task TeardownAsync(bool killIfAlive)
    {
        if (_pipe is { IsConnected: true })
        {
            try { await SendAsync(PipeProtocol.CmdStop()); }
            catch { }
        }

        var process = _process;
        if (process is not null && !process.HasExited)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                if (killIfAlive)
                    try { process.Kill(); } catch { }
            }
        }

        try { _readCts?.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { process?.Dispose(); } catch { }
        _process = null;
        _readCts = null;
    }

    private void EmitLog(string level, string text)
    {
        try { Log?.Invoke(level, text); } catch { /* 订阅方异常不影响管道流程 */ }
    }

    /// <summary>发送预览开关命令。</summary>
    public Task SendPreviewAsync(bool on) => SendAsync(PipeProtocol.CmdPreview(on));

    /// <summary>暂停/继续场景循环（暂停时 Worker 清空手柄输出并停止观察与操作）。</summary>
    public Task SendPauseAsync(bool on) => SendAsync(PipeProtocol.CmdPause(on));

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

    /// <summary>读取 Worker 崩溃日志（exe 旁 data\worker-error.log），供错误消息附带。</summary>
    private static string ReadWorkerError()
    {
        try
        {
            string p = AppPaths.ErrorLogPath("worker-error.log");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Dispose 可能在信号量被占用时调用，不等锁，直接硬回收
            try { _readCts?.Cancel(); } catch { }
            try { _process?.Kill(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
            _pipe = null;
            _process = null;
        }
        finally
        {
            _lifecycle.Dispose();
        }
    }
}
