using System.Diagnostics;
using System.IO;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Sidecar;

// 手动验证 Rust sidecar 全链路：定位 exe → 握手 → ping → inject → 帧环读帧 → DXGI grab（可选）。
// 用法： dotnet run --project tools/VisionProbe [-- <vision-svc.exe 路径>]

// --ocr-ab <png>：同一帧上对比 C# SimdPaddle 与 Rust sidecar OCR（M2 验收）
if (args.Length >= 2 && args[0] == "--ocr-ab")
{
    return await OcrAb.RunAsync(args[1]);
}

// --live-ocr <hwnd>：验证 src=capture（跳过上传拷贝）与上传路径的耗时差
if (args.Length >= 2 && args[0] == "--live-ocr")
{
    return await LiveOcr.RunAsync(args[1]);
}

// --ring-bench：量一下"上传拷贝"本身的成本（src=capture 省掉的就是它）
if (args.Length >= 1 && args[0] == "--ring-bench")
{
    foreach (var (w, h) in new[] { (1022, 636), (1280, 720), (1920, 1080), (2560, 1440) })
    {
        var buf = new byte[w * h * 4];
        var f2 = new FrameData(buf, w, h, 0);
        using var ring2 = new UploadFrameRing($"Local\\EfootballVision-bench-{Guid.NewGuid():N}", Math.Max(w, 1920), Math.Max(h, 1080));
        ring2.Publish(f2);
        var sw2 = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) ring2.Publish(f2);
        sw2.Stop();
        Console.WriteLine($"{w}x{h}: 上传 publish {sw2.Elapsed.TotalMilliseconds / 20:F2} ms/帧（{buf.Length / 1024.0 / 1024:F1} MB/帧）");
    }
    return 0;
}

// --yolo-ab <png>：同一帧上对比「进程内 YOLO」与「侧车 YOLO」的耗时
if (args.Length >= 2 && args[0] == "--yolo-ab")
{
    return await YoloAb.RunAsync(args[1]);
}

var exe = args.Length > 0 && File.Exists(args[0]) ? args[0] : VisionSidecarLocator.Locate(null);
Console.WriteLine($"exe = {exe ?? "<未找到>"}");
if (exe is null)
{
    Console.Error.WriteLine("找不到 vision-svc.exe：先跑 rust\build.bat");
    return 2;
}

var hwndArg = args.FirstOrDefault(a => a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
var hwnd = hwndArg is null ? 0L : long.Parse(hwndArg[2..], System.Globalization.NumberStyles.HexNumber);
var backend = args.FirstOrDefault(a => a.StartsWith("--backend=", StringComparison.OrdinalIgnoreCase))?[10..] ?? "auto";
Console.WriteLine($"hwnd = 0x{hwnd:x}, backend = {backend}");
var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 15, Verbose = true, Backend = backend };
var clock = Stopwatch.StartNew();
VisionSidecarClient.RawTrace += line => Console.WriteLine($"[{clock.ElapsedMilliseconds,6}ms raw] {line}");
string? frameBackend = null;
Console.WriteLine($"[{clock.ElapsedMilliseconds,6}ms] 开始握手");
var sw = Stopwatch.StartNew();
await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: (nint)hwnd, width: 640, height: 360);
Console.WriteLine($"握手 init: {sw.ElapsedMilliseconds}ms, pid={client.Pid}, ver={client.Version}, ring={client.Ring.Name} slots={client.Ring.SlotCount} slotBytes={client.Ring.SlotBytes}");

Console.WriteLine($"[{clock.ElapsedMilliseconds,6}ms] 握手完成");
client.Notification += m =>
{
    if (m.Ev == "log") Console.WriteLine($"[sidecar {m.Level}] {m.Text}");
    if (m.Ev == "frame" && m.Backend is { } b) frameBackend = b;
};
using (var sp = Process.GetProcessById(client.Pid))
{
    sp.Refresh();
    Console.WriteLine($"sidecar 内存: 工作集 {sp.WorkingSet64 / 1024 / 1024} MB（私有 {sp.PrivateMemorySize64 / 1024 / 1024} MB，线程 {sp.Threads.Count}）");
}
var pingSw = Stopwatch.StartNew();
var pong = await client.SendAsync(VisionMsg.CmdOf("ping"), 5000);
Console.WriteLine($"[{clock.ElapsedMilliseconds,6}ms] ping -> ev={pong.Ev} ({pingSw.ElapsedMilliseconds}ms)");

