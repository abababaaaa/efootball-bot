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
    private int _outputWidth;
    private int _outputHeight;
    private int _outputOffsetX;
    private int _outputOffsetY;
    private RawRectangle _windowRect;

    // 后台捕获线程
    private Thread? _captureThread;
    private volatile bool _stopRequested;

    public event EventHandler? FrameUpdated;

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
        // 1. 创建 D3D11 设备（需要 BGRA 支持，桌面复制帧格式为 BGRA8）
        _device = new SharpDX.Direct3D11.Device(DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;

        // 2. 找到游戏窗口所在的显示器
        nint hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);

        // 3. 枚举 DXGI 输出，找到匹配 hMonitor 的 Output
        using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDevice.Adapter;
        Output? targetOutput = null;
        foreach (var output in adapter.Outputs)
        {
            var desc = output.Description;
            if (desc.MonitorHandle == hMonitor)
            {
                targetOutput = output;
                break;
            }
        }
        if (targetOutput is null)
        {
            // 兜底：用第一个已连接桌面的输出
            targetOutput = adapter.Outputs.FirstOrDefault(o => o.Description.IsAttachedToDesktop);
        }
        if (targetOutput is null)
            throw new InvalidOperationException("未找到可用的 DXGI 输出（显示器）。");

        // 4. QueryInterface 到 Output5，调用 DuplicateOutput
        using var output5 = targetOutput.QueryInterface<Output5>();
        var outputDesc = targetOutput.Description;
        _outputWidth = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
        _outputHeight = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;
        // 桌面纹理原点对应该 output 的 DesktopBounds 左上角（副显示器可能为负坐标）
        _outputOffsetX = outputDesc.DesktopBounds.Left;
        _outputOffsetY = outputDesc.DesktopBounds.Top;
        _duplication = output5.DuplicateOutput(_device);

        // 5. 创建与桌面同尺寸的 Staging 纹理（CPU 可读，BGRA8）
        var stagingDesc = new Texture2DDescription
        {
            Width = _outputWidth,
            Height = _outputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };
        _stagingTexture = new Texture2D(_device, stagingDesc);

        // 预先获取窗口在桌面坐标系中的 rect（捕获循环内每帧更新以处理窗口移动）
        UpdateWindowRect();
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
                // 拷贝到 staging 纹理后 Map 读取
                _context.CopyResource(_stagingTexture, d3dResource);
                var dataBox = _context.MapSubresource((SharpDX.Direct3D11.Resource)_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    // 每帧更新窗口 rect（窗口可能被移动 / 调整大小）
                    UpdateWindowRect();

                    int winW = _windowRect.Right - _windowRect.Left;
                    int winH = _windowRect.Bottom - _windowRect.Top;
                    if (winW <= 0 || winH <= 0) return false;

                    // 裁剪窗口区域（换算到 output 纹理坐标，副显示器为相对偏移）
                    int srcX = _windowRect.Left - _outputOffsetX;
                    int srcY = _windowRect.Top - _outputOffsetY;

                    // 降采样到 ≤720p（复用 WindowCapture 的降采样逻辑，保证输出尺寸一致）
                    var raw = CropBgra(dataBox, _outputWidth, _outputHeight, srcX, srcY, winW, winH);
                    var data = WindowCapture.DownscaleIfNeeded(raw, winW, winH, out int ow, out int oh);

                    long tick = Environment.TickCount64;
                    _lastProduceTick = tick;
                    _latest = new FrameData(data, ow, oh, tick);
                    _lastFrameAt = DateTime.Now;
                    FrameUpdated?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    _context.UnmapSubresource(_stagingTexture, 0);
                }
            }
        }
        finally
        {
            try { _duplication.ReleaseFrame(); } catch { }
        }
        return true;
    }

    /// <summary>
    /// 从已 Map 的桌面纹理中裁剪出窗口区域的 BGRA 字节（w×h 紧密排列，越界部分留黑）。
    /// x/y 为相对 output 纹理原点的坐标（可能为负或超出 output，跨显示器时边缘为黑边）。
    /// </summary>
    private static byte[] CropBgra(DataBox dataBox, int deskW, int deskH, int x, int y, int w, int h)
    {
        int stride = dataBox.RowPitch;
        var dst = new byte[w * h * 4];
        nint srcPtr = dataBox.DataPointer;
        for (int row = 0; row < h; row++)
        {
            int srcRow = y + row;
            if (srcRow < 0 || srcRow >= deskH) continue;
            // 水平方向与 output 可见区域求交集
            int xStart = Math.Max(0, x);
            int xEnd = Math.Min(deskW, x + w);
            int copyBytes = (xEnd - xStart) * 4;
            if (copyBytes <= 0) continue;
            nint rowPtr = srcPtr + srcRow * stride + xStart * 4;
            Marshal.Copy(rowPtr, dst, row * w * 4 + (xStart - x) * 4, copyBytes);
        }
        return dst;
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
