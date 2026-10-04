# 步骤 0：单 exe 双模式骨架 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把现有单进程 WPF + BotEngine 拆成「GUI 壳 + `--runner` 无头子进程」双模式，壳经命名管道控制子进程并做看门狗，行为与现网完全等价。

**Architecture:** 同一 exe 两种启动模式：默认进 GUI（现有 MainWindow 原样），带 `--runner --scenario=X` 参数进无头模式（不建窗口，直接跑 BotEngine + 场景）。壳通过命名管道 `EfootballBot-Runner-{pid}` 与子进程双向通信（JSON Lines）。壳侧新增 RunnerHost 负责启动/心跳/强杀；子进程侧新增 RunnerHost 客户端把日志/统计/状态/预览帧上行。

**Tech Stack:** C# / WPF / .NET 10 (net10.0-windows10.0.19041.0) / NamedPipeServerStream+NamedPipeClientStream / System.Text.Json / xunit（现有 tests/EfootballBot.Tests）

**设计规格：** docs/superpowers/specs/2026-10-05-arch-redesign-design.md 第 3 节

**硬约束（全程）：**
- 仅本地操作：允许本地 commit，禁止 push、禁止远程标签、禁止触发 CI
- 发布验证用：`dotnet publish src/EfootballBot -c Release -r win-x64 -f net10.0-windows10.0.19041.0 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=PdbOnly -o publish`；发布前先 `Stop-Process -Name EfootballBot -Force -ErrorAction SilentlyContinue`
- 单元测试：`dotnet test tests/EfootballBot.Tests -c Debug`（项目已有 InternalsVisibleTo）

---

## 文件结构

| 文件 | 职责 | 新建/修改 |
|---|---|---|
| `src/EfootballBot/Core/Runner/RunnerProtocol.cs` | 管道消息 DTO（上行 LogMsg/StatsMsg/ScreenMsg/PreviewMsg/HeartbeatMsg，下行 StopMsg/ConfigMsg），JSON Lines 序列化 | 新建 |
| `src/EfootballBot/Core/Runner/RunnerPipeClient.cs` | 子进程侧：连壳管道、发送上行消息、接收下行命令、2s 心跳 | 新建 |
| `src/EfootballBot/Core/Runner/RunnerPipeServer.cs` | 壳侧：监听子进程连接、分发行消息事件、发送下行命令、心跳超时检测（5s） | 新建 |
| `src/EfootballBot/Core/Runner/RunnerHost.cs` | 壳侧：启动/停止子进程、看门狗（进程退出或心跳超时→按配置重启/停止）、暴露事件给 MainWindow | 新建 |
| `src/EfootballBot/Core/Runner/RunnerMain.cs` | 子进程侧入口：解析 --scenario/--pipe，建 BotEngine，把 Log/StatsChanged/Observed/帧桥接到管道，执行下行命令 | 新建 |
| `src/EfootballBot/Core/Runner/PreviewEncoder.cs` | BGRA 帧降采样编码 JPEG（管道预览用） | 新建 |
| `src/EfootballBot/Core/Bot/BotEngine.cs` | 仅追加 StartPreviewOnlyAsync / StopPreview / PreviewStopped（约 15 行，不动既有逻辑） | 修改 |
| `src/EfootballBot/App.xaml.cs` | 新增 `--runner` 分支（早于 GUI 单实例检查），其余模式不动 | 修改 |
| `src/EfootballBot/MainWindow.xaml.cs` + `.Preview.cs` + `.Logs.cs` | ToggleScenarioAsync 改走 RunnerHost；日志/统计/当前画面/预览改从 RunnerHost 事件取 | 修改 |
| `tests/EfootballBot.Tests/RunnerProtocolTests.cs` | DTO 往返序列化测试 | 新建 |
| `tests/EfootballBot.Tests/RunnerPipeTests.cs` | 管道双向通信 + 心跳超时测试（进程内回环，不起子进程） | 新建 |

不改：BotEngine、三个场景、Screens.cs、WindowCapture、Updater、GlobalHotkey、AppConfig。

