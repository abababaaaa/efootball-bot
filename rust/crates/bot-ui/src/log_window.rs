#![cfg_attr(windows, windows_subsystem = "windows")]

//! 实时日志窗口：连接 C# Worker 命名管道，读 ev=log 事件显示。
//! 用法：bot-log-window.exe [--pipe \\.\pipe\NAME] [--demo]
//! 无管道参数或连接失败时自动走演示流（bot_ipc::Msg 序列化→解析链）。

use std::cell::RefCell;
use std::io::{BufRead, BufReader};
use std::rc::Rc;
use std::sync::mpsc::{channel, Sender};
use std::thread;

use bot_ipc::Msg;
use slint::{ComponentHandle, Timer, TimerMode};

slint::include_modules!();

fn split_log_text(text: &str) -> (String, String, String) {
    let mut time = String::new();
    let mut tag = String::new();
    let mut rest = text.to_string();
    if let Some(r1) = text.strip_prefix('[') {
        if let Some(e) = r1.find(']') {
            time = r1[..e].into();
            rest = r1[e + 1..].trim_start().into();
            if let Some(r2) = rest.strip_prefix('[') {
                if let Some(e2) = r2.find(']') {
                    tag = format!("[{}]", &r2[..e2]);
                    rest = r2[e2 + 1..].trim_start().into();
                }
            }
        }
    }
    (time, tag, rest)
}

fn msg_to_entry(line: &str) -> Option<LogEntry> {
    let m = Msg::from_line(line)?;
    if m.ev.as_deref() != Some("log") { return None; }
    let (time, tag, text) = split_log_text(m.text.as_deref().unwrap_or(""));
    Some(LogEntry {
        time: time.into(), tag: tag.into(), text: text.into(),
        level: m.level.unwrap_or_else(|| "Info".into()).into(),
    })
}

fn spawn_pipe_reader(pipe_name: &str, tx: Sender<String>) {
    let name = pipe_name.to_string();
    thread::spawn(move || {
        // Windows 命名管道用 File::open 即可打开（\\.\pipe\NAME）
        match std::fs::File::open(&name) {
            Ok(file) => {
                for line in BufReader::new(file).lines().map_while(Result::ok) {
                    if tx.send(line).is_err() { break; }
                }
            }
            Err(e) => {
                let _ = tx.send(Msg::ev_log("Warn", format!("[管道] 连接 {name} 失败：{e}，转演示流")).to_line());
                demo_stream(tx);
            }
        }
    });
}

fn demo_stream(tx: Sender<String>) {
    let demo = [
        ("Success", "[启动] 实时日志窗口已就绪"),
        ("Info", "[协议] 读取 bot_ipc::Msg JSON Lines"),
        ("Info", "[提示] 传 --pipe \\\\.\\pipe\\NAME 可接真实 Worker"),
        ("Warn", "[演示] 当前为演示消息流"),
        ("Success", "[OK] 日志三段分色显示正常"),
    ];
    for (lv, t) in demo {
        let _ = tx.send(Msg::ev_log(lv, t).to_line());
        thread::sleep(std::time::Duration::from_millis(400));
    }
}

fn main() -> Result<(), slint::PlatformError> {
    let args: Vec<String> = std::env::args().collect();
    let pipe = args.windows(2).find(|w| w[0] == "--pipe").map(|w| w[1].clone());
    let demo_only = args.iter().any(|a| a == "--demo");

    let ui = LogViewer::new()?;
    ui.set_status(if let Some(p) = &pipe {
        format!("连接管道 {p}…").into()
    } else {
        "实时日志 · 演示流（--pipe 接真实 Worker）".into()
    });

    let (tx, rx) = channel::<String>();
    if demo_only || pipe.is_none() {
        let t = tx.clone();
        thread::spawn(move || demo_stream(t));
    } else {
        spawn_pipe_reader(pipe.as_ref().unwrap(), tx);
    }

    // UI 线程用 Timer 轮询 channel，避免跨线程碰 Slint
    let ui_weak = ui.as_weak();
    let entries = Rc::new(RefCell::new(Vec::<LogEntry>::new()));
    let entries_t = entries.clone();
    let rx = Rc::new(RefCell::new(rx));
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, std::time::Duration::from_millis(120), move || {
        let mut got = 0;
        while got < 50 {
            match rx.borrow_mut().try_recv() {
                Ok(line) => {
                    if let Some(e) = msg_to_entry(&line) { entries_t.borrow_mut().push(e); }
                    got += 1;
                }
                Err(_) => break,
            }
        }
        if got > 0 {
            if let Some(u) = ui_weak.upgrade() {
                let list = entries_t.borrow().clone();
                u.set_rows(slint::ModelRc::new(slint::VecModel::from(list)));
                u.set_count(entries_t.borrow().len() as i32);
            }
        }
    });

    let ui_clear = ui.as_weak();
    let entries_clear = entries.clone();
    ui.on_clear_requested(move || {
        entries_clear.borrow_mut().clear();
        if let Some(u) = ui_clear.upgrade() {
            u.set_rows(slint::ModelRc::new(slint::VecModel::from(Vec::<LogEntry>::new())));
            u.set_count(0);
        }
    });

    ui.run()
}
