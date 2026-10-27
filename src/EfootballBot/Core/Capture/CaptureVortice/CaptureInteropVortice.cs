using Vortice.DXGI;

namespace EfootballBot.Core.Capture;

/// <summary>
/// DXGI 适配器枚举（Vortice 实现）。硬件适配器挑选规则：显存最大者胜；无输出的卡让位有输出的卡；
/// WARP/软件适配器排除；按 Luid 去重。
/// </summary>
internal static class CaptureInteropVortice
{
    /// <summary>软件/WARP 适配器判定：AdapterDescription1 不含可用的 Flags 判定语义，
    /// 用专用显存（软件适配器为 0）+ 名称（Microsoft Basic Render Driver）识别。
    /// 注意 DedicatedVideoMemory 是 Vortice.PointerUSize（包装 nuint）：取 .Value 再比较。
    /// WGC 帧池建在 WARP 设备上会确定性触发 dwm.exe (d3d10warp.dll) 崩溃 → 系统卡死黑屏，
    /// 捕获链路绝不选用软件适配器。</summary>
    private static bool IsHardwareAdapter(AdapterDescription1 d) =>
        d.DedicatedVideoMemory.Value > 0 &&
        d.Description.Contains("Basic", StringComparison.OrdinalIgnoreCase) == false;

    /// <summary>枚举全部硬件适配器名（软件/WARP 适配器除外，按 Luid 去重）。双显卡环境检测用。
    /// 失败时返回已收集部分。</summary>
    public static List<string> GetHardwareAdapterNames()
    {
        var names = new List<string>();
        var seenLuids = new HashSet<long>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int i = 0;
            while (factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success)
            {
                i++; // 先自增再处理：continue 时 finally 释放 adapter 后回到循环条件
                try
                {
                    var d = adapter.Description1;
                    if (!IsHardwareAdapter(d)) continue;
                    if (!seenLuids.Add(ToLuidKey(d.Luid))) continue; // 同一适配器可能被枚举多次
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
    /// 规则：显存最大者优先；但若该卡没有任何显示输出（矿卡/无头卡典型），
    /// 而其他硬件卡有输出，则改选"有输出的硬件卡中显存最大者"（DWM 跨适配器交付帧会断流）。
    /// 名字含 Virtual 的虚拟显示适配器不参与"有输出"优先。
    /// </summary>
    public static IDXGIAdapter1? PickHardwareAdapter(out string name, out bool pickedForOutputs)
    {
        name = "";
        pickedForOutputs = false;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? bestAny = null, bestOut = null;
            long bestAnyMem = 0, bestOutMem = 0;
            var seenLuids = new HashSet<long>();
            int i = 0;
            while (factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success)
            {
                i++;
                try
                {
                    var d = adapter.Description1;
                    if (!IsHardwareAdapter(d)) continue;
                    if (!seenLuids.Add(ToLuidKey(d.Luid))) continue; // 同一适配器可能被枚举多次
                    long mem = (long)d.DedicatedVideoMemory.Value;
                    bool isVirtual = d.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
                    // 枚举输出数量（EnumOutputs 到 NotFound 为止），数完即释放
                    int outputCount = 0;
                    while (adapter.EnumOutputs((uint)outputCount, out IDXGIOutput output).Success)
                    {
                        output.Dispose();
                        outputCount++;
                    }
                    if (!isVirtual && outputCount > 0 &&
                        (bestOut is null || mem > bestOutMem))
                    {
                        if (!ReferenceEquals(bestAny, bestOut)) bestOut?.Dispose();
                        bestOut = adapter;
                        bestOutMem = mem;
                        name = d.Description;
                    }
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
                name = bestAny.Description1.Description;
                return bestAny;
            }
            return null;
        }
        catch { name = ""; return null; }
    }

    /// <summary>LUID（uint LowPart + int HighPart）拼成单个 long 作为去重键。</summary>
    private static long ToLuidKey(Vortice.Luid luid) =>
        ((long)luid.HighPart << 32) | luid.LowPart;
}