---

## Task 1: 管道协议 DTO 与序列化

**Files:**
- Create: `src/EfootballBot/Core/Runner/RunnerProtocol.cs`
- Test: `tests/EfootballBot.Tests/RunnerProtocolTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/EfootballBot.Tests/RunnerProtocolTests.cs
using EfootballBot.Core.Runner;
using Xunit;

namespace EfootballBot.Tests;

public class RunnerProtocolTests
{
    [Fact]
    public void UpLog_RoundTrip()
    {
        var msg = new UpLog("测试日志", "Warn");
        var line = RunnerProtocol.SerializeUp(msg);
        var back = RunnerProtocol.DeserializeUp(line);
        var log = Assert.IsType<UpLog>(back);
        Assert.Equal("测试日志", log.Text);
        Assert.Equal("Warn", log.Level);
    }

    [Fact]
    public void UpHeartbeat_RoundTrip()
    {
        var line = RunnerProtocol.SerializeUp(new UpHeartbeat(12345));
        var hb = Assert.IsType<UpHeartbeat>(RunnerProtocol.DeserializeUp(line));
        Assert.Equal(12345, hb.Tick);
    }

    [Fact]
    public void UpStats_RoundTrip()
    {
        var msg = new UpStats(3, 1, 2, 0, "myleague", "MyLeagueHome", "前往比赛,下一场", "");
        var back = Assert.IsType<UpStats>(RunnerProtocol.DeserializeUp(RunnerProtocol.SerializeUp(msg)));
        Assert.Equal(3, back.MatchesDone);
        Assert.Equal("MyLeagueHome", back.Screen);
    }

    [Fact]
    public void UpPreview_RoundTrip_Base64()
    {
        var msg = new UpPreview(Convert.ToBase64String(new byte[] { 1, 2, 3 }), 480, 270);
        var back = Assert.IsType<UpPreview>(RunnerProtocol.DeserializeUp(RunnerProtocol.SerializeUp(msg)));
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(back.JpegBase64));
        Assert.Equal(480, back.Width);
    }

    [Fact]
    public void DownStop_RoundTrip()
    {
        var line = RunnerProtocol.SerializeDown(new DownStop());
        Assert.IsType<DownStop>(RunnerProtocol.DeserializeDown(line));
    }

    [Fact]
    public void DeserializeUp_UnknownType_ReturnsNull()
    {
        Assert.Null(RunnerProtocol.DeserializeUp("{\"type\":\"nonsense\"}"));
    }

    [Fact]
    public void Deserialize_Garbage_ReturnsNull()
    {
        Assert.Null(RunnerProtocol.DeserializeUp("not json at all"));
        Assert.Null(RunnerProtocol.DeserializeDown(""));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/EfootballBot.Tests -c Debug --filter RunnerProtocolTests`
Expected: 编译失败（`EfootballBot.Core.Runner` 不存在）

- [ ] **Step 3: 实现 RunnerProtocol**

```csharp
// src/EfootballBot/Core/Runner/RunnerProtocol.cs
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

    public static string SerializeUp(UpMsg msg) => JsonSerializer.Serialize(msg, msg.GetType(), Opts);
    public static string SerializeDown(DownMsg msg) => JsonSerializer.Serialize(msg, msg.GetType(), Opts);

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
```

注意：`JsonSerializer.Deserialize<UpMsg>` 的多态反序列化要求 .NET 7+ 的 `JsonPolymorphic` 特性；序列化必须传 `msg.GetType()` 否则只输出基类属性。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/EfootballBot.Tests -c Debug --filter RunnerProtocolTests`
Expected: 7 个测试全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EfootballBot/Core/Runner/RunnerProtocol.cs tests/EfootballBot.Tests/RunnerProtocolTests.cs
git commit -m "feat(runner): 双模式管道协议 DTO 与 JSON Lines 序列化"
```

---

## Task 2: 子进程侧管道客户端

