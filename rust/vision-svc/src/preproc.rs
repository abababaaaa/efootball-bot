//! 图像预处理：BGRA → RGB、双线性缩放、letterbox、CHW 归一化。
//!
//! 与 C# 侧 `Core/Vision/ImageScaling` 保持同一套几何约定（双线性 + 114 灰边），
//! 便于两侧对同一帧做逐像素等价对照（tests/vision 里有对照用例）。

/// BGRA 源图描述。
pub struct Bgra<'a> {
    pub data: &'a [u8],
    pub w: u32,
    pub h: u32,
    pub stride: u32,
}

impl<'a> Bgra<'a> {
    #[inline]
    fn px(&self, x: u32, y: u32) -> [u8; 4] {
        let off = (y * self.stride + x * 4) as usize;
        let p = &self.data[off..off + 4];
        [p[0], p[1], p[2], p[3]]
    }
}

/// 双线性缩放到 `dst_w × dst_h`，输出紧密排列的 RGB24（每像素 3 字节）。
pub fn resize_rgb(src: &Bgra<'_>, dst_w: u32, dst_h: u32, dst: &mut Vec<u8>) {
    dst.clear();
    dst.resize((dst_w * dst_h * 3) as usize, 0);
    if src.w == 0 || src.h == 0 || dst_w == 0 || dst_h == 0 {
        return;
    }
    let sx = src.w as f32 / dst_w as f32;
    let sy = src.h as f32 / dst_h as f32;
    for y in 0..dst_h {
        // 像素中心对齐：与 Skia/WPF 的默认采样一致，避免整体偏移半个像素
        let fy = (y as f32 + 0.5) * sy - 0.5;
        let y0 = fy.floor().max(0.0) as u32;
        let y1 = (y0 + 1).min(src.h - 1);
        let wy = (fy - y0 as f32).clamp(0.0, 1.0);
        let row = (y * dst_w * 3) as usize;
        for x in 0..dst_w {
            let fx = (x as f32 + 0.5) * sx - 0.5;
            let x0 = fx.floor().max(0.0) as u32;
            let x1 = (x0 + 1).min(src.w - 1);
            let wx = (fx - x0 as f32).clamp(0.0, 1.0);

            let p00 = src.px(x0, y0);
            let p10 = src.px(x1, y0);
            let p01 = src.px(x0, y1);
            let p11 = src.px(x1, y1);

            for c in 0..3 {
                // BGRA → RGB：通道 0=B,1=G,2=R 反序取
                let sc = 2 - c;
                let top = p00[sc] as f32 * (1.0 - wx) + p10[sc] as f32 * wx;
                let bot = p01[sc] as f32 * (1.0 - wx) + p11[sc] as f32 * wx;
                let v = top * (1.0 - wy) + bot * wy;
                dst[row + (x * 3) as usize + c] = v.round().clamp(0.0, 255.0) as u8;
            }
        }
    }
}

/// letterbox：等比缩放到 `inner` 区域并居中，四周填 `pad`（默认 114，与 C# ImageScaling 一致）
/// 输出紧密 RGB24。
pub fn letterbox_rgb(
    src: &Bgra<'_>,
    out_w: u32,
    out_h: u32,
    pad: u8,
    dst: &mut Vec<u8>,
) -> (f32, u32, u32, u32, u32) {
    dst.clear();
    dst.resize((out_w * out_h * 3) as usize, pad);
    if src.w == 0 || src.h == 0 {
        return (1.0, 0, 0, 0, 0);
    }
    let scale = (out_w as f32 / src.w as f32).min(out_h as f32 / src.h as f32);
    let nw = ((src.w as f32 * scale).round() as u32).max(1).min(out_w);
    let nh = ((src.h as f32 * scale).round() as u32).max(1).min(out_h);
    let ox = (out_w - nw) / 2;
    let oy = (out_h - nh) / 2;

    let mut inner = Vec::with_capacity((nw * nh * 3) as usize);
    resize_rgb(src, nw, nh, &mut inner);

    for y in 0..nh {
        let srow = (y * nw * 3) as usize;
        let drow = (((oy + y) * out_w + ox) * 3) as usize;
        let len = (nw * 3) as usize;
        dst[drow..drow + len].copy_from_slice(&inner[srow..srow + len]);
    }
    (scale, ox, oy, nw, nh)
}

/// RGB24 → f32 CHW，按 `1/255` 归一（图像分类/检测模型的通用预处理）。
pub fn rgb_to_chw_f32(rgb: &[u8], w: u32, h: u32, out: &mut [f32]) {
    let n = (w * h) as usize;
    debug_assert!(out.len() >= n * 3);
    for i in 0..n {
        let r = rgb[i * 3] as f32 / 255.0;
        let g = rgb[i * 3 + 1] as f32 / 255.0;
        let b = rgb[i * 3 + 2] as f32 / 255.0;
        out[i] = r;
        out[n + i] = g;
        out[2 * n + i] = b;
    }
}

/// RGB24 → f32 CHW，按 ImageNet 均值方差归一（YOLO/Paddle 的常见变体）。
pub fn rgb_to_chw_f32_norm(rgb: &[u8], w: u32, h: u32, mean: [f32; 3], std: [f32; 3], out: &mut [f32]) {
    let n = (w * h) as usize;
    for i in 0..n {
        for c in 0..3 {
            out[c * n + i] = (rgb[i * 3 + c] as f32 / 255.0 - mean[c]) / std[c];
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn letterbox_keeps_aspect_and_pads() {
        let w = 8u32;
        let h = 4u32;
        let mut data = vec![0u8; (w * h * 4) as usize];
        for i in 0..(w * h) as usize {
            data[i * 4] = 10; // B
            data[i * 4 + 1] = 20; // G
            data[i * 4 + 2] = 30; // R
            data[i * 4 + 3] = 255;
        }
        let src = Bgra { data: &data, w, h, stride: w * 4 };
        let mut out = Vec::new();
        let (scale, ox, oy, nw, nh) = letterbox_rgb(&src, 16, 16, 114, &mut out);
        assert_eq!((ox, oy), (0, 4));
        assert_eq!((nw, nh), (16, 8));
        assert!((scale - 2.0).abs() < 1e-6);
        assert_eq!(out.len(), 16 * 16 * 3);
        // 灰边
        assert_eq!(&out[0..3], &[114, 114, 114]);
        // 内容区第二行起点是 RGB(30,20,10)
        let row = (4 * 16 * 3) as usize;
        assert_eq!(&out[row..row + 3], &[30, 20, 10]);
    }

    #[test]
    fn resize_down_keeps_color() {
        let (w, h) = (4u32, 4u32);
        let mut data = vec![0u8; (w * h * 4) as usize];
        for i in 0..(w * h) as usize {
            data[i * 4] = 200;
            data[i * 4 + 1] = 100;
            data[i * 4 + 2] = 50;
            data[i * 4 + 3] = 255;
        }
        let src = Bgra { data: &data, w, h, stride: w * 4 };
        let mut out = Vec::new();
        resize_rgb(&src, 2, 2, &mut out);
        assert_eq!(&out[0..3], &[50, 100, 200]);
    }
}
