using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Logging;
using EfootballBot.Core.System;
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
        // 崩溃自动抓 minidump，越早装越好（挂机中 Worker 被原生崩溃打死是最常见的事故）
        CrashReporter.Install("worker");

        // 解析参数：--worker <mode> [--pipe <name>] [--console]
        string mode = args.Length > 1 ? args[1] : "observe";
        bool console = args.Contains("--console");

        int pid = Environment.ProcessId;
        // --parent：GUI 进程号。GUI 崩溃/被强杀时本进程据它自主收尾并退出，
        // 不留孤儿（孤儿 Worker 会继续跑场景、占着玩家 1 并让真实手柄保持禁用态）。
        int parentIdx = Array.IndexOf(args, "--parent");
        int parentPid = parentIdx >= 0 && parentIdx + 1 < args.Length
            && int.TryParse(args[parentIdx + 1], out int pp) && pp > 0 ? pp : 0;
        int pipeIdx = Array.IndexOf(args, "--pipe");
        // 管道名优先用 GUI 传入的（GUID，无 PID 猜测）；缺省退回 PID 约定
        string pipeName = pipeIdx >= 0 && pipeIdx + 1 < args.Length
            ? args[pipeIdx + 1]
            : $"EfootballBot-Worker-{pid}";

        // 初始化文件日志（Worker 独立进程，需自己初始化）
        // 老版 %APPDATA%\EfootballBot 数据先迁移到 exe 旁 data\（幂等），配置/日志全在程序目录内
        AppPaths.MigrateLegacyData();
        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch (Exception ex)
        {
            // 配置损坏此前在日志初始化之前抛出 → Worker 秒退且零日志（v1.1.19 修复）：
            // 先用默认配置把日志立起来，把异常写进会话文件再继续
            FileLogger.Instance.Initialize(enabled: true, "Debug", maxFiles: 10, retentionDays: 7);
            FileLogger.Instance.Write($"配置加载失败（用默认配置继续）：{ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            cfg = new AppConfig();
        }
        FileLogger.Instance.Initialize(cfg.FileLogEnabled, cfg.FileLogLevel, cfg.MaxLogFiles, cfg.LogRetentionDays);
        FileLogger.Instance.Write($"Worker 进程初始化完成（PID {pid}），创建管道服务器…", LogLevel.Info);

        // 双显卡独显直连自愈（幂等）：正常流程 GUI 启动时已写入，本进程创建时即已按独显
        // 运行；单独 --console 调试/旧 GUI 场景下兜底补写（下次进程生效）
        try { GpuPreference.EnsureHighPerformance(); } catch { }

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

        // ---- 会话标记：GUI 下次启动据此清理上代异常退出遗留的 Worker ----
        string sessionFile = Path.Combine(AppPaths.DataDir, "worker-session.txt");
        void WriteSessionMarker()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(sessionFile, $"{pid}|{parentPid}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch { /* 标记失败不影响运行 */ }
        }
        void DeleteSessionMarker()
        {
            try
            {
                // 只删自己写的（多开档案各自 data 目录，仍防并发覆盖）
                if (File.Exists(sessionFile) && File.ReadAllText(sessionFile).StartsWith($"{pid}|"))
                    File.Delete(sessionFile);
            }
            catch { }
        }
        WriteSessionMarker();

        // ---- GUI 看门狗：GUI 异常退出（崩溃/被强杀/更新重启）时自主收尾后退出 ----
        // 没有它：GUI 一死 Worker 仍跑着场景并占着玩家 1、真实手柄也保持禁用，
        // 用户重开程序点「开始」会被判"手柄抢不到"反复失败（2026-10-23 用户报障）。
        if (parentPid > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    await parent.WaitForExitAsync();
                }
                catch { /* 父进程早已不在：直接收尾 */ }
                try { FileLogger.Instance.Write("GUI 进程已退出：Worker 自主收尾（停引擎 / 恢复真实手柄 / 还原停靠窗口）", LogLevel.Warn); } catch { }
                try { engine?.Stop(); } catch { }
                try { engine?.Dispose(); } catch { }
                try { previewCts?.Cancel(); } catch { }
                try { EfootballBot.Core.Input.PhysicalPadControl.RestoreAll(); } catch { }
                try { EfootballBot.Core.System.BackgroundParker.TryRestoreFromRecord(); } catch { }
                DeleteSessionMarker();
                Environment.Exit(0);
            });
        }

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
                // 不落盘：同一批消息由 GUI 的 AppendLog 统一写日志文件（Worker 只拥有
                // 自己的生命周期行）。此前两边各写一份逐字相同的文件，反馈包里同一次
                // 会话有两份副本，磁盘与打包体积白白翻倍。
                if (console) Console.WriteLine($"[{lv}] {msg}");
                _ = SendAsync(PipeProtocol.EvLog(lv.ToString(), msg));
            };
            e.StatsChanged += s =>
                _ = SendAsync(PipeProtocol.EvStats(s.MatchesDone, s.KeyMatches, s.PlayersMaxed, s.StartedAt, s.LastError));
            // 感知摘要事件（Screen 已是摘要文本："OCR N 词 / 检测 N 框"）
            e.ScreenChanged += s =>
                _ = SendAsync(PipeProtocol.EvScreen(s.Screen, s.Score,
                    string.Join(",", s.Evidence)));
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
            long lastFrameStamp = -1;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && previewOn)
                {
                    try
                    {
                        var f = engine?.LatestFrame;
                        // 帧去重：引擎停止/重建期间 LatestFrame 不更新，重复发同帧无意义
                        if (f is not null && f.TimestampMs != lastFrameStamp)
                        {
                            lastFrameStamp = f.TimestampMs;
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
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                // 管道创建失败此前直接炸出进程，日志只有一行会话头（v1.1.19 修复）：先落盘再抛
                FileLogger.Instance.Write($"创建命名管道服务器失败（{pipeName}）：{ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                throw;
            }

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
                    // 瘦身发布（ExternalModels）：模型可能不在包里 → 首次开始前按需下载一次。
                    // 失败不阻断启动：OCR 会给出明确的缺失提示，用户也可手动放 models 目录。
                    await ModelPack.EnsureAsync((m, lv) =>
                    {
                        if (console) Console.WriteLine($"[{lv}] {m}");
                        _ = SendAsync(PipeProtocol.EvLog(lv.ToString(), m));
                    });
                    engine = new BotEngine(freshCfg);
                    BridgeEvents(engine);
                    try
                    {
                        await engine.StartAsync(msg.Mode);
                        if (console) Console.WriteLine($"[Worker] 场景已启动: {msg.Mode}");
                        // 预览开关若处于开启状态：引擎重建后重新启动推送循环
                        //（每次 start 都重建引擎，旧循环虽引用同一变量但重启更稳）
                        if (previewOn) StartPreviewLoop();
                    }
                    catch (Exception ex)
                    {
                        // 启动失败的引擎里可能残留已开启的捕获会话等资源，整套释放防僵尸抓帧
                        engine?.Dispose();
                        engine = null;
                        await SendAsync(PipeProtocol.EvLog("Error", $"启动失败：{ex.Message}"));
                        await SendAsync(PipeProtocol.EvExited($"启动失败：{ex.Message}"));
                        // 手柄未让路：记下退出码，GUI 据此弹窗且不再自动重启（用户不动手也是白跑）
                        if (ex is EfootballBot.Core.Input.PadTakeoverBlockedException)
                            Environment.ExitCode = EfootballBot.Core.Input.PadTakeoverBlockedException.ExitCode;
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
                    // 确认事件：GUI 收到后立即回收进程。Worker 收 stop 本就不退出
                    // （保活等重连），GUI 侧固定等 5s 进程退出只会白等后 Kill。
                    await SendAsync(PipeProtocol.EvStopped());
                }
                else if (msg.Cmd == "pause")
                {
                    engine?.SetPaused(true);
                }
                else if (msg.Cmd == "resume")
                {
                    engine?.SetPaused(false);
                }
                else if (msg.Cmd == "step")
                {
                    engine?.RequestStep();
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

            // 管道断开后停止场景（避免无头 Worker 空跑）。
            // 引擎一律整套释放：即使场景未在运行，捕获会话等资源也不能留（防僵尸抓帧）。
            if (engine is not null)
            {
                engine.Stop();
                engine.Dispose();
                engine = null;
                if (console) Console.WriteLine("[Worker] 因 GUI 断开而停止并释放引擎");
            }
            StopPreviewLoop();
            readCts.Cancel();
            pipe.Dispose();
            pipe = null;
        }

        DeleteSessionMarker();
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
