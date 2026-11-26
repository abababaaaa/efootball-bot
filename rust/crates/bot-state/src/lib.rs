//! 模块清单与导航状态的单一真源。
//!
//! 数据全部从 C# 侧导出，保证与 `PipeProtocol` 的 `mode` 字符串、`--page` 深链一致：
//! - `ModuleIds` ← `src/EfootballBot/Core/Bot/Modules/ModuleIds.cs`
//! - `NavMode` / `NavKey` ← `src/EfootballBot/Shell/NavRoute.cs`
//!
//! 硬约束：本 crate 不认识 Slint/gpui，也不认识 WPF；UI 侧只能从这里取值。
//!
//! 与协议层的关系：本 crate 持有的 id/模式字符串会作为 `bot_ipc::Msg::cmd_start`
//! 的 mode 参数下发，因此保留 bot-ipc 依赖（编译期锚定两边的版本一致）。

pub mod module_ids;
pub mod nav;

pub use module_ids::{EngineModes, ModuleIds, ALL_MODULES};
pub use nav::{NavKey, NavMode, TopSection};
