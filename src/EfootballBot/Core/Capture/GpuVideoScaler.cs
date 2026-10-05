using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Windows.Graphics.DirectX.Direct3D11;

namespace EfootballBot.Core.Capture;

/// <summary>
/// GPU 硬件缩放：WGC 全分辨率帧（如 3440×1440）在显卡内经 VideoProcessor 缩到高≤720（1720×720），
/// 再回读 CPU。相比「回读全图 19.8MB + CPU 最近邻」，PCIe 传输量降约 80%，并消除大对象 GC 压力。
/// 等比缩放（宽高同一系数），画面不变形；所有归一化 ROI/OCR 识别逻辑不受影响。
/// 任何初始化/执行失败由调用方捕获并回退 CPU 路径，不影响挂机。
/// </summary>
internal sealed class GpuVideoScaler : IDisposable
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
    // 包装现有原生设备指针（构造时 AddRef，Dispose 时 Release，不影响 WindowCapture 持有的设备）
    private SharpDX.Direct3D11.Device? _device;
    private VideoDevice? _videoDevice;
    private DeviceContext? _context;
    private VideoContext? _videoContext;
    private VideoProcessorEnumerator? _enumerator;
    private VideoProcessor? _processor;

    private Texture2D? _mid;        // 全尺寸中转纹理（BIND_DECODER，VP 输入要求）
    private VideoProcessorInputView? _inView;
    private Texture2D? _out;        // 缩放后纹理（BIND_RENDER_TARGET，VP 输出）
    private VideoProcessorOutputView? _outView;
    private Texture2D? _staging;    // 小尺寸 CPU 回读纹理

    private int _srcW, _srcH, _dstW, _dstH;

    public GpuVideoScaler(IntPtr d3dDevicePtr, Action<string> log)
    {
        _log = log;
        _device = new SharpDX.Direct3D11.Device(d3dDevicePtr);
        try
        {
            // 多线程保护（帧回调线程与 WGC 内部线程可能都触碰设备）
            using var mt = _device.QueryInterface<Multithread>();
            mt.SetMultithreadProtected(true);
        }
        catch { /* 部分设备无此接口，忽略 */ }

        _context = _device.ImmediateContext;
        _videoDevice = _device.QueryInterface<VideoDevice>();
        _videoContext = _context.QueryInterface<VideoContext>();
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
        _context!.CopyResource(_mid, frameTex);

        // 3. 硬件缩放（整幅输入 → 整幅输出；默认双线性质量）
        var stream = new VideoProcessorStream
        {
            Enable = new RawBool(true),
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            PInputSurface = _inView,
        };
        _videoContext!.VideoProcessorBlt(_processor, _outView, 0, 1, new[] { stream });

        // 4. 小尺寸结果拷到 STAGING 并回读（仅 ~5MB）
        _context.CopyResource(_staging, _out);
        var box = _context.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
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
            _context.UnmapSubresource(_staging, 0);
        }
    }

    /// <summary>源/目标尺寸变化时整套重建 VP 资源。</summary>
    private void EnsureResources(int srcW, int srcH, int dstW, int dstH)
    {
        if (_processor is not null && srcW == _srcW && srcH == _srcH && dstW == _dstW && dstH == _dstH)
            return;
        ReleaseFrameResources();

        var contentDesc = new VideoProcessorContentDescription
        {
            InputWidth = srcW,
            InputHeight = srcH,
            OutputWidth = dstW,
            OutputHeight = dstH,
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(0, 1),
            OutputFrameRate = new Rational(0, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoDevice!.CreateVideoProcessorEnumerator(ref contentDesc, out _enumerator);
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);

        // VP 输入：全尺寸，BIND_DECODER（CreateVideoProcessorInputView 的硬性要求）
        _mid = new Texture2D(_device, new Texture2DDescription
        {
            Width = srcW,
            Height = srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.Decoder,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        _videoDevice.CreateVideoProcessorInputView(_mid, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                Dimension = VpivDimension.Texture2D,
                Texture2D = new Texture2DVpiv { MipSlice = 0, ArraySlice = 0 },
            }, out _inView);

        // VP 输出：小尺寸，BIND_RENDER_TARGET
        _out = new Texture2D(_device, new Texture2DDescription
        {
            Width = dstW,
            Height = dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        _videoDevice.CreateVideoProcessorOutputView(_out, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                Dimension = VpovDimension.Texture2D,
                Texture2D = new Texture2DVpov { MipSlice = 0 },
            }, out _outView);

        // 回读：小尺寸 STAGING
        _staging = new Texture2D(_device, new Texture2DDescription
        {
            Width = dstW,
            Height = dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });

        _srcW = srcW; _srcH = srcH; _dstW = dstW; _dstH = dstH;
        _log($"[捕获] GPU 硬件缩放就绪：{srcW}×{srcH} → {dstW}×{dstH}（VideoProcessor）");
    }

    /// <summary>从 WGC 帧的 WinRT IDirect3DSurface 提取原生 D3D11 纹理（用完释放引用）。</summary>
    private Texture2D AcquireFrameTexture(IDirect3DSurface surface)
    {
        IntPtr punk = Marshal.GetIUnknownForObject(surface);
        try
        {
            // 转换为 RCW 时 CLR 自动 QueryInterface(IDirect3DDxgiInterfaceAccess)
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(punk);
            Guid texGuid = typeof(Texture2D).GUID;
            int hr = access.GetInterface(in texGuid, out IntPtr pTex);
            if (hr != 0) throw new InvalidOperationException($"GetInterface(ID3D11Texture2D) 失败: 0x{hr:X}");
            // GetInterface 已 AddRef；new Texture2D 会再 AddRef，立即释放一次抵消，
            // 使 SharpDX 对象持有唯一引用，using 退出时正确归零。
            var tex = new Texture2D(pTex);
            Marshal.Release(pTex);
            return tex;
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
        // ImmediateContext 由 Device 内部缓存管理，不单独释放
        _device?.Dispose(); _device = null;
        GC.SuppressFinalize(this);
    }
}
