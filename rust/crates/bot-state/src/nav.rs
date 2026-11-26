//! 左栏导航模式与 `--page` 深链别名。
//! 对应 C# `Shell/NavRoute.cs`，保留全部别名（含 play/setting/net/log）。

use std::collections::HashMap;

/// 上栏一级分区
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TopSection {
    Home,
    Game,
    Tools,
    About,
}

/// 左栏导航模式（一个值 = 一个页面）
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum NavMode {
    Home,
    Game,
    Settings,
    Update,
    GitHub,
    Network,
    LogViewer,
    Feedback,
    About,
}

impl NavMode {
    /// 所属的一级分区（对应 MainWindow.Shell.cs 的 _sectionMembers）
    pub fn section(self) -> TopSection {
        match self {
            NavMode::Home => TopSection::Home,
            NavMode::Game => TopSection::Game,
            NavMode::Settings
            | NavMode::Update
            | NavMode::GitHub
            | NavMode::Network
            | NavMode::LogViewer => TopSection::Tools,
            NavMode::Feedback | NavMode::About => TopSection::About,
        }
    }
}

/// `--page` 别名表（大小写不敏感；与 C# NavKey.Aliases 逐项对应）。
pub struct NavKey;

impl NavKey {
    pub fn aliases() -> HashMap<&'static str, NavMode> {
        use NavMode::*;
        [
            ("home", Home),
            ("game", Game),
            ("play", Game),
            ("settings", Settings),
            ("setting", Settings),
            ("update", Update),
            ("github", GitHub),
            ("network", Network),
            ("net", Network),
            ("logs", LogViewer),
            ("log", LogViewer),
            ("feedback", Feedback),
            ("about", About),
        ]
        .into_iter()
        .collect()
    }

    /// 解析 `--page settings:display` 形式，返回 (模式, 二级段)。
    pub fn parse(raw: &str) -> Option<(NavMode, Option<String>)> {
        let key = raw.trim();
        if key.is_empty() {
            return None;
        }
        let (head, sub) = match key.find(':') {
            Some(i) => (&key[..i], Some(key[i + 1..].trim().to_lowercase())),
            None => (key, None),
        };
        let aliases = Self::aliases();
        let mode = *aliases.get(&head.to_lowercase().as_str())?;
        Some((mode, sub))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn alias_count() {
        assert_eq!(NavKey::aliases().len(), 13);
    }

    #[test]
    fn parse_simple_and_nested() {
        assert_eq!(NavKey::parse(" settings ").unwrap().0, NavMode::Settings);
        let (mode, sub) = NavKey::parse("settings:display").unwrap();
        assert_eq!(mode, NavMode::Settings);
        assert_eq!(sub.as_deref(), Some("display"));
        assert_eq!(NavKey::parse("play").unwrap().0, NavMode::Game);
    }

    #[test]
    fn unknown_returns_none() {
        assert!(NavKey::parse("nope").is_none());
        assert!(NavKey::parse("").is_none());
    }

    #[test]
    fn section_mapping() {
        assert_eq!(NavMode::Game.section(), TopSection::Game);
        assert_eq!(NavMode::Network.section(), TopSection::Tools);
        assert_eq!(NavMode::About.section(), TopSection::About);
    }
}
