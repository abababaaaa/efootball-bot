using System.Runtime.InteropServices;

namespace EfootballBot.Core.Capture;

/// <summary>
/// GDI 兜底捕获器：PrintWindow(PW_RENDERFULLCONTENT) 让目标窗口自行把内容渲染进内存 DIB。
/// 不创建任何 D3D 设备 / 捕获会话，DWM 不参与——在硬件 GPU 不可用（驱动挂起、虚拟显示、
/// 远程会话）等 WGC/DXGI 都无法工作的环境下仍能拿到画面，且不存在"WGC 帧池建在 WARP
/// 设备上触发 dwm.exe 崩溃"的问题。窗口被遮挡 / 位于后台同样可捕获。
/// 代价：整窗 CPU 拷贝、帧率低；挂机感知只需 ≤300ms 粒度，足够。
/// </summary>
public sealed class GdiWindowCapture : ICaptureProvider
{
    /// <summary>Win 8.1+：含 D3D 硬件加速内容在内的窗口强制渲染（后台可用）。</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    private readonly object _gate = new();
    private nint _hwnd;
    private volatile bool _running;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private FrameData? _latest;
    private volatile int _minFrameIntervalMs = 33;
    private long _lastProduceTick;

    private Thread? _thread;
    private volatile bool _stopRequested;

    public event EventHandler? FrameUpdated;

    public bool IsRunning => _running;
    public nint Hwnd => _hwnd;
    public DateTime LastFrameAt => _lastFrameAt;

    /// <summary>
    /// 最近一次抓帧失败的原因（null=成功）。此前抓帧失败是 `catch { ok = false; }` + Sleep 无限
    /// 重试、一行日志都没有，导致"GDI 报已就绪却始终无帧"无法归因（2026-10-29 实机故障现场）。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>累计成功帧数（首帧验证与"报就绪但从未出帧"的诊断用）。</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);
    private long _frameCount;

