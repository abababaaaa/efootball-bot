//! DXGI Desktop Duplication 捕获（Windows crate 直接调 COM，不经任何托管封装）。
//!
//! 与 C# 侧 Core/Capture/CaptureVortice/DxgiDesktopCaptureVortice 同源算法，但：
//! 1. 纹理只在 GPU 上 CopyResource 到 staging，Map 一次；
//! 2. 只把目标窗口客户区那一块逐行搬进 Vec<u8>（整屏 8.3MB → 客户区 1~3MB）；
//! 3. HDR 常见格式 R10G10B10A2 / FP16 直接降位到 BGRA8，不再依赖 GDI 兜底。

use std::io;

use windows::core::Interface;
use windows::Win32::Foundation::{HINSTANCE, HWND, POINT, RECT};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_BOX, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_STAGING, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
    DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_R16G16B16A16_FLOAT,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, IDXGIAdapter1, IDXGIFactory1, IDXGIOutput1, IDXGIOutputDuplication, IDXGIResource,
    DXGI_ERROR_WAIT_TIMEOUT, DXGI_OUTDUPL_FRAME_INFO,
};
use windows::Win32::Graphics::Gdi::ClientToScreen;
use windows::Win32::UI::WindowsAndMessaging::GetClientRect;

/// 一帧 BGRA（紧密排列，stride = w*4）。
pub struct GrabResult {
    pub w: u32,
    pub h: u32,
    pub stride: u32,
    pub bgra: Vec<u8>,
    /// 诊断信息（Map 指针/行距/样本非零数），--selftest 会打印
    pub dbg: String,
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct Crop {
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
}

pub struct DxgiCapturer {
    device: ID3D11Device,
    ctx: ID3D11DeviceContext,
    dupl: IDXGIOutputDuplication,
    staging: Option<(ID3D11Texture2D, D3D11_TEXTURE2D_DESC)>,
    output_origin: (i32, i32),
    crop: Option<Crop>,
    buf: Vec<u8>,
    pub last_format: DXGI_FORMAT,
}

pub(crate) fn client_rect_of(hwnd: HWND) -> io::Result<Crop> {
    unsafe {
        let mut rc = RECT::default();
        GetClientRect(hwnd, &mut rc)
            .map_err(|e| io::Error::other(format!("GetClientRect 失败: {e}")))?;
        let mut pt = POINT { x: 0, y: 0 };
        if !ClientToScreen(hwnd, &mut pt).as_bool() {
            return Err(io::Error::other("ClientToScreen 失败"));
        }
        let w = (rc.right - rc.left).max(0) as u32;
        let h = (rc.bottom - rc.top).max(0) as u32;
        Ok(Crop { x: pt.x, y: pt.y, w, h })
    }
}

fn cursor_pos() -> Option<(i32, i32)> {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;
    let mut p = POINT::default();
    unsafe {
        if GetCursorPos(&mut p).is_ok() {
            Some((p.x, p.y))
        } else {
            None
        }
    }
}

/// 桌面所有输出里最大的那个尺寸（帧环按它开槽，避免窗口比请求尺寸大时写不下）。
pub fn desktop_size() -> Option<(u32, u32)> {
    let mut best: Option<(u32, u32)> = None;
    unsafe {
        let factory: IDXGIFactory1 = CreateDXGIFactory1().ok()?;
        let mut ai = 0u32;
        while let Ok(adapter) = factory.EnumAdapters1(ai) {
            let mut oi = 0u32;
            while let Ok(output) = adapter.EnumOutputs(oi) {
                if let Ok(desc) = output.GetDesc() {
                    let rc = desc.DesktopCoordinates;
                    let w = (rc.right - rc.left).max(0) as u32;
                    let h = (rc.bottom - rc.top).max(0) as u32;
                    if w > 0 && h > 0 {
                        let bigger = match best {
                            Some((bw, bh)) => (w as u64 * h as u64) > (bw as u64 * bh as u64),
                            None => true,
                        };
                        if bigger {
                            best = Some((w, h));
                        }
                    }
                }
                oi += 1;
            }
            ai += 1;
        }
    }
    best
}

/// 列出所有适配器/输出（诊断用：确认哪块是真实显示器、哪块是虚拟显示器）。
pub fn list_outputs() -> io::Result<Vec<String>> {
    let mut out = Vec::new();
    unsafe {
        let factory: IDXGIFactory1 = CreateDXGIFactory1()
            .map_err(|e| io::Error::other(format!("CreateDXGIFactory1 失败: {e}")))?;
        let mut ai = 0u32;
        while let Ok(adapter) = factory.EnumAdapters1(ai) {
            let adesc = adapter.GetDesc().map(|d| String::from_utf16_lossy(
                &d.Description[..d.Description.iter().position(|c| *c == 0).unwrap_or(0)],
            )).unwrap_or_default();
            let mut oi = 0u32;
            while let Ok(output) = adapter.EnumOutputs(oi) {
                if let Ok(desc) = output.GetDesc() {
                    let rc = desc.DesktopCoordinates;
                    let name = String::from_utf16_lossy(
                        &desc.DeviceName[..desc.DeviceName.iter().position(|c| *c == 0).unwrap_or(0)],
                    );
                    out.push(format!(
                        "adapter[{ai}]={adesc}  output[{oi}]={name}  rect=({}, {}, {}x{})  attached={}",
                        rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top,
                        desc.AttachedToDesktop.as_bool()
                    ));
                }
                oi += 1;
            }
            ai += 1;
        }
    }
    Ok(out)
}

impl DxgiCapturer {
    /// hwnd = 0 表示整块主屏。
    pub fn new(hwnd: isize) -> io::Result<DxgiCapturer> {
        let crop = if hwnd != 0 {
            Some(client_rect_of(HWND(hwnd as *mut _))?)
        } else {
            None
        };

        unsafe {
            let factory: IDXGIFactory1 = CreateDXGIFactory1()
                .map_err(|e| io::Error::other(format!("CreateDXGIFactory1 失败: {e}")))?;

            // 未指定窗口时按鼠标所在显示器选输出：本机可能同时挂着虚拟显示器
            // （GameViewer/MuMu 等），它们的输出常常不参与 DWM 合成 → 抓到全黑帧。
            let center = crop
                .map(|c| (c.x + (c.w / 2) as i32, c.y + (c.h / 2) as i32))
                .or_else(cursor_pos);
            let mut chosen: Option<(IDXGIAdapter1, IDXGIOutput1, RECT)> = None;

            let mut ai = 0u32;
            'outer: while let Ok(adapter) = factory.EnumAdapters1(ai) {
                let mut oi = 0u32;
                while let Ok(output) = adapter.EnumOutputs(oi) {
                    if let Ok(desc) = output.GetDesc() {
                        if desc.AttachedToDesktop.as_bool() {
                            let rc = desc.DesktopCoordinates;
                            let hit = match center {
                                Some((cx, cy)) => {
                                    cx >= rc.left && cx < rc.right && cy >= rc.top && cy < rc.bottom
                                }
                                None => rc.left == 0 && rc.top == 0,
                            };
                            if hit {
                                if let Ok(o1) = output.cast::<IDXGIOutput1>() {
                                    chosen = Some((adapter.clone(), o1, rc));
                                    break;
                                }
                            }
                        }
                    }
                    oi += 1;
                }
                if chosen.is_some() {
                    break 'outer;
                }
                ai += 1;
            }

            let (chosen_adapter, output1, out_rect) = chosen.ok_or_else(|| {
                io::Error::other("找不到包含目标窗口的 DXGI 输出（窗口是否在另一块屏/已最小化？）")
            })?;

            let levels = [
                windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_11_0,
                windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_10_1,
                windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_10_0,
            ];
            let mut device: Option<ID3D11Device> = None;
            let mut ctx: Option<ID3D11DeviceContext> = None;
            let mut level = D3D_FEATURE_LEVEL::default();
            D3D11CreateDevice(
                &chosen_adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                HINSTANCE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                Some(&levels),
                D3D11_SDK_VERSION,
                Some(&mut device as *mut _),
                Some(&mut level),
                Some(&mut ctx as *mut _),
            )
            .map_err(|e| io::Error::other(format!("D3D11CreateDevice 失败: {e}")))?;
            let device = device.ok_or_else(|| io::Error::other("D3D11CreateDevice 未返回设备"))?;
            let ctx = ctx.ok_or_else(|| io::Error::other("D3D11CreateDevice 未返回上下文"))?;

            let dupl = output1.DuplicateOutput(&device).map_err(|e| {
                io::Error::other(format!("DuplicateOutput 失败（可能已被其它捕获占用）: {e}"))
            })?;

            Ok(DxgiCapturer {
                device,
                ctx,
                dupl,
                staging: None,
                output_origin: (out_rect.left, out_rect.top),
                crop,
                buf: Vec::new(),
                last_format: DXGI_FORMAT_B8G8R8A8_UNORM,
            })
        }
    }

