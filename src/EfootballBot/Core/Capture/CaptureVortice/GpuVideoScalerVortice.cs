using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EfootballBot.Core.Capture;

/// <summary>
/// GPU 硬件缩放（Vortice 家族，默认实现）：WGC 全分辨率帧（如 3440×1440）在显卡内经
/// VideoProcessor 缩到高≤720（1720×720），再回读 CPU。与 GpuVideoScaler（SharpDX 家族）
/// 逐行为对齐的孪生实现：等比缩放（宽高同一系数），画面不变形；所有归一化 ROI/OCR
/// 识别逻辑不受影响。任何初始化/执行失败由调用方捕获并回退 CPU 路径，不影响挂机。
/// 两家族 COM 包装绝不混挂同一设备指针：本类只接收 Vortice 设备的 NativePointer。
/// </summary>
internal sealed class GpuVideoScalerVortice : IDisposable
{
    // IDirect3DDxgiInterfaceAccess（WGC 帧 Surface 暴露原生 DXGI 资源的内部接口）
    private const string AccessIid = "A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1";

    [ComImport]
    [Guid(AccessIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out IntPtr resource);
    }

    private readonly Action<string> _log;
    private readonly nint _devicePtr;
    // Vortice 包装各自持有独立 COM 引用；_device 包装的是自 AddRef 的独立引用（见构造），不影响属主
    private ID3D11Device? _device;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoContext? _videoContext;

    private ID3D11Texture2D? _mid;        // 全尺寸中转纹理（BIND_DECODER，VP 输入要求）
    private ID3D11VideoProcessorInputView? _inView;
    private ID3D11Texture2D? _out;        // 缩放后纹理（BIND_RENDER_TARGET，VP 输出）
    private ID3D11VideoProcessorOutputView? _outView;
    private ID3D11Texture2D? _staging;    // 小尺寸 CPU 回读纹理
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;

    private int _srcW, _srcH, _dstW, _dstH;

    public GpuVideoScalerVortice(IntPtr d3dDevicePtr, Action<string> log)
    {
        _log = log;
        _devicePtr = d3dDevicePtr;
        // Vortice 的 ComObject(nint) 构造不 AddRef（与 SharpDX 相反），Dispose 却会 Release：
        // 这里对属主（WindowCapture）借来的设备指针先自行 AddRef 一份独立引用，
        // 让包装的生命周期与属主解耦（Dispose 时 Release 恰好归还，绝不提前释放属主设备）
        Marshal.AddRef(_devicePtr);
        _device = new ID3D11Device(d3dDevicePtr);
        try
        {
            // 多线程保护（帧回调线程与 WGC 内部线程可能都触碰设备）
            using var mt = _device.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);
        }
        catch { /* 部分设备无此接口，忽略 */ }

