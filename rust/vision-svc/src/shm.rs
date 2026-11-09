//! 命名共享内存帧环：Rust 写、C# 读。
//!
//! 布局（小端，64 字节头 + N 个等长槽位）：
//! ```text
//! off  0  u32 magic     0x5649534E "VISN"
//! off  4  u32 version   1
//! off  8  u32 width
//! off 12  u32 height
//! off 16  u32 stride
//! off 20  u32 slot_count
//! off 24  u32 slot_bytes
//! off 28  u32 flags     bit0=ready
//! off 32  u64 seq       发布序号（偶数=稳定，奇数=正在写）
//! off 40  u64 ts_ms     发布时刻（Unix ms）
//! off 48  u64 captures  累计抓帧数
//! off 56  u64 drops     覆盖次数（写快于读）
//! off 64  slot 0 .. slot N-1
//! ```
//!
//! 读写规则：writer 把 seq 加到奇数 → 写 `slot = (next_even/2) % slots` → seq 写到偶数。
//! reader 只读偶数 seq，并按同一公式算槽位，天然避免撕裂；读慢于写时读到旧帧而非坏帧。

use std::io;
use std::sync::atomic::{AtomicU64, Ordering};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows::Win32::System::Memory::{
    CreateFileMappingW, MapViewOfFile, OpenFileMappingW, UnmapViewOfFile, FILE_MAP_ALL_ACCESS,
    FILE_MAP_READ, MEMORY_MAPPED_VIEW_ADDRESS, PAGE_READWRITE,
};

pub const MAGIC: u32 = 0x5649_534E;
pub const VERSION: u32 = 1;
pub const HEADER_BYTES: usize = 64;

const OFF_WIDTH: usize = 8;
const OFF_HEIGHT: usize = 12;
const OFF_STRIDE: usize = 16;
const OFF_SLOTS: usize = 20;
const OFF_SLOT_BYTES: usize = 24;
const OFF_FLAGS: usize = 28;
const OFF_SEQ: usize = 32;
const OFF_TS: usize = 40;
const OFF_CAPTURES: usize = 48;
const OFF_DROPS: usize = 56;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 裸指针上的原子视图（单写者 + 跨进程读者，用 volatile 原子保证可见性）。
#[inline]
unsafe fn atomic_at(base: *mut u8, off: usize) -> &'static AtomicU64 {
    &*(base.add(off) as *const AtomicU64)
}

pub struct FrameRing {
    handle: HANDLE,
    view: MEMORY_MAPPED_VIEW_ADDRESS,
    base: *mut u8,
    slots: u32,
    slot_bytes: usize,
    total: usize,
    seq: u64,
    captures: u64,
    drops: u64,
}

// 单写者（捕获线程）+ 进程外读者，内部只有原子序号与裸指针，手动声明可跨线程传递。
unsafe impl Send for FrameRing {}

impl FrameRing {
    pub fn create(name: &str, slot_bytes: usize, slots: u32) -> io::Result<FrameRing> {
        assert!(slots >= 2, "至少两个槽位才能无锁双缓冲");
        let total = HEADER_BYTES + slot_bytes * slots as usize;
        let n = wide(name);
        let handle = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                None,
                PAGE_READWRITE,
                0,
                total as u32,
                PCWSTR(n.as_ptr()),
            )
        }
        .map_err(|e| io::Error::other(format!("CreateFileMappingW({name}) 失败: {e}")))?;

        let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, total) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(io::Error::other(format!("MapViewOfFile({name}) 失败")));
        }
        let base = view.Value as *mut u8;
        let ring = FrameRing {
            handle,
            view,
            base,
            slots,
            slot_bytes,
            total,
            seq: 0,
            captures: 0,
            drops: 0,
        };
        unsafe {
            std::ptr::write_volatile(base.add(OFF_MAGIC_CONST) as *mut u32, MAGIC);
            std::ptr::write_volatile(base.add(4) as *mut u32, VERSION);
            std::ptr::write_volatile(base.add(OFF_SLOTS) as *mut u32, slots);
            std::ptr::write_volatile(base.add(OFF_SLOT_BYTES) as *mut u32, slot_bytes as u32);
            std::ptr::write_volatile(base.add(OFF_FLAGS) as *mut u32, 1);
            atomic_at(base, OFF_SEQ).store(0, Ordering::Release);
            atomic_at(base, OFF_TS).store(now_ms(), Ordering::Release);
        }
        Ok(ring)
    }

    pub fn name_bytes(&self) -> usize {
        self.total
    }

    /// 发布一帧 BGRA 数据（`stride >= w*4`，可为 0 表示 w*4）。返回新的 seq。
    pub fn publish(&mut self, w: u32, h: u32, stride: u32, data: &[u8]) -> u64 {
        let stride = if stride == 0 { w * 4 } else { stride };
        let base = self.base;
        let slots = self.slots as usize;
        let slot_bytes = self.slot_bytes;
        let captures = self.captures + 1;

        unsafe {
            let seq_at = atomic_at(base, OFF_SEQ);
            let prev = seq_at.load(Ordering::Acquire);
            let odd = if prev % 2 == 0 { prev + 1 } else { prev };
            seq_at.store(odd, Ordering::Release);

            let slot = ((odd + 1) / 2) as usize % slots;
            let dst_off = HEADER_BYTES + slot * slot_bytes;
            let n = data.len().min(slot_bytes);
            std::ptr::copy_nonoverlapping(data.as_ptr(), base.add(dst_off), n);

            std::ptr::write_volatile(base.add(OFF_WIDTH) as *mut u32, w);
            std::ptr::write_volatile(base.add(OFF_HEIGHT) as *mut u32, h);
            std::ptr::write_volatile(base.add(OFF_STRIDE) as *mut u32, stride);
            atomic_at(base, OFF_TS).store(now_ms(), Ordering::Release);
            atomic_at(base, OFF_CAPTURES).store(captures, Ordering::Release);

            let even = odd + 1;
            seq_at.store(even, Ordering::Release);
            self.seq = even;
            self.captures = captures;
            even
        }
    }

    /// 读回最新一帧（src=capture 用：OCR 直接吃捕获线程刚写进来的帧，不走上传拷贝）。
    pub fn read_latest(&mut self) -> Option<(Vec<u8>, u32, u32, u32)> {
        let base = self.base;
        unsafe {
            let seq = atomic_at(base, OFF_SEQ).load(Ordering::Acquire);
            if seq == 0 || seq % 2 != 0 {
                return None;
            }
            let w = std::ptr::read_volatile(base.add(OFF_WIDTH) as *const u32);
            let h = std::ptr::read_volatile(base.add(OFF_HEIGHT) as *const u32);
            let stride = std::ptr::read_volatile(base.add(OFF_STRIDE) as *const u32);
            if w == 0 || h == 0 || stride == 0 {
                return None;
            }
            let need = (stride as usize) * (h as usize);
            if need > self.slot_bytes {
                return None;
            }
            let slot = ((seq / 2) as usize) % self.slots as usize;
            let mut out = vec![0u8; need];
            std::ptr::copy_nonoverlapping(base.add(HEADER_BYTES + slot * self.slot_bytes), out.as_mut_ptr(), need);
            Some((out, w, h, stride))
        }
    }

    /// 记录一次“写快于读”的覆盖（C# 侧可据 drops 判断预览是否掉帧）。
    pub fn note_drop(&mut self) {
        self.drops += 1;
        let base = self.base;
        unsafe {
            atomic_at(base, OFF_DROPS).store(self.drops, Ordering::Release);
        }
    }
}