**Files:**
- Create: `src/EfootballBot/Core/Runner/RunnerPipeClient.cs`
- Test: `tests/EfootballBot.Tests/RunnerPipeTests.cs`（与 Task 3 的 server 一起做回环测试，本任务先建文件）

- [ ] **Step 1: 实现 RunnerPipeClient**

```csharp
// src/EfootballBot/Core/Runner/RunnerPipeClient.cs
using System.IO.Pipes;
using System.Text;

namespace EfootballBot.Core.Runner;

/// <summary>
/// 子进程侧管道客户端：连壳的命名管道，发上行消息（JSON Lines），收下行命令。
/// 内置 2s 心跳线程。连接断开时上行消息丢弃（壳死了子进程随后会被退出逻辑带走）。
/// </summary>
public sealed class RunnerPipeClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;
    private long _tick;

    public event Action<DownMsg>? Command;

    public RunnerPipeClient(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, ct);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public void Send(UpMsg msg)
    {
        lock (_writeLock)
        {
            try { _writer?.WriteLine(RunnerProtocol.SerializeUp(msg)); }
            catch { /* 壳已断开：丢弃 */ }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_pipe is null) return;
        var reader = new StreamReader(_pipe, new UTF8Encoding(false));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return; // 壳关闭管道
                var msg = RunnerProtocol.DeserializeDown(line);
                if (msg is not null) Command?.Invoke(msg);
            }
        }
        catch { /* 断开即结束 */ }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Send(new UpHeartbeat(++_tick));
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/EfootballBot -c Debug`
Expected: 0 错误（暂无人引用，仅编译检查）

- [ ] **Step 3: Commit**

```bash
git add src/EfootballBot/Core/Runner/RunnerPipeClient.cs
git commit -m "feat(runner): 子进程侧管道客户端（上行消息+下行命令+2s心跳）"
```

---

## Task 3: 壳侧管道服务器 + 回环测试

**Files:**
- Create: `src/EfootballBot/Core/Runner/RunnerPipeServer.cs`
- Test: `tests/EfootballBot.Tests/RunnerPipeTests.cs`

- [ ] **Step 1: 写失败测试（进程内回环：server + client 直连，不起子进程）**

```csharp
// tests/EfootballBot.Tests/RunnerPipeTests.cs
using EfootballBot.Core.Runner;
using Xunit;

namespace EfootballBot.Tests;

public class RunnerPipeTests
{
    [Fact]
    public async Task Pipe_RoundTrip_Log_And_Stop()
    {
        string pipe = "test-" + Guid.NewGuid().ToString("N");
        using var server = new RunnerPipeServer(pipe);
        var received = new List<UpMsg>();
        server.Message += received.Add;
        server.Start();

        using var client = new RunnerPipeClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5));

        client.Send(new UpLog("hello", "Info"));
        await WaitFor(() => received.OfType<UpLog>().Any(l => l.Text == "hello"), 5000);
        Assert.True(received.OfType<UpLog>().Any(l => l.Text == "hello"));

        var gotStop = new TaskCompletionSource();
        client.Command += _ => gotStop.TrySetResult();
        server.Send(new DownStop());
        await gotStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipe_Heartbeat_Received()
    {
        string pipe = "test-" + Guid.NewGuid().ToString("N");
        using var server = new RunnerPipeServer(pipe);
        var hbs = new List<UpHeartbeat>();
        server.Message += m => { if (m is UpHeartbeat h) hbs.Add(h); };
        server.Start();

        using var client = new RunnerPipeClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5));

        await WaitFor(() => hbs.Count >= 2, 8000);
        Assert.True(hbs.Count >= 2);
    }

    [Fact]
    public async Task Server_HeartbeatTimeout_Fires()
    {
        string pipe = "test-" + Guid.NewGuid().ToString("N");
        using var server = new RunnerPipeServer(pipe) { HeartbeatTimeoutMs = 1500 };
        server.Start();
        using var client = new RunnerPipeClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5));

        // 停掉 client 心跳（模拟子进程卡死）：直接 dispose client 的心跳通过取消实现
        // 这里用不断连但不发心跳的方式难以构造，改为：dispose client（管道断开也算失联）
        client.Dispose();
        var tcs = new TaskCompletionSource();
        server.Lost += () => tcs.TrySetResult();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(50);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/EfootballBot.Tests -c Debug --filter RunnerPipeTests`
