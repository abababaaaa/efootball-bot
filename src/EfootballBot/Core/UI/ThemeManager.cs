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

    // ---------------- 暗色 · Pitch Console（默认） ----------------
    private static readonly Dictionary<string, string> DarkColors = new()
    {
        ["WindowBg"] = "#FF0C1015",
        ["CardBg"] = "#FF151B23",
        ["CardBorder"] = "#FF242D3A",
        ["SideLeftBg"] = "#FF080B0F",
        ["SideRightBg"] = "#FF0F141B",
        ["TextPrimary"] = "#FFECF1F7",
        ["TextSecondary"] = "#FFA6B1C0",
        ["TextMuted"] = "#FF6C7686",
        ["Accent"] = "#FF0FA968",
        ["AccentHover"] = "#FF14C07A",
        ["AccentGlow"] = "#FF35D392",
        ["AccentLight"] = "#FF1C3128",
        ["AccentBorder"] = "#550FA968",
        ["Danger"] = "#FFE5484D",
        ["DangerHover"] = "#FFF2595E",
        ["Warning"] = "#FFF2B13E",
        ["HoverOverlay"] = "#14FFFFFF",
        ["InputBg"] = "#FF0F141B",
        ["InputBorder"] = "#FF2A3442",
        ["ScrollThumb"] = "#FF33404F",
        ["ModuleDot"] = "#FF454F5E",
        ["SegTabFg"] = "#FF94A0B1",
        ["SegTabHover"] = "#16FFFFFF",
        ["SegTabHoverFg"] = "#FFDCE3EC",
        ["LogInfo"] = "#FFC4CDD9",
        ["NavSectionBg"] = "#FF101722",
        ["NavSectionBorder"] = "#FF222C3A",
        ["NavLabelFg"] = "#FF6C7686",
        ["NavDescFg"] = "#FF818C9D",
        ["EnvDotIdle"] = "#FF5E6878",
        ["IntlTabBg"] = "#FF111823",
        ["IntlTabBorder"] = "#FF283242",
        ["PreviewBg"] = "#FF000000",
        // 新增 Token
        ["Surface2"] = "#FF1B222C",
        ["InsetBg"] = "#FF0A0E13",
        ["TrackBg"] = "#FF101722",
        ["Scrim"] = "#F00B0F14",
        ["TextOnAccent"] = "#FFFFFFFF",
    };

    // ---------------- 亮色 ----------------
    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["WindowBg"] = "#FFEDF0F4",
        ["CardBg"] = "#FFFFFFFF",
        ["CardBorder"] = "#FFDDE3EA",
        ["SideLeftBg"] = "#FFF7F9FB",
        ["SideRightBg"] = "#FFF2F5F8",
        ["TextPrimary"] = "#FF151A21",
        ["TextSecondary"] = "#FF55606E",
        ["TextMuted"] = "#FF8C97A6",
        ["Accent"] = "#FF0B8F5B",
        ["AccentHover"] = "#FF0CA86A",
        ["AccentGlow"] = "#FF0C9A61",
        ["AccentLight"] = "#1F0B8F5B",
        ["AccentBorder"] = "#550B8F5B",
        ["Danger"] = "#FFDC3B3B",
        ["DangerHover"] = "#FFC92F2F",
        ["Warning"] = "#FFC77C0A",
        ["HoverOverlay"] = "#12000000",
        ["InputBg"] = "#FFF4F7FA",
        ["InputBorder"] = "#FFC6CED8",
        ["ScrollThumb"] = "#FFB9C2CD",
        ["ModuleDot"] = "#FFB8C0CB",
        ["SegTabFg"] = "#FF55606E",
        ["SegTabHover"] = "#12000000",
        ["SegTabHoverFg"] = "#FF39434F",
        ["LogInfo"] = "#FF151A21",
        ["NavSectionBg"] = "#FFEDF1F5",
        ["NavSectionBorder"] = "#FFDDE3EA",
        ["NavLabelFg"] = "#FF8C97A6",
        ["NavDescFg"] = "#FF8C97A6",
        ["EnvDotIdle"] = "#FF97A2B0",
        ["IntlTabBg"] = "#FFF4F7FA",
        ["IntlTabBorder"] = "#FFDDE3EA",
        ["PreviewBg"] = "#FF000000",
        // 新增 Token
        ["Surface2"] = "#FFF4F7FA",
        ["InsetBg"] = "#FFE9EEF3",
        ["TrackBg"] = "#FFE6EBF0",
        ["Scrim"] = "#CCEDF0F4",
        ["TextOnAccent"] = "#FFFFFFFF",
    };

    // ---------------- 深蓝 ----------------
    private static readonly Dictionary<string, string> DeepBlueColors = new()
    {
        ["WindowBg"] = "#FF081221",
        ["CardBg"] = "#FF101F33",
        ["CardBorder"] = "#FF1E3350",
        ["SideLeftBg"] = "#FF060D19",
        ["SideRightBg"] = "#FF0B1828",
        ["TextPrimary"] = "#FFDFE9F4",
        ["TextSecondary"] = "#FF8AA6C6",
        ["TextMuted"] = "#FF5D7EA3",
        ["Accent"] = "#FF2F94C9",
        ["AccentHover"] = "#FF43A6DA",
        ["AccentGlow"] = "#FF6EC2E8",
        ["AccentLight"] = "#FF17324A",
        ["AccentBorder"] = "#552F94C9",
        ["Danger"] = "#FFEF6464",
        ["DangerHover"] = "#FFF57878",
        ["Warning"] = "#FFF6BC53",
        ["HoverOverlay"] = "#16FFFFFF",
        ["InputBg"] = "#FF0B1829",
        ["InputBorder"] = "#FF26415F",
        ["ScrollThumb"] = "#FF2A4461",
        ["ModuleDot"] = "#FF3F5570",
        ["SegTabFg"] = "#FF82A0C0",
        ["SegTabHover"] = "#16FFFFFF",
        ["SegTabHoverFg"] = "#FFD5E3F0",
        ["LogInfo"] = "#FFC8D7E8",
        ["NavSectionBg"] = "#FF0A1A2D",
        ["NavSectionBorder"] = "#FF1C314B",
        ["NavLabelFg"] = "#FF5D7EA3",
        ["NavDescFg"] = "#FF6E8DAE",
        ["EnvDotIdle"] = "#FF56748F",
        ["IntlTabBg"] = "#FF0B1A2C",
        ["IntlTabBorder"] = "#FF233B58",
        ["PreviewBg"] = "#FF000000",
        // 新增 Token
        ["Surface2"] = "#FF15283F",
        ["InsetBg"] = "#FF060E1A",
        ["TrackBg"] = "#FF0D1C2E",
        ["Scrim"] = "#F007101C",
        ["TextOnAccent"] = "#FFFFFFFF",
    };
}
