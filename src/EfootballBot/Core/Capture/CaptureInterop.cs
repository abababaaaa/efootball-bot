using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EfootballBot.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture 相关的原生 COM interop（零第三方库）。
/// </summary>
internal static class CaptureInterop
{
    /// <summary>IGraphicsCaptureItemInterop 的 IID（用于 RoGetActivationFactory）。</summary>
    public static readonly Guid GraphicsCaptureItemGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    /// <summary>IGraphicsCaptureItem 的 IID（CreateForWindow 出参类型）。</summary>
    public static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length, out nint hstring);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    /// <summary>获取 GraphicsCaptureItem 的 IGraphicsCaptureItemInterop 原生指针。</summary>
    public static nint GetCaptureItemInteropPtr()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out var hstr);
        if (hr != 0) throw new InvalidOperationException($"WindowsCreateString 失败: 0x{hr:X}");
        try
        {
            Guid iid = GraphicsCaptureItemGuid;
            hr = RoGetActivationFactory(hstr, ref iid, out nint factory);
            if (hr != 0) throw new InvalidOperationException($"RoGetActivationFactory 失败: 0x{hr:X}（系统版本过低或不支持窗口捕获）");
            return factory; // 调用方负责 Release
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }

    /// <summary>
    /// 通过 vtable 直接调用 IGraphicsCaptureItemInterop::CreateForWindow（vtable 第 4 槽，索引 3）。
    /// 完全绕开 RCW / CsWinRT 封送，避免 "Specified cast is not valid"。
    /// </summary>
    public static unsafe nint CreateItemForWindow(nint interop, nint hwnd)
    {
        Guid iid = GraphicsCaptureItemIid;
        nint item;
        var vtbl = *(void***)interop;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[3];
        int hr = fn(interop, hwnd, &iid, &item);
        if (hr != 0) throw new InvalidOperationException($"CreateForWindow 失败: 0x{hr:X}");
        return item;
    }

    // ---- D3D11 ----
    public const uint D3D11_SDK_VERSION = 7;
    public const int D3D_DRIVER_TYPE_HARDWARE = 0;
    public const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint context);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall, EntryPoint = "D3D11CreateDevice")]
    public static extern int D3D11CreateDeviceWarp(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        nint featureLevel,
        out nint context);

    public static readonly Guid IDXGIDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    // ---- DXGI 适配器枚举：捕获必须绑定真实 GPU（虚拟 WARP 适配器上 WGC 帧池会崩 DWM）----
    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);

    private static readonly Guid IDXGIFactory1Guid = new("770AAE78-F26F-4DBA-A829-E25E7A275DCF");

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DXGI_ADAPTER_DESC
    {
        public fixed ushort Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    /// <summary>
    /// 枚举 DXGI 适配器，选出最适合捕获的真实硬件适配器：跳过 WARP 软件设备
    /// （GameViewer / MuMu 等虚拟显示适配器是 WARP 设备，在其上创建 WGC 帧池
    /// 会在 dwm.exe 的 d3d10warp.dll 里确定性崩溃 → 系统卡死黑屏），取显存最大者。
    /// 返回 0 表示没有可用硬件适配器（调用方走 D3D_DRIVER_TYPE_HARDWARE 默认路径）。
    /// 返回的适配器指针由调用方 Marshal.Release。
    /// </summary>
    public static unsafe (nint Adapter, string Name) PickHardwareAdapter()
    {
        Guid iid = IDXGIFactory1Guid;
        int hr = CreateDXGIFactory1(ref iid, out nint factory);
        if (hr != 0) return (0, "");
        try
        {
            var vtbl = *(void***)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtbl[7];
            var getDesc = (delegate* unmanaged[Stdcall]<nint, DXGI_ADAPTER_DESC*, int>)vtbl[8];

            const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
            nint best = 0;
            nuint bestMem = 0;
            string bestName = "";
            for (uint i = 0; ; i++)
            {
                nint adapter;
                if (enumAdapters(factory, i, &adapter) != 0) break; // DXGI_ERROR_NOT_FOUND
                DXGI_ADAPTER_DESC desc = default;
                if (getDesc(adapter, &desc) == 0)
                {
                    string name = "";
                    unsafe
                    {
                        var span = new ReadOnlySpan<char>(desc.Description, 128);
                        int len = span.IndexOf('\0');
                        name = new string(len < 0 ? span : span[..len]);
                    }
                    bool software = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                    if (!software && desc.DedicatedVideoMemory > bestMem)
                    {
                        if (best != 0) Marshal.Release(best);
                        best = adapter;
                        bestMem = desc.DedicatedVideoMemory;
                        bestName = name;
                        continue;
                    }
                }
                Marshal.Release(adapter);
            }
            return (best, bestName);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>从原生 D3D11 设备创建 WinRT IDirect3DDevice。</summary>
    public static IDirect3DDevice CreateDirect3DDevice(nint d3dDevice)
    {
        Guid g = IDXGIDeviceGuid;
        int hr = Marshal.QueryInterface(d3dDevice, in g, out var dxgiDevice);
        if (hr != 0) throw new InvalidOperationException($"QueryInterface(IDXGIDevice) 失败: 0x{hr:X}");
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
            if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败: 0x{hr:X}");
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }
}
