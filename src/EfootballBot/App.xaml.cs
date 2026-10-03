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
        else if (args.Length > 0 && args[0] == "--slots")
        {
            Startup += (_, _) =>
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; RunSlots(); }
                catch (Exception ex) { Console.WriteLine($"SLOTS FAIL: {ex}"); }
                Shutdown();
            };
        }
        else
            Startup += (_, _) => StartGui();
    }

    // ================= 单实例（仅 GUI 模式；--dump/--slots 诊断命令不受限） =================

    private const string SingleMutexName = @"Local\EfootballBot-SingleInstance-Mutex";
    private const string SinglePipeName = "EfootballBot-SingleInstance-Pipe";

    private Mutex? _singleMutex;
    private CancellationTokenSource? _pipeCts;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private void StartGui()
    {
        _singleMutex = new Mutex(initiallyOwned: true, SingleMutexName, out bool createdNew);
        if (!createdNew)
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
        new MainWindow().Show();
        StartPipeServer();
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

    /// <summary>对比连接 ViGEm 虚拟手柄前后 XInput 四个槽位的占用情况。</summary>
    private static void RunSlots()
    {
        Console.WriteLine("连接前：");
        PrintSlots();

        using var vg = new VirtualGamepad360();
        vg.Connect();
        Console.WriteLine($"ViGEm 报告 UserIndex = {vg.UserIndex}");
        Thread.Sleep(1200);

        Console.WriteLine("连接后：");
        PrintSlots();
    }

    private static void PrintSlots()
    {
        for (int i = 0; i < 4; i++)
        {
            var st = SlotProbe.GetState(i, out var state);
            SlotProbe.GetCapabilities(i, out var cap);
            Console.WriteLine(st == 0
                ? $"  slot {i}: 已连接 type={cap.Type} subtype={cap.SubType} buttons=0x{state.Buttons:X4}"
                : $"  slot {i}: 空");
        }
    }

    /// <summary>
    /// 离线帧自检：EfootballBot --check-frame &lt;png&gt; [cropY=0] [cropH=0] [frameW=1296] [frameH=759] [png2]
    /// 加载 PNG（可选纵向裁剪）→ 缩放为标准整窗帧 → 用真实 OCR + ChallengeCardReader 跑判定。
    /// 提供 png2 时额外输出两帧签名 MAD（验证「Right 是否真的移动」信号）。
    /// 用于改完选卡逻辑后离线验证，无需操作游戏。
    /// </summary>
    private static async Task RunCheckFrameAsync(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("用法：--check-frame <png> [cropY] [cropH] [frameW] [frameH] [png2]");

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

        var info = ChallengeCardReader.Read(frame, ocr, 8, 31, 1280, 720);
        Console.WriteLine("---- 焦点卡判定（真实生产代码 ChallengeCardReader.Read）----");
        Console.WriteLine($"卡号={info.Number?.ToString() ?? "未识别"} 背景亮={info.BgLum:F0} 锁占比={info.LockRatio:F3} ✓绿={info.GreenRatio:F3} 定位={info.Located}");
        Console.WriteLine($"=> Unlocked(未锁)={info.Unlocked}  Done(已打勾)={info.Done}");
        var actCard = ChallengeCardReader.LocateFocusedCardEx(frame, 8, 31, 1280, 720, 0.40, 0.05, 35, 0.10);
        if (actCard is null)
            Console.WriteLine("活动卡焦点卡定位：失败");
        else
        {
            var (al, at, ar, ab) = actCard.Value;
            double acx = ((al + ar) / 2.0 - 8) / 1280.0;
            Console.WriteLine($"活动卡焦点卡定位：left={al} top={at} right={ar} bot={ab} 中心cx={acx:F3}");
        }
        if (actCard is not null)
        {
            var (al, at, ar, ab) = actCard.Value;
            double acx = ((al + ar) / 2.0 - 8) / 1280.0;
            var fpRoi = ChallengeCardReader.Map(frame, new NormRect(acx - 0.15, 0.10, 0.30, 0.50), 8, 31, 1280, 720);
            string fp = string.Concat(ocr.Words
                .Where(w => fpRoi.Contains(w.CenterX, w.CenterY))
                .OrderBy(w => w.CenterY).ThenBy(w => w.CenterX)
                .Select(w => OcrText.Norm(w.Text)));
            Console.WriteLine($"活动卡焦点卡 OCR 指纹 = {fp}");
        }
        var actFocused = ChallengeCardReader.LocateActivityFocusedCard(frame, 8, 31, 1280, 720);
        if (actFocused is null)
            Console.WriteLine("活动卡白按钮定位焦点卡：失败");
        else
        {
            var (fl, ft, fr, fb) = actFocused.Value;
            double fcx = ((fl + fr) / 2.0 - 8) / 1280.0;
            int white = ChallengeCardReader.CountWhite(frame, fl, fr, fb - (int)((fb - ft) * 0.12), fb);
            Console.WriteLine($"活动卡白按钮定位焦点卡：left={fl} top={ft} right={fr} bot={fb} 中心cx={fcx:F3} 按钮白像素={white}");
        }
        Console.WriteLine($"RT蓝横幅={FrameAnalyzer.FindRtBanner(frame, out double bannerCy)}（cy={bannerCy:F3}）");
        var skipCheckRoi = new NormRect(0.945, 0.021, 0.050, 0.110);
        int skipGreen = FrameAnalyzer.CountBrightGreen(frame, skipCheckRoi);
        var hudWords = ocr.Words
            .Where(w => w.CenterX > 0.78 && w.CenterY < 0.14)
            .OrderBy(w => w.CenterY)
            .ToList();
        var hudLines = new List<List<OcrWord>>();
        foreach (var w in hudWords)
        {
            var line = hudLines.LastOrDefault();
            if (line is not null && Math.Abs(w.CenterY - line.Average(x => x.CenterY)) <= 0.02)
                line.Add(w);
            else
                hudLines.Add(new List<OcrWord> { w });
        }
        var hudSb = new StringBuilder();
        foreach (var line in hudLines.OrderBy(l => l.Average(x => x.CenterY)))
            foreach (var w in line.OrderBy(x => x.CenterX))
                hudSb.Append(OcrText.Norm(w.Text));
        string hudJoined = hudSb.ToString();
        bool hudSkip = hudJoined.Contains("跳过比赛");
        Console.WriteLine($"跳过比赛绿勾={hudSkip && skipGreen >= 100}（HUD含跳过比赛={hudSkip}，亮绿像素={skipGreen}，阈值100）");

        if (args.Length >= 7 && !string.IsNullOrWhiteSpace(args[6]))
        {
            var frame2 = LoadFrame(args[6], cropY, cropH, frameW, frameH);
            byte[] s1 = ChallengeCardReader.Signature(frame, 8, 31, 1280, 720);
            byte[] s2 = ChallengeCardReader.Signature(frame2, 8, 31, 1280, 720);
            double mad = ChallengeCardReader.SignatureMad(s1, s2);
            Console.WriteLine($"---- 签名对比：{Path.GetFileName(pngPath)} vs {Path.GetFileName(args[6])} ----");
            Console.WriteLine($"MAD={mad:F1}（阈值 {ChallengeCardReader.MoveMadMin}）=> 判定「{(mad >= ChallengeCardReader.MoveMadMin ? "已移动" : "未移动")}」");
        }
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
        string procName = args.Length > 3 ? args[3] : CnConfig.ProcessName;
        string? actions = args.Length > 4 ? args[4] : null;

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "dump");
        Directory.CreateDirectory(outDir);

        var win = GameWindow.FindFirst(procName)
            ?? throw new InvalidOperationException($"未找到进程「{procName}」的可见窗口，请确认游戏已启动。");

        using var capture = new WindowCapture();
        capture.StepLog += s => Console.WriteLine($"[cap] {s}");

        var (w, h) = GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        Console.WriteLine($"目标窗口：{win.Title}（{win.ProcessName}）客户区 {w}×{h}");
        capture.Start(win.Handle, w, h);

        VirtualGamepad360? vg = null;
        var mouse = new BackgroundMouse();
        var fgMouse = new ForegroundMouse();
        var oldFg = new IntPtr[1];
        try
        {
            if (!string.IsNullOrWhiteSpace(actions))
            {
                bool needPad = actions.Split(',', StringSplitOptions.TrimEntries)
                    .Any(t => t.ToUpperInvariant() is "L" or "R" or "U" or "D" or "A" or "B" or "X" or "Y" or "START");
                if (needPad)
                {
                    vg = new VirtualGamepad360();
                    vg.Connect();
                    Console.WriteLine($"虚拟手柄已连接（XInput 玩家 {vg.UserIndex + 1}）");
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

/// <summary>XInput 槽位探测（仅诊断用）。</summary>
internal static class SlotProbe
{
    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int i, out XState s);
    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern int XInputGetCapabilities(int i, int flags, out XCap c);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct XState
    {
        public uint EventNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short Lx, Ly, Rx, Ry;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct XCap
    {
        public byte Type, SubType;
        public ushort Flags;
        public XState State;
        public short Lv, Rv;
    }

    public static int GetState(int i, out XState s) => XInputGetState(i, out s);
    public static int GetCapabilities(int i, out XCap c) => XInputGetCapabilities(i, 0, out c);
}
