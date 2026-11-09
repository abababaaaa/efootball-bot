//! PP-OCR（det + rec）推理与后处理：模型结构来自 %MODEL%/v6。
//!
//! 只依赖 ort（ONNX Runtime C API）+ 自写后处理：
//! - det：DB 概率图 → 阈值二值化 → 8 邻域连通域 → bbox → unclip 外扩
//! - rec：按框裁剪 → 高度 48 等比缩放 → CTC 贪心解码（dict 对齐 PaddleOCR 词典）
//!
//! 与 C# 侧 RapidOcrNet / SimdPaddleOCR 的差别只在实现，模型权重完全一样，
//! 所以识别结果应当高度一致（验收见 docs/plans/2026-11-08-rust-vision-sidecar.md）。

use std::io;

use crate::preproc::{Bgra, rgb_to_chw_f32_norm, resize_rgb};

pub const DET_MEAN: [f32; 3] = [0.485, 0.456, 0.406];
pub const DET_STD: [f32; 3] = [0.229, 0.224, 0.225];

/// 一个检测框（原图坐标，像素）。
#[derive(Debug, Clone, Copy)]
pub struct DetBox {
    pub x0: f32,
    pub y0: f32,
    pub x1: f32,
    pub y1: f32,
}

impl DetBox {
    pub fn w(&self) -> f32 {
        (self.x1 - self.x0).max(1.0)
    }
    pub fn h(&self) -> f32 {
        (self.y1 - self.y0).max(1.0)
    }
    pub fn center(&self) -> (f32, f32) {
        ((self.x0 + self.x1) * 0.5, (self.y0 + self.y1) * 0.5)
    }
}

/// det 预处理：等比缩放到长边 ≤ limit 且对齐 32，右侧/下侧补 0（PaddleOCR 同款）。
/// 返回 (CHW f32 数据, 输入宽, 输入高)。
pub fn det_preprocess(src: &Bgra<'_>, limit: u32) -> (Vec<f32>, u32, u32) {
    let (sw, sh) = (src.w.max(1), src.h.max(1));
    let ratio = (limit as f32 / sw as f32).min(limit as f32 / sh as f32);
    let mut rw = (sw as f32 * ratio).round() as u32;
    let mut rh = (sh as f32 * ratio).round() as u32;
    rw = rw.clamp(32, limit);
    rh = rh.clamp(32, limit);
    // 对齐到 32 的倍数（向上取整），模型 stride 才不会被裁掉
    let in_w = ((rw + 31) / 32) * 32;
    let in_h = ((rh + 31) / 32) * 32;

    let mut rgb = Vec::new();
    resize_rgb(src, rw, rh, &mut rgb);
    let mut padded = vec![0u8; (in_w * in_h * 3) as usize];
    for y in 0..rh as usize {
        let src_off = y * rw as usize * 3;
        let dst_off = y * in_w as usize * 3;
        padded[dst_off..dst_off + rw as usize * 3]
            .copy_from_slice(&rgb[src_off..src_off + rw as usize * 3]);
    }

    let mut chw = vec![0f32; (in_w * in_h * 3) as usize];
    rgb_to_chw_f32_norm(&padded, in_w, in_h, DET_MEAN, DET_STD, &mut chw);
    (chw, in_w, in_h)
}

/// DB 后处理：阈值 → 连通域 → bbox → unclip 外扩，坐标按 ratio 还原到原图。
pub fn db_postprocess(
    prob: &[f32],
    pw: usize,
    ph: usize,
    scale_x: f32,
    scale_y: f32,
    thresh: f32,
    min_area: usize,
    unclip: f32,
) -> Vec<DetBox> {
    let mut visited = vec![false; pw * ph];
    let mut boxes = Vec::new();
    let mut stack: Vec<usize> = Vec::new();

    for start in 0..pw * ph {
        if visited[start] || prob[start] <= thresh {
            continue;
        }
        // 8 邻域泛洪
        visited[start] = true;
        stack.clear();
        stack.push(start);
        let (mut x0, mut y0, mut x1, mut y1) = (usize::MAX, usize::MAX, 0usize, 0usize);
        let mut count = 0usize;
        while let Some(idx) = stack.pop() {
            let (px, py) = (idx % pw, idx / pw);
            count += 1;
            x0 = x0.min(px);
            y0 = y0.min(py);
            x1 = x1.max(px);
            y1 = y1.max(py);
            for dy in -1i32..=1 {
                for dx in -1i32..=1 {
                    if dx == 0 && dy == 0 {
                        continue;
                    }
                    let nx = px as i32 + dx;
                    let ny = py as i32 + dy;
                    if nx < 0 || ny < 0 || nx >= pw as i32 || ny >= ph as i32 {
                        continue;
                    }
                    let n = ny as usize * pw + nx as usize;
                    if !visited[n] && prob[n] > thresh {
                        visited[n] = true;
                        stack.push(n);
                    }
                }
            }
        }
        if count < min_area {
            continue;
        }

        // unclip：pyclipper 的多边形外扩距离 d = area * ratio / perimeter，
        // 这里用矩形近似（每边外扩同一个 d），比"按比例放大"更贴近 PaddleOCR，
        // 也不会像比例外扩那样把相邻行/图标一起圈进来（那是字符级误识的主因）。
        let bw = (x1 - x0 + 1) as f32;
        let bh = (y1 - y0 + 1) as f32;
        let d = (bw * bh) * unclip / (2.0 * (bw + bh)).max(1.0);
        let cx = (x0 as f32 + x1 as f32) * 0.5;
        let cy = (y0 as f32 + y1 as f32) * 0.5;
        let bx0 = ((cx - bw * 0.5 - d) * scale_x).max(0.0);
        let by0 = ((cy - bh * 0.5 - d) * scale_y).max(0.0);
        let bx1 = ((cx + bw * 0.5 + d) * scale_x).max(0.0);
        let by1 = ((cy + bh * 0.5 + d) * scale_y).max(0.0);
        if bx1 - bx0 < 4.0 || by1 - by0 < 4.0 {
            continue;
        }
        boxes.push(DetBox { x0: bx0, y0: by0, x1: bx1, y1: by1 });
    }

    // 阅读顺序：先按行（y 中心聚类），行内按 x
    boxes.sort_by(|a, b| {
        let ay = a.center().1;
        let by = b.center().1;
        if (ay - by).abs() > a.h().max(b.h()) * 0.6 {
            ay.partial_cmp(&by).unwrap_or(std::cmp::Ordering::Equal)
        } else {
            a.center().0.partial_cmp(&b.center().0).unwrap_or(std::cmp::Ordering::Equal)
        }
    });
    boxes
}