sw.Restart();
var inject = await client.SendAsync(new VisionMsg { Cmd = "inject", W = 640, H = 360 }, 5000);
Console.WriteLine($"inject -> ev={inject.Ev} seq={inject.Seq} {sw.ElapsedMilliseconds}ms");

sw.Restart();
FrameData? frame = null;
while (sw.ElapsedMilliseconds < 3000 && frame is null)
{
    if (client.Ring.TryReadLatest(out var f)) frame = f;
    else await Task.Delay(10);
}
if (frame is null)
{
    Console.Error.WriteLine("帧环读不到注入帧");
    return 3;
}
Console.WriteLine($"帧环读帧: {frame.Width}x{frame.Height} {frame.Bgra.Length} bytes, 等待 {sw.ElapsedMilliseconds}ms, 首像素 BGRA={frame.Bgra[0]},{frame.Bgra[1]},{frame.Bgra[2]},{frame.Bgra[3]}");
var distinct = frame.Bgra.Where((_, i) => i % 4 == 0).Distinct().Take(6).ToArray();
Console.WriteLine($"B 通道取值样本: {string.Join(",", distinct)}");

// DXGI 冒烟：有的机器（虚拟显示器/远程会话）桌面复制会返回全黑帧，这里只报告不判失败
var grab = await client.SendAsync(new VisionMsg { Cmd = "grab", Hwnd = hwnd }, 8000);
Console.WriteLine($"grab -> ev={grab.Ev} {grab.W}x{grab.H} {grab.Ms:F1}ms {grab.Text}");
if (grab.Ev == "frame" && client.Ring.TryReadLatest(out var g))
{
    var nonZero = g.Bgra.Count(b => b != 0);
    Console.WriteLine($"grab 帧非零字节 {nonZero}/{g.Bgra.Length} ({100.0 * nonZero / g.Bgra.Length:F2}%)");
    using var sp2 = Process.GetProcessById(client.Pid);
    sp2.Refresh();
    Console.WriteLine($"sidecar 内存（抓过整屏后）: 工作集 {sp2.WorkingSet64 / 1024 / 1024} MB（私有 {sp2.PrivateMemorySize64 / 1024 / 1024} MB）");
}

// —— 服务模式：start → 等帧事件 → 读帧环，确认 backend 与真实像素 ——
if (hwnd != 0)
{
    var started = await client.SendAsync(new VisionMsg { Cmd = "start", Hwnd = hwnd, Fps = 15, Backend = backend }, 8000);
    Console.WriteLine($"start -> ev={started.Ev} {started.Text}");
    var wait = Stopwatch.StartNew();
    FrameData? live = null;
    string? usedBackend = null;
    while (wait.ElapsedMilliseconds < 4000 && live is null)
    {
        if (client.Ring.TryReadLatest(out var lf)) live = lf;
        else await Task.Delay(15);
    }
    live ??= null;
    if (live is not null)
    {
        var nz = live.Bgra.Count(b => b != 0);
        Console.WriteLine($"服务模式帧: {live.Width}x{live.Height} 非零 {100.0 * nz / live.Bgra.Length:F1}%（等待 {wait.ElapsedMilliseconds}ms，后端 {frameBackend ?? "?"}）");
    }
    else
    {
        Console.WriteLine("服务模式：4s 内没拿到帧");
    }
    await client.SendAsync(VisionMsg.CmdOf("stop"), 3000);
}

// —— 预处理 A/B：C# ImageScaling 与 Rust（--bench-preproc）同尺寸同算法 ——
{
    const int w = 3440, h = 1440;
    var big = new byte[w * h * 4];
    Random.Shared.NextBytes(big);
    var bigFrame = new FrameData(big, w, h, 0);
    var warm = ImageScaling.DownscaleBilinear(bigFrame, 1720);
    var bench = Stopwatch.StartNew();
    FrameData? last = null;
    for (var i = 0; i < 30; i++) last = ImageScaling.DownscaleBilinear(bigFrame, 1720);
    bench.Stop();
    Console.WriteLine($"C# ImageScaling 3440x1440 -> {last!.Width}x{last.Height}: {bench.Elapsed.TotalMilliseconds / 30:F2} ms/次（含新数组分配）");
}

