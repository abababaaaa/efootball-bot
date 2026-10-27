using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EfootballBot.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture 相关的 COM interop（家族中立：仅 WinRT 投影，不持有 D3D/DXGI 对象）。
/// DXGI 适配器枚举/设备创建走 CaptureInteropVortice（Vortice 家族），勿在此手写 DXGI 原生调用
/// （手写 CreateDXGIFactory1 P/Invoke 实机探测恒返 E_NOINTERFACE，2026-10-11）。
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

    public static readonly Guid IDXGIDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    /// <summary>从原生 D3D11 设备创建 WinRT IDirect3DDevice（WGC 会话用）。</summary>
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
