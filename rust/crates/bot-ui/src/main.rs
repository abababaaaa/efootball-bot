#![cfg_attr(windows, windows_subsystem = "windows")] // 隐藏控制台黑框，单 exe 无命令行

//! bot-ui — 复刻正式版 UI 的 Slint 主壳（4 分区 × 9 页）。
//!
//! 结构映射 C# Shell/NavRoute.cs；文案来自 MainWindow.xaml。
//! 共享层数据（bot-state / bot-ipc / bot-theme）在此自检并桥接给 UI。

use slint::ComponentHandle;

slint::include_modules!();

fn main() -> Result<(), slint::PlatformError> {
    // 共享层自检：证明三个 crate 都链接进来了
    println!(
        "bot-ui S2: {} modules / {} nav aliases / {} theme tokens",
        bot_state::ALL_MODULES.len(),
        bot_state::NavKey::aliases().len(),
        bot_theme::TOKEN_COUNT,
    );

    // 协议层自检：与 C# PipeProtocol 兼容的命令序列化
    let wire = bot_state::module_ids::start_command(bot_state::ModuleIds::INTL_MY_LEAGUE).to_line();
    println!("ipc: {wire}");

    let app = App::new()?;
    app.set_status(format!(
        "测试版 S2 · {} 模块 / {} 主题token",
        bot_state::ALL_MODULES.len(),
        bot_theme::TOKEN_COUNT
    ).into());

    // --page 深链：仿 C# NavKey.parse，支持启动时直跳页面
    let page_arg = std::env::args().find(|a| a.starts_with("--page="));
    if let Some(arg) = page_arg {
        let raw = arg.trim_start_matches("--page=");
        if let Some((mode, _sub)) = bot_state::NavKey::parse(raw) {
            use bot_state::NavMode;
            let target = match mode {
                NavMode::Home => PageId::Home,
                NavMode::Game => PageId::Game,
                NavMode::Settings => PageId::Settings,
                NavMode::Update => PageId::Update,
                NavMode::GitHub => PageId::Github,
                NavMode::Network => PageId::Network,
                NavMode::LogViewer => PageId::Logs,
                NavMode::Feedback => PageId::Feedback,
                NavMode::About => PageId::About,
            };
            use bot_state::TopSection;
            let section = match mode.section() {
                TopSection::Home => SectionId::Home,
                TopSection::Game => SectionId::Game,
                TopSection::Tools => SectionId::Tools,
                TopSection::About => SectionId::About,
            };
            app.set_page(target);
            app.set_section(section);
            println!("深链 --page={raw} => {target:?}");
        }
    }

    app.run()
}
