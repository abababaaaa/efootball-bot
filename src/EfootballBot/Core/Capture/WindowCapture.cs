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
public sealed class WindowCapture : IDisposable
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
    /// 采样分辨率上限：高≤720，等比缩放。全屏 3440×1440 → 1720×720（DWM 拷贝量降 4 倍）。
    /// 所有 ROI 检测均为归一化坐标+占比判定，降采样不影响识别；窗口化 1280×720 本来就不超上限。
    /// </summary>
    private static SizeInt32 CapSize(int width, int height)
    {
        width = Math.Max(2, width); height = Math.Max(2, height);
        if (height <= 720) return new SizeInt32 { Width = width, Height = height };
        double scale = 720.0 / height;
        int w = Math.Max(2, (int)Math.Round(width * scale));
        return new SizeInt32 { Width = w, Height = 720 };
    }

    public void Start(nint hwnd, int width, int height)
    {
        lock (_gate)
        {
            if (_running) return;
            _hwnd = hwnd;
            _size = CapSize(width, height);
            if (_size.Width != width || _size.Height != height)
                Step($"[捕获] 降采样：{width}×{height} → {_size.Width}×{_size.Height}（降低 DWM 拷贝开销）");

            Step("① 创建 D3D11 设备");
            CreateDevice();

            Step("② 获取捕获工厂 (IGraphicsCaptureItemInterop)");
            nint interop = CaptureInterop.GetCaptureItemInteropPtr();
            try
            {
                Step("③ CreateForWindow 并包装 GraphicsCaptureItem");
                nint itemPtr = CaptureInterop.CreateItemForWindow(interop, hwnd);
                var item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);

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
                Step("⑥ 捕获已开始");
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            if (!_running || _pool is null) return;
            var capped = CapSize(width, height);
            // 幂等：引擎按客户区原始尺寸调用，封顶后尺寸与当前一致时不重建
            if (capped.Width == _size.Width && capped.Height == _size.Height) return;
            _size = capped;
            ReleaseHeldFrames();
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
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
        int hr = CaptureInterop.D3D11CreateDevice(
            nint.Zero,
            CaptureInterop.D3D_DRIVER_TYPE_HARDWARE,
            nint.Zero,
            CaptureInterop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nint.Zero, 0,
            CaptureInterop.D3D11_SDK_VERSION,
            out _d3dDevicePtr,
            out _,
            out _d3dContextPtr);
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

            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                Step($"[捕获] 帧尺寸变化/缩放不支持：{_size.Width}×{_size.Height} → {frame.ContentSize.Width}×{frame.ContentSize.Height}");
                lock (_gate)
                {
                    _size = frame.ContentSize;
                    ReleaseHeldFrames();
                    _pool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                }
                return;
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

            using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                .AsTask().GetAwaiter().GetResult();
            var data = ToBgraArray(bitmap, out int w, out int h);
            _latest = new FrameData(data, w, h, tick);
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
