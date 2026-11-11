using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EfootballBot.Core.Capture;

/// <summary>
/// DWM 实时缩略图预览（Windows 任务栏悬停预览 / Aero Peek 用的是同一个 API 家族）。
///
/// 原理：DwmRegisterThumbnail 让桌面窗口管理器（DWM）在它自己的合成阶段，把「源窗口的
/// 合成表面」直接搬到「目标窗口客户区里的指定矩形」——GPU 合成器内部的指针搬运，
/// 全程零拷贝，不过 CPU、不抓帧、不进 .NET 堆，更不经过命名管道。
/// 因此它的帧率 = 显示器刷新率，CPU 占用 ≈ 0，这也是任务栏预览看起来「比自己截的图流畅得多」
/// 的唯一原因：它压根没截图。
///
/// 与现有 Worker 预览链路的区别：
///   Worker 链路：抓帧 → 降采样 → PNG 编码 → base64 → JSON → 命名管道 → GUI 解码 → 贴图（每帧一次 CPU 往返）
///   DWM 链路  ：注册一次 → 摆好矩形 → DWM 每帧自己合（此后 CPU 开销为零）
///
/// 已知限制（都是 DWM 属性，不是实现缺陷）：
///   1) 只能「看」，读不到像素——要拿像素喂 YOLO 仍必须走 WGC/GDI 捕获（那条链路不变）。
///   2) 缩略图矩形是硬边矩形，DWM 不做圆角裁剪；需要圆角观感就内缩几个像素留背景色。
///   3) 目标窗口不能是分层窗口（WPF 里 AllowsTransparency=true 才是分层窗口，本程序不是）。
///   4) 源窗口最小化时 DWM 冻结最后一帧（配合 fFreezeOnMinimize，本实现保持默认冻结）。
///   5) 源窗口被完全移出所有显示器范围时，DWM 可能不再为它维持合成表面 → 缩略图空白。
///      游戏挂机时的「后台停靠」正好是这种情况，因此上层必须做可用性自检 + 回退（见 MainWindow）。
/// </summary>
public sealed class DwmLivePreview : IDisposable
{
    private nint _thumb;
    private nint _destHwnd;
    private nint _srcHwnd;

    /// <summary>缩略图已注册（可以调用 <see cref="UpdatePlacement"/>）。</summary>
    public bool IsAttached => _thumb != nint.Zero;

    /// <summary>当前绑定的源窗口句柄。</summary>
    public nint SourceHandle => _srcHwnd;

    /// <summary>DWM 合成是否可用（Win8+ 恒为 true；Win7 经典主题下可能为 false）。</summary>
    public static bool IsCompositionEnabled()
    {
        try { return DwmNative.DwmIsCompositionEnabled(out int on) == 0 && on != 0; }
        catch { return false; }
    }

    /// <summary>注册缩略图。失败返回 false 并给出原因（上层据此回退到抓帧预览）。</summary>
    public static bool TryAttach(nint destHwnd, nint srcHwnd, out DwmLivePreview? preview, out string error)
    {
        preview = null;
        error = string.Empty;
        if (destHwnd == nint.Zero) { error = "目标窗口句柄无效"; return false; }
        if (srcHwnd == nint.Zero) { error = "源窗口句柄无效"; return false; }
        if (!IsCompositionEnabled()) { error = "DWM 合成未启用"; return false; }

        int hr;
        nint thumb;
        try { hr = DwmNative.DwmRegisterThumbnail(destHwnd, srcHwnd, out thumb); }
        catch (Exception ex) { error = "DwmRegisterThumbnail 调用异常：" + ex.Message; return false; }

        if (hr != 0 || thumb == nint.Zero)
        {
            error = $"DwmRegisterThumbnail 失败（hr=0x{hr:X8}）";
            return false;
        }

        preview = new DwmLivePreview { _thumb = thumb, _destHwnd = destHwnd, _srcHwnd = srcHwnd };
        return true;
    }

