//! Rust 版 arch-guard（对应 C# 侧 tools/arch-guard/arch-guard.ps1 的 11 条规则）。
//!
//! 这里用源码扫描实现「依赖方向锁死」，作为 cargo test 的一部分在 CI/本地执行：
//!   - 共享层（bot-ipc/bot-state/bot-theme）不许依赖任何 UI 框架 crate
//!   - bot-state 不许反向依赖 bot-ui
//!   - bot-ui 是唯一允许出现 slint/gpui 依赖的 crate
//!   - 模块 id 字面量只在 bot-state/src/module_ids.rs 出现

use std::fs;
use std::path::PathBuf;

/// 只取 Cargo.toml 的依赖段（[dependencies] / [build-dependencies] 等），
/// 避免 description/注释里提到框架名造成误报。
fn dependency_sections(toml: &str) -> String {
    let mut out = String::new();
    let mut in_dep = false;
    for line in toml.lines() {
        let t = line.trim();
        if t.starts_with('[') {
            in_dep = t.starts_with("[dependencies")
                || t.starts_with("[build-dependencies")
                || t.starts_with("[dev-dependencies")
                || t.starts_with("[target.");
        }
        if in_dep {
            out.push_str(line);
            out.push('\n');
        }
    }
    out
}

fn rust_root() -> PathBuf {
    // CARGO_MANIFEST_DIR = rust/crates/bot-ui，向上两级到 rust/ workspace 根。
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn read(rel: &str) -> String {
    let path = rust_root().join(rel);
    fs::read_to_string(&path).unwrap_or_else(|e| panic!("读取 {path:?} 失败: {e}"))
}

fn manifest(crate_name: &str) -> String {
    read(&format!("crates/{crate_name}/Cargo.toml"))
}

/// ARCH-UI-FW-ONLY：slint/gpui 只允许出现在 bot-ui 的 Cargo.toml。
#[test]
fn arch_ui_framework_only_in_bot_ui() {
    let shared = ["bot-ipc", "bot-state", "bot-theme", "vision-svc"];
    for name in shared {
        let toml = if name == "vision-svc" {
            read("vision-svc/Cargo.toml")
        } else {
            manifest(name)
        };
        let lower = dependency_sections(&toml).to_ascii_lowercase();
        assert!(
            !lower.contains("slint") && !lower.contains("gpui"),
            "ARCH-UI-FW-ONLY: {name} 不许依赖 UI 框架"
        );
    }
    let ui = dependency_sections(&manifest("bot-ui")).to_ascii_lowercase();
    assert!(ui.contains("bot-ipc") && ui.contains("bot-state") && ui.contains("bot-theme"),
        "ARCH-UI-LINK: bot-ui 必须消费三个共享 crate");
}

/// ARCH-NO-BACK-DEP：共享层不许反向依赖 bot-ui。
#[test]
fn arch_no_shared_depends_on_ui() {
    for name in ["bot-ipc", "bot-state", "bot-theme"] {
        let toml = dependency_sections(&manifest(name)).to_ascii_lowercase();
        assert!(!toml.contains("bot-ui"), "ARCH-NO-BACK-DEP: {name} 反向依赖了 bot-ui");
    }
}

/// ARCH-ID-LITERAL：模块 id 字面量只在 bot-state/src/module_ids.rs 出现。
#[test]
fn arch_module_id_literals_centralized() {
    let allowed = read("crates/bot-state/src/module_ids.rs");
    let ids = ["intlweekly", "intlladder", "intlstudbuy", "cnmyleague", "cnweekly", "cndaily"];
    // 允许文件里每个 id 至少出现一次（定义处）
    for id in ids {
        assert!(allowed.contains(id), "模块 id {id} 不在唯一定义处");
    }
    // 其他 crate 源码里不许出现这些字面量
    for entry in fs::read_dir(rust_root().join("crates")).unwrap() {
        let p = entry.unwrap().path();
        if !p.is_dir() {
            continue;
        }
        for f in fs::read_dir(p.join("src")).unwrap().flatten() {
            let fp = f.path();
            if fp.extension().and_then(|s| s.to_str()) != Some("rs") {
                continue;
            }
            if fp == rust_root().join("crates/bot-state/src/module_ids.rs") {
                continue;
            }
            let text = fs::read_to_string(&fp).unwrap_or_default().to_ascii_lowercase();
            for id in ids {
                assert!(!text.contains(id), "ARCH-ID-LITERAL: {} 里出现了模块 id {id}", fp.display());
            }
        }
    }
}

/// ARCH-THEME-SINGLE-SOURCE：bot-theme 必须由 build.rs 从 Tokens.xaml 生成。
#[test]
fn arch_theme_single_source() {
    let toml = manifest("bot-theme");
    assert!(toml.contains("build.rs") || toml.contains("build ="), "bot-theme 缺少 build.rs");
    let build = read("crates/bot-theme/build.rs");
    assert!(build.contains("Tokens.xaml"), "build.rs 没有引用 Tokens.xaml");
    assert!(build.contains("rerun-if-changed"), "build.rs 没有声明 token 变更监听");
}