    pub fn set_crop_from_window(&mut self, hwnd: isize) -> io::Result<()> {
        self.crop = if hwnd != 0 {
            Some(client_rect_of(HWND(hwnd as *mut _))?)
        } else {
            None
        };
        Ok(())
    }

    /// 取一帧。超时/无新帧返回 Ok(None)，调用方可直接睡一下再来。
    pub fn next_frame(&mut self, timeout_ms: u32) -> io::Result<Option<GrabResult>> {
        unsafe {
            let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
            let mut resource: Option<IDXGIResource> = None;
            if let Err(e) = self.dupl.AcquireNextFrame(timeout_ms, &mut info, &mut resource) {
                if e.code() == DXGI_ERROR_WAIT_TIMEOUT {
                    return Ok(None);
                }
                return Err(io::Error::other(format!("AcquireNextFrame 失败: {e}")));
            }

            let result = self.copy_from_resource(resource);
            let _ = self.dupl.ReleaseFrame();
            result
        }
    }

    unsafe fn copy_from_resource(
        &mut self,
        resource: Option<IDXGIResource>,
    ) -> io::Result<Option<GrabResult>> {
        let resource = match resource {
            Some(r) => r,
            None => return Ok(None),
        };
        let texture: ID3D11Texture2D = resource.cast().map_err(|e| {
            io::Error::other(format!("IDXGIResource 转 ID3D11Texture2D 失败: {e}"))
        })?;

        let mut desc = D3D11_TEXTURE2D_DESC::default();
        texture.GetDesc(&mut desc);
        self.last_format = desc.Format;

        // 先在桌面坐标系里算好裁剪框，再按框大小建 staging —— GPU 侧只搬这块，
        // 回读量与窗口客户区成正比（窗口化 1920×1080 时整屏 19.8MB → 8.3MB）。
        let (ox, oy, cw, ch) = match self.crop {
            Some(c) => {
                let x = (c.x - self.output_origin.0).max(0) as u32;
                let y = (c.y - self.output_origin.1).max(0) as u32;
                let x = x.min(desc.Width.saturating_sub(1));
                let y = y.min(desc.Height.saturating_sub(1));
                (
                    x,
                    y,
                    c.w.min(desc.Width - x).max(1),
                    c.h.min(desc.Height - y).max(1),
                )
            }
            None => (0, 0, desc.Width, desc.Height),
        };

        let need_new = match &self.staging {
            None => true,
            Some((_, d)) => d.Width != cw || d.Height != ch || d.Format != desc.Format,
        };
        if need_new {
            let sdesc = D3D11_TEXTURE2D_DESC {
                Width: cw,
                Height: ch,
                MipLevels: 1,
                ArraySize: 1,
                Format: desc.Format,
                SampleDesc: desc.SampleDesc,
                Usage: D3D11_USAGE_STAGING,
                BindFlags: 0,
                CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                MiscFlags: 0,
            };
            let mut staging: Option<ID3D11Texture2D> = None;
            self.device
                .CreateTexture2D(&sdesc, None, Some(&mut staging as *mut _))
                .map_err(|e| io::Error::other(format!("CreateTexture2D(staging) 失败: {e}")))?;
            let staging = staging.ok_or_else(|| io::Error::other("staging 纹理创建失败"))?;
            self.staging = Some((staging, sdesc));
        }

        let (staging, _) = self.staging.as_ref().unwrap();
        let src_box = D3D11_BOX {
            left: ox,
            top: oy,
            front: 0,
            right: ox + cw,
            bottom: oy + ch,
            back: 1,
        };
        self.ctx.CopySubresourceRegion(
            staging,
            0,
            0,
            0,
            0,
            &texture,
            0,
            Some(&src_box as *const _),
        );

        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        self.ctx
            .Map(staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped as *mut _))
            .map_err(|e| io::Error::other(format!("Map(staging) 失败: {e}")))?;