    /// <summary>源窗口客户区尺寸（DWM 视角）。任一分量为 0 说明 DWM 当前没有为它维持表面。</summary>
    public bool TryGetSourceSize(out int width, out int height)
    {
        width = height = 0;
        if (_thumb == nint.Zero) return false;
        try
        {
            if (DwmNative.DwmQueryThumbnailSourceSize(_thumb, out var size) != 0) return false;
            width = size.cx;
            height = size.cy;
            return width > 0 && height > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把缩略图摆到目标窗口客户区的指定像素矩形里，内部按源宽高比做 letterbox 居中
    /// （DWM 本身会拉伸填满，不保比例，所以比例得自己算）。
    /// </summary>
    public void UpdatePlacement(Int32Rect destPx)
    {
        if (_thumb == nint.Zero) return;
        if (destPx.Width <= 0 || destPx.Height <= 0) return;

        var rect = destPx;
        if (TryGetSourceSize(out int sw, out int sh))
        {
            double scale = Math.Min((double)destPx.Width / sw, (double)destPx.Height / sh);
            int w = Math.Max(1, (int)Math.Round(sw * scale));
            int h = Math.Max(1, (int)Math.Round(sh * scale));
            rect = new Int32Rect(
                destPx.X + (destPx.Width - w) / 2,
                destPx.Y + (destPx.Height - h) / 2,
                w, h);
        }

        var props = new DwmNative.DwmThumbnailProperties
        {
            Flags = DwmNative.DWM_TNP_RECTDESTINATION | DwmNative.DWM_TNP_VISIBLE
                  | DwmNative.DWM_TNP_OPACITY | DwmNative.DWM_TNP_SOURCECLIENTAREAONLY,
            Destination = new DwmNative.RECT
            {
                Left = rect.X,
                Top = rect.Y,
                Right = rect.X + rect.Width,
                Bottom = rect.Y + rect.Height
            },
            Opacity = 255,
            Visible = 1,
            SourceClientAreaOnly = 1
        };

        try { DwmNative.DwmUpdateThumbnailProperties(_thumb, ref props); }
        catch { /* 源窗口已销毁：下一次健康检查会走回退 */ }
    }

    /// <summary>临时隐藏/恢复缩略图（不注销，恢复时不需要重新摆矩形）。</summary>
    public void SetVisible(bool visible)
    {
        if (_thumb == nint.Zero) return;
        var props = new DwmNative.DwmThumbnailProperties
        {
            Flags = DwmNative.DWM_TNP_VISIBLE,
            Visible = visible ? 1 : 0
        };
        try { DwmNative.DwmUpdateThumbnailProperties(_thumb, ref props); }
        catch { }
    }

    public void Dispose()
    {
        if (_thumb == nint.Zero) return;
        try { DwmNative.DwmUnregisterThumbnail(_thumb); } catch { }
        _thumb = nint.Zero;
        _destHwnd = nint.Zero;
        _srcHwnd = nint.Zero;
    }

    /// <summary>
    /// 把 WPF 元素当前占据的区域换算成「目标窗口客户区」的像素矩形。
    /// 进程是 PerMonitorV2 DPI aware（app.manifest 已声明），PointToScreen 直接给设备像素，
    /// 减去客户区原点的屏幕坐标即得 DWM 需要的客户区相对坐标。
    /// </summary>
    public static bool TryGetElementRectPx(FrameworkElement element, Window owner, out Int32Rect rectPx)
    {
        rectPx = default;
        if (element is null || owner is null) return false;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;

        nint dest = new WindowInteropHelper(owner).Handle;
        if (dest == nint.Zero) return false;
        if (!DwmNative.GetClientRect(dest, out var client)) return false;

        var origin = new DwmNative.POINT();
        if (!DwmNative.ClientToScreen(dest, ref origin)) return false;

        Point tl, br;
        try
        {
            tl = element.PointToScreen(new Point(0, 0));
            br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        }
        catch { return false; }

        int l = (int)Math.Round(tl.X) - origin.X;
        int t = (int)Math.Round(tl.Y) - origin.Y;
        int r = (int)Math.Round(br.X) - origin.X;
        int b = (int)Math.Round(br.Y) - origin.Y;

        // 裁到客户区内：窗口被拖到屏幕外/被标题栏推挤时 DWM 会拒绝越界矩形
        l = Math.Max(0, Math.Min(l, client.Right));
        t = Math.Max(0, Math.Min(t, client.Bottom));
        r = Math.Max(0, Math.Min(r, client.Right));
        b = Math.Max(0, Math.Min(b, client.Bottom));

        if (r - l <= 0 || b - t <= 0) return false;
        rectPx = new Int32Rect(l, t, r - l, b - t);
        return true;
    }
}

internal static class DwmNative
{
    public const int DWM_TNP_RECTDESTINATION = 0x00000001;
    public const int DWM_TNP_RECTSOURCE = 0x00000002;
    public const int DWM_TNP_OPACITY = 0x00000004;
    public const int DWM_TNP_VISIBLE = 0x00000008;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public int Flags;
        public RECT Destination;
        public RECT Source;
        public byte Opacity;
        public int Visible;              // BOOL
        public int SourceClientAreaOnly; // BOOL
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmIsCompositionEnabled(out int enabled);

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(nint thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(nint thumb, ref DwmThumbnailProperties props);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(nint thumb, out SIZE size);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(nint hwnd, ref POINT point);
}
