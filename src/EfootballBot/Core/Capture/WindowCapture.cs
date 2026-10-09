using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace EfootballBot.Core.Capture;

/// <summary>一帧画面（BGRA8 预乘，托管数组，可跨线程安全使用）。</summary>
public sealed record FrameData(byte[] Bgra, int Width, int Height, long TimestampMs)
{
    public DateTime CapturedAt { get; } = DateTime.Now;
}

/// <summary>
/// 基于 Windows Graphics Capture 的窗口捕获器：窗口被遮挡 / 位于后台也能持续拿到画面。
/// </summary>
public sealed class WindowCapture : ICaptureProvider
{
    private readonly object _gate = new();
    private nint _hwnd;
    private nint _d3dDevicePtr;
    private nint _d3dContextPtr;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _size;
    private FrameData? _latest;
    private long _lastProduceTick;
    private GpuVideoScaler? _gpuScaler;
    private bool _gpuFailed;          // 本捕获会话 GPU 缩放不可用后静默回退 CPU
    private bool _running;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private int _frameErrorCount;

    // —— 背压节流（全屏挂机 DWM 降载）——
    // 原理：帧池仅 2 缓冲，压住不释放则 DWM 停产；定时器到点释放恢复采样。
    private volatile int _minFrameIntervalMs;          // 0 = 全速（33ms 跳帧）
    private readonly List<Direct3D11CaptureFrame> _heldFrames = new(2);
    private global::System.Threading.Timer? _throttleTimer;
    private int _throttleTimerPeriod;                  // 当前定时器周期，避免重复重建

    public event EventHandler? FrameUpdated;
    public event EventHandler<string>? CaptureClosed;
    /// <summary>诊断日志（捕获初始化各步骤）。</summary>
    public event Action<string>? StepLog;
    private void Step(string s) => StepLog?.Invoke(s);

    public bool IsRunning => _running;
    public nint Hwnd => _hwnd;
    public bool HasFrame => _latest is not null;
    public DateTime LastFrameAt => _lastFrameAt;

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    /// <summary>
    /// 输出帧分辨率上限：高≤720，等比缩放。全屏 3440×1440 → 1720×720（帧数据量降 4 倍）。
    /// 所有 ROI 检测均为归一化坐标+占比判定，降采样不影响识别。
    /// 注意：此缩放只能在 CPU 侧做——WGC 帧池尺寸小于内容尺寸时是「裁切左上角」而非缩放。
    /// </summary>
    internal static SizeInt32 CapSize(int width, int height)
    {
        width = Math.Max(2, width); height = Math.Max(2, height);
        if (height <= 720) return new SizeInt32 { Width = width, Height = height };
        double scale = 720.0 / height;
        int w = Math.Max(2, (int)Math.Round(width * scale));
        return new SizeInt32 { Width = w, Height = 720 };
    }

    private static SizeInt32 ClampSize(int width, int height)
        => new() { Width = Math.Max(2, width), Height = Math.Max(2, height) };

    /// <summary>CPU 侧最近邻等比降采样到高≤720；未超上限时原样返回（不复制）。</summary>
    internal static byte[] DownscaleIfNeeded(byte[] src, int sw, int sh, out int dw, out int dh)
    {
        var t = CapSize(sw, sh);
        dw = t.Width; dh = t.Height;
        if (dw == sw && dh == sh) return src;
        return NearestSample(src, sw, sh, dw, dh);
    }

