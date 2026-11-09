//! 图片读取（仅用于 --ocr-test 这类离线自检，运行时不走这条路）。

use std::io;

/// 读 PNG → BGRA8（紧密排列）。不支持 16 位/索引色以外的花哨变体，够自检用。
pub fn load_png_bgra(path: &str) -> io::Result<(Vec<u8>, u32, u32)> {
    let file = std::fs::File::open(path)?;
    let decoder = png::Decoder::new(file);
    let mut reader = decoder.read_info()?;
    let mut buf = vec![0u8; reader.output_buffer_size()];
    let info = reader.next_frame(&mut buf)?;
    let (w, h) = (info.width, info.height);
    let src = &buf[..info.buffer_size()];

    let mut bgra = vec![0u8; (w as usize) * (h as usize) * 4];
    match info.color_type {
        png::ColorType::Rgba => {
            for i in 0..(w * h) as usize {
                bgra[i * 4] = src[i * 4 + 2];
                bgra[i * 4 + 1] = src[i * 4 + 1];
                bgra[i * 4 + 2] = src[i * 4];
                bgra[i * 4 + 3] = src[i * 4 + 3];
            }
        }
        png::ColorType::Rgb => {
            for i in 0..(w * h) as usize {
                bgra[i * 4] = src[i * 3 + 2];
                bgra[i * 4 + 1] = src[i * 3 + 1];
                bgra[i * 4 + 2] = src[i * 3];
                bgra[i * 4 + 3] = 255;
            }
        }
        png::ColorType::Grayscale => {
            for i in 0..(w * h) as usize {
                let v = src[i];
                bgra[i * 4] = v;
                bgra[i * 4 + 1] = v;
                bgra[i * 4 + 2] = v;
                bgra[i * 4 + 3] = 255;
            }
        }
        other => {
            return Err(io::Error::other(format!("不支持的 PNG 颜色类型 {other:?}")));
        }
    }
    Ok((bgra, w, h))
}
