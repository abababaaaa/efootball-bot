using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EfootballBot.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture 相关的 COM interop。
/// 适配器枚举/设备创建一律走 SharpDX（工程已引用，DXGI 捕获同款路径、实机已验证）——
/// 手写 CreateDXGIFactory1 P/Invoke 在实机探测中恒返 E_NOINTERFACE（2026-10-11），
/// 曾让"绑定真实 GPU"的修复静默落空，勿再手写 DXGI/D3D11 原生调用。
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

    // ---- DXGI 适配器枚举（SharpDX）----

    /// <summary>软件/WARP 适配器判定：SharpDX AdapterDescription 不含 Flags，
    /// 用专用显存（软件适配器为 0）+ 名称（Microsoft Basic Render Driver）识别。
    /// 注意 DedicatedVideoMemory 是 SharpDX.PointerSize：与 int 字面量比较会走溢出的
    /// 隐式转换（16GB 显存直接 OverflowException），必须与 long 比较。
    /// WGC 帧池建在 WARP 设备上会确定性触发 dwm.exe (d3d10warp.dll) 崩溃 → 系统卡死黑屏，
    /// 捕获链路绝不选用软件适配器。</summary>
    private static bool IsHardwareAdapter(SharpDX.DXGI.AdapterDescription d) =>
        d.DedicatedVideoMemory > 0L &&
        !d.Description.Contains("Basic", StringComparison.OrdinalIgnoreCase);

    /// <summary>最近一次适配器枚举失败的诊断信息（正常为空）。</summary>
    public static string? LastEnumError;

    /// <summary>枚举全部硬件适配器名（软件/WARP 适配器除外，按 Luid 去重——同一适配器
    /// 可能被枚举多次，实机 2026-10-11：单卡 AMD 7800 XT 被列出 3 次误报"双显卡"）。双显卡环境检测用。</summary>
    public static List<string> GetHardwareAdapterNames()
    {
        var names = new List<string>();
        var seenLuids = new HashSet<long>();
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            foreach (var adapter in factory.Adapters)
            {
                try
                {
                    var d = adapter.Description;
                    if (!IsHardwareAdapter(d)) continue;
                    if (!seenLuids.Add(d.Luid)) continue;
                    names.Add(d.Description);
                }
                finally { adapter.Dispose(); }
            }
        }
        catch { /* 枚举失败返回已收集部分 */ }
        return names;
    }

    /// <summary>
    /// 选出最适合捕获的真实硬件适配器。返回的适配器由调用方 Dispose；
    /// null = 无可用硬件适配器（调用方走默认硬件适配器路径）。
    /// 规则：显存最大者优先；但若该卡没有任何显示输出（矿卡/无头卡典型，如 CMP 30HX），
    /// 而其他硬件卡有输出，则改选"有输出的硬件卡中显存最大者"——DWM 跑在有输出的卡上，
    /// WGC 设备绑到无输出卡要跨适配器交付帧，老驱动下窗口一移出屏幕就断帧
    /// （2026-10-13 矿卡工作站用户实测：绑定 CMP 30HX 后停靠即黑，Intel P4600 是唯一有输出的卡）。
    /// 名字含 Virtual 的虚拟显示适配器不参与"有输出"优先（其输出是虚拟显示器，非 DWM 属主）。
    /// </summary>
    public static SharpDX.DXGI.Adapter? PickHardwareAdapter(out string name, out bool pickedForOutputs)
    {
        name = "";
        pickedForOutputs = false;
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            SharpDX.DXGI.Adapter? bestAny = null, bestOut = null;
            long bestAnyMem = 0, bestOutMem = 0;
            var seenLuids = new HashSet<long>();
            foreach (var adapter in factory.Adapters)
            {
                try
                {
                    var d = adapter.Description;
                    if (!IsHardwareAdapter(d)) continue;
                    if (!seenLuids.Add(d.Luid)) continue; // 同一适配器可能被枚举多次（实机 2026-10-11 AMD 单卡列 3 次）
                    long mem = (long)d.DedicatedVideoMemory;
                    bool isVirtual = d.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
                    var outs = adapter.Outputs;
                    try
                    {
                        if (!isVirtual && outs.Length > 0 &&
                            (bestOut is null || mem > bestOutMem))
                        {
                            if (!ReferenceEquals(bestAny, bestOut)) bestOut?.Dispose();
                            bestOut = adapter;
                            bestOutMem = mem;
                            name = d.Description;
                        }
                    }
                    finally { foreach (var o in outs) o.Dispose(); }
                    if (bestAny is null || mem > bestAnyMem)
                    {
                        if (!ReferenceEquals(bestAny, bestOut)) bestAny?.Dispose();
                        bestAny = adapter;
                        bestAnyMem = mem;
                    }
                }
                finally
                {
                    if (!ReferenceEquals(bestAny, adapter) && !ReferenceEquals(bestOut, adapter))
                        adapter.Dispose();
                }
            }
            if (bestOut is not null && !ReferenceEquals(bestOut, bestAny))
            {
                // 显存最大的卡没有输出：弃用它，改绑有输出的卡
                bestAny?.Dispose();
                pickedForOutputs = true;
                return bestOut;
            }
            if (bestAny is not null)
            {
                name = bestAny.Description.Description;
                return bestAny;
            }
            return null;
        }
        catch { name = ""; return null; }
    }

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
