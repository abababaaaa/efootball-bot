using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Logging;
using EfootballBot.Ipc;

namespace EfootballBot.Worker;

/// <summary>
/// 无头工作进程：接收 GUI 命令，运行 BotEngine，事件通过命名管道回传。
/// 管道名：EfootballBot-Worker-{pid}
/// </summary>
public static class WorkerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        // 解析参数：--worker <mode> [--pipe <name>] [--console]
        string mode = args.Length > 1 ? args[1] : "myleague";
        bool console = args.Contains("--console");

        int pid = Environment.ProcessId;
        int pipeIdx = Array.IndexOf(args, "--pipe");
        // 管道名优先用 GUI 传入的（GUID，无 PID 猜测）；缺省退回 PID 约定
        string pipeName = pipeIdx >= 0 && pipeIdx + 1 < args.Length
            ? args[pipeIdx + 1]
            : $"EfootballBot-Worker-{pid}";

        // 初始化文件日志（Worker 独立进程，需自己初始化）
        var cfg = AppConfig.Load();
        FileLogger.Instance.Initialize(cfg.FileLogEnabled, cfg.FileLogLevel, cfg.MaxLogFiles, cfg.LogRetentionDays);

        if (console)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine($"[Worker] PID={pid} mode={mode} pipe={pipeName}");
            Console.WriteLine("[Worker] 等待 GUI 连接…");
        }

        BotEngine? engine = null;
        bool previewOn = false;
        CancellationTokenSource? previewCts = null;
        NamedPipeServerStream? pipe = null;
        CancellationTokenSource? readCts = null;

        async Task SendAsync(PipeProtocol.Msg msg)
        {
            if (pipe is null || !pipe.IsConnected) return;
            try { await PipeProtocol.WriteLineAsync(pipe, msg); }
            catch { /* 管道断开 */ }
        }

        void BridgeEvents(BotEngine e)
        {
            e.Log += (msg, lv) =>
            {
                FileLogger.Instance.Write(msg, lv);
                if (console) Console.WriteLine($"[{lv}] {msg}");
                _ = SendAsync(PipeProtocol.EvLog(lv.ToString(), msg));
            };
            e.StatsChanged += s =>
                _ = SendAsync(PipeProtocol.EvStats(s.MatchesDone, s.KeyMatches, s.Forfeited, s.StartedAt, s.LastError));
            // 画面判定事件（替代旧 Observed，载荷更精简：仅 screen/score/evidence）
            e.ScreenChanged += s =>
                _ = SendAsync(PipeProtocol.EvScreen(s.Screen.ToString(), s.Score,
                    string.Join(",", s.Evidence)));
            // 探针 Top-5 候选：序列化为 JSON 字符串发给 GUI 图谱面板
            e.ProbeUpdated += p =>
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(
                        p.TopCandidates.Select(c => new
                        {
                            screen = c.Screen.ToString(),
                            score = c.RawScore,
                            evidence = string.Join(",", c.Evidence),
                        }));
                    _ = SendAsync(PipeProtocol.EvProbe(json));
                }
                catch { /* 序列化失败不影响主流程 */ }
            };
            // 导航寻路路径
            e.NavPathFound += n =>
                _ = SendAsync(PipeProtocol.EvNav(n.From, n.To));
            // 比赛阶段切换
            e.FlowStageChanged += f =>
                _ = SendAsync(PipeProtocol.EvFlow(f.Stage.ToString(), f.Deadline));
            e.Capture.StepLog += s =>
            {
                if (console) Console.WriteLine($"[cap] {s}");
                _ = SendAsync(PipeProtocol.EvLog("Info", $"[捕获] {s}"));
            };
        }

        void StartPreviewLoop()
        {
            previewCts?.Cancel();
            previewCts = new CancellationTokenSource();
            var ct = previewCts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && previewOn)
                {
                    try
                    {
                        var f = engine?.LatestFrame;
                        if (f is not null)
                        {
                            // 降采样到 480×270 再编码 PNG base64
                            string b64 = EncodePreviewPng(f.Bgra, f.Width, f.Height);
                            await SendAsync(PipeProtocol.EvPreview(b64));
                        }
                    }
                    catch { }
                    await Task.Delay(500, ct);
                }
            }, ct);
        }

        void StopPreviewLoop()
        {
            previewOn = false;
            previewCts?.Cancel();
            previewCts = null;
        }

        // 监听管道连接（循环，GUI 断开后等待重连）
        while (true)
        {
            pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            try
            {
                if (console) Console.WriteLine("[Worker] 等待连接…");
                FileLogger.Instance.Write("Worker 管道服务器已建立，等待 GUI 连接…", LogLevel.Info);
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await pipe.WaitForConnectionAsync(connectCts.Token);
                if (console) Console.WriteLine("[Worker] GUI 已连接");
                FileLogger.Instance.Write("GUI 已连接到 Worker 管道", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                if (console) Console.WriteLine("[Worker] 60s 无重连，退出进程。");
                FileLogger.Instance.Write("60s 无 GUI 连接，Worker 退出", LogLevel.Warn);
                pipe.Dispose();
                break; // 退出 while(true)，进程结束
            }
            catch { pipe.Dispose(); continue; }

            readCts = new CancellationTokenSource();

            // 首条消息握手标记：客户端冷启动期超时残留的 CreateFile 可能被幽灵授予本实例，
            // 表现为双方"已连接"但收不到任何字节。5s 内无首条消息 → 回收本实例重新监听。
            bool gotFirstMessage = false;

            // 读循环：处理 GUI 命令
            _ = PipeProtocol.ReadLoopAsync(pipe, async msg =>
            {
                gotFirstMessage = true;
                if (console) Console.WriteLine($"[Worker] 收到命令: {PipeProtocol.Serialize(msg)}");

                if (msg.Cmd == "ping")
                {
                    await SendAsync(PipeProtocol.EvPong());
                }
                else if (msg.Cmd == "start" && msg.Mode is not null)
                {
                    FileLogger.Instance.Write($"收到 start 命令：mode={msg.Mode}", LogLevel.Info);
                    if (engine is { IsRunning: true })
                    {
                        await SendAsync(PipeProtocol.EvLog("Warn", "场景已在运行中，忽略重复 start"));
                        return;
                    }
                    // 每次 start 重建引擎（干净状态）
                    engine?.Dispose();
                    var freshCfg = AppConfig.Load(); // 每次 start 读最新配置
                    engine = new BotEngine(freshCfg);
                    BridgeEvents(engine);
                    try
                    {
                        await engine.StartAsync(msg.Mode);
                        if (console) Console.WriteLine($"[Worker] 场景已启动: {msg.Mode}");
                    }
                    catch (Exception ex)
                    {
                        await SendAsync(PipeProtocol.EvLog("Error", $"启动失败：{ex.Message}"));
                        await SendAsync(PipeProtocol.EvExited($"启动失败：{ex.Message}"));
                    }
                }
                else if (msg.Cmd == "stop")
                {
                    if (engine is { IsRunning: true })
                    {
                        engine.Stop();
                        await SendAsync(PipeProtocol.EvLog("Info", "已停止"));
                    }
                    StopPreviewLoop();
                }
                else if (msg.Cmd == "preview")
                {
                    previewOn = msg.On == true;
                    if (previewOn) StartPreviewLoop();
                    else StopPreviewLoop();
                    if (console) Console.WriteLine($"[Worker] 预览={(previewOn ? "开" : "关")}");
                }
                else if (msg.Cmd == "reloadConfig")
                {
                    // M1 简化：仅记录日志，下次 start 时自动读最新配置
                    await SendAsync(PipeProtocol.EvLog("Info", "配置将在下次启动场景时生效"));
                }
            }, readCts.Token);

            // 握手窗口：5s 内必须收到首条字节，否则判定为幽灵连接，丢弃重建实例
            long deadlineMs = Environment.TickCount64 + 5000;
            while (!gotFirstMessage && pipe.IsConnected && Environment.TickCount64 < deadlineMs)
                await Task.Delay(100);

            if (!gotFirstMessage)
            {
                if (console) Console.WriteLine("[Worker] 5s 内无首条消息，判定幽灵连接，回收实例");
                FileLogger.Instance.Write("连接建立后 5s 内无首条消息（幽灵连接），回收管道实例重新监听", LogLevel.Warn);
                readCts.Cancel();
                pipe.Dispose();
                pipe = null;
                continue;
            }

            // 等待管道断开（GUI 关闭/崩溃）
            try
            {
                while (pipe.IsConnected)
                    await Task.Delay(500);
            }
            catch { }

            if (console) Console.WriteLine("[Worker] GUI 断开，等待重连…");

            // 管道断开后停止场景（避免无头 Worker 空跑）
            if (engine is { IsRunning: true })
            {
                engine.Stop();
                if (console) Console.WriteLine("[Worker] 因 GUI 断开而停止场景");
            }
            StopPreviewLoop();
            readCts.Cancel();
            pipe.Dispose();
            pipe = null;
        }

        return 0;
    }

    /// <summary>将 BGRA 帧降采样到 480×270 并编码为 PNG base64。WPF 位图对象需经 Dispatcher 线程创建。</summary>
    private static string EncodePreviewPng(byte[] bgra, int sw, int sh)
    {
        // 降采样是纯字节运算，可在任意线程做
        const int pw = 480, ph = 270;
        int cw = pw, ch = (int)Math.Round((double)pw * sh / sw);
        if (ch > ph) { ch = ph; cw = (int)Math.Round((double)ph * sw / sh); }
        int offX = (pw - cw) / 2, offY = (ph - ch) / 2;

        var buf = new byte[pw * ph * 4];
        for (int y = 0; y < ch; y++)
        {
            int srow = (y * sh / ch) * sw * 4;
            int drow = ((y + offY) * pw + offX) * 4;
            for (int x = 0; x < cw; x++)
            {
                int si = srow + (x * sw / cw) * 4;
                int di = drow + x * 4;
                buf[di] = bgra[si];
                buf[di + 1] = bgra[si + 1];
                buf[di + 2] = bgra[si + 2];
                buf[di + 3] = bgra[si + 3];
            }
        }

        // 位图编码走 WPF Dispatcher 线程（WinExe 无窗口但 Application.Current 存在）
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return EncodePngBytes(buf, pw, ph);
        return dispatcher.Invoke(() => EncodePngBytes(buf, pw, ph));
    }

    private static string EncodePngBytes(byte[] buf, int pw, int ph)
    {
        var wb = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, pw, ph), buf, pw * 4, 0);
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