        let src_ptr = mapped.pData as *const u8;
        let src_pitch = mapped.RowPitch as usize;
        let stride = cw * 4;
        let need = (stride as usize) * (ch as usize);
        if self.buf.len() != need {
            self.buf.resize(need, 0);
        }

        // 诊断：前 64 行非零样本数（0 = 复制没生效，或桌面复制本身给了黑帧）
        let mut probe_nonzero = 0usize;
        let probe_len = (src_pitch * 64).min(1 << 20);
        for i in (0..probe_len).step_by(16) {
            if *src_ptr.add(i) != 0 {
                probe_nonzero += 1;
            }
        }
        let dbg = format!(
            "RowPitch={} crop=({ox},{oy},{cw}x{ch}) probe_nonzero={}/{} tex={}x{} fmt={:?} misc={}",
            mapped.RowPitch,
            probe_nonzero,
            probe_len / 16,
            desc.Width,
            desc.Height,
            desc.Format,
            desc.MiscFlags
        );

        match desc.Format {
            DXGI_FORMAT_B8G8R8A8_UNORM | DXGI_FORMAT_B8G8R8A8_UNORM_SRGB => {
                for row in 0..ch as usize {
                    let src = src_ptr.add(row * src_pitch);
                    let dst = self.buf.as_mut_ptr().add(row * stride as usize);
                    std::ptr::copy_nonoverlapping(src, dst, stride as usize);
                }
            }
            DXGI_FORMAT_R10G10B10A2_UNORM => {
                convert_10bit(&mut self.buf, src_ptr, src_pitch, 0, 0, cw, ch, stride);
            }
            DXGI_FORMAT_R16G16B16A16_FLOAT => {
                convert_fp16(&mut self.buf, src_ptr, src_pitch, 0, 0, cw, ch, stride);
            }
            other => {
                self.ctx.Unmap(staging, 0);
                return Err(io::Error::other(format!("不支持的桌面格式 {other:?}")));
            }
        }