/// rec 预处理：按框裁剪 → 高度 48（宽等比，最长 640）→ 归一化 (x/255-0.5)/0.5。
/// 返回 (CHW f32, 宽度)。
pub fn rec_preprocess(src: &Bgra<'_>, b: &DetBox, dst_h: u32, max_w: u32) -> (Vec<f32>, u32) {
    let x0 = b.x0.floor().max(0.0) as u32;
    let y0 = b.y0.floor().max(0.0) as u32;
    let x1 = b.x1.ceil().min(src.w as f32).max(1.0) as u32;
    let y1 = b.y1.ceil().min(src.h as f32).max(1.0) as u32;
    let cw = x1.saturating_sub(x0).max(1);
    let ch = y1.saturating_sub(y0).max(1);

    // 先裁到局部 BGRA 缓冲
    let mut crop = vec![0u8; (cw * ch * 4) as usize];
    for row in 0..ch as usize {
        let s = ((y0 as usize + row) * src.stride as usize + x0 as usize * 4) as usize;
        let d = row * cw as usize * 4;
        crop[d..d + cw as usize * 4].copy_from_slice(&src.data[s..s + cw as usize * 4]);
    }
    let crop_src = Bgra { data: &crop, w: cw, h: ch, stride: cw * 4 };

    let scale = dst_h as f32 / ch as f32;
    let rw = ((cw as f32 * scale).round() as u32).clamp(1, max_w);
    let mut rgb = Vec::new();
    resize_rgb(&crop_src, rw, dst_h, &mut rgb);

    let mut chw = vec![0f32; (rw * dst_h * 3) as usize];
    let n = (rw * dst_h) as usize;
    for i in 0..n {
        for c in 0..3 {
            chw[c * n + i] = (rgb[i * 3 + c] as f32 / 255.0 - 0.5) / 0.5;
        }
    }
    (chw, rw)
}

/// CTC 贪心解码。字典顺序与 PaddleOCR 一致：index 0 = blank，character[i] = dict[i-1]。
/// CTC 贪心解码。
///
/// `t_major` = true 表示输出布局是 [1, T, C]（index = t*C + c）；
/// false 表示 [1, C, T]（index = c*T + t）。PP-OCRv6 的 rec 实测是后者。
/// 类别 0 = blank，1..=dict.len() 对应词典，最后一个类别是空格。
pub fn ctc_decode(logits: &[f32], steps: usize, classes: usize, dict: &[String], t_major: bool) -> (String, f32) {
    let at = |t: usize, c: usize| -> f32 {
        let i = if t_major { t * classes + c } else { c * steps + t };
        logits.get(i).copied().unwrap_or(f32::NEG_INFINITY)
    };
    let mut out = String::new();
    let mut conf_sum = 0f32;
    let mut conf_n = 0usize;
    let mut prev = usize::MAX;
    for t in 0..steps {
        let mut best = 0usize;
        let mut best_v = f32::NEG_INFINITY;
        for c in 0..classes {
            let v = at(t, c);
            if v > best_v {
                best_v = v;
                best = c;
            }
        }
        if best != 0 && best != prev {
            if best <= dict.len() {
                out.push_str(&dict[best - 1]);
            } else {
                out.push(' ');
            }
            conf_sum += best_v;
            conf_n += 1;
        }
        prev = best;
    }
    let conf = if conf_n == 0 { 0.0 } else { conf_sum / conf_n as f32 };
    (out, conf)
}

/// 读取 PaddleOCR 词典（每行一个字符/词，保持顺序）。
pub fn load_dict(path: &str) -> io::Result<Vec<String>> {
    let text = std::fs::read_to_string(path)?;
    Ok(text
        .lines()
        .map(|l| l.trim_end_matches(['\r', '\n']).to_string())
        .filter(|l| !l.is_empty())
        .collect())
}

/// 词块（坐标归一化到 0..1），与 C# OcrWord 语义一致。
#[derive(Debug, Clone)]
pub struct Word {
    pub text: String,
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    pub score: f32,
}
