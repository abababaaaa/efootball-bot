using Vortice.DXGI;

namespace EfootballBot.Core.System;

/// <summary>
/// HDR（高级色）显示器状态探测（等效「设置→系统→屏幕→HDR」开关）：
/// HDR 开启时 WGC 捕获的 8-bit 帧会整体过曝（SDR 白色被按显示器亮度线性拉伸后裁切到 255），
/// 本程序大量依赖绝对色探针与 OCR 对比度，画面过曝即全线漂移，必须预警。
/// 检测走 DXGI IDXGIOutput6.ColorSpace：HDR 激活的输出会切到 PQ(G2084) 色彩空间，SDR 恒为 G22(sRGB)；
/// 纯枚举查询、不创建捕获对象，与捕获后端选择互不影响。
/// 查询失败（老驱动无 Output6 / 枚举异常）一律按"无法判定"返回，调用方静默跳过——宁漏报不误报。
/// </summary>
public static class HdrStatus
{
    /// <summary>HDR 激活时输出的色彩空间（PQ 传输曲线），= DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020。</summary>
    private const ColorSpaceType HdrColorSpace = ColorSpaceType.RgbFullG2084NoneP2020;

    /// <summary>
    /// 枚举全部适配器的全部输出，返回已开启 HDR 的输出名（如 \\.\DISPLAY2）。
    /// 返回 null = 无法判定（枚举/查询失败），与空列表（全部 SDR，未开 HDR）区分。
    /// </summary>
    public static List<string>? QueryHdrOutputs()
    {
        try
        {
            var hdr = new List<string>();
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            using (adapter)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out IDXGIOutput output).Success; j++)
                using (output)
                {
                    // RDP 虚拟输出/老驱动可能没有 Output6：QueryInterface 失败即跳过该输出
                    using var o6 = output.QueryInterface<IDXGIOutput6>();
                    if (o6.Description1.ColorSpace == HdrColorSpace)
                        hdr.Add(o6.Description1.DeviceName);
                }
            }
            return hdr;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>便捷判定：任一显示器 HDR 已开。无法判定（null）按未开处理。</summary>
    public static bool AnyHdrEnabled() => QueryHdrOutputs() is { Count: > 0 };

    /// <summary>开始挂机前的警告文案；无 HDR / 无法判定时返回 null（不打扰用户）。</summary>
    public static string? BuildStartWarning()
    {
        var hdr = QueryHdrOutputs();
        if (hdr is not { Count: > 0 }) return null;
        return "★检测到显示器已开启 HDR：" + string.Join("、", hdr) + "。" +
               "HDR 会让挂机捕获的画面整体过曝，色块/亮度判定和 OCR 都可能失灵。" +
               "建议：Windows 设置→系统→屏幕→关闭 HDR 后再挂机（挂机期间不需要 HDR）。";
    }
}
