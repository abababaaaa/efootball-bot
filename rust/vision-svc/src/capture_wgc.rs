//! Windows Graphics Capture（WGC）捕获后端。
//!
//! 与 DXGI 桌面复制互补：DXGI 抓的是屏幕上的合成结果（窗口被遮挡/移出屏幕即黑），
//! WGC 抓的是窗口自己（被遮挡、后台、虚拟显示器/远程会话都能出图），
//! 正是主程序默认走 WGC 的原因。Rust 这边通过 WinRT 互操作拿
//! `GraphicsCaptureItem` → `Direct3D11CaptureFramePool` → 帧表面 → D3D11 纹理 → staging 回读。

use std::io;
use std::sync::Once;

use windows::core::{HSTRING, Interface, Result as WResult};
use windows::Foundation::TimeSpan;
use windows::Graphics::Capture::{
    Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::{HINSTANCE, HWND, RECT};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, D3D11_BOX, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_STAGING, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Dxgi::{IDXGIDevice, IDXGIResource};
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RoGetActivationFactory, RoInitialize, RO_INIT_MULTITHREADED};
use windows::Win32::UI::WindowsAndMessaging::GetClientRect;

use crate::capture::{client_rect_of, GrabResult};

static RO_INIT: Once = Once::new();

/// WinRT 必须在任何 RoGetActivationFactory 之前初始化一次（MTA 适合后台线程捕获）。
fn ensure_winrt() {
    RO_INIT.call_once(|| unsafe {
        let _ = RoInitialize(RO_INIT_MULTITHREADED);
    });
}

pub struct WgcCapturer {
    device: ID3D11Device,
    ctx: ID3D11DeviceContext,
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    winrt_device: IDirect3DDevice,
    staging: Option<(ID3D11Texture2D, D3D11_TEXTURE2D_DESC)>,
    frame_size: (u32, u32),
    /// 目标客户区尺寸（0 = 全帧）
    client: (u32, u32),
    buf: Vec<u8>,
    pub last_frame_time: TimeSpan,
}

fn make_device() -> io::Result<(ID3D11Device, ID3D11DeviceContext)> {
    unsafe {
        let levels = [
            windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_11_0,
            windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_10_1,
        ];
        let mut device: Option<ID3D11Device> = None;
        let mut ctx: Option<ID3D11DeviceContext> = None;
        let mut level = windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL::default();
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HINSTANCE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&levels),
            D3D11_SDK_VERSION,
            Some(&mut device as *mut _),
            Some(&mut level),
            Some(&mut ctx as *mut _),
        )
        .map_err(|e| io::Error::other(format!("WGC 建 D3D11 设备失败: {e}")))?;
        let device = device.ok_or_else(|| io::Error::other("WGC 未返回设备"))?;
        let ctx = ctx.ok_or_else(|| io::Error::other("WGC 未返回设备上下文"))?;
        Ok((device, ctx))
    }
}