Console.WriteLine("OK");
return 0;

static class OcrAb
{
    public static async Task<int> RunAsync(string png)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        if (bmp is null) { Console.Error.WriteLine($"读不了 {png}"); return 2; }
        var info = new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var conv = new SkiaSharp.SKBitmap(info);
        bmp.CopyTo(conv, SkiaSharp.SKColorType.Bgra8888);
        var frame = new FrameData(conv.Bytes, bmp.Width, bmp.Height, 0);

        Console.WriteLine($"帧 {frame.Width}x{frame.Height}  {System.IO.Path.GetFileName(png)}");

        // ---- C# SimdPaddle ----
        var swCs = Stopwatch.StartNew();
        List<OcrResult> csRuns = new();
        using (var cs = new OcrRecognizer(OcrEngineKind.SimdPaddle))
        {
            _ = await cs.RecognizeAsync(frame); // 预热
            swCs.Restart();
            var r = await cs.RecognizeAsync(frame);
            csRuns.Add(r);
            Console.WriteLine($"C# SimdPaddle: {swCs.ElapsedMilliseconds}ms  {r.Words.Count} 词  标签={cs.LanguageTag}");
            foreach (var w in r.Words.Take(12)) Console.WriteLine($"   {w.Text}");
        }

        // ---- Rust sidecar ----
        var exe = VisionSidecarLocator.Locate(null);
        if (exe is null) { Console.WriteLine("未找到 vision-svc.exe，跳过 Rust 侧"); return 0; }
        var cfg = new VisionSidecarConfig { ExePath = exe, Verbose = true };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 1920, height: 1080);
        var swRs = Stopwatch.StartNew();
        var rr = await client.RecognizeAsync(frame);
        Console.WriteLine($"Rust sidecar : {swRs.ElapsedMilliseconds}ms  {rr.Words.Count} 词");
        foreach (var w in rr.Words.Take(12)) Console.WriteLine($"   {w.Text}");

        // ---- YOLO 检测 ----
        try
        {
            var detSw = Stopwatch.StartNew();
            var boxes = await client.DetectAsync(frame);
            var first = boxes.Count > 0 ? boxes[0].ToString() : "none";
            Console.WriteLine($"Rust YOLO    : {detSw.ElapsedMilliseconds}ms  {boxes.Count} 框  {client.LastDetectInfo}  首框={first}");
        }
        catch (Exception ex) { Console.WriteLine($"YOLO 跳过: {ex.Message}"); }

        // ---- 简单一致性：按 index 比较文本 ----
        var n = Math.Min(csRuns[0].Words.Count, rr.Words.Count);
        var same = 0;
        for (var i = 0; i < n; i++)
            if (csRuns[0].Words[i].Text == rr.Words[i].Text) same++;
        Console.WriteLine($"逐条一致 {same}/{n}（{(n == 0 ? 0 : 100.0 * same / n):F0}%）");
        return 0;
    }

}

static class LiveOcr
{
    public static async Task<int> RunAsync(string hwndArg)
    {
        VisionSidecarClient? c = null;
        try { return await RunCoreAsync(hwndArg, x => c = x); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败: " + ex.Message);
            if (c is not null) Console.Error.WriteLine("--- sidecar stderr ---\n" + c.StderrSnapshot);
            return 4;
        }
    }

    private static async Task<int> RunCoreAsync(string hwndArg, Action<VisionSidecarClient> onClient)
    {
        var hwnd = long.Parse(hwndArg.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber);
        var exe = VisionSidecarLocator.Locate(null);
        if (exe is null) { Console.Error.WriteLine("未找到 vision-svc.exe"); return 2; }
        var cfg = new VisionSidecarConfig { ExePath = exe, Fps = 15, Verbose = true, Backend = "auto" };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: (nint)hwnd, width: 640, height: 360);
        onClient(client);
        var started = await client.SendAsync(new VisionMsg { Cmd = "start", Hwnd = hwnd, Fps = 15 }, 8000);
        Console.WriteLine($"start -> {started.Ev}");

