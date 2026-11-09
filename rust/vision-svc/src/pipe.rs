//! 命名管道客户端（Rust 连接 C# 创建的 server，沿用现有 Worker 的「GUI 建 server」约定）。
//!
//! 为什么用 PeekNamedPipe 轮询而不是「读线程 + 阻塞 ReadFile」：
//! Rust 这里拿到的是**同步**管道句柄，同一个句柄上的阻塞读会与写互相串行化——
//! 读线程一阻塞在 ReadFile，主线程的 WriteFile 就发不出去（实测 ack 会卡到下一次读返回）。
//! 因此改成单线程轮询：peek 有数据才 read，读完再写事件，读写永不互相阻塞。

use std::fs::File;
use std::io::{self, Write};
use std::os::windows::io::FromRawHandle;
use std::time::{Duration, Instant};

use windows::core::PCWSTR;
use windows::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, ReadFile, FILE_ATTRIBUTE_NORMAL, FILE_GENERIC_READ, FILE_GENERIC_WRITE,
    FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::Pipes::PeekNamedPipe;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub struct PipeConn {
    handle: HANDLE,
    writer: File,
    pending: Vec<u8>,
}

impl PipeConn {
    /// 连接 \\.\pipe\<name>，在 timeout 内轮询重试（C# server 可能还没建好）。
    pub fn connect(name: &str, timeout: Duration) -> io::Result<PipeConn> {
        let path = format!(r"\\.\pipe\{name}");
        let w = wide(&path);
        let deadline = Instant::now() + timeout;
        let mut last = String::new();
        loop {
            let handle = unsafe {
                CreateFileW(
                    PCWSTR(w.as_ptr()),
                    (FILE_GENERIC_READ | FILE_GENERIC_WRITE).0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    None,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    INVALID_HANDLE_VALUE,
                )
            };
            match handle {
                Ok(h) => {
                    let writer = unsafe { File::from_raw_handle(h.0 as _) };
                    return Ok(PipeConn { handle: h, writer, pending: Vec::new() });
                }
                Err(e) => last = format!("{e}"),
            }
            if Instant::now() >= deadline {
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!("连接管道 {path} 超时: {last}"),
                ));
            }
            std::thread::sleep(Duration::from_millis(50));
        }
    }

    /// 非阻塞取一行（没有完整行就返回 None）。
    pub fn poll_line(&mut self) -> io::Result<Option<String>> {
        loop {
            if let Some(pos) = self.pending.iter().position(|b| *b == b'\n') {
                let line: Vec<u8> = self.pending.drain(..=pos).collect();
                let text = String::from_utf8_lossy(&line[..line.len().saturating_sub(1)])
                    .trim()
                    .to_string();
                if text.is_empty() {
                    continue;
                }
                return Ok(Some(text));
            }

            let mut avail = 0u32;
            match unsafe { PeekNamedPipe(self.handle, None, 0, None, Some(&mut avail), None) } {
                Ok(()) => {}
                Err(e) => return Err(io::Error::other(format!("PeekNamedPipe 失败: {e}"))),
            }
            if avail == 0 {
                return Ok(None);
            }

            let mut buf = [0u8; 16 * 1024];
            let want = (avail as usize).min(buf.len());
            let mut read = 0u32;
            match unsafe { ReadFile(self.handle, Some(&mut buf[..want]), Some(&mut read), None) } {
                Ok(()) => {}
                Err(e) => return Err(io::Error::other(format!("ReadFile 失败: {e}"))),
            }
            if read == 0 {
                return Ok(None);
            }
            self.pending.extend_from_slice(&buf[..read as usize]);
        }
    }

    pub fn send(&mut self, line: &str) -> io::Result<()> {
        self.writer.write_all(line.as_bytes())?;
        self.writer.flush()
    }
}

/// 父进程看门狗：GUI/Worker 死掉后 sidecar 自行退出，避免残留进程占 GPU/句柄。
pub fn spawn_parent_watchdog(parent_pid: u32) {
    if parent_pid == 0 {
        return;
    }
    std::thread::spawn(move || {
        use windows::Win32::Foundation::WAIT_OBJECT_0;
        use windows::Win32::System::Threading::{OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE};
        unsafe {
            if let Ok(h) = OpenProcess(PROCESS_SYNCHRONIZE, false, parent_pid) {
                if WaitForSingleObject(h, u32::MAX) == WAIT_OBJECT_0 {
                    std::process::exit(0);
                }
            }
        }
    });
}
