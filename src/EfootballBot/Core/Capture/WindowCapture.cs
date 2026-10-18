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
    private readonly bool _useSharpDx;   // 捕获家族开关：true=SharpDX 旧实现（回退），false=Vortice 新实现（默认）
    private nint _hwnd;
    private nint _d3dDevicePtr;
    /// <summary>SharpDX 设备包装（持有 D3D11 设备生命周期；NativePointer 供 GpuVideoScaler/WGC 用）。</summary>
    private SharpDX.Direct3D11.Device? _sharpDevice;
    /// <summary>Vortice 设备包装（Vortice 家族持有 D3D11 设备生命周期，与 _sharpDevice 互斥，绝不并存）。</summary>
    private Vortice.Direct3D11.ID3D11Device? _vorticeDevice;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _size;
    private FrameData? _latest;
    private long _lastProduceTick;
    private GpuVideoScaler? _gpuScaler;
    private GpuVideoScalerVortice? _gpuScalerVortice;
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

    /// <summary>
    /// useSharpDx = true 时整个捕获家族（D3D 设备创建 + GPU 缩放）走旧 SharpDX 实现（回退路径）；
    /// false（默认）走 Vortice 新实现。默认 true 保持既有调用方（--dump 命令行模式等）行为不变。
    /// </summary>
    public WindowCapture(bool useSharpDx = true)
    {
        _useSharpDx = useSharpDx;
    }

    public bool IsRunning => _running;
    public nint Hwnd => _hwnd;
    public bool HasFrame => _latest is not null;
    public DateTime LastFrameAt => _lastFrameAt;

    /// <summary>强制用 WARP 软渲染设备建帧池。硬件设备 WGC 反复无帧时由观察服务启用
    /// （虚拟显示器/远程会话机器：桌面合成在 WDDM1.3 间接显示驱动上，硬件设备的跨适配器
    /// 帧表面 GPU 缩放 E_INVALIDARG、CPU 拷贝无产出——v1.x 恰因手写 P/Invoke 失败一直
    /// 落在 WARP 上，反而这些机器全部可用）。Stop 不复位，整个运行期保持。</summary>
    public bool ForceWarpDevice { get; set; }

    /// <summary>边框幻影差判定：Win10 的 item.Size 含不可见边框（如 1296×759），首帧 ContentSize
    /// 是可见内容（1282×752）。内容不超出帧池且差值≤32px（纯函数便于单测）。</summary>
    internal static bool IsBorderOnlyContent(int poolW, int poolH, int contentW, int contentH) =>
        contentW <= poolW && contentH <= poolH &&
        poolW - contentW <= 32 && poolH - contentH <= 32;

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
            try
            {
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
            catch
            {
                // 启动中途失败：释放已创建的半套资源，保证下次 Start 干净重来
                // （Stop 有 !_running 短路，不会清理这些）
                try { _session?.Dispose(); } catch { }
                try { _pool?.Dispose(); } catch { }
                _session = null;
                _pool = null;
                FreeDevice();
                throw;
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
        // 家族整体切换：Vortice（默认）或 SharpDX（回退）。同一指针绝不被两套 COM 包装同时持有，
        // 设备创建与后续 GpuVideoScaler 构造必须走同一家族。
        if (!_useSharpDx)
        {
            CreateDeviceVortice();
            return;
        }

        // 兼容模式：硬件设备 WGC 在虚拟显示器/远程会话机器上反复无帧（跨适配器表面失效），
        // 观察服务熔断前强制切 WARP——表面为系统内存，CPU 拷贝必达（即 v1.x 实际运行形态）。
        if (ForceWarpDevice)
        {
            _sharpDevice = new SharpDX.Direct3D11.Device(
                SharpDX.Direct3D.DriverType.Warp,
                SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
            Step("[捕获] 帧池使用 WARP 软渲染设备（虚拟显示器/远程环境兼容模式，CPU 拷贝帧）");
            _d3dDevicePtr = _sharpDevice.NativePointer;
            _winrtDevice = CaptureInterop.CreateDirect3DDevice(_d3dDevicePtr);
            return;
        }

        // 常规路径两级尝试：显式硬件适配器（显存最大，如独显）→ 默认硬件适配器；任一成功即用。
        // 默认不走 WARP（某些环境下 WGC 帧池建在 WARP 设备上会触发 dwm.exe (d3d10warp.dll)
        // 崩溃）；仅在 ForceWarpDevice 兼容模式下启用（见上）。设备创建走 SharpDX：
        // 手写 CreateDXGIFactory1/D3D11CreateDevice
        // P/Invoke 在实机探测中分别恒返 E_NOINTERFACE / E_INVALIDARG（2026-10-11），
        // 曾使显式 GPU 绑定永远落空、WGC 在所有机器上启动失败。
        var adapter = CaptureInterop.PickHardwareAdapter(out var adapterName, out var pickedForOutputs);
        bool boundExplicit = false;
        if (adapter is not null)
        {
            try
            {
                var dev = new SharpDX.Direct3D11.Device(adapter,
                    SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
                _sharpDevice = dev;
                boundExplicit = true;
                Step(pickedForOutputs
                    ? $"[捕获] D3D11 设备绑定硬件 GPU：{adapterName}（显存最大的独显无显示输出，为保帧流改绑有输出的显卡）"
                    : $"[捕获] D3D11 设备绑定硬件 GPU：{adapterName}");
            }
            catch (Exception ex)
            {
                Step($"[捕获] 绑定硬件 GPU {adapterName} 失败（{ex.Message}），改试默认适配器");
            }
            finally { adapter.Dispose(); }
        }

        if (!boundExplicit)
        {
            // 默认硬件适配器；无硬件 GPU 时直接抛出（无硬件则创建失败），由引擎降级链改走 DXGI/GDI
            var dev = new SharpDX.Direct3D11.Device(
                SharpDX.Direct3D.DriverType.Hardware,
                SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
            _sharpDevice = dev;
            Step("[捕获] D3D11 设备使用默认硬件适配器");
        }

        var device = _sharpDevice ?? throw new InvalidOperationException("D3D11 设备创建失败");
        _d3dDevicePtr = device.NativePointer;
        _winrtDevice = CaptureInterop.CreateDirect3DDevice(_d3dDevicePtr);
    }

    /// <summary>
    /// Vortice 家族设备创建：三路径与 SharpDX 版 CreateDevice 一一对应
    /// （ForceWarpDevice 兼容模式 → 显式硬件适配器（CaptureInteropVortice 同款挑选规则）→ 默认硬件适配器）。
    /// 适配器枚举走 CaptureInteropVortice，设备包装为 Vortice.ID3D11Device，与 _sharpDevice 互斥。
    /// WGC 的 WinRT 投影（IDirect3DDevice/帧池）与家族无关，仍走 CaptureInterop.CreateDirect3DDevice。
    /// </summary>
    private void CreateDeviceVortice()
    {
        // 兼容模式：与 SharpDX 版同因（虚拟显示器/远程会话机器跨适配器表面失效）
        if (ForceWarpDevice)
        {
            _vorticeDevice = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                Vortice.Direct3D.DriverType.Warp,
                Vortice.Direct3D11.DeviceCreationFlags.BgraSupport);
            Step("[捕获] 帧池使用 WARP 软渲染设备（虚拟显示器/远程环境兼容模式，CPU 拷贝帧）");
            _d3dDevicePtr = _vorticeDevice.NativePointer;
            _winrtDevice = CaptureInterop.CreateDirect3DDevice(_d3dDevicePtr);
            return;
        }

        // 常规路径两级尝试：显式硬件适配器（显存最大/有输出优先）→ 默认硬件适配器；任一成功即用。
        // 不走 WARP 的原因同 SharpDX 版（WGC 帧池建在 WARP 设备上会触发 dwm.exe 崩溃）。
        var adapter = CaptureInteropVortice.PickHardwareAdapter(out var adapterName, out var pickedForOutputs);
        bool boundExplicit = false;
        if (adapter is not null)
        {
            // 传显式适配器时 DriverType 必须为 Unknown（D3D11CreateDevice 规范）；
            // 此重载返回 Result 而非抛异常，失败时保持与 SharpDX 版相同的"改试默认适配器"日志
            var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(adapter,
                Vortice.Direct3D.DriverType.Unknown, Vortice.Direct3D11.DeviceCreationFlags.BgraSupport, null,
                out var candidate);
            if (result.Success && candidate is not null)
            {
                _vorticeDevice = candidate;
                boundExplicit = true;
                Step(pickedForOutputs
                    ? $"[捕获] D3D11 设备绑定硬件 GPU：{adapterName}（显存最大的独显无显示输出，为保帧流改绑有输出的显卡）"
                    : $"[捕获] D3D11 设备绑定硬件 GPU：{adapterName}");
            }
            else
            {
                Step($"[捕获] 绑定硬件 GPU {adapterName} 失败（0x{result.Code:X8}），改试默认适配器");
            }
            adapter.Dispose();
        }

        if (!boundExplicit)
        {
            // 默认硬件适配器；无硬件 GPU 时直接抛出（无硬件则创建失败），由引擎降级链改走 DXGI/GDI
            _vorticeDevice = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                Vortice.Direct3D.DriverType.Hardware,
                Vortice.Direct3D11.DeviceCreationFlags.BgraSupport);
            Step("[捕获] D3D11 设备使用默认硬件适配器");
        }

        var device = _vorticeDevice ?? throw new InvalidOperationException("D3D11 设备创建失败");
        _d3dDevicePtr = device.NativePointer;
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
            // ① 边框幻影差（Win10 item.Size 含不可见边框 1296×759，内容 1282×752）：
            //    内容不超出帧池即无裁切，差值≤32px 对归一化 ROI 无感——不重建帧池直接产出
            //    （WGC 重建在多适配器环境下是 DWM 崩溃放大点，且 Win10 上重建后易无帧）。
            // ② 真实尺寸变化（窗口化↔全屏等）：按新内容尺寸整套重建；降采样在取帧后做。
            var content = ClampSize(frame.ContentSize.Width, frame.ContentSize.Height);
            if ((content.Width != _size.Width || content.Height != _size.Height) &&
                !IsBorderOnlyContent(_size.Width, _size.Height, content.Width, content.Height))
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
                    // GPU 缩放器与设备创建走同一家族（_d3dDevicePtr 属于哪家包装就交给谁）
                    if (_useSharpDx)
                    {
                        _gpuScaler ??= new GpuVideoScaler(_d3dDevicePtr, Step);
                        data = _gpuScaler.ScaleToBgra(frame.Surface, _size.Width, _size.Height, out ow, out oh);
                    }
                    else
                    {
                        _gpuScalerVortice ??= new GpuVideoScalerVortice(_d3dDevicePtr, Step);
                        data = _gpuScalerVortice.ScaleToBgra(frame.Surface, _size.Width, _size.Height, out ow, out oh);
                    }
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
                // 跨适配器/异常表面可能让 WinRT 异步拷贝永不完成（挂住回调线程、画面永远无产出）：
                // 3s 超时按帧处理失败上报，交给引擎重建/升级捕获设备。
                var copyTask = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask();
                using var bitmap = copyTask.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
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
            try { _gpuScalerVortice?.Dispose(); } catch { }
            _gpuScalerVortice = null;
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
        _d3dDevicePtr = 0;
        // D3D 设备生命周期由所属家族的包装持有，Dispose 即释放（勿再对指针 Marshal.Release）
        try { _sharpDevice?.Dispose(); } catch { }
        _sharpDevice = null;
        try { _vorticeDevice?.Dispose(); } catch { }
        _vorticeDevice = null;
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
