//! GUI 壳 ↔ Worker 子进程 的命名管道协议（JSON Lines，一行一条 UTF-8 JSON）。
//!
//! 字段与 C# `src/EfootballBot/Ipc/PipeProtocol.cs::Msg` 一一对应，全部可选、
//! 序列化时跳过 None，保证与现有 C# 端双向兼容。
//!
//! 命令方向 GUI→Worker：start/stop/pause/resume/step/swapTest/preview/reloadConfig/ping
//! 事件方向 Worker→GUI：log/stats/screen/preview/pong/pid/exited/stopped

use serde::{Deserialize, Serialize};

/// 一条消息（命令或事件），扁平结构，与 C# `PipeProtocol.Msg` 同形。
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Msg {
    // ---- 命令（GUI → Worker）----
    /// start | stop | pause | resume | step | swapTest | preview | reloadConfig | ping
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cmd: Option<String>,
    /// start: 模块 id（唯一来源见 bot_state::ModuleIds）或 "observe" 纯观察
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mode: Option<String>,
    /// preview: true/false；pause: 暂停态
    #[serde(skip_serializing_if = "Option::is_none")]
    pub on: Option<bool>,

    // ---- 事件（Worker → GUI）----
    /// log | stats | screen | preview | pong | pid | exited | stopped
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ev: Option<String>,
    /// log: Info/Success/Warn/Error/Debug
    #[serde(skip_serializing_if = "Option::is_none")]
    pub level: Option<String>,
    /// log.msg / exited.reason
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    /// screen: 感知摘要文本
    #[serde(skip_serializing_if = "Option::is_none")]
    pub screen: Option<String>,
    /// screen.score
    #[serde(default)]
    pub score: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub evidence: Option<String>,
    /// preview: base64 PNG
    #[serde(skip_serializing_if = "Option::is_none")]
    pub png: Option<String>,

    // ---- stats ----
    #[serde(default)]
    pub matches: i32,
    #[serde(default)]
    pub key_matches: i32,
    #[serde(default)]
    pub players_maxed: i32,
    #[serde(default)]
    pub skipped: i32,
    /// ISO 8601
    #[serde(skip_serializing_if = "Option::is_none")]
    pub started_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
    /// 分段耗时摘要（引擎 1 分钟刷新一次）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub perf: Option<String>,
    /// pid 事件：Worker 进程号
    #[serde(default)]
    pub pid: i32,
}

impl Msg {
    // ---- 命令构造 ----
    pub fn cmd_start(mode: impl Into<String>) -> Self {
        Self { cmd: Some("start".into()), mode: Some(mode.into()), ..Default::default() }
    }
    pub fn cmd_stop() -> Self {
        Self { cmd: Some("stop".into()), ..Default::default() }
    }
    pub fn cmd_pause(on: bool) -> Self {
        Self { cmd: Some(if on { "pause" } else { "resume" }.into()), ..Default::default() }
    }
    pub fn cmd_step() -> Self {
        Self { cmd: Some("step".into()), ..Default::default() }
    }
    pub fn cmd_swap_test() -> Self {
        Self { cmd: Some("swapTest".into()), ..Default::default() }
    }
    pub fn cmd_preview(on: bool) -> Self {
        Self { cmd: Some("preview".into()), on: Some(on), ..Default::default() }
    }
    pub fn cmd_reload_config() -> Self {
        Self { cmd: Some("reloadConfig".into()), ..Default::default() }
    }
    pub fn cmd_ping() -> Self {
        Self { cmd: Some("ping".into()), ..Default::default() }
    }

    // ---- 事件构造 ----
    pub fn ev_log(level: impl Into<String>, text: impl Into<String>) -> Self {
        Self { ev: Some("log".into()), level: Some(level.into()), text: Some(text.into()), ..Default::default() }
    }
    pub fn ev_screen(screen: impl Into<String>, score: f64, evidence: Option<String>) -> Self {
        Self { ev: Some("screen".into()), screen: Some(screen.into()), score, evidence, ..Default::default() }
    }
    pub fn ev_preview(png_base64: impl Into<String>) -> Self {
        Self { ev: Some("preview".into()), png: Some(png_base64.into()), ..Default::default() }
    }
    pub fn ev_pong() -> Self {
        Self { ev: Some("pong".into()), ..Default::default() }
    }
    pub fn ev_pid(pid: i32) -> Self {
        Self { ev: Some("pid".into()), pid, ..Default::default() }
    }
    pub fn ev_exited(reason: impl Into<String>) -> Self {
        Self { ev: Some("exited".into()), text: Some(reason.into()), ..Default::default() }
    }
    pub fn ev_stopped() -> Self {
        Self { ev: Some("stopped".into()), ..Default::default() }
    }
    #[allow(clippy::too_many_arguments)]
    pub fn ev_stats(
        matches: i32,
        key_matches: i32,
        players_maxed: i32,
        skipped: i32,
        started_at: Option<String>,
        last_error: Option<String>,
        perf: Option<String>,
    ) -> Self {
        Self {
            ev: Some("stats".into()),
            matches,
            key_matches,
            players_maxed,
            skipped,
            started_at,
            last_error,
            perf,
            ..Default::default()
        }
    }

    // ---- JSON Lines ----
    pub fn to_line(&self) -> String {
        // C# 用 UnsafeRelaxedJsonEscaping（中文不转义），serde_json 默认也输出 UTF-8 原文。
        serde_json::to_string(self).expect("Msg 序列化不会失败")
    }
    pub fn from_line(line: &str) -> Option<Self> {
        serde_json::from_str(line).ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cmd_start_wire_shape_matches_csharp() {
        // C# System.Text.Json 对 int/double（非 nullable）的零值也会输出；
        // WhenWritingNull 只跳过引用类型的 null。所以真实线上格式带零值数值字段。
        let line = Msg::cmd_start("intl").to_line();
        assert!(line.starts_with(r#"{"cmd":"start","mode":"intl","#), "{line}");
        assert!(line.contains(r#""keyMatches":0"#), "{line}");
        // 所有 Option 字段都被省略
        assert!(!line.contains("\"ev\""));
        assert!(!line.contains("\"png\""));
    }

    #[test]
    fn none_fields_omitted() {
        let line = Msg::cmd_stop().to_line();
        assert!(line.starts_with(r#"{"cmd":"stop","#), "{line}");
        assert!(!line.contains("\"mode\""));
        assert!(!line.contains("\"on\""));
        // 数值字段按 C# 行为保留零值
        assert!(line.contains(r#""score":0.0"#));
        assert!(line.contains(r#""matches":0"#));
    }

    #[test]
    fn stats_roundtrip() {
        let m = Msg::ev_stats(12, 3, 1, 2, Some("2026-11-25T00:00:00".into()), None, None);
        let parsed = Msg::from_line(&m.to_line()).unwrap();
        assert_eq!(parsed.ev.as_deref(), Some("stats"));
        assert_eq!(parsed.matches, 12);
        assert_eq!(parsed.key_matches, 3);
        assert_eq!(parsed.skipped, 2);
    }

    #[test]
    fn csharp_style_camel_case() {
        let line = Msg {
            ev: Some("stats".into()),
            key_matches: 7,
            started_at: Some("t".into()),
            ..Default::default()
        }
        .to_line();
        assert!(line.contains("\"keyMatches\":7"));
        assert!(line.contains("\"startedAt\":\"t\""));
    }
}
