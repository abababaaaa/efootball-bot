using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot;

public partial class App : Application
{
    public App()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // 多开档案（--profile <名> / --profile=<名>）：必须最先解析——data\、logs\、
        // 单实例锁、管道名全部按档案隔离，解析晚了静态路径就已经定型。
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Length) AppPaths.SetProfile(args[i + 1]);
            else if (args[i].StartsWith("--profile=", StringComparison.Ordinal))
                AppPaths.SetProfile(args[i]["--profile=".Length..]);
        }
        if (args.Length > 0 && args[0] is "--dump" or "--cn-dump")
        {
            Startup += async (_, _) =>
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; await RunDumpAsync(args); }
                catch (Exception ex) { Console.WriteLine($"DUMP FAIL: {ex}"); }
                Shutdown();
            };
        }
        else if (args.Length > 0 && args[0] == "--check-frame")
        {
            Startup += async (_, _) =>
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; await RunCheckFrameAsync(args); }
                catch (Exception ex) { Console.WriteLine($"CHECK FAIL: {ex}"); }
                Shutdown();
            };
        }
        else if (args.Length > 0 && args[0] == "--worker")
        {
            // 无头工作进程：不获取单实例 Mutex、不检查更新、不显示主窗口。
            // WinExe 默认无窗口时 ShutdownMode=OnLastWindowClose 会立即退出，
            // 必须改为 OnExplicitShutdown 保持进程存活直到 WorkerProgram 主动退出。
            Startup += async (_, _) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await EfootballBot.Worker.WorkerProgram.RunAsync(args); }
                catch (Exception ex)
                {
                    // Worker 无人值守，异常必须落盘供 GUI 侧诊断
                    try
                    {
                        string dir = AppPaths.DataDir;
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "worker-error.log"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}");
                    }
                    catch { }
                }
                Shutdown();
            };
        }
        else if (args.Length > 0 && args[0] == "--optimize-github")
        {
            // 提权离线执行 GitHub 直连优化（由 --optimize-github --report <path> 调用），结果写文件后退出。
            Startup += (_, _) =>
            {
                try
                {
                    string report = args.Length > 2 && args[1] == "--report" ? args[2] : "";
                    string result = GitHostsOptimizer.Optimize();
                    if (!string.IsNullOrEmpty(report))
                        File.WriteAllText(report, result);
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (args.Length > 2 && args[1] == "--report")
                            File.WriteAllText(args[2], "优化失败：" + ex.Message);
                    }
                    catch { }
                }
                Shutdown();
            };
        }
        else if (args.Length > 0 && (args[0] == "--pad-config" || args[0] == "--hidhide-config"))
        {
            // 提权配置模式：--pad-config <job文件路径|pad-hide|pad-restore|tasks>。
            // --hidhide-config 为旧参数别名：升级过渡期旧计划任务/shim 仍指向它，按新入口解析不断链。
            // 立即退出，无窗口、无单实例 Mutex、无日志初始化。
            Startup += (_, _) =>
            {
                try
                {
                    string arg = args.Length > 1 ? args[1] : "tasks";
                    int exit = EfootballBot.Core.Input.PadConfigJob.Run(arg);
                    Environment.ExitCode = exit;
                }
                catch (Exception ex)
                {
                    try
                    {
                        string dir = AppPaths.DataDir;
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "pad-config-error.log"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}");
                    }
                    catch { }
                }
                Shutdown();
            };
        }
        else if (args.Length > 0 && args[0] == "--make-patch")
        {
            // 发布链工具：--make-patch <旧exe> <新exe> <补丁输出>
            // CI 在发版时对「上一个 tag 的 exe」生成一跳补丁，上传到同源 patches/ 目录；
            // 客户端更新时优先尝试该补丁（见 UpdateApplier.TryDeltaAsync）。
            Startup += (_, _) =>
            {
                try
                {
                    if (args.Length < 4) throw new ArgumentException("用法：--make-patch <旧exe> <新exe> <补丁输出>");
                    Core.System.DeltaPatch.Create(args[1], args[2], args[3]);
                    long patchBytes = new FileInfo(args[3]).Length;
                    long newBytes = new FileInfo(args[2]).Length;
                    Console.WriteLine($"PATCH OK: {args[3]} — {Core.System.DeltaPatch.Describe(patchBytes, newBytes)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("PATCH FAIL: " + ex.Message);
                    Environment.ExitCode = 1;
                }
                Shutdown();
            };
        }
        else
            Startup += (_, _) => StartGui(fromUpdate: args.Contains("--updated"));

        // GUI 未处理异常落盘（与 worker-error.log 对应），便于定位启动崩溃
        DispatcherUnhandledException += (_, e) =>
        {
            WriteGuiError(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteGuiError(e.ExceptionObject as Exception ?? new Exception("未知异常"));
    }

    /// <summary>GUI 异常落盘到 exe 旁 data\gui-error.log（不在 C 盘用户目录留文件）。</summary>
    internal static void WriteGuiError(Exception? ex)
    {
        try
        {
            string dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "gui-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    // ================= 单实例（仅 GUI 模式；--dump 诊断命令不受限） =================

    // 单实例锁与唤醒管道按档案命名：多个 --profile 实例可同时驻留（各自挂一个账号）
    private static string SingleMutexName => AppPaths.Profile.Length == 0
        ? @"Local\EfootballBot-SingleInstance-Mutex"
        : $@"Local\EfootballBot-SingleInstance-Mutex-{AppPaths.Profile}";
    private static string SinglePipeName => AppPaths.Profile.Length == 0
        ? "EfootballBot-SingleInstance-Pipe"
        : $"EfootballBot-SingleInstance-Pipe-{AppPaths.Profile}";

    private Mutex? _singleMutex;
    private CancellationTokenSource? _pipeCts;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private void StartGui(bool fromUpdate = false)
    {
        // 崩溃自动抓 minidump（原生 + 托管兜底），反馈包自动附带
        Core.System.CrashReporter.Install("gui");

        // 更新重启的新实例：旧实例正在优雅退出（存配置→停 Worker→释放单实例锁），
        // 轮询等锁最多 20s——若跳过等待，会被下方单实例逻辑当成"第二个实例"自杀，
        // 旧实例一退就没有进程接班了。超时（旧实例卡死未退）则走正常分支唤醒旧实例并
        // 退出：exe 已换好，用户下次启动即新版本
        if (fromUpdate)
        {
            UpdateApplier.Log("更新重启：新实例等待旧实例退出…");
            for (int i = 0; i < 40 && !TryOwnSingleMutex(); i++)
                Thread.Sleep(500);
        }

        if (_singleMutex is null && !TryOwnSingleMutex())
        {
            // 已有实例在运行：允许它取得前台，管道通知它把窗口弹到最前，然后本进程退出
            try
            {
                AllowSetForegroundWindow(-1);
                using var client = new NamedPipeClientStream(".", SinglePipeName, PipeDirection.Out);
                client.Connect(1500);
                client.WriteByte(1);
            }
            catch { /* 通知失败也不允许第二个 GUI 驻留 */ }
            Shutdown();
            return;
        }

        Exit += (_, _) =>
        {
            _pipeCts?.Cancel();
            _singleMutex?.ReleaseMutex();
        };
        // 双显卡笔记本自动独显直连：必须先于 Worker 子进程创建写入——GPU 偏好在进程
        // 创建时读取，GUI 写完后新起的 Worker 即按独显运行（2026-10-11 用户实测：
        // 核显本不设高性能时捕获黑屏，图形设置→高性能恢复正常；此处自动化该操作）
        try { GpuPreference.EnsureHighPerformance(); } catch { }
        // 老版 %APPDATA%\EfootballBot 的持久数据（config 等）一次性搬到 exe 旁 data\，
        // 所有数据收在程序文件夹内（2026-10-11 用户要求）
        AppPaths.MigrateLegacyData();
        // 更新收尾（每次启动）：中断还原 .old / 延迟清理旧版备份（无更新残留时无事发生）
        UpdateApplier.FinalizeAfterUpdate();
        new MainWindow().Show();
        StartPipeServer();

        // 上次非正常退出（强制关闭/更新重启/崩溃）可能把游戏窗口丢在屏幕外：启动即还原
        EfootballBot.Core.System.BackgroundParker.TryRestoreFromRecord();
    }

    /// <summary>尝试创建并持有单实例互斥锁；已被其他实例持有时返回 false（并清空 _singleMutex）。</summary>
    private bool TryOwnSingleMutex()
    {
        _singleMutex = new Mutex(initiallyOwned: true, SingleMutexName, out bool createdNew);
        if (createdNew) return true;
        _singleMutex.Dispose();
        _singleMutex = null;
        return false;
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeServerAsync(_pipeCts.Token));
    }

    /// <summary>循环监听第二个实例的唤起信号；每收到一次就让主窗口恢复并前置。</summary>
    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                SinglePipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct);
                try { server.ReadByte(); } catch { /* 信号内容无意义 */ }
            }
            catch (OperationCanceledException) { return; }
            catch { /* 单次监听异常：重建服务器继续 */ }
            finally { server.Dispose(); }

            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow w) w.BringToFront();
            });
        }
    }

    /// <summary>
    /// 离线帧自检：EfootballBot --check-frame &lt;png&gt; [cropY=0] [cropH=0] [frameW=1296] [frameH=759]
    /// 加载 PNG（可选纵向裁剪）→ 缩放为标准整窗帧 → 用真实 OCR 输出关键词。
    /// 用于改完 OCR 逻辑后离线验证，无需操作游戏。
    /// </summary>
    private static async Task RunCheckFrameAsync(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("用法：--check-frame <png> [cropY] [cropH] [frameW] [frameH]");

        string pngPath = args[1];
        int cropY = args.Length > 2 && int.TryParse(args[2], out int cy) ? Math.Max(0, cy) : 0;
        int cropH = args.Length > 3 && int.TryParse(args[3], out int ch2) ? Math.Max(0, ch2) : 0;
        int frameW = args.Length > 4 && int.TryParse(args[4], out int fw) ? fw : 1296;
        int frameH = args.Length > 5 && int.TryParse(args[5], out int fh) ? fh : 759;

        var frame = LoadFrame(pngPath, cropY, cropH, frameW, frameH);

        var ocrEng = new OcrRecognizer(null);
        Console.WriteLine($"OCR 语言：{ocrEng.LanguageTag}，帧：{frameW}×{frameH}（源 {Path.GetFileName(pngPath)}）");
        var ocr = await ocrEng.RecognizeAsync(frame);

        Console.WriteLine("---- OCR 原始词 ----");
        foreach (var wd in ocr.Words.OrderBy(x => x.Y).ThenBy(x => x.X))
            Console.WriteLine($"[{wd.X:F3},{wd.Y:F3} {wd.W:F3}x{wd.H:F3}] {wd.Text} => {OcrText.Norm(wd.Text)}");
        Console.WriteLine($"JoinedText = {ocr.JoinedText}");
    }

    private static FrameData LoadFrame(string pngPath, int cropY, int cropH, int frameW, int frameH)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(Path.GetFullPath(pngPath));
        src.EndInit();
        src.Freeze();

        BitmapSource bs = src;
        if (cropH > 0 && cropY + cropH <= src.PixelHeight)
            bs = new CroppedBitmap(src, new Int32Rect(0, cropY, src.PixelWidth, cropH));

        if (bs.PixelWidth != frameW || bs.PixelHeight != frameH)
        {
            double sx = frameW / (double)bs.PixelWidth;
            double sy = frameH / (double)bs.PixelHeight;
            bs = new TransformedBitmap(bs, new ScaleTransform(sx, sy));
        }
        bs.Freeze();

        var converted = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        int stride = frameW * 4;
        byte[] bgra = new byte[stride * frameH];
        converted.CopyPixels(bgra, stride, 0);
        return new FrameData(bgra, frameW, frameH, 0);
    }

    /// <summary>
    /// 命令行抓帧校准模式：EfootballBot --dump [次数=1] [间隔ms=1500] [进程名=eFootballOnline] [动作序列]
    /// 动作序列在抓帧前执行，逗号分隔：L/R/U/D=方向键，A/B/X/Y=按键，START=菜单，整数=等待毫秒。
    /// 例：--dump 2 1800 eFootballOnline "R,700,A,2800"
    /// 不显示主窗口，用 WGC 抓后台窗口，保存 PNG，并打印每行 OCR 原文与归一化结果。
    /// </summary>
    private static async Task RunDumpAsync(string[] args)
    {
        int count = args.Length > 1 && int.TryParse(args[1], out int c) ? Math.Max(1, c) : 1;
        int gap = args.Length > 2 && int.TryParse(args[2], out int g) ? g : 1500;
        string procName = args.Length > 3 ? args[3] : BotEngine.CnProcessName;
        string? actions = args.Length > 4 ? args[4] : null;

        // 锚 exe 旁目录：v2.0.6 起强制管理员启动，UAC 提权拉起时 CWD 会被系统设为
        // System32，跟着 CWD 走会把 dump 写进系统目录
        string outDir = Path.Combine(AppPaths.ExeDir, "dump");
        Directory.CreateDirectory(outDir);

        var win = GameWindow.FindFirst(procName)
            ?? throw new InvalidOperationException($"未找到进程「{procName}」的可见窗口，请确认游戏已启动。");

        using var capture = new WindowCapture();
        capture.StepLog += s => Console.WriteLine($"[cap] {s}");

        var (w, h) = GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        Console.WriteLine($"目标窗口：{win.Title}（{win.ProcessName}）客户区 {w}×{h}");
        capture.Start(win.Handle, w, h);

        VigemPad? vg = null;
        var mouse = new BackgroundMouse();
        var fgMouse = new ForegroundMouse();
        var oldFg = new IntPtr[1];
        try
        {
            if (!string.IsNullOrWhiteSpace(actions))
            {
                bool needPad = actions.Split(',', StringSplitOptions.TrimEntries)
                    .Any(t => t.ToUpperInvariant() is "L" or "R" or "U" or "D" or "A" or "B" or "X" or "Y" or "START" or "RT");
                if (needPad)
                {
                    vg = new VigemPad(Console.WriteLine);
                    vg.Connect();
                    Console.WriteLine("虚拟手柄已连接（ViGEm Xbox 360）");
                }
                var pad = vg is null ? null : new Pad(vg, new TimingConfig());
                Console.WriteLine($"执行动作：{actions}");
                await RunActionsAsync(pad, mouse, fgMouse, oldFg, win.Handle, w, h, actions);
            }

            var ocr = new OcrRecognizer(null);
            Console.WriteLine($"OCR 语言：{ocr.LanguageTag}");

            for (int i = 0; i < count; i++)
            {
                await Task.Delay(Math.Max(700, gap));
                var f = capture.TryGetLatest();
                if (f is null) { Console.WriteLine($"#{i + 1} 尚未拿到帧"); continue; }

                string stamp = DateTime.Now.ToString("HHmmss_fff");
                string png = Path.Combine(outDir, $"dump_{stamp}.png");
                SavePng(f, png);

                var r = await ocr.RecognizeAsync(f);
                Console.WriteLine($"---- #{i + 1} {f.Width}×{f.Height} -> {png} ----");
                foreach (var wd in r.MergedWords.OrderBy(x => x.Y).ThenBy(x => x.X))
                    Console.WriteLine($"[{wd.X:F3},{wd.Y:F3} {wd.W:F3}x{wd.H:F3}] {wd.Text}  =>  {OcrText.Norm(wd.Text)}");
            }
        }
        finally
        {
            vg?.Dispose();
            if (oldFg[0] != IntPtr.Zero)
            {
                await Task.Delay(400);
                fgMouse.RestoreFront(oldFg[0]);
            }
        }
    }

    private static async Task RunActionsAsync(Pad? pad, BackgroundMouse mouse, ForegroundMouse fgMouse,
        IntPtr[] oldFg, IntPtr hWnd, int cw, int ch, string actions)
    {
        foreach (var tok in actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(tok, out int ms)) { await Task.Delay(ms); continue; }

            if (tok.Equals("FG", StringComparison.OrdinalIgnoreCase))
            {
                oldFg[0] = fgMouse.BringToFront(hWnd);
                await Task.Delay(650);
                continue;
            }

            // 鼠标：坐标用 x 分隔，如 CL:0.312x0.67（归一化客户区），SI 为真实光标点击
            int colon = tok.IndexOf(':');
            if (colon > 0)
            {
                string head = tok[..colon].ToUpperInvariant();
                var parts = tok[(colon + 1)..].Split('x', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && double.TryParse(parts[0], out double a) && double.TryParse(parts[1], out double b))
                {
                    bool pixel = head is "CLP" or "CLSP" or "MVP";
                    int x = pixel ? (int)a : (int)Math.Round(a * cw);
                    int y = pixel ? (int)b : (int)Math.Round(b * ch);
                    switch (head)
                    {
                        case "CL": case "CLP": await mouse.ClickAsync(hWnd, x, y); break;
                        case "CLS": case "CLSP": mouse.ClickSend(hWnd, x, y); break;
                        case "MV": case "MVP": mouse.Move(hWnd, x, y); break;
                        case "SI": await fgMouse.ClickClientAsync(hWnd, x, y); break;
                        default: Console.WriteLine($"[pad] 未知动作：{tok}"); break;
                    }
                    continue;
                }
            }

            if (pad is null) { Console.WriteLine($"[pad] 未连接手柄，忽略：{tok}"); continue; }
            switch (tok.ToUpperInvariant())
            {
                case "L": await pad.Dpad(PadDir.Left); break;
                case "R": await pad.Dpad(PadDir.Right); break;
                case "U": await pad.Dpad(PadDir.Up); break;
                case "D": await pad.Dpad(PadDir.Down); break;
                case "A": await pad.Confirm(); break;
                case "B": await pad.Back(); break;
                case "X": await pad.X(); break;
                case "Y": await pad.Y(); break;
                case "START": await pad.Start(); break;
                case "RT": await pad.RT(); break;
                default: Console.WriteLine($"[pad] 未知动作：{tok}"); break;
            }
        }
    }

    private static void SavePng(FrameData f, string path)
    {
        var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        using var fs = File.OpenWrite(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        enc.Save(fs);
    }
}