        _context = _device.ImmediateContext;
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
    }

    /// <summary>
    /// 把 WGC 帧 Surface 等比缩放到高≤720，返回紧密排列 BGRA 字节。
    /// srcW/srcH 为帧内容原始尺寸；输出尺寸由 WindowCapture.CapSize 决定。
    /// </summary>
    public byte[] ScaleToBgra(IDirect3DSurface surface, int srcW, int srcH, out int outW, out int outH)
    {
        var cap = WindowCapture.CapSize(srcW, srcH);
        outW = cap.Width; outH = cap.Height;
        if (outW == srcW && outH == srcH)
            throw new InvalidOperationException("源尺寸未超上限，无需 GPU 缩放");

        EnsureResources(srcW, srcH, outW, outH);

        // 1. 从 WinRT 帧 Surface 取出原生 ID3D11Texture2D
        using var frameTex = AcquireFrameTexture(surface);

        // 2. 全尺寸 GPU→GPU 拷贝到 VP 输入纹理（显存内操作，不经 PCIe/CPU）
        _context!.CopyResource(_mid!, frameTex);

        // 3. 硬件缩放（整幅输入 → 整幅输出；默认双线性质量）。
        //    视图对象存于实例字段，Blt 调用期间必然存活。
        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = _inView,
        };
        _videoContext!.VideoProcessorBlt(_processor!, _outView!, 0, new[] { stream }).CheckError();

        // 4. 小尺寸结果拷到 STAGING 并回读（仅 ~5MB）
        _context.CopyResource(_staging!, _out!);
        _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None,
            out MappedSubresource box).CheckError();
        try
        {
            int rowBytes = outW * 4;
            var dst = new byte[rowBytes * outH];
            unsafe
            {
                byte* src = (byte*)box.DataPointer;
                for (int y = 0; y < outH; y++)
                    Marshal.Copy((IntPtr)(src + y * box.RowPitch), dst, y * rowBytes, rowBytes);
            }
            return dst;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    /// <summary>源/目标尺寸变化时整套重建 VP 资源。</summary>
    private void EnsureResources(int srcW, int srcH, int dstW, int dstH)
    {
        if (_processor is not null && srcW == _srcW && srcH == _srcH && dstW == _dstW && dstH == _dstH)
            return;
        ReleaseFrameResources();
        try
        {
            EnsureResourcesCore(srcW, srcH, dstW, dstH);
        }
        catch
        {
            ReleaseFrameResources(); // 半初始化资源不留到下次（部分 AMD 驱动 CreateVideoProcessorInputView 即抛）
            throw;
        }
    }

    private void EnsureResourcesCore(int srcW, int srcH, int dstW, int dstH)
    {
        var contentDesc = new VideoProcessorContentDescription
        {
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputWidth = (uint)dstW,
            OutputHeight = (uint)dstH,
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(0, 1),
            OutputFrameRate = new Rational(0, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoDevice!.CreateVideoProcessorEnumerator(ref contentDesc, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator!, 0, out _processor).CheckError();

        // VP 输入：全尺寸，BIND_DECODER（CreateVideoProcessorInputView 的硬性要求）
        _mid = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW,
            Height = (uint)srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.Decoder,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _videoDevice.CreateVideoProcessorInputView(_mid, _enumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            }, out _inView).CheckError();

        // VP 输出：小尺寸，BIND_RENDER_TARGET
        _out = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _videoDevice.CreateVideoProcessorOutputView(_out, _enumerator!,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            }, out _outView).CheckError();

        // 回读：小尺寸 STAGING
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _srcW = srcW; _srcH = srcH; _dstW = dstW; _dstH = dstH;
        _log($"[捕获] GPU 硬件缩放就绪：{srcW}×{srcH} → {dstW}×{dstH}（VideoProcessor，Vortice）");
    }

    /// <summary>从 WGC 帧的 WinRT IDirect3DSurface 提取原生 D3D11 纹理（用完释放引用）。</summary>
    private ID3D11Texture2D AcquireFrameTexture(IDirect3DSurface surface)
    {
        nint punk = Marshal.GetIUnknownForObject(surface);
        try
        {
            // 转换为 RCW 时 CLR 自动 QueryInterface(IDirect3DDxgiInterfaceAccess)
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(punk);
            Guid texGuid = typeof(ID3D11Texture2D).GUID;
            int hr = access.GetInterface(in texGuid, out IntPtr pTex);
            if (hr != 0) throw new InvalidOperationException($"GetInterface(ID3D11Texture2D) 失败: 0x{hr:X}");
            // GetInterface 已 AddRef；Vortice 包装构造不 AddRef、直接接管这份引用，
            // using 结束 Dispose 时 Release 恰好归零（SharpDX 版需再 Release 抵消其构造的 AddRef，此处不要）
            return new ID3D11Texture2D(pTex);
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    private void ReleaseFrameResources()
    {
        _inView?.Dispose(); _inView = null;
        _outView?.Dispose(); _outView = null;
        _processor?.Dispose(); _processor = null;
        _enumerator?.Dispose(); _enumerator = null;
        _mid?.Dispose(); _mid = null;
        _out?.Dispose(); _out = null;
        _staging?.Dispose(); _staging = null;
    }

    public void Dispose()
    {
        ReleaseFrameResources();
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
        // ImmediateContext 持独立 COM 引用（GetImmediateContext 出参），先于设备释放
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null; // Release 构造时自行 AddRef 的那份引用
        GC.SuppressFinalize(this);
    }
}
