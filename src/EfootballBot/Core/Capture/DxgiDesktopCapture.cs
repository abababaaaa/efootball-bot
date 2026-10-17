using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace EfootballBot.Core.Capture;

/// <summary>
/// 基于 DXGI Desktop Duplication API 的全屏捕获器。
/// 从游戏窗口所在显示器复制桌面帧，按窗口 rect 裁剪后降采样到 ≤720p。
/// 作为 WGC 捕获的默认实现（在某些独占全屏 / 系统版本下 WGC 可能不可用）。
/// </summary>
public sealed class DxgiDesktopCapture : ICaptureProvider
{
    private readonly object _gate = new();
    private nint _hwnd;
    private int _clientW;
    private int _clientH;
    private bool _running;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private FrameData? _latest;
    private volatile int _minFrameIntervalMs;
    private long _lastProduceTick;

    // D3D11 / DXGI 资源
    private SharpDX.Direct3D11.Device? _device;
    private DeviceContext? _context;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;
    private int _stagingW;
    private int _stagingH;
    private int _outputWidth;
    private int _outputHeight;
    private int _outputOffsetX;
    private int _outputOffsetY;
    private RawRectangle _windowRect;

    // 后台捕获线程
    private Thread? _captureThread;
    private volatile bool _stopRequested;

    public event EventHandler? FrameUpdated;

    /// <summary>诊断日志（适配器/输出绑定选择过程）。</summary>
    public event Action<string>? StepLog;
    private void Step(string s) => StepLog?.Invoke(s);

    public bool IsRunning => _running;
    public nint Hwnd => _hwnd;
    public DateTime LastFrameAt => _lastFrameAt;

    public void Start(nint hwnd, int clientW, int clientH)
    {
        lock (_gate)
        {
            if (_running) return;
            _hwnd = hwnd;
            _clientW = Math.Max(2, clientW);
            _clientH = Math.Max(2, clientH);
            _stopRequested = false;

            try
            {
                InitializeD3D();
                _running = true;
                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "DxgiDesktopCapture",
                    Priority = ThreadPriority.AboveNormal,
                };
                _captureThread.Start();
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }
    }

    private void InitializeD3D()
    {
        // 混合显卡笔记本（iGPU + RTX 独显）上，默认适配器未必拥有游戏窗口所在的显示器输出：
        // DuplicateOutput 建在"非该输出属主"的适配器上会启动成功但帧全黑
        // （2026-10-11 用户实机：RTX 3060 Laptop，DXGI 捕获画面全黑 → 感知全盲）。
        // 因此逐 适配器×输出 现场取帧做黑帧校验，首个能出真实画面的组合胜出；
        // 窗口所在显示器的组合优先尝试（正常机器一次命中，零额外开销）。
        nint hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);

        var candidates = new List<(SharpDX.DXGI.Adapter Adapter, Output Output)>();
        using (var factory = new Factory1())
        {
            foreach (var adapter in factory.Adapters)
            {
                // WARP 绝不尝试：SharpDX AdapterDescription 不暴露 Flags，
                // 用名称/厂商 ID 识别软件适配器（Microsoft Basic Render Driver、WARP 0x1414）
                var desc = adapter.Description;
                bool software = desc.Description.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                                desc.VendorId == 0x1414;
                if (software) continue;
                foreach (var output in adapter.Outputs)
                {
                    if (!output.Description.IsAttachedToDesktop) continue;
                    if (output.Description.MonitorHandle == hMonitor)
                        candidates.Insert(0, (adapter, output)); // 窗口所在输出优先
                    else
                        candidates.Add((adapter, output));
                }
            }
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("未找到可用的 DXGI 输出（显示器）。");

        Exception? lastError = null;
        try
        {
            foreach (var (adapter, output) in candidates)
            {
                try
                {
                    if (TryInitializeOn(adapter, output)) return;
                }
                catch (Exception ex)
                {
                    Step($"候选输出复制失败：{ex.Message}，试下一个");
                    lastError = ex;
                }
                ReleaseResources(); // 该候选失败/全黑：清掉半套资源，试下一个
            }
        }
        finally
        {
            foreach (var (adapter, output) in candidates)
            {
                try { output.Dispose(); } catch { }
                try { adapter.Dispose(); } catch { }
            }
        }
        throw new InvalidOperationException(lastError is null
            ? "DXGI 桌面复制在所有显示器输出上均不可用或全黑。"
            : $"DXGI 桌面复制在所有显示器输出上均不可用或全黑：{lastError.Message}", lastError);
    }

