using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EfootballBot.Core.UI;

/// <summary>
/// 主题切换管理：把预定义的 Token→颜色值写入 Application.Resources，所有 DynamicResource 自动刷新。
///
/// 【两个轴】亮度主题（暗色 / 亮色 / 深蓝）× 视觉世界（Fluent / Glass）：
///  - Fluent = Windows 11 云母/亚克力 + 系统强调蓝，v7.3 的原始观感（默认）；
///  - Glass  = Apple 黑玻璃：近纯黑毛玻璃 + 中性浅灰选中 + 玻璃渐变卡片 + 抖色噪点。
///  世界只影响「颜色 / 材质 / 投影 / 噪点 / 窗口不透明度」——版式、字体阶梯、响应式布局两个世界共用，
///  所以同一份 Styles 跑两套外观，不用维护两套 XAML。
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string DeepBlue = "DeepBlue";
    public static readonly string[] All = [Dark, Light, DeepBlue];

    /// <summary>视觉世界：Windows 11 Fluent（默认，v7.3 观感）。</summary>
    public const string WorldFluent = "Fluent";
    /// <summary>视觉世界：Apple Glass（近纯黑毛玻璃）。</summary>
    public const string WorldGlass = "Glass";
    public static readonly string[] Worlds = [WorldFluent, WorldGlass];

    private static bool _validated;

    public static string DisplayName(string key) => key switch
    {
        Light => "亮色",
        DeepBlue => "深蓝",
        _ => "暗色",
    };

    public static string WorldDisplayName(string? world) => IsGlassWorld(world)
        ? "Apple Glass（黑玻璃）"
        : "Windows 11（云母）";

    /// <summary>是否是 Glass 世界（未知/空值一律视为默认的 Fluent）。</summary>
    public static bool IsGlassWorld(string? world)
        => string.Equals(world?.Trim(), WorldGlass, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeWorld(string? world) => IsGlassWorld(world) ? WorldGlass : WorldFluent;

    /// <summary>把指定主题 + 视觉世界的所有 Token 颜色写入 Application.Current.Resources。</summary>
    public static void ApplyTheme(string? themeName, string? world = null)
    {
        bool glass = IsGlassWorld(world);
        var source = ThemePalette.For(themeName, world);

        // v7.3 分层是刻意的（材质透底 + 半透明栏 + 卡片层），不再把三栏/卡片压成同一个颜色。
        // 只把「同一层」的轨道类 token 对齐，避免滚动条轨道与栏底出现色差接缝。
        var colors = new Dictionary<string, string>(source);
        foreach (var key in new[] { "NavSectionBg", "TrackBg", "TabTrackBg" })
            colors[key] = colors["TrackBg"];

        // 6 张表（2 世界 × 3 亮度）Token 名必须齐全：漏一个就是「该主题下某块区域仍用别的颜色」，
        // 这种 bug 只在切换主题后肉眼可见，所以启动时先点名（结果进 data\app.log）。
        if (!_validated && ThemePalette.Validate() is { Length: > 0 } problems)
        {
            _validated = true;
            var msg = "主题 Token 不完整：" + string.Join("；", problems);
            global::System.Diagnostics.Debug.WriteLine(msg);
            try
            {
                // LogLevel 在 Core.Logging 命名空间下（不是 Core.Bot）
                global::EfootballBot.Core.Logging.FileLogger.Instance
                    .Write(msg, global::EfootballBot.Core.LogLevel.Warn);
            }
            catch { /* 日志未初始化：忽略 */ }
        }

        var app = Application.Current.Resources;
        foreach (var (key, hex) in colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            app[key] = new SolidColorBrush(color);
            // 同一份颜色再以 Color 形式发布一份：渐变停点（GradientStop.Color）只能用 Color，
            // 否则页面里的渐变只能写死十六进制 —— 那正是「换个主题就有一块不跟」的老问题。
            app[key + "Color"] = color;
        }

        // ---- 世界级复合资源（渐变色块 / 投影 / 噪点）：颜色 Token 写完后统一重建 ----
        app["GlassFill"] = BuildGradient(colors, "GlassFillTop", "GlassFillMid", "GlassFillBottom", 0.0, 0.16, 1.0);
        app["PrimaryBtnFill"] = BuildGradient(colors, "PrimaryBtnFrom", "PrimaryBtnMid", "PrimaryBtnTo", 0.0, 0.55, 1.0);
        app["CardShadowEffect"] = glass
            ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 2, Opacity = 0.14 }
            : new DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 0, Opacity = 0 };  // Fluent 卡片无投影
        app["DitherNoiseBrush"] = glass ? CreateNoiseBrush() : Brushes.Transparent;

        // WPF-UI（Fluent 2）控件随自定义主题同步：亮色→Light，暗色/深蓝→Dark
        try
        {
            ApplicationThemeManager.Apply(themeName == Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
        catch
        {
            // 主题服务尚未初始化时忽略：自定义 Token 仍然生效
        }
    }

    /// <summary>用三个 Token 颜色搭一条竖向渐变（同一颜色三停 = 平铺，用于 Fluent 世界）。</summary>
    private static LinearGradientBrush BuildGradient(
        IReadOnlyDictionary<string, string> colors, string from, string mid, string to,
        double at0, double at1, double at2)
    {
        Color Pick(string key) => colors.TryGetValue(key, out var hex)
            ? (Color)ColorConverter.ConvertFromString(hex)
            : Colors.Transparent;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Pick(from), at0));
        brush.GradientStops.Add(new GradientStop(Pick(mid), at1));
        brush.GradientStops.Add(new GradientStop(Pick(to), at2));
        brush.Freeze();
        return brush;
    }

    /// <summary>抖色噪点：打散 Acrylic 深色背景的色带（banding），只贴在窗口最底层、不参与命中测试。</summary>
    private static Brush CreateNoiseBrush()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri("pack://application:,,,/Assets/dither-noise.png", UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 128, 128),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;   // 资源缺失：噪点只是锦上添花，不该影响界面
        }
    }

    /// <summary>
    /// 窗口材质（Mica / Acrylic / Tabbed）。必须在 <see cref="ApplyTheme"/> 之后调用：
    /// 本方法会把 WindowBg 换成带透明度的版本，材质才从外壳透得出来；
    /// 不支持的系统（Win10 21H2 及更早）或配置为 None 时自动回退纯色，观感与旧版一致。
    ///
    /// 不透明度按世界区分：Fluent 0xF2（云母薄透）、Glass 0x8C（黑色毛玻璃，壁纸磨砂后明显透出）。
    /// </summary>
    /// <returns>是否真的启用了材质。</returns>
    public static bool ApplyWindowBackdrop(Window? window, string? preference, string? world = null)
    {
        if (window is null || Application.Current is null) return false;

        var want = (preference ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "none" => WindowBackdropType.None,
            "acrylic" => WindowBackdropType.Acrylic,
            "tabbed" => WindowBackdropType.Tabbed,
            "mica" => WindowBackdropType.Mica,
            _ => WindowBackdropType.Mica,   // Auto：先按 Mica 试，IsSupported 决定要不要回退
        };

        // Glass 世界必须是 Acrylic：Mica 取的是桌面壁纸的「纸感」，做不出透过去的黑玻璃
        if (IsGlassWorld(world) && want is WindowBackdropType.Mica or WindowBackdropType.Auto)
            want = WindowBackdropType.Acrylic;

        byte shellAlpha = IsGlassWorld(world) ? (byte)0x8C : (byte)0xF2;

        try
        {
            if (want != WindowBackdropType.None && !WindowBackdrop.IsSupported(want))
                want = WindowBackdropType.None;

            if (want == WindowBackdropType.None)
            {
                WindowBackdrop.RemoveBackdrop(window);
                if (Application.Current.Resources["WindowBg"] is Brush solid) window.Background = solid;
                return false;
            }

            WindowBackdrop.ApplyBackdrop(window, want);

            if (Application.Current.Resources["WindowBg"] is SolidColorBrush cur)
            {
                var glass = new SolidColorBrush(Color.FromArgb(shellAlpha, cur.Color.R, cur.Color.G, cur.Color.B));
                glass.Freeze();
                Application.Current.Resources["WindowBg"] = glass;
                window.Background = glass;
            }
            return true;
        }
        catch
        {
            return false;   // 材质失败不影响功能：保留纯色外壳
        }
    }

    /// <summary>获取某主题（可指定世界）下某个 Token 的颜色（用于主题选择预览卡片）。</summary>
    public static Brush GetBrush(string themeName, string token, string? world = null)
    {
        var colors = ThemePalette.For(themeName, world);
        return colors.TryGetValue(token, out var hex)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            : Brushes.Transparent;
    }
}
