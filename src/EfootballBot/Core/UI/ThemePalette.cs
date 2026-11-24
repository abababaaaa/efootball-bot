namespace EfootballBot.Core.UI;

/// <summary>
/// 主题调色板数据（纯数据，不含任何 WPF 类型）。
///
/// 【两个轴】视觉世界(World) × 亮度(Theme)，共 6 张表：
///  - 世界 Fluent（默认）：Windows 11 云母/亚克力 + 系统强调蓝 —— v7.3 起的原始观感；
///  - 世界 Glass：Apple 黑玻璃（近纯黑毛玻璃 + 中性浅灰选中 + 玻璃渐变卡片 + 抖色噪点）。
///  亮度 Dark / Light / DeepBlue 在两个世界各有一套。
///
/// 【唯一真相】同一世界内三张表必须给出完全相同的 Token 名；
/// 只跟世界走的 Token（SelectFill / GlassFill* / PrimaryBtn* / TopBarBg …）由
/// <see cref="ApplyWorldTokens"/> 一次派生，6 张表不必重复写。
/// 新增 Token：先在 Themes/Tokens.xaml 加默认值，再在两个世界的三张表补齐；
/// 漏加会被 <see cref="Validate"/> 在启动时点名（以前漏加就是「亮色主题下某块还是暗的」）。
/// </summary>
public static class ThemePalette
{
    /// <summary>按 (亮度主题, 视觉世界) 取调色板；未知值分别回落到暗色 / Fluent。</summary>
    public static IReadOnlyDictionary<string, string> For(string? themeName, string? world = null)
    {
        bool glass = ThemeManager.IsGlassWorld(world);
        var colors = new Dictionary<string, string>(
            glass ? GlassDict(themeName) : FluentDict(themeName), StringComparer.Ordinal);
        ApplyWorldTokens(colors, glass);
        return colors;
    }

    private static Dictionary<string, string> FluentDict(string? name) => name switch
    {
        ThemeManager.Light => FluentLight,
        ThemeManager.DeepBlue => FluentDeepBlue,
        _ => FluentDark,
    };

    private static Dictionary<string, string> GlassDict(string? name) => name switch
    {
        ThemeManager.Light => GlassLight,
        ThemeManager.DeepBlue => GlassDeepBlue,
        _ => GlassDark,
    };