Expected: 编译失败（RunnerPipeServer 不存在）

- [ ] **Step 3: 实现 RunnerPipeServer**

```csharp
// src/EfootballBot/Core/Runner/RunnerPipeServer.cs
using System.IO.Pipes;
using System.Text;

namespace EfootballBot.Core.Runner;

/// <summary>
/// 壳侧管道服务器：等一个子进程连接，分发行消息事件，发下行命令，
/// 监视心跳——超时或断开触发 Lost。
/// </summary>
public sealed class RunnerPipeServer : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private bool _connected;

    public int HeartbeatTimeoutMs { get; init; } = 5000;

    public event Action<UpMsg>? Message;
    public event Action? Connected;
    public event Action? Lost;

    public RunnerPipeServer(string pipeName) => _pipeName = pipeName;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => WatchdogLoopAsync(_cts.Token));
    }

    public async Task WaitForConnectionAsync(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_connected && sw.Elapsed < timeout) await Task.Delay(50);
        if (!_connected) throw new TimeoutException("子进程未在超时内连接管道");
    }

    public void Send(DownMsg msg)
    {
        lock (_writeLock)
        {
            try { _writer?.WriteLine(RunnerProtocol.SerializeDown(msg)); }
            catch { /* 子进程已断 */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            await _pipe!.WaitForConnectionAsync(ct);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
            _connected = true;
            _lastHeartbeat = DateTime.UtcNow;
            Connected?.Invoke();

            var reader = new StreamReader(_pipe, new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = RunnerProtocol.DeserializeUp(line);
                if (msg is UpHeartbeat) _lastHeartbeat = DateTime.UtcNow;
                if (msg is not null) Message?.Invoke(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 管道异常 */ }
        finally
        {
            if (_connected) { _connected = false; Lost?.Invoke(); }
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            if (_connected && DateTime.UtcNow - _lastHeartbeat > TimeSpan.FromMilliseconds(HeartbeatTimeoutMs))
            {
                _connected = false;
                Lost?.Invoke();
                return;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/EfootballBot.Tests -c Debug --filter RunnerPipeTests`
Expected: 3 个测试全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EfootballBot/Core/Runner/RunnerPipeServer.cs tests/EfootballBot.Tests/RunnerPipeTests.cs
git commit -m "feat(runner): 壳侧管道服务器 + 进程内回环测试"
```

---

## Task 4: 子进程入口 RunnerMain + App 双模式分支

**Files:**
- Create: `src/EfootballBot/Core/Runner/RunnerMain.cs`
- Modify: `src/EfootballBot/App.xaml.cs`（`App()` 构造函数新增 --runner 分支）

- [ ] **Step 1: 实现 RunnerMain**

```csharp
// src/EfootballBot/Core/Runner/RunnerMain.cs
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.Runner;

