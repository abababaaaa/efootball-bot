using System.Windows;
using System.Windows.Media;

namespace EfootballBot.Core.UI;

/// <summary>主题切换管理：把预定义的 Token→颜色值写入 Application.Resources，所有 DynamicResource 自动刷新。</summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string DeepBlue = "DeepBlue";
    public static readonly string[] All = { Dark, Light, DeepBlue };

    public static string DisplayName(string key) => key switch
    {
        Light => "亮色",
        DeepBlue => "深蓝",
        _ => "暗色",
    };

    /// <summary>把指定主题的所有 Token 颜色写入 Application.Current.Resources。</summary>
    public static void ApplyTheme(string themeName)
    {
        var colors = themeName switch
        {
            Light => LightColors,
            DeepBlue => DeepBlueColors,
            _ => DarkColors,
        };
        var app = Application.Current.Resources;
        foreach (var (key, hex) in colors)
            app[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>获取某主题下某个 Token 的颜色（用于主题选择预览卡片）。</summary>
    public static Brush GetBrush(string themeName, string token)
    {
        var colors = themeName switch
        {
            Light => LightColors,
            DeepBlue => DeepBlueColors,
            _ => DarkColors,
        };
        return colors.TryGetValue(token, out var hex)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            : Brushes.Transparent;
    }

    // ---------------- 暗色（当前默认） ----------------
    private static readonly Dictionary<string, string> DarkColors = new()
    {
        ["WindowBg"] = "#FF0E1218",
        ["CardBg"] = "#FF1E2631",
        ["CardBorder"] = "#FF303B4D",
        ["SideLeftBg"] = "#FF0A0E14",
        ["SideRightBg"] = "#FF161E2C",
        ["TextPrimary"] = "#FFEDF1F7",
        ["TextSecondary"] = "#FFB6BFCC",
        ["TextMuted"] = "#FF7E899B",
        ["Accent"] = "#FF2E7D5B",
        ["AccentGlow"] = "#FF4CC38A",
        ["AccentLight"] = "#1F2E7D5B",
        ["AccentBorder"] = "#552E7D5B",
        ["Danger"] = "#FFB3403C",
        ["Warning"] = "#FFE5C06E",
        ["HoverOverlay"] = "#14FFFFFF",
        ["InputBg"] = "#FF12171F",
        ["InputBorder"] = "#FF3A4658",
        ["ScrollThumb"] = "#FF3A465E",
        ["ModuleDot"] = "#FF4A5464",
        ["SegTabFg"] = "#FF93A0B4",
        ["SegTabHover"] = "#16FFFFFF",
        ["SegTabHoverFg"] = "#FFD7DDE7",
        ["LogInfo"] = "#FFC9D1DD",
        ["NavSectionBg"] = "#FF101722",
        ["NavSectionBorder"] = "#FF232C3B",
        ["NavLabelFg"] = "#FF7E899B",
        ["NavDescFg"] = "#FF828DA0",
        ["EnvDotIdle"] = "#FF7A8494",
        ["IntlTabBg"] = "#FF141B26",
        ["IntlTabBorder"] = "#FF2A3446",
        ["PreviewBg"] = "#FF000000",
    };

    // ---------------- 亮色 ----------------
    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["WindowBg"] = "#FFF5F7FA",
        ["CardBg"] = "#FFFFFFFF",
        ["CardBorder"] = "#FFD0D5DD",
        ["SideLeftBg"] = "#FFE9EDF2",
        ["SideRightBg"] = "#FFF0F3F7",
        ["TextPrimary"] = "#FF1A1A2E",
        ["TextSecondary"] = "#FF5A5A72",
        ["TextMuted"] = "#FF9AA0B0",
        ["Accent"] = "#FF27AE60",
        ["AccentGlow"] = "#FF27AE60",
        ["AccentLight"] = "#3327AE60",
        ["AccentBorder"] = "#5527AE60",
        ["Danger"] = "#FFD32F2F",
        ["Warning"] = "#FFE65100",
        ["HoverOverlay"] = "#14000000",
        ["InputBg"] = "#FFF0F2F5",
        ["InputBorder"] = "#FFB0BEC5",
        ["ScrollThumb"] = "#FFB0BEC5",
        ["ModuleDot"] = "#FFB0BEC5",
        ["SegTabFg"] = "#FF5A5A72",
        ["SegTabHover"] = "#16000000",
        ["SegTabHoverFg"] = "#FF5A5A72",
        ["LogInfo"] = "#FF1A1A2E",
        ["NavSectionBg"] = "#FFE4E9EF",
        ["NavSectionBorder"] = "#FFD0D5DD",
        ["NavLabelFg"] = "#FF9AA0B0",
        ["NavDescFg"] = "#FF9AA0B0",
        ["EnvDotIdle"] = "#FFB0BEC5",
        ["IntlTabBg"] = "#FFF0F2F5",
        ["IntlTabBorder"] = "#FFD0D5DD",
        ["PreviewBg"] = "#FF000000",
    };

    // ---------------- 深蓝 ----------------
    private static readonly Dictionary<string, string> DeepBlueColors = new()
    {
        ["WindowBg"] = "#FF0A1628",
        ["CardBg"] = "#FF132238",
        ["CardBorder"] = "#FF1E3A5F",
        ["SideLeftBg"] = "#FF081220",
        ["SideRightBg"] = "#FF0E1E33",
        ["TextPrimary"] = "#FFE0E6ED",
        ["TextSecondary"] = "#FF8BA3C4",
        ["TextMuted"] = "#FF5A7A9E",
        ["Accent"] = "#FF2E86AB",
        ["AccentGlow"] = "#FF6BB3D9",
        ["AccentLight"] = "#1F2E86AB",
        ["AccentBorder"] = "#552E86AB",
        ["Danger"] = "#FFE57373",
        ["Warning"] = "#FFFFB74D",
        ["HoverOverlay"] = "#14FFFFFF",
        ["InputBg"] = "#FF0D1B2E",
        ["InputBorder"] = "#FF2E4A6F",
        ["ScrollThumb"] = "#FF2E4A6F",
        ["ModuleDot"] = "#FF4A5464",
        ["SegTabFg"] = "#FF8BA3C4",
        ["SegTabHover"] = "#16FFFFFF",
        ["SegTabHoverFg"] = "#FFD7DDE7",
        ["LogInfo"] = "#FFC9D1DD",
        ["NavSectionBg"] = "#FF0A1B2E",
        ["NavSectionBorder"] = "#FF1E3A5F",
        ["NavLabelFg"] = "#FF5A7A9E",
        ["NavDescFg"] = "#FF5A7A9E",
        ["EnvDotIdle"] = "#FF5A7A9E",
        ["IntlTabBg"] = "#FF0D1B2E",
        ["IntlTabBorder"] = "#FF1E3A5F",
        ["PreviewBg"] = "#FF000000",
    };
}