    /// <summary>在指定 适配器×输出 上建设备与复制会话，并现场取帧校验非黑。
    /// 成功时填充实例字段并返回 true；失败/全黑返回 false 或抛异常（由调用方清理重试）。</summary>
    private bool TryInitializeOn(SharpDX.DXGI.Adapter adapter, Output output)
    {
        var device = new SharpDX.Direct3D11.Device(adapter, DeviceCreationFlags.BgraSupport);
        _device = device;
        _context = device.ImmediateContext;

        var outputDesc = output.Description;
        _outputWidth = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
        _outputHeight = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;
        // 桌面纹理原点对应该 output 的 DesktopBounds 左上角（副显示器可能为负坐标）
        _outputOffsetX = outputDesc.DesktopBounds.Left;
        _outputOffsetY = outputDesc.DesktopBounds.Top;

        using var output5 = output.QueryInterface<Output5>();
        _duplication = output5.DuplicateOutput(device);

        // staging 纹理按窗口实际尺寸惰性创建（EnsureStaging，随窗口移动/缩放重建）——
        // 不再预建整幅桌面大小：4K 桌面全幅 staging 每帧 GPU→CPU 回读 ~33MB，窗口通常
        // 只占一小块，绝大部分是白读的带宽
        UpdateWindowRect();

        // 现场取帧校验（≤1.5s）：全黑 = 该适配器不是此输出的属主，换下一个候选。
        // 桌面有任意变化时首帧即到；静止桌面 AcquireNextFrame 首次调用也会返回当前整幅。
        var duplication = _duplication;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var result = duplication.TryAcquireNextFrame(300, out _, out var desktopResource);
            if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code) continue;
            if (!result.Success) throw new SharpDXException(result);
            try
            {
                using (desktopResource)
                using (var d3dResource = desktopResource.QueryInterface<SharpDX.Direct3D11.Resource>())
                {
                    UpdateWindowRect();
                    var raw = CopyWindowBgra(d3dResource);
                    if (raw.Length > 0 && !IsMostlyBlack(raw))
                    {
                        Step($"桌面复制绑定：{adapter.Description.Description}" +
                             $"（输出 {_outputWidth}×{_outputHeight}）");
                        return true;
                    }
                }
            }
            finally
            {
                try { duplication.ReleaseFrame(); } catch { }
            }
        }
        Step("候选输出复制画面全黑（适配器非该输出属主），换下一个输出");
        return false; // 反复取帧全黑：属主不对，换下一个候选
    }
    /// <summary>采样判定一帧是否接近全黑（RGB 均 &lt;8 的采样点 ≥99%）。</summary>
    private static bool IsMostlyBlack(byte[] bgra)
    {
        if (bgra.Length < 4) return true;
        const int step = 97;
        int total = 0, dark = 0;
        for (int i = 0; i + 3 < bgra.Length; i += step * 4)
        {
            total++;
            if (bgra[i] < 8 && bgra[i + 1] < 8 && bgra[i + 2] < 8) dark++;
        }
        return total > 0 && dark * 100 / total >= 99;
    }

    private void CaptureLoop()
    {
        while (!_stopRequested)
        {
            try
            {
                if (!TryAcquireAndProcessFrame())
                {
                    // 无新帧（超时），短暂休眠后重试
                    Thread.Sleep(10);
                    continue;
                }

                // 节流：按最小帧间隔休眠
                int interval = Math.Max(33, _minFrameIntervalMs);
                long elapsed = Environment.TickCount64 - _lastProduceTick;
                if (elapsed < interval)
                    Thread.Sleep((int)(interval - elapsed));
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
            {
                // 设备丢失（显示器切换 / 独占全屏切换），标记需要重建
                _running = false;
                break;
            }
            catch (Exception)
            {
                // 其他瞬时错误，继续循环
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>获取一帧桌面、裁剪窗口区域、降采样、产出 FrameData。返回 false 表示无新帧。</summary>
    private bool TryAcquireAndProcessFrame()
    {
        if (_duplication is null || _stagingTexture is null || _context is null)
            return false;

        var result = _duplication.TryAcquireNextFrame(100, out var frameInfo, out var desktopResource);
        if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code)
            return false;
        if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
            throw new SharpDXException(result);
        if (!result.Success)
            throw new SharpDXException(result);

        try
        {
            using (desktopResource)
            {
                // DXGI.Resource → D3D11.Resource（桌面帧底层是 D3D11 纹理）
                using var d3dResource = desktopResource.QueryInterface<SharpDX.Direct3D11.Resource>();
                // 每帧更新窗口 rect（窗口可能被移动 / 调整大小）
                UpdateWindowRect();

                int winW = _windowRect.Right - _windowRect.Left;
                int winH = _windowRect.Bottom - _windowRect.Top;
                if (winW <= 0 || winH <= 0) return false;

                // 只回读窗口区域（CopyWindowBgra，GPU 端按矩形拷贝），
                // 再降采样到 ≤720p（复用 WindowCapture 的降采样逻辑，保证输出尺寸一致）
                var raw = CopyWindowBgra(d3dResource);
                var data = WindowCapture.DownscaleIfNeeded(raw, winW, winH, out int ow, out int oh);

                long tick = Environment.TickCount64;
                _lastProduceTick = tick;
                _latest = new FrameData(data, ow, oh, tick);
                _lastFrameAt = DateTime.Now;
                FrameUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            try { _duplication.ReleaseFrame(); } catch { }
        }
        return true;
    }

    /// <summary>
    /// 把桌面帧中的窗口区域拷到窗口尺寸 staging 纹理并读回。GPU 端 CopySubresourceRegion
    /// 只拷窗口与 output 的交集矩形，替代旧「整幅桌面 CopyResource → CPU 裁剪」——
    /// 4K 桌面每帧省 ~30MB 的 GPU→CPU 回读带宽。窗口越出本 output 的部分留黑
    /// （与旧 CropBgra 语义一致，引擎黑帧守卫接管全黑情形）。调用前需 UpdateWindowRect()。
    /// 仅捕获线程调用。
    /// </summary>
    private byte[] CopyWindowBgra(SharpDX.Direct3D11.Resource desktop)
    {
        int winW = _windowRect.Right - _windowRect.Left;
        int winH = _windowRect.Bottom - _windowRect.Top;
        var dst = new byte[Math.Max(0, winW) * Math.Max(0, winH) * 4];
        if (winW <= 0 || winH <= 0) return dst;

        // 与 output 可见区域求交集（窗口在其他显示器/跨屏时为部分或空）
        int srcX = _windowRect.Left - _outputOffsetX;
        int srcY = _windowRect.Top - _outputOffsetY;
        int cx = Math.Max(0, srcX);
        int cy = Math.Max(0, srcY);
        int cxEnd = Math.Min(_outputWidth, srcX + winW);
        int cyEnd = Math.Min(_outputHeight, srcY + winH);
        int boxW = cxEnd - cx;
        int boxH = cyEnd - cy;
        if (boxW <= 0 || boxH <= 0) return dst; // 完全不在本 output：全黑帧

        EnsureStaging(boxW, boxH);
        // SharpDX 签名：（源, 源子资源, 源区域, 目标, 目标子资源, 目标X, Y, Z）
        _context!.CopySubresourceRegion(desktop, 0,
            new ResourceRegion(cx, cy, 0, cxEnd, cyEnd, 1),
            _stagingTexture!, 0, 0, 0, 0);
        var dataBox = _context.MapSubresource(_stagingTexture!, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            int stride = dataBox.RowPitch;
            nint srcPtr = dataBox.DataPointer;
            for (int row = 0; row < boxH; row++)
            {
                Marshal.Copy(srcPtr + row * stride, dst,
                    ((cy - srcY) + row) * (winW * 4) + (cx - srcX) * 4, boxW * 4);
            }
        }
        finally
        {
            _context.UnmapSubresource(_stagingTexture!, 0);
        }
        return dst;
    }

    /// <summary>staging 纹理按需创建/随窗口尺寸变化重建（仅捕获线程调用）。</summary>
    private void EnsureStaging(int w, int h)
    {
        if (_stagingTexture is not null && _stagingW == w && _stagingH == h) return;
        _stagingTexture?.Dispose();
        _stagingTexture = new Texture2D(_device!, new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });
        _stagingW = w;
        _stagingH = h;
    }

    private void UpdateWindowRect()
    {
        if (GetWindowRect(_hwnd, out var rect))
        {
            _windowRect = new RawRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
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
            if (!_running && _captureThread is null) return;
            _stopRequested = true;
            _running = false;
        }
        _captureThread?.Join(1000);
        _captureThread = null;
        ReleaseResources();
        _latest = null;
    }

    private void ReleaseResources()
    {
        try { _duplication?.ReleaseFrame(); } catch { }
        try { _duplication?.Dispose(); } catch { }
        try { _stagingTexture?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _duplication = null;
        _stagingTexture = null;
        _stagingW = 0;
        _stagingH = 0;
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ---- user32 interop ----
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