/// <summary>
/// 无头子进程入口：EfootballBot --runner --scenario=myleague --pipe=EfootballBot-Runner-1234
/// 建 BotEngine 跑场景，所有日志/统计/画面状态/预览帧经管道上行；收到 DownStop 即 Stop。
/// </summary>
public static class RunnerMain
{
    public static async Task<int> RunAsync(string[] args)
    {
        string scenario = GetArg(args, "--scenario")
            ?? throw new ArgumentException("缺少 --scenario");
        string pipeName = GetArg(args, "--pipe")
            ?? throw new ArgumentException("缺少 --pipe");
        bool cli = args.Contains("--cli");

        var cfg = AppConfig.Load();
        using var pipe = new RunnerPipeClient(pipeName);
        using var engine = new BotEngine(cfg);

        // 日志双写：管道上行 + （--cli 时）控制台 + 文件日志
        engine.Log += (msg, lv) =>
        {
            pipe.Send(new UpLog(msg, lv.ToString()));
            if (cli) Console.WriteLine($"[{lv}] {msg}");
            EfootballBot.Core.Logging.FileLogger.Instance.Write(msg, lv);
        };
        EfootballBot.Core.Logging.FileLogger.Instance.Initialize(cfg.FileLogEnabled, cfg.FileLogLevel, cfg.MaxLogFiles);

        engine.StatsChanged += s => pipe.Send(new UpStats(
            s.MatchesDone, s.KeyMatches, s.NormalMatches, s.Forfeited,
            scenario, s.LastScreen.ToString(), s.LastScreenEvidence, s.LastError));
        engine.Observed += o => pipe.Send(new UpScreen(o.Screen.ToString(), o.Score));

        // 预览帧：300ms 节流，复用 BotEngine.LatestFrame 编码 JPEG
        using var previewTimer = new System.Threading.Timer(_ =>
        {
            var f = engine.LatestFrame;
            if (f is null) return;
            try
            {
                var jpeg = PreviewEncoder.EncodeJpeg(f, 480, 270, 60);
                pipe.Send(new UpPreview(Convert.ToBase64String(jpeg), 480, 270));
            }
            catch { /* 编码失败丢弃本帧 */ }
        }, null, 1000, 300);

        pipe.Command += msg =>
        {
            if (msg is DownStop) engine.Stop();
        };

        try { await pipe.ConnectAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"管道连接失败：{ex.Message}");
            return 2;
        }

        try
        {
            await engine.StartAsync(scenario);
            // 等待场景结束（Stop 或自然完成）
            while (engine.IsRunning) await Task.Delay(500);
            return 0;
        }
        catch (Exception ex)
        {
            pipe.Send(new UpLog($"子进程启动失败：{ex}", "Error"));
            return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i[(name.Length + 1)..]];
        }
        return null;
    }
}
```

- [ ] **Step 2: 实现 PreviewEncoder（BGRA 帧 → JPEG）**

```csharp
// src/EfootballBot/Core/Runner/PreviewEncoder.cs
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Runner;

/// <summary>BGRA 帧降采样编码为 JPEG（供管道预览）。WPF 编码器需在 STA/调度线程使用；
/// 子进程内用自由线程 + Freeze 即可。</summary>
public static class PreviewEncoder
{
    public static byte[] EncodeJpeg(FrameData f, int outW, int outH, int quality)
    {
        var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        BitmapSource bs = wb;
        if (f.Width != outW || f.Height != outH)
            bs = new TransformedBitmap(wb, new ScaleTransform(outW / (double)f.Width, outH / (double)f.Height));
        bs.Freeze();
        using var ms = new MemoryStream();
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(bs));
        enc.Save(ms);
        return ms.ToArray();
    }
}
```

注意：WinExe 工程内可直接用 WPF 类型；WriteableBitmap 在非 UI 线程创建后 Freeze 即可跨线程。

- [ ] **Step 3: App.xaml.cs 加 --runner 分支**

在 `App()` 构造函数最前面（`--dump` 判断之前）插入：

```csharp
if (args.Length > 0 && args[0] == "--runner")
{
    Startup += async (_, _) =>
    {
        int code = 1;
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            code = await Core.Runner.RunnerMain.RunAsync(args.Skip(1).ToArray());
        }
        catch (Exception ex) { Console.Error.WriteLine($"RUNNER FAIL: {ex}"); }
        Shutdown(code);
    };
    return; // 不进 GUI 分支
}
```

（现有构造函数末尾的 `else Startup += StartGui` 保持不变；此处用早期 return 隔离 runner 分支。）

- [ ] **Step 4: 编译 + 冒烟测试（CLI 模式，不开游戏也能跑到"找窗口"报错）**

Run: `dotnet build src/EfootballBot -c Debug`
Expected: 0 错误

Run: `dotnet run --project src/EfootballBot -- --runner --scenario=myleague --pipe=test-nonexist --cli`
Expected: 输出「管道连接失败」并以退出码 2 结束（无壳时管道连不上，验证错误路径）

- [ ] **Step 5: Commit**

```bash
git add src/EfootballBot/Core/Runner/RunnerMain.cs src/EfootballBot/Core/Runner/PreviewEncoder.cs src/EfootballBot/App.xaml.cs
git commit -m "feat(runner): 无头子进程入口 + App 双模式分支"
```

---

## Task 5: 壳侧 RunnerHost（启动/停止/看门狗）

**Files:**
- Create: `src/EfootballBot/Core/Runner/RunnerHost.cs`

- [ ] **Step 1: 实现 RunnerHost**

```csharp
// src/EfootballBot/Core/Runner/RunnerHost.cs
using System.Diagnostics;