impl Drop for FrameRing {
    fn drop(&mut self) {
        unsafe {
            let _ = UnmapViewOfFile(self.view);
            let _ = CloseHandle(self.handle);
        }
    }
}

const OFF_MAGIC_CONST: usize = 0;

/// 只读帧环（用于 C# 上传的帧：OCR/检测在任意 FrameData 上跑）。
pub struct FrameRingReader {
    view: MEMORY_MAPPED_VIEW_ADDRESS,
    base: *mut u8,
    pub w: u32,
    pub h: u32,
    pub stride: u32,
    pub slots: u32,
    pub slot_bytes: usize,
    last_seq: u64,
}

unsafe impl Send for FrameRingReader {}

impl FrameRingReader {
    pub fn open(name: &str) -> io::Result<FrameRingReader> {
        let n = wide(name);
        let handle = unsafe { OpenFileMappingW(FILE_MAP_READ.0, false, PCWSTR(n.as_ptr())) }
            .map_err(|e| io::Error::other(format!("OpenFileMappingW({name}) 失败: {e}")))?;
        let view = unsafe { MapViewOfFile(handle, FILE_MAP_READ, 0, 0, 0) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(io::Error::other(format!("MapViewOfFile({name}) 失败")));
        }
        let base = view.Value as *mut u8;
        unsafe {
            let magic = std::ptr::read_volatile(base.add(0) as *const u32);
            if magic != MAGIC {
                return Err(io::Error::other(format!("帧环 magic 不符: {magic:#x}")));
            }
        }
        let mut r = FrameRingReader {
            view,
            base,
            w: 0,
            h: 0,
            stride: 0,
            slots: 0,
            slot_bytes: 0,
            last_seq: 0,
        };
        r.refresh();
        Ok(r)
    }

    fn refresh(&mut self) {
        unsafe {
            self.w = std::ptr::read_volatile(self.base.add(OFF_WIDTH) as *const u32);
            self.h = std::ptr::read_volatile(self.base.add(OFF_HEIGHT) as *const u32);
            self.stride = std::ptr::read_volatile(self.base.add(OFF_STRIDE) as *const u32);
            self.slots = std::ptr::read_volatile(self.base.add(OFF_SLOTS) as *const u32);
            self.slot_bytes = std::ptr::read_volatile(self.base.add(OFF_SLOT_BYTES) as *const u32) as usize;
        }
    }

    pub fn seq(&self) -> u64 {
        unsafe { atomic_at(self.base, OFF_SEQ).load(Ordering::Acquire) }
    }

    /// 读指定 seq 的帧（seq 为 0 时读最新）。返回 (BGRA, w, h, stride)。
    pub fn read(&mut self, want_seq: u64) -> Option<(Vec<u8>, u32, u32, u32)> {
        let seq = if want_seq == 0 { self.seq() } else { want_seq };
        if seq == 0 || seq % 2 != 0 {
            return None;
        }
        self.refresh();
        if self.slots == 0 || self.slot_bytes == 0 || self.w == 0 || self.h == 0 {
            return None;
        }
        let need = (self.stride as usize) * (self.h as usize);
        if need == 0 || need > self.slot_bytes {
            return None;
        }
        let slot = ((seq / 2) as usize) % self.slots as usize;
        let mut out = vec![0u8; need];
        unsafe {
            let src = self.base.add(HEADER_BYTES + slot * self.slot_bytes);
            std::ptr::copy_nonoverlapping(src, out.as_mut_ptr(), need);
        }
        self.last_seq = seq;
        Some((out, self.w, self.h, self.stride))
    }
}

impl Drop for FrameRingReader {
    fn drop(&mut self) {
        unsafe {
            let _ = UnmapViewOfFile(self.view);
        }
    }
}
