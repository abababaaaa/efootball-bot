//! 主题 token 的单一真源，供 Slint 主壳与 gpui 预览窗共用。
//!
//! 结构照搬 C# `Core/UI/ThemePalette.cs`：
//!   两个视觉世界（Fluent / Glass） × 三档亮度（Dark / Light / DeepBlue）= 6 张表；
//!   同一世界内三张表 token 名完全一致，按世界派生的 token 由 `world_tokens()` 给出。
//!
//! token 的键与默认值由 build.rs 从 `Themes/Tokens.xaml` 生成，不手抄。

include!(concat!(env!("OUT_DIR"), "/tokens_generated.rs"));

/// 视觉世界（对应 ThemeManager.IsGlassWorld）
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum World {
    /// Windows 11 云母/亚克力 + 系统强调蓝（默认，v7.3 原始观感）
    Fluent,
    /// Apple 黑玻璃（近纯黑毛玻璃 + 中性浅灰选中 + 玻璃渐变卡片 + 抖色噪点）
    Glass,
}

impl World {
    pub fn from_str(s: &str) -> Self {
        if s.eq_ignore_ascii_case("glass") {
            World::Glass
        } else {
            World::Fluent
        }
    }
    pub fn as_str(self) -> &'static str {
        match self {
            World::Fluent => "fluent",
            World::Glass => "glass",
        }
    }
}

/// 亮度档（对应 ThemeManager.Light / DeepBlue / Dark）
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Appearance {
    Dark,
    Light,
    DeepBlue,
}

impl Appearance {
    pub fn from_str(s: &str) -> Self {
        if s.eq_ignore_ascii_case("light") {
            Appearance::Light
        } else if s.eq_ignore_ascii_case("deepblue") {
            Appearance::DeepBlue
        } else {
            Appearance::Dark
        }
    }
    pub fn as_str(self) -> &'static str {
        match self {
            Appearance::Dark => "dark",
            Appearance::Light => "light",
            Appearance::DeepBlue => "deepblue",
        }
    }
}

/// 查默认 token 值（来自 Tokens.xaml 的 Glass 暗色基线值）。
pub fn default_token(name: &str) -> Option<&'static str> {
    TOKEN_DEFAULTS
        .iter()
        .find(|(k, _)| *k == name)
        .map(|(_, v)| *v)
}

/// 按世界派生的 token（对应 ThemePalette.ApplyWorldTokens），返回键值对覆盖层。
/// UI 框架把默认表 + 该覆盖层合并即可得到某一 (world, appearance) 的完整调色板。
pub fn world_tokens(world: World, appearance: Appearance) -> Vec<(&'static str, &'static str)> {
    let is_light = matches!(appearance, Appearance::Light);
    match world {
        World::Glass => vec![
            ("SelectBar", default_token("SelectFg").unwrap_or("#FFFFFFFF")),
            ("SubTabSelFill", "#664A4A50"),
            ("SubTabSelStroke", "#4DFFFFFF"),
            ("PrimaryBtnFrom", default_token("Accent").unwrap_or("#2563EB")),
            ("PrimaryBtnMid", default_token("Accent").unwrap_or("#2563EB")),
            ("PrimaryBtnTo", default_token("BrandTo").unwrap_or("#1D4ED8")),
            ("TextOnPrimary", default_token("TextOnAccent").unwrap_or("#FFFFFF")),
            ("PrimaryBtnStroke", if is_light { "#22000000" } else { "#2EFFFFFF" }),
            ("BtnFlash", "#FFFFFFFF"),
            ("GlassFillTop", if is_light { "#E0FFFFFF" } else { "#14FFFFFF" }),
            ("GlassFillMid", if is_light { "#CCFFFFFF" } else { "#0DFFFFFF" }),
            ("GlassFillBottom", if is_light { "#B8FFFFFF" } else { "#09FFFFFF" }),
            ("TopBarBg", default_token("ChromeGlass").unwrap_or("#08080A")),
        ],
        World::Fluent => vec![
            ("SelectBar", default_token("Accent").unwrap_or("#2563EB")),
            ("SubTabSelFill", default_token("Surface2").unwrap_or("#1CFFFFFF")),
            ("SubTabSelStroke", default_token("AccentBorder").unwrap_or("#660A84FF")),
            ("ChipFill", default_token("ControlFill").unwrap_or("#18FFFFFF")),
            ("ChipStroke", default_token("ControlStroke").unwrap_or("#2EFFFFFF")),
            ("ComboBg", default_token("InputBg").unwrap_or("#1FFFFFFF")),
            ("PrimaryBtnFrom", default_token("Accent").unwrap_or("#2563EB")),
            ("PrimaryBtnMid", default_token("Accent").unwrap_or("#2563EB")),
            ("PrimaryBtnTo", default_token("Accent").unwrap_or("#2563EB")),
            ("TextOnPrimary", default_token("TextOnAccent").unwrap_or("#FFFFFF")),
            ("PrimaryBtnStroke", "#00000000"),
            ("BtnFlash", "#FFFFFFFF"),
        ],
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tokens_generated_from_xaml() {
        assert!(TOKEN_COUNT > 100, "token 数太少，可能没读到 Tokens.xaml: {TOKEN_COUNT}");
        assert!(default_token("WindowBg").is_some());
        assert_eq!(default_token("Accent"), Some("#2563EB"));
        // #FFFFFFFF 归一化成 #FFFFFF
        assert_eq!(default_token("TextPrimary"), Some("#FFFFFF"));
    }

    #[test]
    fn six_combinations() {
        for w in [World::Fluent, World::Glass] {
            for a in [Appearance::Dark, Appearance::Light, Appearance::DeepBlue] {
                assert!(!world_tokens(w, a).is_empty());
            }
        }
    }

    #[test]
    fn world_override_differs() {
        let fluent: Vec<_> = world_tokens(World::Fluent, Appearance::Dark)
            .into_iter()
            .map(|(k, _)| k)
            .collect();
        let glass: Vec<_> = world_tokens(World::Glass, Appearance::Dark)
            .into_iter()
            .map(|(k, _)| k)
            .collect();
        assert!(fluent.contains(&"SelectBar"));
        assert!(glass.contains(&"GlassFillTop"));
        assert!(!fluent.contains(&"GlassFillTop"));
    }
}
