using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EfootballBot.Core.UI;

/// <summary>主题切换管理：把预定义的 Token→颜色值写入 Application.Resources，所有 DynamicResource 自动刷新。</summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string DeepBlue = "DeepBlue";
    public static readonly string[] All = { Dark, Light, DeepBlue };

    private static bool _validated;

    public static string DisplayName(string key) => key switch
    {
        Light => "亮色",
        DeepBlue => "深蓝",
        _ => "暗色",
    };

    /// <summary>把指定主题的所有 Token 颜色写入 Application.Current.Resources。</summary>
    public static void ApplyTheme(string themeName)
    {
        var source = ThemePalette.For(themeName);

        // v7.3 Win11 世界：分层是刻意的（云母透底 + 半透明栏 + 卡片层），
        // 不再把三栏/卡片压成同一个颜色——那样材质就透不出来了。
        // 只把「同一层」的轨道类 token 对齐，避免滚动条轨道与栏底出现色差接缝。
        var colors = new Dictionary<string, string>(source);
        foreach (var key in new[] { "NavSectionBg", "TrackBg", "TabTrackBg" })
            colors[key] = colors["TrackBg"];

        // 三套主题 Token 名必须齐全：漏一个就是「该主题下某块区域仍用暗色值」，
        // 这种 bug 只在切换主题后肉眼可见，所以启动时先点名（结果进 data\app.log）。
        if (!_validated && ThemePalette.Validate() is { Length: > 0 } problems)
        {
            _validated = true;
            var msg = "主题 Token 不完整：" + string.Join("；", problems);
            global::System.Diagnostics.Debug.WriteLine(msg);
            try
            {
                // LogLevel 在 Core.Bot 命名空间下（不是 Core.Logging）
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

    /// <summary>
    /// 窗口材质（Mica / Acrylic / Tabbed）。必须在 <see cref="ApplyTheme"/> 之后调用：
    /// 本方法会把 WindowBg 换成同色 90% 不透明的版本，材质才从外壳透得出来；
    /// 不支持的系统（Win10 21H2 及更早）或配置为 None 时自动回退纯色，观感与旧版一致。
    /// </summary>
    /// <returns>是否真的启用了材质。</returns>
    public static bool ApplyWindowBackdrop(Window? window, string? preference)
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

            // 外壳底色留 60% 不透明度（v7.3）：云母/亚克力的磨砂透出来才是「毛玻璃」；
            // 不支持材质的机器会退回纯色外壳（见上面的 None 分支），观感仍然完整。
            if (Application.Current.Resources["WindowBg"] is SolidColorBrush cur)
            {
                var glass = new SolidColorBrush(Color.FromArgb(0xF2, cur.Color.R, cur.Color.G, cur.Color.B));
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

    /// <summary>获取某主题下某个 Token 的颜色（用于主题选择预览卡片）。</summary>
    public static Brush GetBrush(string themeName, string token)
    {
        var colors = ThemePalette.For(themeName);
        return colors.TryGetValue(token, out var hex)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            : Brushes.Transparent;
    }

}
