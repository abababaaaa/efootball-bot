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
    private TaskCompletionSource<bool>? _stopAck;     // stop 命令的 Worker 确认（stopped 事件）
    private BotStats _lastStats = new();
    private bool _disposed;
    private volatile int _remotePid;                   // Worker 通过 pid 事件上报（诊断/兜底反查进程）

    // ---- 意外退出自动重启（3.2.0）----
    /// <summary>用户期望场景运行中（手动开始置位、手动停止/Dispose 复位）；仅此状态下才自动重启。</summary>
    private volatile bool _expectedRunning;
    private volatile bool _autoRestarting;            // 重启进行中：IsRunning 保持 true，GUI 不复位界面
    private string? _lastMode;
    private DateTime _lastAutoRestartAt = DateTime.MinValue;
    private int _autoRestartCount;
    private DateTime _lastRunStartAt = DateTime.MinValue;

    private static readonly TimeSpan MinRestartSpacing = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StableRunReset = TimeSpan.FromMinutes(5);
    private const int MaxConsecutiveRestarts = 3;
    private const int AutoRestartDelaySeconds = 3;

    public event Action<string, string>? Log;           // level, msg
    public event Action<BotStats>? StatsChanged;
    public event Action<string, double, string?>? ScreenObserved; // 感知摘要文本, 评分, 证据
    public event Action<string>? PreviewReceived;       // pngBase64
    public event Action<string>? Exited;                // reason
    public event Action? Disconnected;                  // 管道断开 / 进程退出（自动重启已放弃时才触发）

    /// <summary>需要用户动手才能继续的阻断（目前只有"真实手柄占着玩家 1"）：
    /// 退出原因文本，GUI 收到后弹窗说明，且不做自动重启。</summary>
    public event Action<string>? ActionRequired;

    /// <summary>Worker 最后一次"需要用户动手"的退出原因（自动重启分支里靠它止损）。</summary>
    private volatile string? _actionRequiredReason;

    /// <summary>场景在跑（见 <see cref="ScenarioRunning"/>）：start 命令送达即置位，
    /// stopped / exited / 管道断开 / 回收会话 / 启动失败时复位。</summary>
    private volatile bool _scenarioRunning;

    /// <summary>Worker 进程是否存活（含自动重启间隙）。仅表示"进程还活着"，**不代表场景在跑**。</summary>
    public bool IsRunning => !_disposed && (_autoRestarting || _process is { HasExited: false });

    /// <summary>
    /// 场景（Bot 引擎）是否在跑。GUI 的开始/暂停/热键/监控一律看这个标志。
    /// 为什么必须与 IsRunning 分开：Worker 收到 stop 或引擎启动失败后**进程仍保活等重连**
    /// （设计如此，便于再次开始时复用热进程），此时 IsRunning=true 但场景已结束。
    /// 旧实现统一用 IsRunning → "引擎启动失败 → 进程还活着 → GUI 永远认为在跑 →
    /// 所有开始按钮永久禁用（只有结束按钮没有、点开始也没反应）"，即用户报的
    /// 「非正常退出后开始卡住/永久点不动」。自动重启期间保持 true（重启即恢复同一场景）。
    /// </summary>
    public bool ScenarioRunning => !_disposed && _scenarioRunning;
    public BotStats LastStats => _lastStats;

    /// <summary>启动 Worker 子进程并建立管道连接（与 StopAsync 互斥，重入时排队等待）。</summary>
    public async Task StartAsync(string mode)
    {
        // 关闭竞态：Dispose 可能在启停进行中销毁信号量，Wait/Release 都必须容忍
        if (!await TryEnterLifecycleAsync()) return;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastMode = mode;
            _expectedRunning = true;
            // 用户手动开始 = 全新的信任预算：清空自动重启计数与上次的阻断原因
            _autoRestartCount = 0;
            _actionRequiredReason = null;
            await StartInternalAsync(mode);
        }
        catch
        {
            // 任何失败（连接超时/握手失败/NRE）都必须回收残留子进程与句柄，
            // 否则孤儿 Worker 会让下一次点击走进"进程还在但管道没连上"的死分支
            await TeardownAsync();
            throw;
        }
        finally
        {
            try { _lifecycle.Release(); }
            catch (ObjectDisposedException) { /* 关闭中：信号量已销毁 */ }
        }
    }

    private async Task StartInternalAsync(string mode)
    {
        // 已在跑且管道健康：直接发 start。管道为空说明是上次失败残留的孤儿，走下方重建
        if (_process is { HasExited: false } && _pipe is { IsConnected: true })
        {
            // ★先置位再发命令：Worker 可能秒回 exited（如找不到游戏窗口），
            // 若置位在发送之后，读循环先清零、这里再置 true → 永久"在跑"（按钮全禁）
            _scenarioRunning = true;
            await SendAsync(PipeProtocol.CmdStart(mode), CommandWriteTimeout);
            return;
        }
        if (_process is not null)
            await TeardownAsync();

        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法获取当前 exe 路径");

        // 管道名用 GUID 并通过命令行传给 Worker，避免猜测子进程 PID
        string pipeName = $"EfootballBot-Worker-{Guid.NewGuid():N}";
        // 多开：Worker 必须继承 GUI 的档案名，否则它会读写默认 data\ 下的配置
        string ProfileArg = AppPaths.Profile.Length == 0 ? "" : $" --profile {AppPaths.Profile}";

        // --parent：Worker 用它监视 GUI 进程；GUI 崩溃/被强杀时 Worker 自主停引擎、
        // 恢复真实手柄、还原停靠窗口后退出（否则孤儿 Worker 会一直跑着并占着玩家 1，
        // 让下一次「开始」被判定为手柄抢不到而反复失败）
        int parentPid = Environment.ProcessId;
        string workerArgs = $"--worker {mode} --pipe {pipeName} --parent {parentPid}{ProfileArg}";
        var psi = new ProcessStartInfo(exePath, workerArgs)
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
            if (process?.HasExited == true)
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
        if (process?.HasExited == true)
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
                    case "pid":
                        _remotePid = msg.Pid;
                        if (_process is null)
                        {
                            try { _process = Process.GetProcessById(msg.Pid); }
                            catch { /* 进程可能已退出 */ }
                        }
                        break;
                    case "exited":
                        _scenarioRunning = false;
                        // 手柄未让路这类"要用户动手"的退出：记下原因并通知 GUI 弹窗，
                        // 后续进程退出处理据此跳过自动重启（用户不解决重开也是同样结果）
                        if (EfootballBot.Core.Input.PadTakeoverBlockedException.IsBlocked(msg.Text))
                        {
                            var reason = EfootballBot.Core.Input.PadTakeoverBlockedException.StripCode(msg.Text!);
                            _actionRequiredReason = reason;
                            ActionRequired?.Invoke(reason);
                        }
                        Exited?.Invoke(msg.Text ?? "");
                        break;
                    case "stopped":
                        _scenarioRunning = false;
                        _stopAck?.TrySetResult(true);
                        break;
                }
            }
            catch { /* 事件回调异常不中断读循环，也不冒出调用方 */ }
            return Task.CompletedTask;
        }, readCts.Token);

        // 看门狗：局部捕获 process，Stop/Cleanup 置空字段也不会 NRE。
        // 进程退出后交给 HandleProcessExitAsync：用户手动停止→原样断开；
        // 意外退出→按预算自动重启恢复挂机（3.2.0，杀掉挂机的 SDK 原生崩溃不再需要人守）
        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync();
            }
            catch { }
            try { readCts.Cancel(); } catch { }
            await HandleProcessExitAsync(process);
        });

        // 发送 start 命令（同样的先置位原因，见上）
        _scenarioRunning = true;
        await SendAsync(PipeProtocol.CmdStart(mode), CommandWriteTimeout);
        _lastRunStartAt = DateTime.Now;
    }

    /// <summary>
    /// 进程退出处理：手动停止（_expectedRunning=false）或已销毁→照常触发 Disconnected；
    /// 意外退出→按预算自动重启：距上次自动重启 ≥5 分钟、连续 ≤3 次（稳定运行 5 分钟后计数归零）。
    /// 重启期间 <see cref="IsRunning"/> 保持 true，GUI 不会把重启间隙误判成"场景已结束"；
    /// 预算耗尽或重启失败才触发 Disconnected（GUI 复位界面）。
    /// </summary>
    private async Task HandleProcessExitAsync(Process? process)
    {
        if (_disposed || !_expectedRunning)
        {
            FireDisconnected();
            return;
        }
        try { await _lifecycle.WaitAsync(); }
        catch (ObjectDisposedException) { FireDisconnected(); return; }
        try
        {
            if (_disposed || !_expectedRunning)
            {
                FireDisconnected();
                return;
            }

            // 需要用户动手的退出（真实手柄占着玩家 1）：重开一次还是同样结果，
            // 直接收摊交回 GUI，由弹窗指引用户关手柄电源/拔线。
            if (_actionRequiredReason is { } blockedReason)
            {
                _actionRequiredReason = null;
                EmitLog("Error", $"场景已终止：{blockedReason}");
                await TeardownAsync();
                FireDisconnected();
                return;
            }

            var now = DateTime.Now;
            if (now - _lastRunStartAt >= StableRunReset)
                _autoRestartCount = 0; // 上一段稳定运行足够久：重置连续计数
            if (now - _lastAutoRestartAt < MinRestartSpacing)
            {
                EmitLog("Error", "Worker 意外退出，且距上次自动重启不足 5 分钟——不再重启，请查看日志排查后手动开始。");
                await TeardownAsync();
                FireDisconnected();
                return;
            }
            if (_autoRestartCount >= MaxConsecutiveRestarts)
            {
                EmitLog("Error", "Worker 意外退出，自动重启已连续 3 次——不再重启，请查看日志排查后手动开始。");
                await TeardownAsync();
                FireDisconnected();
                return;
            }

            _autoRestartCount++;
            _lastAutoRestartAt = now;
            EmitLog("Warn", $"Worker 意外退出，{AutoRestartDelaySeconds} 秒后自动重启并恢复挂机（第 {_autoRestartCount}/{MaxConsecutiveRestarts} 次）…");
            _autoRestarting = true;
            try
            {
                await TeardownAsync(); // 回收残留句柄（进程已退出，只清管道与 Process 对象）
                await Task.Delay(TimeSpan.FromSeconds(AutoRestartDelaySeconds));
                if (_disposed || !_expectedRunning) return; // 用户恰好在延迟内点了停止
                await StartInternalAsync(_lastMode!);
                _lastRunStartAt = DateTime.Now;
                EmitLog("Success", "Worker 已自动重启，挂机继续。");
            }
            catch (Exception ex)
            {
                EmitLog("Error", $"Worker 自动重启失败：{ex.Message}");
            }
            finally
            {
                _autoRestarting = false;
            }
            if ((_process is null or { HasExited: true }) && !(_pipe is { IsConnected: true }))
                FireDisconnected(); // 重启失败：交回 GUI 复位（子会话模式下管道在即视为已重启）
        }
        finally
        {
            try { _lifecycle.Release(); }
            catch (ObjectDisposedException) { /* 关闭中 */ }
        }
    }

    /// <summary>等待启停串行锁；超时（上一次操作卡死）返回 false，绝不让调用方永久排队。</summary>
    private async Task<bool> TryEnterLifecycleAsync()
    {
        var timeout = TimeSpan.FromSeconds(60);
        try
        {
            if (await _lifecycle.WaitAsync(timeout)) return true;
            EmitLog("Error", $"上一个启停操作仍未结束（超过 {timeout.TotalSeconds:F0}s），本次操作已放弃。请点「发现问题手动停止」或重启程序。");
            return false;
        }
        catch (ObjectDisposedException) { return false; } // 关闭中：Dispose 已硬回收
    }

    private void FireDisconnected()
    {
        try { Disconnected?.Invoke(); } catch { /* 订阅方异常不影响回收 */ }
    }

    /// <summary>发送 stop 命令，收到 Worker 确认（典型 <1s）即 Kill 回收；确认超时 2s 也直接 Kill。
    /// Worker 收 stop 本就不退出进程（保活等重连），旧实现固定等 5s 进程退出纯属白等。</summary>
    public async Task StopAsync()
    {
        _expectedRunning = false; // 用户主动停止：看门狗不再自动重启
        if (!await TryEnterLifecycleAsync())
        {
            // 上一次启停还卡着（Worker 半死）：不再排队，直接硬杀进程兜底
            try { _process?.Kill(); } catch { }
            _scenarioRunning = false;
            return;
        }
        try
        {
            await TeardownAsync();
        }
        finally
        {
            try { _lifecycle.Release(); }
            catch (ObjectDisposedException) { /* 关闭中：信号量已销毁 */ }
        }
    }

    /// <summary>回收当前会话：发 stop → 等 stopped 确认（≤2s）→ Kill 兜底 → 取消读循环 → 释放句柄。幂等。</summary>
    private async Task TeardownAsync()
    {
        _scenarioRunning = false;
        if (_pipe is { IsConnected: true })
        {
            try
            {
                var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopAck = ack;
                await SendAsync(PipeProtocol.CmdStop(), CommandWriteTimeout);
                await ack.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        var process = _process ?? ResolveRemoteProcess();
        if (process is not null && !process.HasExited)
        {
            try { process.Kill(); } catch { }
            // Kill 后短暂等退出，避免僵尸进程残留句柄（引擎已在 stop 时清理，无需宽限）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(cts.Token); } catch { }
        }

        _stopAck = null;
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

    /// <summary>按 Worker 上报的 pid 反查进程；跨会话 GetProcessById 可用。失败返回 null。</summary>
    private Process? ResolveRemoteProcess()
    {
        int pid = _remotePid;
        if (pid <= 0) return null;
        try
        {
            var p = Process.GetProcessById(pid);
            return p.HasExited ? null : p;
        }
        catch { return null; }
    }

    /// <summary>发送预览开关命令。用 start/stop 同级的写超时：自动开预览恰在开始挂机后，
    /// Worker 正忙着引擎启动（手柄接管/停靠，实测最长 20s+），5s 写超时会把「启动中」
    /// 误判成「Worker 可能已卡死」（2026-10-24 反馈包实机）。</summary>
    public Task SendPreviewAsync(bool on) => SendAsync(PipeProtocol.CmdPreview(on), CommandWriteTimeout);

    /// <summary>暂停/继续场景循环（暂停时 Worker 清空手柄输出并停止观察与操作）。</summary>
    public Task SendPauseAsync(bool on) => SendAsync(PipeProtocol.CmdPause(on));

    /// <summary>单步执行一拍（仅暂停状态有效）：观察一次 + 场景决策一次后继续暂停。</summary>
    public Task SendStepAsync() => SendAsync(PipeProtocol.CmdStep());

    /// <summary>手动一键测试满级换人：Worker 置位引擎请求，回联赛主页无弹窗时立即巡检。</summary>
    public Task SendSwapTestAsync() => SendAsync(PipeProtocol.CmdSwapTest());

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

    /// <summary>写命令到管道。★必须带超时：
    /// Worker 卡死/半死时管道写入可能永久阻塞，旧实现无超时会让 StartAsync/StopAsync
    /// 一直挂着、_lifecycle 永不释放，于是后续每次点开始都石沉大海（永久不可点）。</summary>
    private static readonly TimeSpan PipeWriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// start / stop 用更长的写超时：Worker 的引擎启动（WGC 建会话 + 手柄接管/让位最长 20s）
    /// 期间读循环是忙的，越小越容易把"启动中"误判成"Worker 卡死"。其余命令照旧 5s。
    /// </summary>
    private static readonly TimeSpan CommandWriteTimeout = TimeSpan.FromSeconds(25);

    private Task SendAsync(PipeProtocol.Msg msg) => SendAsync(msg, PipeWriteTimeout);

    private async Task SendAsync(PipeProtocol.Msg msg, TimeSpan timeout)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
            throw new InvalidOperationException("Worker 管道未连接");
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await PipeProtocol.WriteLineAsync(pipe, msg, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"向 Worker 发送命令超时（{timeout.TotalSeconds:F0}s），Worker 可能已卡死");
        }
    }

    /// <summary>
    /// 清理上一代 GUI 异常退出后遗留的 Worker（读 Worker 启动时写的 data\worker-session.txt）。
    /// Worker 自身有父进程看门狗，正常情况会自己退出；这里是旧版本 Worker / 看门狗失效时的兜底，
    /// 否则它会一直占着玩家 1，让新的「开始」被判"手柄抢不到"而反复失败。
    /// 只处理 pid 存活 + 映像路径与本程序一致 + 记录的父进程不是本次 GUI 的进程。
    /// </summary>
    public static void KillOrphanedWorkers(Action<string, string>? log = null)
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDir, "worker-session.txt");
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path).Trim();
            var parts = text.Split('|');
            File.Delete(path);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int pid) || !int.TryParse(parts[1], out int parentPid))
                return;
            if (pid == Environment.ProcessId) return;

            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch { return; } // 进程早已退出
            using (proc)
            {
                if (proc.HasExited) return;
                string self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string image = "";
                try { image = proc.MainModule?.FileName ?? ""; } catch { return; }
                if (self.Length == 0 || !string.Equals(self, image, StringComparison.OrdinalIgnoreCase)) return;

                log?.Invoke("Warn", $"发现上次异常退出遗留的挂机工作进程（PID {pid}，原 GUI {parentPid}），正在清理…");
                try { proc.Kill(entireProcessTree: true); } catch { try { proc.Kill(); } catch { } }
                try { proc.WaitForExit(3000); } catch { }
                log?.Invoke("Info", "遗留工作进程已清理，真实手柄与游戏窗口随后自动恢复。");
            }
        }
        catch { /* 清理失败不阻断启动 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expectedRunning = false; // 关闭程序：自动重启链终止
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