    /// <summary>
    /// 世界维度 Token 一次派生：
    ///  - Fluent 侧全部由既有 Token 推出 —— 切回默认外观时观感与 v7.3 一致
    ///    （蓝强调色指示条、实心强调色主按钮、无卡片投影、顶栏透明）；
    ///  - Glass 侧给出玻璃质感数值（浅灰选中、白玻璃主按钮、渐变卡片、顶栏淡黑玻璃）。
    /// </summary>
    private static void ApplyWorldTokens(Dictionary<string, string> c, bool glass)
    {
        if (glass)
        {
            // 选中语言：中性浅灰玻璃表达选中，指示条与选中文字同色（不靠色相）
            c["SelectBar"] = c["SelectFg"];
            c["SubTabSelFill"] = Get(c, "SubTabSelFill", "#664A4A50");
            c["SubTabSelStroke"] = Get(c, "SubTabSelStroke", "#4DFFFFFF");
            // 主按钮：白玻璃渐变 + 深色字 + 顶高光描边
            c["PrimaryBtnFrom"] = "#FFFFFFFF";
            c["PrimaryBtnMid"] = "#FFF2F2F5";
            c["PrimaryBtnTo"] = "#FFE2E2E8";
            c["TextOnPrimary"] = "#FF1C1C1E";
            c["PrimaryBtnStroke"] = "#FFFFFFFF";
            c["BtnFlash"] = "#FF9A9AA0";                     // 白玻璃按钮上的压暗反馈
            // 卡片玻璃渐变：顶部亮、底部透（高级感来自光影，不是半透明色块）
            c["GlassFillTop"] = "#3DFFFFFF";
            c["GlassFillMid"] = "#14FFFFFF";
            c["GlassFillBottom"] = "#0DFFFFFF";
            // 顶栏玻璃蒙版：与左右栏同一张淡黑玻璃
            c["TopBarBg"] = c["ChromeGlass"];
        }
        else
        {
            c["SelectFill"] = c["ControlFill"];
            c["SelectFillStrong"] = c["Surface2"];
            c["SelectBorder"] = "#00000000";
            c["SelectFg"] = c["TextPrimary"];
            c["SelectBar"] = c["Accent"];                    // Win11 导航项的蓝色指示条
            c["SubTabSelFill"] = c["Surface2"];
            c["SubTabSelStroke"] = c["AccentBorder"];
            c["ChipFill"] = c["ControlFill"];
            c["ChipStroke"] = c["ControlStroke"];
            c["ComboBg"] = c["InputBg"];
            c["ComboPopupBg"] = Opaque(c["ContentBg"]);      // 实心底，盖住下层文字
            c["PrimaryBtnFrom"] = c["Accent"];
            c["PrimaryBtnMid"] = c["Accent"];
            c["PrimaryBtnTo"] = c["Accent"];
            c["TextOnPrimary"] = c["TextOnAccent"];
            c["PrimaryBtnStroke"] = "#00000000";
            c["BtnFlash"] = "#FFFFFFFF";                     // Win11 强调色按钮悬停提亮
            c["GlassFillTop"] = c["CardBg"];                 // 三停同色 = 平铺的旧版卡片底
            c["GlassFillMid"] = c["CardBg"];
            c["GlassFillBottom"] = c["CardBg"];
            c["TopBarBg"] = "#00000000";
            // 玻璃蒙版 token 在 Fluent 里用不到，但六张表必须同名（Validate 会点名）：
            // 给透明值，任何误引用都只是「不画」，不会变成一块脏色。
            c["ChromeGlass"] = "#00000000";
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> c, string key, string fallback)
        => c.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>把带透明度的 #AARRGGBB 换成不透明（下拉弹出层必须是实底）。</summary>
    private static string Opaque(string hex) => hex.Length == 9 ? "#FF" + hex[3..] : hex;

    /// <summary>自检：2 世界 × 3 亮度派生后 Token 名必须完全一致。返回空表示完整。</summary>
    public static string[] Validate()
    {
        var reference = For(ThemeManager.Dark, ThemeManager.WorldFluent);
        var problems = new List<string>();
        foreach (var world in ThemeManager.Worlds)
        {
            foreach (var theme in ThemeManager.All)
            {
                var other = For(theme, world);
                string label = $"{ThemeManager.WorldDisplayName(world)}/{ThemeManager.DisplayName(theme)}";
                problems.AddRange(reference.Keys.Except(other.Keys).Select(k => $"{label} 缺 {k}"));
                problems.AddRange(other.Keys.Except(reference.Keys).Select(k => $"{label} 多 {k}"));
            }
        }
        return [.. problems];
    }

    // ================= 世界 Fluent（Win11，默认）=================

    private static readonly Dictionary<string, string> FluentDark = new()
    {
        ["WindowBg"] = "#FF1F1F1F",
        ["ContentBg"] = "#F21F1F1F",
        ["CardBg"] = "#14FFFFFF",
        ["CardBorder"] = "#1FFFFFFF",
        ["SideLeftBg"] = "#66000000",
        ["SideRightBg"] = "#4D000000",
        ["Surface2"] = "#1FFFFFFF",
        ["InsetBg"] = "#0F000000",
        ["TrackBg"] = "#4D000000",
        ["GlassBg"] = "#CC262626",
        ["GlassStroke"] = "#1FFFFFFF",
        ["DialogBg"] = "#F5222226",
        ["ControlFill"] = "#0FFFFFFF",
        ["ControlStroke"] = "#26FFFFFF",
        ["TextPrimary"] = "#FFFFFFFF",
        ["TextSecondary"] = "#FFC7C7C7",
        ["TextMuted"] = "#FF9A9A9A",
        ["NavLabelFg"] = "#FF9A9A9A",
        ["NavDescFg"] = "#FFB4B4B4",
        ["SegTabFg"] = "#FFC7C7C7",
        ["SegTabHover"] = "#14FFFFFF",
        ["SegTabHoverFg"] = "#FFFFFFFF",
        ["LogInfo"] = "#FFD6D6D6",
        ["LogTime"] = "#FFFFB86C",
        ["LogTag"] = "#FF6CB6FF",
        ["Accent"] = "#FF60CDFF",
        ["AccentHover"] = "#FF7EDBFF",
        ["AccentGlow"] = "#FF9BEBFF",
        ["AccentLight"] = "#3360CDFF",
        ["AccentBorder"] = "#5560CDFF",
        ["AccentSoft"] = "#1F60CDFF",
        ["AccentBorderStrong"] = "#8060CDFF",
        ["TextOnAccent"] = "#FF003544",
        ["Danger"] = "#FFFF99A4",
        ["DangerHover"] = "#FFFFB3BC",
        ["DangerText"] = "#FFFF99A4",
        ["DangerBorder"] = "#66FF99A4",
        ["DangerSoft"] = "#26FF99A4",
        ["TextOnDanger"] = "#FF2B080C",
        ["Warning"] = "#FFFCE100",
        ["Success"] = "#FF6CCB5F",
        ["Info"] = "#FF60CDFF",
        ["Amber"] = "#FFFCE100",
        ["Orange"] = "#FFFFB900",
        ["Teal"] = "#FF6CCB5F",
        ["InputBg"] = "#0FFFFFFF",
        ["InputBorder"] = "#26FFFFFF",
        ["HoverOverlay"] = "#14FFFFFF",
        ["ScrollThumb"] = "#66FFFFFF",
        ["ToggleOff"] = "#33FFFFFF",
        ["ToggleOn"] = "#FF60CDFF",
        ["ToggleThumbOn"] = "#FF003544",
        ["ModuleDot"] = "#FF7A7A7A",
        ["EnvDotIdle"] = "#FF8A8A8A",
        ["Divider"] = "#17FFFFFF",
        ["HairlineStrong"] = "#33FFFFFF",
        ["Scrim"] = "#F00A0A0A",
        ["ScrimSoft"] = "#CC0A0A0A",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#33FFFFFF",
        ["NavSectionBorder"] = "#1FFFFFFF",
        ["TabTrackBg"] = "#4D000000",
        ["IntlTabBg"] = "#4D000000",
        ["IntlTabBorder"] = "#1FFFFFFF",
        ["BrandFrom"] = "#FF60CDFF",
        ["BrandTo"] = "#FF0F6CBD",
        ["BannerFrom"] = "#FF0F6CBD",
        ["BannerTo"] = "#FF0B4F8A",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#1A60CDFF",
        ["HeroGlowFade"] = "#0060CDFF",
        ["HeroTitleFrom"] = "#FFFFFFFF",
        ["HeroTitleMid"] = "#FFCFEFFF",
        ["HeroTitleTo"] = "#FF9BEBFF",
        ["TooltipBg"] = "#F22B2B2B",
        ["TooltipBorder"] = "#2EFFFFFF",
        ["TooltipFg"] = "#FFFFFFFF",
    };

    // ---------------- 亮色 · Windows 11 浅色 ----------------
    private static readonly Dictionary<string, string> FluentLight = new()
    {
        ["WindowBg"] = "#FFF3F3F3",
        ["ContentBg"] = "#FFF9F9F9",
        ["CardBg"] = "#B3FFFFFF",
        ["CardBorder"] = "#1A000000",
        ["SideLeftBg"] = "#99FFFFFF",
        ["SideRightBg"] = "#80FFFFFF",
        ["Surface2"] = "#F2FFFFFF",
        ["InsetBg"] = "#FFF2F2F2",
        ["TrackBg"] = "#0A000000",
        ["GlassBg"] = "#E6FBFBFB",
        ["GlassStroke"] = "#14000000",
        ["DialogBg"] = "#FAFFFFFF",
        ["ControlFill"] = "#B3FFFFFF",
        ["ControlStroke"] = "#29000000",
        ["TextPrimary"] = "#FF1A1A1A",
        ["TextSecondary"] = "#FF5C5C5C",
        ["TextMuted"] = "#FF6E6E6E",
        ["NavLabelFg"] = "#FF6E6E6E",
        ["NavDescFg"] = "#FF5C5C5C",
        ["SegTabFg"] = "#FF5C5C5C",
        ["SegTabHover"] = "#0A000000",
        ["SegTabHoverFg"] = "#FF1A1A1A",
        ["LogInfo"] = "#FF1A1A1A",
        ["LogTime"] = "#FFB45309",
        ["LogTag"] = "#FF1D4ED8",
        ["Accent"] = "#FF0067C0",
        ["AccentHover"] = "#FF1975C5",
        ["AccentGlow"] = "#FF0F6CBD",
        ["AccentLight"] = "#1A0067C0",
        ["AccentBorder"] = "#550067C0",
        ["AccentSoft"] = "#140067C0",
        ["AccentBorderStrong"] = "#800067C0",
        ["TextOnAccent"] = "#FFFFFFFF",
        ["Danger"] = "#FFC42B1C",
        ["DangerHover"] = "#FFA92215",
        ["DangerText"] = "#FFC42B1C",
        ["DangerBorder"] = "#55C42B1C",
        ["DangerSoft"] = "#1AC42B1C",
        ["TextOnDanger"] = "#FFFFFFFF",
        ["Warning"] = "#FF9D5D00",
        ["Success"] = "#FF0F7B0F",
        ["Info"] = "#FF0067C0",
        ["Amber"] = "#FF9D5D00",
        ["Orange"] = "#FF8A5300",
        ["Teal"] = "#FF0F7B0F",
        ["InputBg"] = "#B3FFFFFF",
        ["InputBorder"] = "#29000000",
        ["HoverOverlay"] = "#0A000000",
        ["ScrollThumb"] = "#66000000",
        ["ToggleOff"] = "#33000000",
        ["ToggleOn"] = "#FF0067C0",
        ["ToggleThumbOn"] = "#FFFFFFFF",
        ["ModuleDot"] = "#FF8A8A8A",
        ["EnvDotIdle"] = "#FF9A9A9A",
        ["Divider"] = "#14000000",
        ["HairlineStrong"] = "#22000000",
        ["Scrim"] = "#CCF3F3F3",
        ["ScrimSoft"] = "#99F3F3F3",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#4DFFFFFF",
        ["NavSectionBorder"] = "#14000000",
        ["TabTrackBg"] = "#0A000000",
        ["IntlTabBg"] = "#0A000000",
        ["IntlTabBorder"] = "#14000000",
        ["BrandFrom"] = "#FF0067C0",
        ["BrandTo"] = "#FF0F6CBD",
        ["BannerFrom"] = "#FF0067C0",
        ["BannerTo"] = "#FF0B4F8A",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#140067C0",
        ["HeroGlowFade"] = "#000067C0",
        ["HeroTitleFrom"] = "#FF1A1A1A",
        ["HeroTitleMid"] = "#FF0F6CBD",
        ["HeroTitleTo"] = "#FF0067C0",
        ["TooltipBg"] = "#F7FFFFFF",
        ["TooltipBorder"] = "#29000000",
        ["TooltipFg"] = "#FF1A1A1A",
    };

    // ---------------- 深蓝 · Win11 深色 + 蓝调背景 ----------------
    private static readonly Dictionary<string, string> FluentDeepBlue = new()
    {
        ["WindowBg"] = "#FF101828",
        ["ContentBg"] = "#F2141C2B",
        ["CardBg"] = "#14FFFFFF",
        ["CardBorder"] = "#1F9CC7FF",
        ["SideLeftBg"] = "#66060A12",
        ["SideRightBg"] = "#4D060A12",
        ["Surface2"] = "#1FFFFFFF",
        ["InsetBg"] = "#0F000000",
        ["TrackBg"] = "#4D060A12",
        ["GlassBg"] = "#991B2436",
        ["GlassStroke"] = "#1F9CC7FF",
        ["DialogBg"] = "#F5151E2E",
        ["ControlFill"] = "#0FFFFFFF",
        ["ControlStroke"] = "#33FFFFFF",
        ["TextPrimary"] = "#FFF5F8FF",
        ["TextSecondary"] = "#FFC6D3E6",
        ["TextMuted"] = "#FF98AAC4",
        ["NavLabelFg"] = "#FF98AAC4",
        ["NavDescFg"] = "#FFAEC0D8",
        ["SegTabFg"] = "#FFC6D3E6",
        ["SegTabHover"] = "#14FFFFFF",
        ["SegTabHoverFg"] = "#FFFFFFFF",
        ["LogInfo"] = "#FFD8E4F5",
        ["LogTime"] = "#FFFFB86C",
        ["LogTag"] = "#FF6CB6FF",
        ["Accent"] = "#FF7CC4FF",
        ["AccentHover"] = "#FF9AD4FF",
        ["AccentGlow"] = "#FFB7E2FF",
        ["AccentLight"] = "#337CC4FF",
        ["AccentBorder"] = "#557CC4FF",
        ["AccentSoft"] = "#1F7CC4FF",
        ["AccentBorderStrong"] = "#807CC4FF",
        ["TextOnAccent"] = "#FF04243D",
        ["Danger"] = "#FFFF9AA5",
        ["DangerHover"] = "#FFFFB4BD",
        ["DangerText"] = "#FFFF9AA5",
        ["DangerBorder"] = "#66FF9AA5",
        ["DangerSoft"] = "#26FF9AA5",
        ["TextOnDanger"] = "#FF2B080C",
        ["Warning"] = "#FFFCE100",
        ["Success"] = "#FF7BD86E",
        ["Info"] = "#FF7CC4FF",
        ["Amber"] = "#FFFCE100",
        ["Orange"] = "#FFFFB900",
        ["Teal"] = "#FF7BD86E",
        ["InputBg"] = "#0FFFFFFF",
        ["InputBorder"] = "#33FFFFFF",
        ["HoverOverlay"] = "#14FFFFFF",
        ["ScrollThumb"] = "#66FFFFFF",
        ["ToggleOff"] = "#33FFFFFF",
        ["ToggleOn"] = "#FF7CC4FF",
        ["ToggleThumbOn"] = "#FF04243D",
        ["ModuleDot"] = "#FF8098B8",
        ["EnvDotIdle"] = "#FF92A6C0",
        ["Divider"] = "#1FFFFFFF",
        ["HairlineStrong"] = "#33FFFFFF",
        ["Scrim"] = "#F005080F",
        ["ScrimSoft"] = "#CC05080F",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#33060A12",
        ["NavSectionBorder"] = "#1FFFFFFF",
        ["TabTrackBg"] = "#4D060A12",
        ["IntlTabBg"] = "#4D060A12",
        ["IntlTabBorder"] = "#1FFFFFFF",
        ["BrandFrom"] = "#FF7CC4FF",
        ["BrandTo"] = "#FF1F6FB5",
        ["BannerFrom"] = "#FF1F6FB5",
        ["BannerTo"] = "#FF144F84",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#1A7CC4FF",
        ["HeroGlowFade"] = "#007CC4FF",
        ["HeroTitleFrom"] = "#FFFFFFFF",
        ["HeroTitleMid"] = "#FFD5EBFF",
        ["HeroTitleTo"] = "#FFB7E2FF",
        ["TooltipBg"] = "#F0131C2A",
        ["TooltipBorder"] = "#33FFFFFF",
        ["TooltipFg"] = "#FFF5F8FF",
    };

    // ================= 世界 Glass（Apple 黑玻璃）=================

    private static readonly Dictionary<string, string> GlassDark = new()
    {
        ["WindowBg"] = "#FF08080A",
        ["ContentBg"] = "#F208080A",
        ["CardBg"] = "#12FFFFFF",
        // 下拉选择卡：控件本体与弹出层用更实的底色，盖住下层文字避免重叠难辨
        ["ComboBg"] = "#2A242428",
        ["ComboPopupBg"] = "#F0161618",
        ["CardBorder"] = "#20FFFFFF",
        ["SideLeftBg"] = "#59000000",
        ["SideRightBg"] = "#42000000",
        ["Surface2"] = "#1CFFFFFF",
        ["InsetBg"] = "#0A000000",
        ["TrackBg"] = "#4D000000",
        ["GlassBg"] = "#D9101012",
        ["GlassStroke"] = "#24FFFFFF",
        ["DialogBg"] = "#F516161A",
        ["ControlFill"] = "#18FFFFFF",
        ["ControlStroke"] = "#2EFFFFFF",
        // Chip（热键等标签）：更实的深色毛玻璃 + 方形小圆角
        ["ChipFill"] = "#3D000000",
        ["ChipStroke"] = "#3DFFFFFF",
        ["ChromeGlass"] = "#4D08080A",
        ["TextPrimary"] = "#FFFFFFFF",
        ["TextSecondary"] = "#FFE5E5EA",
        ["TextMuted"] = "#FF98989F",
        ["NavLabelFg"] = "#FF98989F",
        ["NavDescFg"] = "#FFB8B8BF",
        ["SegTabFg"] = "#FFE5E5EA",
        ["SegTabHover"] = "#1AFFFFFF",
        ["SegTabHoverFg"] = "#FFFFFFFF",
        ["LogInfo"] = "#FFE5E5EA",
        ["LogTime"] = "#FFFFB86C",
        ["LogTag"] = "#FF6CB6FF",
        ["Accent"] = "#FF0A84FF",
        ["AccentHover"] = "#FF3D9CFF",
        ["AccentGlow"] = "#FF7DBBFF",
        ["AccentLight"] = "#330A84FF",
        ["AccentBorder"] = "#660A84FF",
        ["AccentSoft"] = "#260A84FF",
        ["AccentBorderStrong"] = "#990A84FF",
        ["TextOnAccent"] = "#FFFFFFFF",
        // 浅灰毛玻璃选中态：替代蓝色高亮，靠玻璃质感而非色相表达"选中"
        ["SelectFill"] = "#2EFFFFFF",
        ["SelectFillStrong"] = "#3DFFFFFF",
        ["SelectBorder"] = "#3DFFFFFF",
        ["SelectFg"] = "#FFFFFFFF",
        // 功能分类页签选中：明显的灰色磨砂方框
        ["SubTabSelFill"] = "#664A4A50",
        ["SubTabSelStroke"] = "#4DFFFFFF",
        ["Danger"] = "#FFFF453A",
        ["DangerHover"] = "#FFFF695F",
        ["DangerText"] = "#FFFF6966",
        ["DangerBorder"] = "#66FF453A",
        ["DangerSoft"] = "#26FF453A",
        ["TextOnDanger"] = "#FFFFFFFF",
        ["Warning"] = "#FFFFD60A",
        ["Success"] = "#FF30D158",
        ["Info"] = "#FF0A84FF",
        ["Amber"] = "#FFFFD60A",
        ["Orange"] = "#FFFF9F0A",
        ["Teal"] = "#FF40C8E0",
        ["InputBg"] = "#1FFFFFFF",
        ["InputBorder"] = "#33FFFFFF",
        ["HoverOverlay"] = "#1AFFFFFF",
        ["ScrollThumb"] = "#66FFFFFF",
        ["ToggleOff"] = "#38FFFFFF",
        ["ToggleOn"] = "#FF30D158",
        ["ToggleThumbOn"] = "#FFFFFFFF",
        ["ModuleDot"] = "#FF8E8E93",
        ["EnvDotIdle"] = "#FF8E8E93",
        ["Divider"] = "#29FFFFFF",
        ["HairlineStrong"] = "#3DFFFFFF",
        ["Scrim"] = "#F0000000",
        ["ScrimSoft"] = "#CC000000",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#33FFFFFF",
        ["NavSectionBorder"] = "#1FFFFFFF",
        ["TabTrackBg"] = "#4D000000",
        ["IntlTabBg"] = "#4D000000",
        ["IntlTabBorder"] = "#1FFFFFFF",
        ["BrandFrom"] = "#FF0A84FF",
        ["BrandTo"] = "#FF0057D6",
        ["BannerFrom"] = "#FF0A84FF",
        ["BannerTo"] = "#FF0057D6",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#260A84FF",
        ["HeroGlowFade"] = "#000A84FF",
        ["HeroTitleFrom"] = "#FFFFFFFF",
        ["HeroTitleMid"] = "#FFCFE6FF",
        ["HeroTitleTo"] = "#FF9CCBFF",
        ["TooltipBg"] = "#F22C2C2E",
        ["TooltipBorder"] = "#33FFFFFF",
        ["TooltipFg"] = "#FFFFFFFF",
    };

    // ---------------- 亮色 · Apple 浅色 ----------------
    private static readonly Dictionary<string, string> GlassLight = new()
    {
        ["WindowBg"] = "#FFF2F2F7",
        ["ContentBg"] = "#F2FFFFFF",
        ["CardBg"] = "#B3FFFFFF",
        // 亮色下拉选择卡：浅色实底
        ["ComboBg"] = "#E8F2F2F7",
        ["ComboPopupBg"] = "#FAF2F2F7",
        ["CardBorder"] = "#1A000000",
        ["SideLeftBg"] = "#99F2F2F7",
        ["SideRightBg"] = "#80F2F2F7",
        ["Surface2"] = "#F2FFFFFF",
        ["InsetBg"] = "#FFF2F2F7",
        ["TrackBg"] = "#14000000",
        ["GlassBg"] = "#E6FFFFFF",
        ["GlassStroke"] = "#1A000000",
        ["DialogBg"] = "#FAF7F7FA",
        ["ControlFill"] = "#B3FFFFFF",
        ["ControlStroke"] = "#29000000",
        // 亮色 Chip：浅灰磨砂 + 深描边（确保深色文字清晰可读）
        ["ChipFill"] = "#18000000",
        ["ChipStroke"] = "#2E000000",
        ["ChromeGlass"] = "#59F2F2F7",
        ["TextPrimary"] = "#FF1C1C1E",
        ["TextSecondary"] = "#FF3C3C43",
        ["TextMuted"] = "#FF6E6E73",
        ["NavLabelFg"] = "#FF6E6E73",
        ["NavDescFg"] = "#FF3C3C43",
        ["SegTabFg"] = "#FF3C3C43",
        ["SegTabHover"] = "#0A000000",
        ["SegTabHoverFg"] = "#FF1C1C1E",
        ["LogInfo"] = "#FF1C1C1E",
        ["LogTime"] = "#FFB45309",
        ["LogTag"] = "#FF1D4ED8",
        ["Accent"] = "#FF007AFF",
        ["AccentHover"] = "#FF3399FF",
        ["AccentGlow"] = "#FF66B2FF",
        ["AccentLight"] = "#1A007AFF",
        ["AccentBorder"] = "#55007AFF",
        ["AccentSoft"] = "#14007AFF",
        ["AccentBorderStrong"] = "#80007AFF",
        ["TextOnAccent"] = "#FFFFFFFF",
        // 亮色主题的浅灰玻璃选中态：半透明黑底 + 深色字
        ["SelectFill"] = "#1A000000",
        ["SelectFillStrong"] = "#26000000",
        ["SelectBorder"] = "#26000000",
        ["SelectFg"] = "#FF1C1C1E",
        // 亮色功能页签选中：稍实的灰色磨砂方框
        ["SubTabSelFill"] = "#3D8E8E93",
        ["SubTabSelStroke"] = "#268E8E93",
        ["Danger"] = "#FFFF3B30",
        ["DangerHover"] = "#FFFF5A50",
        ["DangerText"] = "#FFFF3B30",
        ["DangerBorder"] = "#66FF3B30",
        ["DangerSoft"] = "#1AFF3B30",
        ["TextOnDanger"] = "#FFFFFFFF",
        ["Warning"] = "#FFFFCC00",
        ["Success"] = "#FF34C759",
        ["Info"] = "#FF007AFF",
        ["Amber"] = "#FFFFCC00",
        ["Orange"] = "#FFFF9500",
        ["Teal"] = "#FF30B0C7",
        ["InputBg"] = "#B3FFFFFF",
        ["InputBorder"] = "#29000000",
        ["HoverOverlay"] = "#0A000000",
        ["ScrollThumb"] = "#59000000",
        ["ToggleOff"] = "#2E000000",
        ["ToggleOn"] = "#FF34C759",
        ["ToggleThumbOn"] = "#FFFFFFFF",
        ["ModuleDot"] = "#FF8E8E93",
        ["EnvDotIdle"] = "#FF8E8E93",
        ["Divider"] = "#1A000000",
        ["HairlineStrong"] = "#29000000",
        ["Scrim"] = "#CCF2F2F7",
        ["ScrimSoft"] = "#99F2F2F7",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#4DFFFFFF",
        ["NavSectionBorder"] = "#1A000000",
        ["TabTrackBg"] = "#0A000000",
        ["IntlTabBg"] = "#0A000000",
        ["IntlTabBorder"] = "#1A000000",
        ["BrandFrom"] = "#FF007AFF",
        ["BrandTo"] = "#FF0057D6",
        ["BannerFrom"] = "#FF007AFF",
        ["BannerTo"] = "#FF0057D6",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#1A007AFF",
        ["HeroGlowFade"] = "#00007AFF",
        ["HeroTitleFrom"] = "#FF1C1C1E",
        ["HeroTitleMid"] = "#FF007AFF",
        ["HeroTitleTo"] = "#FF0057D6",
        ["TooltipBg"] = "#F7FFFFFF",
        ["TooltipBorder"] = "#29000000",
        ["TooltipFg"] = "#FF1C1C1E",
    };

    // ---------------- 深蓝 · Apple 石墨灰 + 青蓝强调（冷色变体） ----------------
    private static readonly Dictionary<string, string> GlassDeepBlue = new()
    {
        ["WindowBg"] = "#FF1C1C20",
        ["ContentBg"] = "#E61C1C20",
        ["CardBg"] = "#1AFFFFFF",
        // 深蓝下拉选择卡
        ["ComboBg"] = "#2A1C2028",
        ["ComboPopupBg"] = "#F0161A22",
        ["CardBorder"] = "#24A0C8FF",
        ["SideLeftBg"] = "#66151A22",
        ["SideRightBg"] = "#4D151A22",
        ["Surface2"] = "#24FFFFFF",
        ["InsetBg"] = "#14FFFFFF",
        ["TrackBg"] = "#4D000000",
        ["GlassBg"] = "#CC2C3036",
        ["GlassStroke"] = "#29A0C8FF",
        ["DialogBg"] = "#F5181C24",
        ["ControlFill"] = "#1FFFFFFF",
        ["ControlStroke"] = "#33FFFFFF",
        // 深蓝 Chip：更实的深色毛玻璃
        ["ChipFill"] = "#420A0E14",
        ["ChipStroke"] = "#3DFFFFFF",
        ["ChromeGlass"] = "#4D08080A",
        ["TextPrimary"] = "#FFF5F8FF",
        ["TextSecondary"] = "#FFE0E8F2",
        ["TextMuted"] = "#FF98A6B8",
        ["NavLabelFg"] = "#FF98A6B8",
        ["NavDescFg"] = "#FFBAC8D8",
        ["SegTabFg"] = "#FFE0E8F2",
        ["SegTabHover"] = "#1AFFFFFF",
        ["SegTabHoverFg"] = "#FFFFFFFF",
        ["LogInfo"] = "#FFE0E8F2",
        ["LogTime"] = "#FFFFB86C",
        ["LogTag"] = "#FF6CB6FF",
        ["Accent"] = "#FF5AC8FA",
        ["AccentHover"] = "#FF7ED4FF",
        ["AccentGlow"] = "#FFA6E4FF",
        ["AccentLight"] = "#335AC8FA",
        ["AccentBorder"] = "#665AC8FA",
        ["AccentSoft"] = "#265AC8FA",
        ["AccentBorderStrong"] = "#995AC8FA",
        ["TextOnAccent"] = "#FF06131F",
        // 深蓝主题的浅灰玻璃选中态
        ["SelectFill"] = "#2EFFFFFF",
        ["SelectFillStrong"] = "#3DFFFFFF",
        ["SelectBorder"] = "#3DFFFFFF",
        ["SelectFg"] = "#FFF5F8FF",
        // 深蓝功能页签选中：灰色磨砂方框
        ["SubTabSelFill"] = "#664A4A50",
        ["SubTabSelStroke"] = "#4DFFFFFF",
        ["Danger"] = "#FFFF453A",
        ["DangerHover"] = "#FFFF695F",
        ["DangerText"] = "#FFFF6966",
        ["DangerBorder"] = "#66FF453A",
        ["DangerSoft"] = "#26FF453A",
        ["TextOnDanger"] = "#FFFFFFFF",
        ["Warning"] = "#FFFFD60A",
        ["Success"] = "#FF30D158",
        ["Info"] = "#FF5AC8FA",
        ["Amber"] = "#FFFFD60A",
        ["Orange"] = "#FFFF9F0A",
        ["Teal"] = "#FF40C8E0",
        ["InputBg"] = "#1FFFFFFF",
        ["InputBorder"] = "#33FFFFFF",
        ["HoverOverlay"] = "#1AFFFFFF",
        ["ScrollThumb"] = "#66FFFFFF",
        ["ToggleOff"] = "#38FFFFFF",
        ["ToggleOn"] = "#FF30D158",
        ["ToggleThumbOn"] = "#FFFFFFFF",
        ["ModuleDot"] = "#FF8E9AB0",
        ["EnvDotIdle"] = "#FF8E9AB0",
        ["Divider"] = "#29A0C8FF",
        ["HairlineStrong"] = "#3DFFFFFF",
        ["Scrim"] = "#F00C0D10",
        ["ScrimSoft"] = "#CC0C0D10",
        ["PreviewBg"] = "#FF000000",
        ["NavSectionBg"] = "#33A0C8FF",
        ["NavSectionBorder"] = "#1FFFFFFF",
        ["TabTrackBg"] = "#4D151A22",
        ["IntlTabBg"] = "#4D151A22",
        ["IntlTabBorder"] = "#1FFFFFFF",
        ["BrandFrom"] = "#FF5AC8FA",
        ["BrandTo"] = "#FF0A84FF",
        ["BannerFrom"] = "#FF5AC8FA",
        ["BannerTo"] = "#FF0A84FF",
        ["TextOnBanner"] = "#FFFFFFFF",
        ["HeroGlowFrom"] = "#265AC8FA",
        ["HeroGlowFade"] = "#005AC8FA",
        ["HeroTitleFrom"] = "#FFFFFFFF",
        ["HeroTitleMid"] = "#FFD6ECFF",
        ["HeroTitleTo"] = "#FFA6E4FF",
        ["TooltipBg"] = "#F22C3036",
        ["TooltipBorder"] = "#33A0C8FF",
        ["TooltipFg"] = "#FFF5F8FF",
    };
}