        self.ctx.Unmap(staging, 0);
        Ok(Some(GrabResult {
            w: cw,
            h: ch,
            stride,
            bgra: std::mem::take(&mut self.buf),
            dbg,
        }))
    }
}

#[inline]
#[allow(clippy::too_many_arguments)]
unsafe fn convert_10bit(
    dst_buf: &mut [u8],
    src_ptr: *const u8,
    src_pitch: usize,
    ox: u32,
    oy: u32,
    cw: u32,
    ch: u32,
    stride: u32,
) {
    for row in 0..ch as usize {
        let src_row = src_ptr.add((oy as usize + row) * src_pitch) as *const u32;
        let dst = dst_buf.as_mut_ptr().add(row * stride as usize);
        for col in 0..cw as usize {
            let v = *src_row.add(ox as usize + col);
            let r = ((v & 0x3FF) >> 2) as u8;
            let g = (((v >> 10) & 0x3FF) >> 2) as u8;
            let b = (((v >> 20) & 0x3FF) >> 2) as u8;
            let d = dst.add(col * 4);
            *d = b;
            *d.add(1) = g;
            *d.add(2) = r;
            *d.add(3) = 255;
        }
    }
}

#[inline]
#[allow(clippy::too_many_arguments)]
unsafe fn convert_fp16(
    dst_buf: &mut [u8],
    src_ptr: *const u8,
    src_pitch: usize,
    ox: u32,
    oy: u32,
    cw: u32,
    ch: u32,
    stride: u32,
) {
    for row in 0..ch as usize {
        let src_row = src_ptr.add((oy as usize + row) * src_pitch) as *const u16;
        let dst = dst_buf.as_mut_ptr().add(row * stride as usize);
        for col in 0..cw as usize {
            let base = (ox as usize + col) * 4;
            let r = half_to_u8(*src_row.add(base));
            let g = half_to_u8(*src_row.add(base + 1));
            let b = half_to_u8(*src_row.add(base + 2));
            *dst.add(col * 4) = b;
            *dst.add(col * 4 + 1) = g;
            *dst.add(col * 4 + 2) = r;
            *dst.add(col * 4 + 3) = 255;
        }
    }
}