    /// <summary>
    /// 启动并等待首帧（最多 timeoutMs）。返回 true=已出帧，可对外宣称"GDI 就绪"；
    /// false=启动后仍拿不到画面，LastError 说明原因。
    /// 存在的意义：旧代码 `Start()` 一返回就打"GDI 兜底捕获已就绪"，于是日志里出现
    /// 「21:28:14 已就绪」紧接「21:28:16 迟迟没有产出画面」的自相矛盾，并让下次启动
    /// 基于这个未经验证的判定直连 GDI。
    /// </summary>
    public bool StartAndWaitFirstFrame(nint hwnd, int clientW, int clientH, int timeoutMs = 2000)
    {
        Start(hwnd, clientW, clientH);
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (TryGetLatest() is not null) return true;
            Thread.Sleep(50);
        }
        return TryGetLatest() is not null;
    }

    public void Start(nint hwnd, int clientW, int clientH)
    {
        lock (_gate)
        {
            if (_running) return;
            _hwnd = hwnd;
            _stopRequested = false;
            _running = true;
            LastError = null;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "GdiWindowCapture",
            };
            _thread.Start();
        }
    }

    private void CaptureLoop()
    {
        nint hdcMem = nint.Zero;
        nint hBmp = nint.Zero, hBmpOld = nint.Zero;
        nint bits = nint.Zero;
        int bmpW = 0, bmpH = 0;
        try
        {
            var hdcScreen = GetDC(nint.Zero);
            try { hdcMem = CreateCompatibleDC(hdcScreen); }
            finally { ReleaseDC(nint.Zero, hdcScreen); }
            if (hdcMem == nint.Zero) return;

            while (!_stopRequested)
            {
                // 节流：按最小帧间隔产出（与 DXGI 后端的休眠节流一致）
                int interval = Math.Max(33, _minFrameIntervalMs);
                long wait = interval - (Environment.TickCount64 - _lastProduceTick);
                if (wait > 0)
                {
                    Thread.Sleep((int)Math.Min(wait, 100));
                    continue;
                }

                bool ok;
                try
                {
                    ok = CaptureOne(hdcMem, ref hBmp, ref hBmpOld, ref bits, ref bmpW, ref bmpH);
                }
                catch (Exception ex)
                {
                    ok = false;
                    LastError = $"抓帧异常：{ex.GetType().Name}: {ex.Message}";
                }

                if (ok)
                {
                    _lastProduceTick = Environment.TickCount64;
                    _lastFrameAt = DateTime.Now;
                    LastError = null;
                    Interlocked.Increment(ref _frameCount);
                }
                else
                {
                    Thread.Sleep(100); // 窗口无效 / 渲染失败：稍后重试
                }
            }
        }
        finally
        {
            if (hBmpOld != nint.Zero) SelectObject(hdcMem, hBmpOld);
            if (hBmp != nint.Zero) DeleteObject(hBmp);
            if (hdcMem != nint.Zero) DeleteDC(hdcMem);
            _running = false;
        }
    }

    /// <summary>抓一帧：按窗口 rect 尺寸准备 DIB → PrintWindow → 拷出 BGRA → 降采样 ≤720。
    /// 每个失败分支都写 LastError——这条链路静默失败过一次（2026-10-29 实机：三层兜底全无帧
    /// 但日志空白），归因信息比"失败"本身更重要。</summary>
    private bool CaptureOne(nint hdcMem, ref nint hBmp, ref nint hBmpOld, ref nint bits, ref int bmpW, ref int bmpH)
    {
        if (!GetWindowRect(_hwnd, out var rc))
        {
            LastError = "GetWindowRect 失败（窗口句柄可能已失效）";
            return false;
        }
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192)
        {
            LastError = $"窗口尺寸异常 {w}×{h}";
            return false;
        }

        // 窗口尺寸变化（含首次）：重建 DIB。biHeight 取负 = top-down，与 WGC 输出行序一致
        if (w != bmpW || h != bmpH)
        {
            if (hBmpOld != nint.Zero) { SelectObject(hdcMem, hBmpOld); hBmpOld = nint.Zero; }
            if (hBmp != nint.Zero) { DeleteObject(hBmp); hBmp = nint.Zero; }
            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            hBmp = CreateDIBSection(hdcMem, in bi, DIB_RGB_COLORS, out bits, nint.Zero, 0);
            if (hBmp == nint.Zero || bits == nint.Zero)
            {
                hBmp = nint.Zero;
                LastError = $"CreateDIBSection 失败（{w}×{h}）";
                return false;
            }
            hBmpOld = SelectObject(hdcMem, hBmp);
            bmpW = w;
            bmpH = h;
        }

        // PW_RENDERFULLCONTENT：窗口被遮挡 / 在后台时也强制其自行渲染
        if (!PrintWindow(_hwnd, hdcMem, PW_RENDERFULLCONTENT))
        {
            // 个别老窗口不支持：仅前台可见时 BitBlt 兜底
            var wdc = GetWindowDC(_hwnd);
            try
            {
                if (!BitBlt(hdcMem, 0, 0, w, h, wdc, 0, 0, SRCCOPY))
                {
                    LastError = "PrintWindow 与 BitBlt 均失败" +
                        (EfootballBot.Core.System.GameWindow.IsResponding(_hwnd) ? "（窗口有响应）" : "（窗口无响应，疑似游戏卡死）");
                    return false;
                }
            }
            finally { ReleaseDC(_hwnd, wdc); }
        }

        var full = new byte[w * h * 4];
        Marshal.Copy(bits, full, 0, full.Length);

        // PrintWindow 黑帧（实机 2026-10-11：全屏 D3D 交换链下 PrintWindow 客户区全黑、
        // 仅标题栏可见）→ 退化为屏幕区域 BitBlt：窗口矩形所对的屏幕内容（全屏游戏即游戏画面）
        if (IsMostlyBlackLocal(full, w, h))
        {
            var sdc = GetDC(nint.Zero);
            try
            {
                if (BitBlt(hdcMem, 0, 0, w, h, sdc, rc.Left, rc.Top, SRCCOPY))
                    Marshal.Copy(bits, full, 0, full.Length);
            }
            finally { ReleaseDC(nint.Zero, sdc); }
        }

        var data = WindowCapture.DownscaleIfNeeded(full, w, h, out int ow, out int oh);
        _latest = new FrameData(data, ow, oh, Environment.TickCount64);
        FrameUpdated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>粗黑帧判定：跳过标题栏（非客户区可正常取到），从 1/4 高度起隔行采样，
    /// 全部 RGB 和 &lt; 24 即黑（全屏 D3D 交换链下 PrintWindow 客户区整幅黑）。</summary>
    private static bool IsMostlyBlackLocal(byte[] bgra, int w, int h)
    {
        for (int y = Math.Max(4, h / 4); y < h - 4; y += 8)
            for (int x = 4; x < w - 4; x += 8)
            {
                int i = (y * w + x) * 4;
                if (bgra[i] + bgra[i + 1] + bgra[i + 2] >= 24) return false;
            }
        return true;
    }

    public FrameData? TryGetLatest() => _latest;

    public void SetMinFrameInterval(int ms)
    {
        ms = Math.Max(0, ms);
        if (ms > 0 && ms < 33) ms = 33;
        _minFrameIntervalMs = ms;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running && _thread is null) return;
            _stopRequested = true;
            _running = false;
        }
        try { _thread?.Join(2000); } catch { }
        _thread = null;
        _latest = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ---- Win32 interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(
        nint hdc, in BITMAPINFOHEADER pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObject);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
