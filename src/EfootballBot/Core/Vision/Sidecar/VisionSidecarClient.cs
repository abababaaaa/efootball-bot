using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// Rust <c>vision-svc</c> 的托管客户端：拉起进程、建管道 server、握手 init、收发 JSON Lines、读共享内存帧。
/// 方向约定沿用现有 Worker：**GUI/Worker 建 server，sidecar 作为 client 连进来**（与
/// <c>Ipc.WorkerHost</c> 完全同构），因此 sidecar 也能在调试时用 <c>--selftest</c> 单独跑。
/// </summary>
public sealed class VisionSidecarClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<VisionMsg>> _pending = new();
    private readonly StringBuilder _stderr;
    private readonly Task _readLoop;
    private int _nextId;

    public string PipeName { get; }
    /// <summary>帧环（init ack 之后必定非空）。</summary>
    public SharedFrameRing Ring { get; private set; } = null!;
    public string? Version { get; private set; }
    public int Pid => _proc.Id;
    /// <summary>非请求/响应类事件（frame / log / error）。</summary>
    public event Action<VisionMsg>? Notification;
    /// <summary>调试用：收到的每一行原始 JSON（含 ready/ack），排障时挂它。</summary>
    public static event Action<string>? RawTrace;
    private UploadFrameRing? _up;
    private readonly object _upGate = new();

    /// <summary>最近一次 detect 的 sidecar 诊断文本（dims/max_score）。</summary>
    public string? LastDetectInfo { get; private set; }

    public string StderrSnapshot { get { lock (_stderr) { return _stderr.ToString(); } } }

    /// <summary>
    /// 只在管道**已连接**之后构造：StreamWriter 的 AutoFlush 会在构造时立即写一次，
    /// 未连接的 NamedPipeServerStream 上这样做会抛 "Pipe hasn't been connected yet"。
    /// </summary>
    private VisionSidecarClient(Process proc, string pipeName, NamedPipeServerStream pipe, StringBuilder stderr)
    {
        _proc = proc;
        PipeName = pipeName;
        _pipe = pipe;
        _stderr = stderr;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>启动 sidecar 并完成 init（帧环就绪）。失败会杀掉子进程并抛出原因。</summary>
    public static async Task<VisionSidecarClient> StartAsync(
        VisionSidecarConfig cfg, nint hwnd, int width, int height, CancellationToken ct = default)
    {
        var exe = VisionSidecarLocator.Locate(cfg.ExePath);
        if (exe is null)
        {
            throw new FileNotFoundException(
                "找不到 vision-svc.exe（先跑 rust 侧 cargo build --release，或在配置 VisionSidecar.ExePath 指定路径）");
        }

        var pipeName = $"EfootballBot-Vision-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (cfg.Verbose) psi.ArgumentList.Add("--console");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 8000);
            }
        };
        proc.OutputDataReceived += (_, _) => { };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            try { pipe.Dispose(); } catch { }
            throw new InvalidOperationException($"启动 vision-svc 失败：{ex.Message}", ex);
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await pipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            try { pipe.Dispose(); } catch { }
            string snapshot;
            lock (stderr) { snapshot = stderr.ToString(); }
            throw new TimeoutException($"vision-svc 在 20 秒内没有连上管道 {pipeName}（stderr: {snapshot}）");
        }

        var client = new VisionSidecarClient(proc, pipeName, pipe, stderr);

        var shmName = $"Local\\EfootballVision-v1-{pipeName}";
        var init = new VisionMsg
        {
            Cmd = "init",
            Shm = shmName,
            Slots = 2,
            W = (uint)Math.Max(2, width),
            H = (uint)Math.Max(2, height),
            Hwnd = hwnd,
            Backend = cfg.Backend,
            Models = SidecarModelLocator.Locate(cfg.ModelsDir),
            Up = client.EnsureUploadRing(),
        };

        var ack = await client.SendAsync(init, 15000, ct).ConfigureAwait(false);
        if (ack.Ev == "error")
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"vision-svc init 失败：{ack.Text}（stderr: {client.StderrSnapshot}）");
        }

        try
        {
            client.Ring = SharedFrameRing.Open(ack.Shm ?? shmName, 3000);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"帧环打开失败：{ex.Message}", ex);
        }

        client.Version = ack.Version;
        return client;
    }

    /// <summary>上传帧环名（init 时告知 sidecar；首次调用时创建）。</summary>
    public string EnsureUploadRing()
    {
        lock (_upGate)
        {
            _up ??= new UploadFrameRing($"Local\\EfootballVision-up-{PipeName}", 3840, 2160);
            return _up.Name;
        }
    }

    /// <summary>
    /// 对任意一帧跑 sidecar OCR（帧走上传共享内存，不经管道）。
    /// 返回的 <see cref="OcrResult"/> 坐标与传入帧的宽高一致（归一化）。
    /// </summary>
    /// <param name="fromCapture">true = 这一帧就是本侧车刚捕获的帧，直接用，不写上传共享内存。</param>
    public async Task<OcrResult> RecognizeAsync(FrameData frame, bool fromCapture = false, CancellationToken ct = default)
    {
        VisionMsg req;
        if (fromCapture)
        {
            req = new VisionMsg { Cmd = "ocr", Src = "capture" };
        }
        else
        {
            long seq;
            lock (_upGate)
            {
                _up ??= new UploadFrameRing($"Local\\EfootballVision-up-{PipeName}", 3840, 2160);
                seq = _up.Publish(frame);
            }
            req = new VisionMsg { Cmd = "ocr", UpSeq = (ulong)seq };
        }
        var reply = await SendAsync(req, 60000, ct).ConfigureAwait(false);
        if (reply.Ev == "error")
            throw new InvalidOperationException($"sidecar OCR 失败：{reply.Text}");
        var words = (reply.Words ?? new List<SidecarWord>())
            .Select(w => new OcrWord(w.T, w.X, w.Y, w.W, w.H))
            .ToList();
        return new OcrResult(words, frame.Width, frame.Height);
    }

    /// <summary>
    /// 对任意一帧跑 sidecar YOLO 检测（帧同样走上传共享内存）。
    /// </summary>
    public async Task<IReadOnlyList<(int C, float S, float X, float Y, float W, float H)>> DetectAsync(
        FrameData frame, bool fromCapture = false, CancellationToken ct = default)
    {
        VisionMsg req;
        if (fromCapture)
        {
            req = new VisionMsg { Cmd = "detect", Src = "capture" };
        }
        else
        {
            long seq;
            lock (_upGate)
            {
                _up ??= new UploadFrameRing($"Local\\EfootballVision-up-{PipeName}", 3840, 2160);
                seq = _up.Publish(frame);
            }
            req = new VisionMsg { Cmd = "detect", UpSeq = (ulong)seq };
        }
        var reply = await SendAsync(req, 60000, ct).ConfigureAwait(false);
        if (reply.Ev == "error")
            throw new InvalidOperationException($"sidecar detect 失败：{reply.Text}");
        LastDetectInfo = reply.Text;
        return (reply.Boxes ?? new List<SidecarBox>())
            .Select(b => (b.C, b.S, b.X, b.Y, b.W, b.H))
            .ToList();
    }

    /// <summary>发一条命令并等待同 id 的响应（ack / frame / error 都算响应）。</summary>
    public async Task<VisionMsg> SendAsync(VisionMsg cmd, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var id = (uint)Interlocked.Increment(ref _nextId);
        cmd.Id = id;
        var tcs = new TaskCompletionSource<VisionMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(cmd, VisionMsg.Json)).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new VisionMsg { Ev = "error", Id = id, Text = $"等到 {timeoutMs}ms 无响应" };
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                try { RawTrace?.Invoke(line is null ? "<EOF>" : line); } catch { }
                if (line is null) break;
                if (line.Length == 0) continue;

                VisionMsg? msg;
                try { msg = JsonSerializer.Deserialize<VisionMsg>(line, VisionMsg.Json); }
                catch (JsonException ex)
                {
                    try { RawTrace?.Invoke("<parse-error> " + ex.Message); } catch { }
                    continue;
                }
                if (msg is null) continue;

                if (msg.Id is { } id)
                {
                    var matched = _pending.TryRemove(id, out var tcs);
                    try { RawTrace?.Invoke($"<id {id} pending={matched}>"); } catch { }
                    if (matched)
                    {
                        tcs!.TrySetResult(msg);
                        continue;
                    }
                }

                if (msg.Ev == "ready" && msg.Version is not null) Version = msg.Version;
                try { Notification?.Invoke(msg); } catch { /* 订阅方异常不影响管道 */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var kv in _pending)
                kv.Value.TrySetResult(new VisionMsg { Ev = "error", Id = kv.Key, Text = $"管道断开: {ex.Message}" });
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe.IsConnected)
                await SendAsync(VisionMsg.CmdOf("shutdown"), 800).ConfigureAwait(false);
        }
        catch { }

        _cts.Cancel();
        try { await _readLoop.ConfigureAwait(false); } catch { }
        try { _pipe.Dispose(); } catch { }
        TryKill(_proc);
        try { _proc.Dispose(); } catch { }
        Ring?.Dispose();
        lock (_upGate) { _up?.Dispose(); _up = null; }
    }
}

