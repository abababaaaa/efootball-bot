using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;

namespace EfootballBot.Core.Capture;

/// <summary>
/// 基于 DXGI Desktop Duplication API 的全屏捕获器（Vortice 家族，默认实现）。
/// 与 DxgiDesktopCapture（SharpDX 家族）逐行为对齐的孪生实现：
/// 从游戏窗口所在显示器复制桌面帧，按窗口 rect 裁剪后降采样到 ≤720p。
/// 两家族是两套 COM 包装，绝不混挂同一设备指针；切换见 AppConfig.ModuleFallback.UseSharpDxCapture。
/// </summary>
public sealed class DxgiDesktopCaptureVortice : ICaptureProvider
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

    // D3D11 / DXGI 资源（Vortice 包装，各自持有独立 COM 引用，Dispose 即 Release）
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingW;
    private int _stagingH;
    private int _outputWidth;
    private int _outputHeight;
    private int _outputOffsetX;
    private int _outputOffsetY;
    private Vortice.RawRect _windowRect;

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
                    Name = "DxgiDesktopCaptureVortice",
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
        // 与 SharpDX 版同策略：混合显卡笔记本上默认适配器未必拥有游戏窗口所在显示器输出，
        // DuplicateOutput 建在"非该输出属主"的适配器上会启动成功但帧全黑。
        // 因此逐 适配器×输出 现场取帧做黑帧校验，首个能出真实画面的组合胜出；
        // 窗口所在显示器的组合优先尝试（正常机器一次命中，零额外开销）。
        nint hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);

        var candidates = new List<(IDXGIAdapter1 Adapter, IDXGIOutput Output)>();
        using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            int i = 0;
            while (factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success)
            {
                i++;
                // WARP 绝不尝试：用名称/厂商 ID 识别软件适配器（Microsoft Basic Render Driver、WARP 0x1414）
                var desc = adapter.Description1;
                bool software = desc.Description.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                                desc.VendorId == 0x1414;
                if (software)
                {
                    adapter.Dispose();
                    continue;
                }
                int j = 0;
                while (adapter.EnumOutputs((uint)j, out IDXGIOutput output).Success)
                {
                    j++;
                    var od = output.Description;
                    if (!od.AttachedToDesktop)
                    {
                        output.Dispose();
                        continue;
                    }
                    if (od.Monitor == hMonitor)
                        candidates.Insert(0, (adapter, output)); // 窗口所在输出优先
                    else
                        candidates.Add((adapter, output));
                }
                // adapter 引用已随候选保存（有 output 命中的场合）；无任何 output 的适配器在此释放
                bool kept = candidates.Any(c => ReferenceEquals(c.Adapter, adapter));
                if (!kept) adapter.Dispose();
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
    private bool TryInitializeOn(IDXGIAdapter1 adapter, IDXGIOutput output)
    {
        // 传显式适配器时 DriverType 必须为 Unknown（D3D11CreateDevice 规范）
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            null, out var device).CheckError();
        var dev = device!; // CheckError 成功后 out 参数必非空
        _device = dev;
        // ImmediateContext 属性包装了 COM GetImmediateContext 出参（持独立引用），随实例字段统一释放
        _context = dev.ImmediateContext;

        var outputDesc = output.Description;
        _outputWidth = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        _outputHeight = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
        // 桌面纹理原点对应该 output 的 DesktopCoordinates 左上角（副显示器可能为负坐标）
        _outputOffsetX = outputDesc.DesktopCoordinates.Left;
        _outputOffsetY = outputDesc.DesktopCoordinates.Top;

        using var output5 = output.QueryInterface<IDXGIOutput5>();
        _duplication = output5.DuplicateOutput(device);

        // staging 纹理按窗口实际尺寸惰性创建（EnsureStaging，随窗口移动/缩放重建），
        // 不预建整幅桌面大小，省 GPU→CPU 回读带宽
        UpdateWindowRect();

        // 现场取帧校验（≤1.5s）：全黑 = 该适配器不是此输出的属主，换下一个候选。
        // 桌面有任意变化时首帧即到；静止桌面 AcquireNextFrame 首次调用也会返回当前整幅。
        var duplication = _duplication!;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var result = duplication.AcquireNextFrame(300, out _, out IDXGIResource desktopResource);
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
            if (result.Failure) throw new SharpGenException(result);
            try
            {
                using (desktopResource)
                using (var desktop = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    UpdateWindowRect();
                    var raw = CopyWindowBgra(desktop);
                    if (raw.Length > 0 && !IsMostlyBlack(raw))
                    {
                        Step($"桌面复制绑定：{adapter.Description1.Description}" +
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
            catch (SharpGenException ex) when (ex.ResultCode.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
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

        var result = _duplication.AcquireNextFrame(100, out _, out IDXGIResource desktopResource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            return false;
        if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
            throw new SharpGenException(result);
        if (result.Failure)
            throw new SharpGenException(result);

        try
        {
            using (desktopResource)
            {
                // DXGI 资源 → D3D11 纹理（桌面帧底层是 D3D11 纹理）
                using var desktop = desktopResource.QueryInterface<ID3D11Texture2D>();
                // 每帧更新窗口 rect（窗口可能被移动 / 调整大小）
                UpdateWindowRect();

                int winW = _windowRect.Right - _windowRect.Left;
                int winH = _windowRect.Bottom - _windowRect.Top;
                if (winW <= 0 || winH <= 0) return false;

                // 只回读窗口区域（CopyWindowBgra，GPU 端按矩形拷贝），
                // 再降采样到 ≤720p（复用 WindowCapture 的降采样逻辑，保证输出尺寸一致）
                var raw = CopyWindowBgra(desktop);
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
    /// 只拷窗口与 output 的交集矩形。窗口越出本 output 的部分留黑
    /// （与 SharpDX 版语义一致，引擎黑帧守卫接管全黑情形）。调用前需 UpdateWindowRect()。
    /// 仅捕获线程调用。
    /// </summary>
    private byte[] CopyWindowBgra(ID3D11Texture2D desktop)
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
        // Vortice 签名（注意：目标在前）：CopySubresourceRegion(目标, 目标子资源, dstX/Y/Z, 源, 源子资源, 源区域)
        // Box 构造顺序：(left, top, front, right, bottom, back)
        _context!.CopySubresourceRegion(_stagingTexture!, 0, 0, 0, 0,
            desktop, 0, new Box(cx, cy, 0, cxEnd, cyEnd, 1));
        _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None,
            out MappedSubresource dataBox).CheckError();
        try
        {
            int stride = (int)dataBox.RowPitch;
            nint srcPtr = dataBox.DataPointer;
            for (int row = 0; row < boxH; row++)
            {
                Marshal.Copy(srcPtr + row * stride, dst,
                    ((cy - srcY) + row) * (winW * 4) + (cx - srcX) * 4, boxW * 4);
            }
        }
        finally
        {
            _context.Unmap(_stagingTexture!, 0);
        }
        return dst;
    }

    /// <summary>staging 纹理按需创建/随窗口尺寸变化重建（仅捕获线程调用）。</summary>
    private void EnsureStaging(int w, int h)
    {
        if (_stagingTexture is not null && _stagingW == w && _stagingH == h) return;
        _stagingTexture?.Dispose();
        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingW = w;
        _stagingH = h;
    }

    private void UpdateWindowRect()
    {
        if (GetWindowRect(_hwnd, out var rect))
        {
            _windowRect = new Vortice.RawRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
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