#[inline]
fn half_to_u8(h: u16) -> u8 {
    (half_to_f32(h) * 255.0).round().clamp(0.0, 255.0) as u8
}

#[inline]
fn half_to_f32(h: u16) -> f32 {
    let sign = (h >> 15) & 1;
    let exp = (h >> 10) & 0x1F;
    let mant = (h & 0x3FF) as u32;
    let v = if exp == 0 {
        (mant as f32) * 2f32.powi(-24)
    } else if exp == 0x1F {
        if mant == 0 {
            f32::INFINITY
        } else {
            f32::NAN
        }
    } else {
        (1.0 + mant as f32 / 1024.0) * 2f32.powi(exp as i32 - 15)
    };
    if sign == 1 {
        -v
    } else {
        v
    }
}

/// DXGI_ERROR_WAIT_TIMEOUT 的 HRESULT 值（供上层判断用）。
pub const HRESULT_WAIT_TIMEOUT: i32 = DXGI_ERROR_WAIT_TIMEOUT.0;

/// 采样判断是否基本全黑（每 64 个像素取 1，阈值与 C# BotEngine.IsMostlyBlack 同口径）。
pub fn mostly_black(f: &GrabResult) -> bool {
    let step = 4 * 64;
    let mut sum: u64 = 0;
    let mut n: u64 = 0;
    let mut i = 0usize;
    while i + 3 < f.bgra.len() {
        sum += f.bgra[i] as u64 + f.bgra[i + 1] as u64 + f.bgra[i + 2] as u64;
        n += 1;
        i += step;
    }
    n == 0 || sum / (n * 3) < 8
}

/// 捕获后端统一入口：DXGI（整屏/窗口裁剪）与 WGC（窗口级，遮挡/后台可用）。
pub enum Capturer {
    Dxgi(DxgiCapturer),
    Wgc(crate::capture_wgc::WgcCapturer),
}

impl Capturer {
    /// backend: "dxgi" | "wgc" | 其它（=auto：先 DXGI，失败且窗口有效则 WGC）
    pub fn new(backend: &str, hwnd: isize) -> io::Result<Capturer> {
        match backend {
            "wgc" => Ok(Capturer::Wgc(crate::capture_wgc::WgcCapturer::new(hwnd)?)),
            "dxgi" => Ok(Capturer::Dxgi(DxgiCapturer::new(hwnd)?)),
            _ => match DxgiCapturer::new(hwnd) {
                Ok(c) => Ok(Capturer::Dxgi(c)),
                Err(e) if hwnd != 0 => {
                    eprintln!("DXGI 不可用（{e}），改走 WGC");
                    Ok(Capturer::Wgc(crate::capture_wgc::WgcCapturer::new(hwnd)?))
                }
                Err(e) => Err(e),
            },
        }
    }

    pub fn backend_name(&self) -> &'static str {
        match self {
            Capturer::Dxgi(_) => "dxgi",
            Capturer::Wgc(_) => "wgc",
        }
    }

    pub fn next_frame(&mut self, timeout_ms: u32) -> io::Result<Option<GrabResult>> {
        match self {
            Capturer::Dxgi(c) => c.next_frame(timeout_ms),
            Capturer::Wgc(c) => c.next_frame(timeout_ms),
        }
    }

    pub fn set_crop_from_window(&mut self, hwnd: isize) -> io::Result<()> {
        match self {
            Capturer::Dxgi(c) => c.set_crop_from_window(hwnd),
            Capturer::Wgc(_) => Ok(()), // WGC 帧本身就是窗口内容
        }
    }
}