/// <summary>models 目录定位：配置路径 → 程序目录 → 仓库源码目录（开发期）。</summary>
public static class SidecarModelLocator
{
    public static string Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        var candidates = new List<string>
        {
            Path.Combine(AppPaths.ExeDir, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
        };
        var dir = new DirectoryInfo(AppPaths.ExeDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir is null) break;
            candidates.Add(Path.Combine(dir.FullName, "src", "EfootballBot", "models"));
        }
        return candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "v6")));
    }
}

/// <summary>vision-svc.exe 定位：配置路径 → 程序目录 → 开发期 cargo 产物。</summary>
public static class VisionSidecarLocator
{
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var exeDir = AppPaths.ExeDir;
        var candidates = new List<string>
        {
            Path.Combine(exeDir, "vision-svc.exe"),
            Path.Combine(exeDir, "vision", "vision-svc.exe"),
            Path.Combine(AppContext.BaseDirectory, "vision-svc.exe"),
        };

        var dir = new DirectoryInfo(exeDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir is null) break;
            candidates.Add(Path.Combine(dir.FullName, "rust", "bin", "vision-svc.exe"));
            candidates.Add(Path.Combine(dir.FullName, "rust", "target", "release", "vision-svc.exe"));
            candidates.Add(Path.Combine(dir.FullName, "rust", "target", "debug", "vision-svc.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