    /// <summary>最近邻重采样（BGRA）：目标像素按比例映射回源像素，保留完整画面不裁切。</summary>
    internal static byte[] NearestSample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int srow = (y * sh / dh) * sw * 4;
            int drow = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                int si = srow + (x * sw / dw) * 4;
                int di = drow + x * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }

    public void Start(nint hwnd, int width, int height)
    {
        lock (_gate)
        {
            if (_running) return;
            _hwnd = hwnd;
            // 帧池必须按内容原始尺寸建：WGC 小帧池会裁切左上角而不是缩放
            // （超宽全屏 3440×1440 建 1720×720 池只能拿到左上 1/4 画面）。
            // 降采样在取帧后于 CPU 侧做，输出帧仍为高≤720。
            _size = ClampSize(width, height);
            var capped = CapSize(width, height);
            if (capped.Width != _size.Width || capped.Height != _size.Height)
                Step($"[捕获] 原始 {_size.Width}×{_size.Height}：全尺寸采集，输出降采样到 {capped.Width}×{capped.Height}");

            Step("① 创建 D3D11 设备");
            CreateDevice();

            Step("② 获取捕获工厂 (IGraphicsCaptureItemInterop)");
            nint interop = CaptureInterop.GetCaptureItemInteropPtr();
            try
            {
                Step("③ CreateForWindow 并包装 GraphicsCaptureItem");
                nint itemPtr = CaptureInterop.CreateItemForWindow(interop, hwnd);
                var item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                try
                {
                    // 帧池尺寸必须与捕获内容一致：客户区尺寸（如 1280×720）≠ 捕获内容
                    // （含窗口边框阴影，如 1282×752）。用错误值建池会启动即重建帧池
                    // （WGC 重建在高负载/多适配器环境下是 DWM 崩溃放大点）。
                    var frameSize = item.Size;
                    _size = ClampSize(frameSize.Width, frameSize.Height);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }

                Step("④ 创建帧池 (CreateFreeThreaded)");
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _size);
                _pool.FrameArrived += OnFrameArrived;

                Step("⑤ 创建捕获会话");
                _session = _pool.CreateCaptureSession(item);
                _session.StartCapture();
                _running = true;
                Step($"⑥ 捕获已开始（{_size.Width}×{_size.Height}）");
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
    }

    public FrameData? TryGetLatest() => _latest;

    /// <summary>
    /// 设置最小帧间隔（ms）。≤33 = 全速；&gt;33 = 节流（背压压住帧池使 DWM 停产，定时释放）。
    /// 仅在值变化时生效并打日志。
    /// </summary>
    public void SetMinFrameInterval(int ms)
    {
        ms = Math.Max(0, ms);
        if (ms > 0 && ms < 33) ms = 33;
        if (_minFrameIntervalMs == ms) return;
        _minFrameIntervalMs = ms;
        Step($"[捕获] 节流档位切换 → {(ms <= 33 ? "全速" : $"{ms}ms")}");

        if (ms <= 33)
        {
            // 回全速：停定时器 + 立即释放压住的帧
            _throttleTimer?.Dispose();
            _throttleTimer = null;
            _throttleTimerPeriod = 0;
            ReleaseHeldFrames();
        }
        else if (_throttleTimerPeriod != ms)
        {
            _throttleTimer?.Dispose();
            _throttleTimer = new global::System.Threading.Timer(_ => ReleaseHeldFrames(), null, ms, ms);
            _throttleTimerPeriod = ms;
        }
    }

    /// <summary>释放所有压住的帧（允许 DWM 恢复生产）。</summary>
    private void ReleaseHeldFrames()
    {
        lock (_heldFrames)
        {
            foreach (var f in _heldFrames) { try { f.Dispose(); } catch { } }
            _heldFrames.Clear();
        }
    }

    private void CreateDevice()
    {
        // 系统存在虚拟显示适配器（GameViewer/MuMu 等远程/模拟器工具的 WARP 软件设备）时，
        // 默认适配器可能落在虚拟设备上——WGC 帧池建在其上会触发 dwm.exe (d3d10warp.dll)
        // 确定性崩溃（系统卡死黑屏）。必须显式枚举并绑定真实硬件 GPU。
        nint adapter = 0;
        string adapterName = "";
        try
        {
            (adapter, adapterName) = CaptureInterop.PickHardwareAdapter();
        }
        catch { /* 枚举失败走默认路径 */ }

        int hr = CaptureInterop.D3D11CreateDevice(
            adapter,
            adapter != 0 ? CaptureInterop.D3D_DRIVER_TYPE_UNKNOWN : CaptureInterop.D3D_DRIVER_TYPE_HARDWARE,
            nint.Zero,
            CaptureInterop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nint.Zero, 0,
            CaptureInterop.D3D11_SDK_VERSION,
            out _d3dDevicePtr,
            out _,
            out _d3dContextPtr);
        if (adapter != 0)
        {
            Marshal.Release(adapter);
            if (hr == 0)
                Step($"[捕获] D3D11 设备绑定硬件 GPU：{adapterName}");
        }
        if (hr != 0)
        {
            // 兜底：WARP 软渲染设备
            hr = CaptureInterop.D3D11CreateDevice(
                nint.Zero,
                CaptureInterop.D3D_DRIVER_TYPE_WARP,
                nint.Zero,
                CaptureInterop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nint.Zero, 0,
                CaptureInterop.D3D11_SDK_VERSION,
                out _d3dDevicePtr,
                out _,
                out _d3dContextPtr);
            if (hr != 0) throw new InvalidOperationException($"创建 D3D11 设备失败: 0x{hr:X}");
            Step("[捕获] 硬件 GPU 不可用，已回退 WARP 软渲染设备");
        }
        _winrtDevice = CaptureInterop.CreateDirect3DDevice(_d3dDevicePtr);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // 帧池必须与内容原始尺寸严格一致（小了裁切、大了拉伸/黑边）。
            // 尺寸变化（窗口化↔全屏）时按新内容尺寸整套重建；降采样在取帧后做。
            var content = ClampSize(frame.ContentSize.Width, frame.ContentSize.Height);
            if (content.Width != _size.Width || content.Height != _size.Height)
            {
                Exception? recreateError = null;
                lock (_gate)
                {
                    if (_running && _pool is not null && _winrtDevice is not null &&
                        (content.Width != _size.Width || content.Height != _size.Height))
                    {
                        Step($"[捕获] 帧尺寸变化：重建帧池 {_size.Width}×{_size.Height} → " +
                             $"{content.Width}×{content.Height}");
                        _size = content;
                        ReleaseHeldFrames();
                        try
                        {
                            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                        }
                        catch (Exception ex)
                        {
                            recreateError = ex;
                            _running = false; // 帧池已坏，等引擎 WaitForFreshFrameAsync 整套重建
                        }
                    }
                }
                if (recreateError is not null)
                    CaptureClosed?.Invoke(this, $"重建帧池失败：{recreateError.Message}");
                return; // 尺寸过渡帧不产出画面
            }

            // 背压节流：未到间隔时压住帧（池满→DWM 停产），由 _throttleTimer 周期释放。
            // 全速档维持原 33ms 跳帧（直接丢弃，finally 释放）。
            long tick = Environment.TickCount64;
            int interval = _minFrameIntervalMs;
            int effInterval = Math.Max(33, interval);
            if (tick - _lastProduceTick < effInterval)
            {
                if (interval > 33 && _heldFrames.Count < 2)
                {
                    lock (_heldFrames)
                    {
                        if (_heldFrames.Count < 2)
                        {
                            _heldFrames.Add(frame);
                            frame = null; // 所有权移交流单，防止 finally 释放
                        }
                    }
                }
                return;
            }
            _lastProduceTick = tick;
            ReleaseHeldFrames(); // 到点：顺带释放（定时器可能已提前释放，幂等）

            // 取帧：高分辨率时优先走 GPU 硬件缩放（显卡内缩到高≤720 再回读，免全尺寸 CPU 拷贝）；
            // GPU 不可用或源本就≤720p 时回退原 CPU 路径（SoftwareBitmap + 最近邻）。
            byte[] data = null!;
            int ow = 0, oh = 0;
            bool gpuDone = false;
            var cap = CapSize(_size.Width, _size.Height);
            if (!_gpuFailed && (cap.Width != _size.Width || cap.Height != _size.Height))
            {
                try
                {
                    _gpuScaler ??= new GpuVideoScaler(_d3dDevicePtr, Step);
                    data = _gpuScaler.ScaleToBgra(frame.Surface, _size.Width, _size.Height, out ow, out oh);
                    gpuDone = true;
                }
                catch (Exception gex)
                {
                    _gpuFailed = true;
                    Step($"[捕获] GPU 硬件缩放不可用，本捕获会话回退 CPU 缩放：{gex.Message}");
                }
            }
            if (!gpuDone)
            {
                using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                    .AsTask().GetAwaiter().GetResult();
                var full = ToBgraArray(bitmap, out int w, out int h);
                // CPU 侧降采样（高≤720）：下游 OCR/ROI 只接触小图，全尺寸数组随本次 GC 释放
                data = DownscaleIfNeeded(full, w, h, out ow, out oh);
            }
            _latest = new FrameData(data, ow, oh, tick);
            _lastFrameAt = DateTime.Now;
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ObjectDisposedException)
        {
            // 捕获过程中窗口关闭 / 设备丢失
            CaptureClosed?.Invoke(this, ex.Message);
        }
        catch (Exception ex)
        {
            // 记录前几次瞬时错误，便于诊断
            if (++_frameErrorCount <= 3)
                Step($"[捕获] 帧处理失败: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    /// <summary>把 SoftwareBitmap 像素拷贝成紧密排列的 BGRA 字节数组。</summary>
    internal static byte[] ToBgraArray(SoftwareBitmap bitmap, out int width, out int height)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            throw new InvalidOperationException("仅支持 BGRA8 格式");

        // 纯托管路径：CopyToBuffer 紧凑拷贝（stride = width*4），无需 IMemoryBufferByteAccess
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        var buf = new global::Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        bitmap.CopyToBuffer(buf);
        return global::System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buf, 0, width * height * 4);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _minFrameIntervalMs = 0;
            _throttleTimer?.Dispose();
            _throttleTimer = null;
            _throttleTimerPeriod = 0;
            ReleaseHeldFrames();
            try { _gpuScaler?.Dispose(); } catch { }
            _gpuScaler = null;
            _gpuFailed = false;
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null;
            _pool = null;
            FreeDevice();
        }
        _latest = null;
    }

    private void FreeDevice()
    {
        if (_winrtDevice is not null)
        {
            _winrtDevice.Dispose();
            _winrtDevice = null;
        }
        if (_d3dContextPtr != 0) { Marshal.Release(_d3dContextPtr); _d3dContextPtr = 0; }
        if (_d3dDevicePtr != 0) { Marshal.Release(_d3dDevicePtr); _d3dDevicePtr = 0; }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D83FEFC8E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out nint buffer, out uint capacity);
    }
}