        // 等一帧
        FrameData? frame = null;
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 5000 && frame is null)
        {
            if (client.Ring.TryReadLatest(out var f)) frame = f;
            else await Task.Delay(15);
        }
        if (frame is null) { Console.Error.WriteLine("5s 内没拿到捕获帧"); return 3; }
        Console.WriteLine($"捕获帧 {frame.Width}x{frame.Height}（等待 {wait.ElapsedMilliseconds}ms）");

        // 预热（同时加载模型）
        var w0 = Stopwatch.StartNew();
        var warm = await client.RecognizeAsync(frame, fromCapture: true);
        Console.WriteLine($"预热(src=capture) {w0.ElapsedMilliseconds}ms  {warm.Words.Count} 词");

        for (var i = 0; i < 1; i++)
        {
            var a = Stopwatch.StartNew();
            var ra = await client.RecognizeAsync(frame, fromCapture: true);
            var tA = a.ElapsedMilliseconds;
            var b = Stopwatch.StartNew();
            var rb = await client.RecognizeAsync(frame, fromCapture: false);
            var tB = b.ElapsedMilliseconds;
            Console.WriteLine($"#{i} src=capture {tA}ms/{ra.Words.Count}词   upload {tB}ms/{rb.Words.Count}词   差 {tA - tB}ms");
        }

        var detSw = Stopwatch.StartNew();
        var boxes = await client.DetectAsync(frame, fromCapture: true);
        Console.WriteLine($"YOLO(src=capture) {detSw.ElapsedMilliseconds}ms  {boxes.Count} 框  {client.LastDetectInfo}");
        var detSw2 = Stopwatch.StartNew();
        var boxes2 = await client.DetectAsync(frame, fromCapture: false);
        Console.WriteLine($"YOLO(upload)      {detSw2.ElapsedMilliseconds}ms  {boxes2.Count} 框");
        Console.WriteLine("--- sidecar stderr ---");
        Console.WriteLine(client.StderrSnapshot);
        await client.SendAsync(VisionMsg.CmdOf("stop"), 3000);
        return 0;
    }
}

static class YoloAb
{
    public static async Task<int> RunAsync(string png)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        var info = new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var conv = new SkiaSharp.SKBitmap(info);
        bmp.CopyTo(conv, SkiaSharp.SKColorType.Bgra8888);
        var frame = new FrameData(conv.Bytes, bmp.Width, bmp.Height, 0);
        Console.WriteLine($"帧 {frame.Width}x{frame.Height}");

        // 进程内
        var model = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "yolo", "yolo26n.onnx");
        if (model is null) { Console.WriteLine("找不到 yolo 模型"); return 2; }
        var classes = EfootballBot.Core.System.AppPaths.FindBundledFile("models", "yolo", "classes.txt");
        var factory = new EfootballBot.Core.Inference.OnnxSessionFactory(null);
        using var det = new EfootballBot.Core.Vision.Yolo.YoloDetector(model, classes, factory, 0.25f, 0.45f);
        for (var i = 0; i < 3; i++) det.Detect(frame, default);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) det.Detect(frame, default);
        sw.Stop();
        Console.WriteLine($"进程内 YOLO: {sw.Elapsed.TotalMilliseconds / 5:F1} ms/帧  标签={det.ModelLabel}");

        // 侧车
        var exe = VisionSidecarLocator.Locate(null);
        if (exe is null) { Console.WriteLine("未找到 vision-svc.exe"); return 0; }
        var cfg = new EfootballBot.Core.Config.VisionSidecarConfig { ExePath = exe, Verbose = true };
        await using var client = await VisionSidecarClient.StartAsync(cfg, hwnd: 0, width: 1920, height: 1080);
        await client.DetectAsync(frame); // 预热（含模型加载）
        var sw2 = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await client.DetectAsync(frame);
        sw2.Stop();
        Console.WriteLine($"侧车   YOLO: {sw2.Elapsed.TotalMilliseconds / 5:F1} ms/帧  {client.LastDetectInfo}");
        return 0;
    }
}