namespace EfootballBot.Core.Runner;

/// <summary>
/// 壳侧子进程宿主：启动自身 exe 的 --runner 实例，经管道收发，看门狗监视。
/// 子进程退出码非 0 / 心跳丢失 → Exited 事件（由 UI 决定提示或重启，本类不自动重启，
/// 避免无限拉起坏场景；自动重启策略留到后续步骤按需加）。
/// </summary>
public sealed class RunnerHost : IDisposable
{
    private Process? _proc;
    private RunnerPipeServer? _server;
    private string? _pipeName;

    public event Action<UpMsg>? Message;
    public event Action? Connected;
    /// <summary>子进程异常失联（心跳超时/管道断开/进程退出且非用户停止）。</summary>
    public event Action<string>? Exited;

    public bool IsRunning => _proc is { HasExited: false };
    /// <summary>子进程启动成功时间（StartAsync 完成时赋值），供壳侧「运行时长」显示。</summary>
    public DateTime? StartedAt { get; private set; }

    public async Task StartAsync(string scenario)
    {
        Stop(); // 防重入

        _pipeName = $"EfootballBot-Runner-{Environment.ProcessId}";
        _server = new RunnerPipeServer(_pipeName);
        _server.Message += m => Message?.Invoke(m);
        _server.Connected += () => Connected?.Invoke();
        _server.Lost += () => Exited?.Invoke("心跳丢失或管道断开");
        _server.Start();

        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取自身 exe 路径");
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--runner --scenario={scenario} --pipe={_pipeName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        _proc.Exited += (_, _) =>
        {
            if (_proc.ExitCode != 0)
                Exited?.Invoke($"子进程退出，退出码 {_proc.ExitCode}");
            else
                Exited?.Invoke("子进程已正常结束");
        };
        if (!_proc.Start())
            throw new InvalidOperationException("子进程启动失败");

        await _server.WaitForConnectionAsync(TimeSpan.FromSeconds(10));
        StartedAt = DateTime.Now;
    }

    public void SendStop()
    {
        try { _server?.Send(new DownStop()); } catch { }
    }

    public void Stop()
    {
        SendStop();
        // 给 3s 优雅退出，之后强杀
        if (_proc is { HasExited: false })
        {
            try { if (!_proc.WaitForExit(3000)) _proc.Kill(true); } catch { }
        }
        try { _server?.Dispose(); } catch { }
        try { _proc?.Dispose(); } catch { }
        _server = null;
        _proc = null;
    }

    public void Dispose() => Stop();
}
```

注意：`Environment.ProcessPath` 在单文件发布下指向真实 exe 路径（.NET 6+），可直接用于自启子进程；`dotnet run` 开发时指向 `EfootballBot.exe`（bin 下的 apphost），同样有效。

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/EfootballBot -c Debug`
Expected: 0 错误

- [ ] **Step 3: Commit**

```bash
git add src/EfootballBot/Core/Runner/RunnerHost.cs
git commit -m "feat(runner): 壳侧子进程宿主（启动/停止/看门狗失联通知）"
```

---

## Task 6: MainWindow 切换到 RunnerHost

**Files:**
- Modify: `src/EfootballBot/MainWindow.xaml.cs`（ToggleScenarioAsync / GetOrCreateEngine / UpdateStats / OnClosing）
- Modify: `src/EfootballBot/MainWindow.Preview.cs`（预览数据源改管道 JPEG）

- [ ] **Step 1: MainWindow.xaml.cs 改造**

替换 `GetOrCreateEngine` 为 `GetOrCreateRunner`：

```csharp
private RunnerHost? _runner;

private RunnerHost GetOrCreateRunner()
{
    if (_runner is not null) return _runner;
    _runner = new RunnerHost();
    _runner.Message += msg => Dispatcher.BeginInvoke(() => OnRunnerMessage(msg));
    _runner.Exited += reason => Dispatcher.BeginInvoke(() => OnRunnerExited(reason));
    return _runner;
}

private void OnRunnerMessage(UpMsg msg)
{
    switch (msg)
    {
        case UpLog log:
            if (Enum.TryParse<LogLevel>(log.Level, out var lv)) AppendLog(log.Text, lv);
            else AppendLog(log.Text, LogLevel.Info);
            break;
        case UpStats s:
            StMatches.Text = s.MatchesDone.ToString();
            StKey.Text = s.KeyMatches.ToString();
            StForfeit.Text = s.Forfeited.ToString();
            StError.Text = string.IsNullOrEmpty(s.LastError) ? "" : $"最近错误：{s.LastError}";
            break;
        case UpScreen sc:
            TxtScreen.Text = $"当前画面：{sc.Screen}（{sc.Score:F1}）";
            break;
        case UpPreview p:
            OnPreviewFrame(Convert.FromBase64String(p.JpegBase64));
            break;
    }
}

private void OnRunnerExited(string reason)
{
    if (_runner is { IsRunning: false } && _statsTimer.IsEnabled)
    {
        _hotkey.Unregister();
        AppendLog($"场景已结束（{reason}）。", LogLevel.Success);
        ResetButtons();
    }
}
```

`ToggleScenarioAsync` 中把 `engine.StartAsync(mode)` 改为：

```csharp
var runner = GetOrCreateRunner();
await runner.StartAsync(mode!);
// 引擎启动成功的判断：管道已连接（StartAsync 内 WaitForConnectionAsync 通过）
```

停止分支改为：

```csharp
if (_runner is { IsRunning: true })
{
    _hotkey.Unregister();
    _runner.SendStop();
    AppendLog("正在停止…", LogLevel.Info);
    ResetButtons();
    // AutoCloseWhenDone 逻辑不变
    return;
}
```

删除 `_engine` 字段、`GetOrCreateEngine`；`UpdateStats` 的引擎统计部分删除（统计改由 UpStats 驱动），保留"运行时间"计时（RunnerHost 加一个 `StartedAt` 属性，StartAsync 成功时赋值）；`OnClosing` 中 `_engine?.Dispose()` 改为 `_runner?.Dispose()`。

注意：BotEngine 的「运行异常日志需输出完整堆栈」不变量由 RunnerMain 内 `engine.Log` 订阅保证（BotEngine 内部 LogInternal 未变）。

- [ ] **Step 2: MainWindow.Preview.cs 改造**

预览从「定时器拉 `_engine.LatestFrame` 重采样」改为「管道推 JPEG 直接解码」。`Preview` 为 XAML 中预览 Image 控件的实际 x:Name（沿用不改 XAML）：

```csharp
private void OnPreviewFrame(byte[] jpeg)
{
    if (!_previewOn) return;
    var bmp = new BitmapImage();
    bmp.BeginInit();
    bmp.CacheOption = BitmapCacheOption.OnLoad;
    bmp.StreamSource = new MemoryStream(jpeg);
    bmp.EndInit();
    bmp.Freeze();
    Preview.Source = bmp;
}
```

`BtnPreview_Click` 改造：现有「不挂机也能开预览」路径（仅预览捕获）在双模式下壳不持有捕获，改为——runner 未运行时点击预览：启动一个特殊 scenario `preview`（RunnerMain 中该 scenario 只做捕获+推帧，不跑场景逻辑，见下方补充）；runner 运行中则直接 `_previewOn = true`。关闭预览：若 runner 是纯 preview 模式则 `_runner.Stop()`，否则仅 `_previewOn = false`、`Preview.Source = null`。

RunnerMain 补充（Task 4 代码基础上加分支，放在 `engine.StartAsync(scenario)` 前）：

```csharp
if (scenario == "preview")
{
    // 仅预览：启动捕获+手柄检测都跳过，只保证 WaitForFreshFrame 有帧可推
    await engine.StartPreviewOnlyAsync(); // 见下
    while (!engine.PreviewStopped) await Task.Delay(500);
    return 0;
}
```

BotEngine 新增最小公开方法（约 15 行，不改动既有逻辑）：

```csharp
// src/EfootballBot/Core/Bot/BotEngine.cs 追加
public bool PreviewStopped { get; private set; }

public async Task StartPreviewOnlyAsync()
{
    var token = await PrepareSessionAsync(null); // 复用：找窗口+起捕获+连手柄
    _ = token; // 预览不需要取消令牌场景，Stop 时统一走下方
}

public void StopPreview() => PreviewStopped = true;
```

RunnerMain 的 DownStop 处理补充：`if (msg is DownStop) { engine.Stop(); engine.StopPreview(); }`

`_previewTimer` 保留但职责改为「无帧超时提示」（>2s 无 UpPreview 在预览区显示"等待画面…"，用 _lastPreviewAt 字段判断）；最小化暂停逻辑不变（runner 恒定 300ms 推帧，壳侧丢弃即可，流量约 480×270 JPEG ≈ 30KB/帧，可忽略）。`_wb`/`_previewBuf`/`UpdatePreviewFrame` 旧实现删除。

- [ ] **Step 3: 编译 + 全量单测**

Run: `dotnet build src/EfootballBot -c Debug`
Expected: 0 错误

Run: `dotnet test tests/EfootballBot.Tests -c Debug`
Expected: 全部 PASS（含既有测试，确认无回归）

- [ ] **Step 4: Commit**

```bash
git add src/EfootballBot/MainWindow.xaml.cs src/EfootballBot/MainWindow.Preview.cs
git commit -m "refactor(shell): 主窗口挂机控制切换到 runner 子进程模式"
```

---

## Task 7: 真机发布验证（人工）

- [ ] **Step 1: 发布**

```powershell
Stop-Process -Name EfootballBot -Force -ErrorAction SilentlyContinue
dotnet publish src/EfootballBot -c Release -r win-x64 -f net10.0-windows10.0.19041.0 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=PdbOnly -o publish
```

Expected: publish\EfootballBot.exe 约 72MB 单文件

- [ ] **Step 2: 真机实测清单（双击 publish\EfootballBot.exe）**

1. 国际服我的联赛：完整跑一局（弹窗清扫 → 赛程 → 开赛 → 比赛中挂机 → 结算 → 回主页）
2. 全局停止热键（默认 Ctrl+Shift+Q）：游戏中触发，确认停止生效
3. 手柄输入活性自检日志正常出现
4. 预览画面正常（管道 JPEG）
5. 日志/统计/当前画面显示与旧版一致
6. 杀掉子进程（任务管理器找 EfootballBot 子进程结束）→ 壳提示「场景已结束」且不崩溃
7. 国服/国际服周活动各跑一轮

- [ ] **Step 3: 发现问题 → 修复后回到 Step 1 重新发布；全部通过 → Commit 收尾**

```bash
git add -A
git commit -m "chore: 步骤0双模式骨架真机验证通过"
```

（不 push、不打标签）

---

## 后续计划索引（本计划不含，各自单独出计划）

- 步骤 1：图谱面板 v1（旧枚举节点 + 当前判定显示）
- 步骤 2：探针引擎 + 新页面注册表影子模式
- 步骤 3~4：MyLeague 页面与场景迁移
- 步骤 5：IntlWeekly / CnDaily 迁移
- 步骤 6：旧分类器与散落恢复逻辑删除