impl WgcCapturer {
    pub fn new(hwnd: isize) -> io::Result<WgcCapturer> {
        if hwnd == 0 {
            return Err(io::Error::other("WGC 需要窗口句柄（hwnd=0 的整屏模式请用 DXGI）"));
        }
        ensure_winrt();

        let (device, ctx) = make_device()?;

        unsafe {
            let factory: IGraphicsCaptureItemInterop =
                RoGetActivationFactory(&HSTRING::from("Windows.Graphics.Capture.GraphicsCaptureItem"))
                    .map_err(|e| io::Error::other(format!("拿 GraphicsCaptureItem 工厂失败: {e}")))?;
            let item: GraphicsCaptureItem = factory
                .CreateForWindow(HWND(hwnd as *mut _))
                .map_err(|e| io::Error::other(format!("CreateForWindow 失败（窗口是否已销毁？）: {e}")))?;

            let size = item
                .Size()
                .map_err(|e| io::Error::other(format!("读窗口尺寸失败: {e}")))?;

            let dxgi_device: IDXGIDevice = device
                .cast()
                .map_err(|e| io::Error::other(format!("IDXGIDevice 转换失败: {e}")))?;
            let inspectable = CreateDirect3D11DeviceFromDXGIDevice(&dxgi_device)
                .map_err(|e| io::Error::other(format!("包装 WinRT 设备失败: {e}")))?;
            let winrt_device: IDirect3DDevice = inspectable
                .cast()
                .map_err(|e| io::Error::other(format!("IDirect3DDevice 转换失败: {e}")))?;

            let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
                &winrt_device,
                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                size,
            )
            .map_err(|e| io::Error::other(format!("CreateFreeThreaded 失败: {e}")))?;
            let session = pool
                .CreateCaptureSession(&item)
                .map_err(|e| io::Error::other(format!("CreateCaptureSession 失败: {e}")))?;
            let _ = session.SetIsCursorCaptureEnabled(false);
            session
                .StartCapture()
                .map_err(|e| io::Error::other(format!("StartCapture 失败: {e}")))?;

            let client = match client_rect_of(HWND(hwnd as *mut _)) {
                Ok(c) => (c.w, c.h),
                Err(_) => (0, 0),
            };

            Ok(WgcCapturer {
                device,
                ctx,
                pool,
                session,
                winrt_device,
                staging: None,
                frame_size: (size.Width.max(1) as u32, size.Height.max(1) as u32),
                client,
                buf: Vec::new(),
                last_frame_time: TimeSpan::default(),
            })
        }
    }

    pub fn backend_name(&self) -> &'static str {
        "wgc"
    }

    /// 取一帧；超时返回 Ok(None)。
    pub fn next_frame(&mut self, timeout_ms: u32) -> io::Result<Option<GrabResult>> {
        let deadline = std::time::Instant::now() + std::time::Duration::from_millis(timeout_ms as u64);
        loop {
            match unsafe { self.pool.TryGetNextFrame() } {
                Ok(frame) => {
                    self.last_frame_time = frame.SystemRelativeTime().unwrap_or_default();
                    let result = unsafe { self.copy_frame(&frame) };
                    let _ = frame.Close();
                    return result;
                }
                Err(_) => {
                    if std::time::Instant::now() >= deadline {
                        return Ok(None);
                    }
                    std::thread::sleep(std::time::Duration::from_millis(2));
                }
            }
        }
    }

    unsafe fn copy_frame(&mut self, frame: &windows::Graphics::Capture::Direct3D11CaptureFrame) -> io::Result<Option<GrabResult>> {
        let size = frame.ContentSize().unwrap_or(windows::Graphics::SizeInt32 {
            Width: self.frame_size.0 as i32,
            Height: self.frame_size.1 as i32,
        });
        let (fw, fh) = (size.Width.max(1) as u32, size.Height.max(1) as u32);

        if (fw, fh) != self.frame_size {
            // 窗口尺寸变化：重建帧池与 staging
            self.frame_size = (fw, fh);
            self.staging = None;
            let _ = self.pool.Recreate(
                &self.winrt_device,
                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                windows::Graphics::SizeInt32 { Width: fw as i32, Height: fh as i32 },
            );
        }

        let surface = frame
            .Surface()
            .map_err(|e| io::Error::other(format!("取帧表面失败: {e}")))?;
        let access: IDirect3DDxgiInterfaceAccess = surface
            .cast()
            .map_err(|e| io::Error::other(format!("IDirect3DDxgiInterfaceAccess 转换失败: {e}")))?;
        let texture: ID3D11Texture2D = access
            .GetInterface()
            .map_err(|e| io::Error::other(format!("GetInterface(ID3D11Texture2D) 失败: {e}")))?;

        // 只回读目标客户区（WGC 帧本身就是窗口内容，坐标从 0 开始）
        let cw = if self.client.0 == 0 { fw } else { self.client.0.min(fw) };
        let ch = if self.client.1 == 0 { fh } else { self.client.1.min(fh) };

        let need_new = match &self.staging {
            None => true,
            Some((_, d)) => d.Width != cw || d.Height != ch,
        };
        if need_new {
            let sdesc = D3D11_TEXTURE2D_DESC {
                Width: cw,
                Height: ch,
                MipLevels: 1,
                ArraySize: 1,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc: windows::Win32::Graphics::Dxgi::Common::DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
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
            left: 0,
            top: 0,
            front: 0,
            right: cw,
            bottom: ch,
            back: 1,
        };
        self.ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, &texture, 0, Some(&src_box as *const _));

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
        for row in 0..ch as usize {
            let src = src_ptr.add(row * src_pitch);
            let dst = self.buf.as_mut_ptr().add(row * stride as usize);
            std::ptr::copy_nonoverlapping(src, dst, stride as usize);
        }

        let mut probe_nonzero = 0usize;
        let probe_len = (src_pitch * 64).min(1 << 20);
        for i in (0..probe_len).step_by(16) {
            if *src_ptr.add(i) != 0 {
                probe_nonzero += 1;
            }
        }

        self.ctx.Unmap(staging, 0);

        let _: Option<IDXGIResource> = None; // 保持 import 使用，避免 cfg 差异
        Ok(Some(GrabResult {
            w: cw,
            h: ch,
            stride,
            bgra: std::mem::take(&mut self.buf),
            dbg: format!(
                "backend=wgc RowPitch={} frame={}x{} crop={}x{} probe_nonzero={}/{}",
                mapped.RowPitch,
                fw,
                fh,
                cw,
                ch,
                probe_nonzero,
                probe_len / 16
            ),
        }))
    }
}

impl Drop for WgcCapturer {
    fn drop(&mut self) {
        unsafe {
            let _ = self.session.Close();
            let _ = self.pool.Close();
        }
        let _: RECT = RECT::default();
    }
}
