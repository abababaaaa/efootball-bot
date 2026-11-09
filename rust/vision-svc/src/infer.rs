//! ONNX 推理：PP-OCR（det + rec）与 YOLO26n 检测，全部走 ort（ONNX Runtime C API）。
//!
//! 线程模型：引擎只在捕获线程之外的单线程里跑（主循环收到 ocr/detect 才调用），
//! 避免多线程同时进同一个 Session（ORT 的 Session 本身可并发，但这边没必要）。

use std::time::Instant;

use ort::session::Session;
use ort::value::Tensor;

use crate::ocr::{ctc_decode, db_postprocess, det_preprocess, load_dict, rec_preprocess, DetBox, Word};
use crate::preproc::{letterbox_rgb, rgb_to_chw_f32, Bgra};

/// 一个检测框（坐标归一化到 0..1，与 C# YoloDetection 同语义）。
#[derive(Debug, Clone)]
pub struct Detect {
    pub c: i32,
    pub s: f32,
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

/// YOLO26n 目标检测（end-to-end NMS-free 与常规预测头两种输出都吃）。
pub struct YoloEngine {
    session: Session,
    pub classes: Vec<String>,
    input_w: u32,
    input_h: u32,
    pub last_ms: f64,
    pub last_dims: Vec<usize>,
    pub last_max_score: f32,
}

impl YoloEngine {
    pub fn new(model: &str, classes_txt: Option<&str>, threads: usize, ep: Ep) -> Result<YoloEngine, Box<dyn std::error::Error>> {
        let session = builder(ep, threads)?.commit_from_file(model)?;
        let classes = match classes_txt.and_then(|p| std::fs::read_to_string(p).ok()) {
            Some(t) => t.lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect(),
            None => Vec::new(),
        };
        Ok(YoloEngine {
            session,
            classes,
            input_w: 640,
            input_h: 640,
            last_ms: 0.0,
            last_dims: Vec::new(),
            last_max_score: 0.0,
        })
    }

    pub fn detect(&mut self, img: &Bgra<'_>, score_thresh: f32, iou_thresh: f32) -> Result<Vec<Detect>, Box<dyn std::error::Error>> {
        let t0 = Instant::now();
        let (iw, ih) = (self.input_w, self.input_h);
        let mut rgb = Vec::new();
        let (scale, ox, oy, nw, nh) = letterbox_rgb(img, iw, ih, 114, &mut rgb);
        let mut chw = vec![0f32; (iw * ih * 3) as usize];
        rgb_to_chw_f32(&rgb, iw, ih, &mut chw);

        let input = Tensor::<f32>::from_array(([1usize, 3, ih as usize, iw as usize], chw))?;
        let outputs = self.session.run(ort::inputs![input])?;
        let (shape, data) = outputs[0].try_extract_tensor::<f32>()?;
        let dims: Vec<usize> = shape.iter().map(|d| *d as usize).collect();

        self.last_dims = dims.clone();
        self.last_max_score = data.iter().fold(f32::NEG_INFINITY, |a, b| a.max(*b));
        // 两种布局：[1, N, 4+nc] / [1, 4+nc, N] / 端到端 [1, N, 6]
        let mut dets = Vec::new();
        if dims.len() == 3 {
            let (d1, d2) = (dims[1], dims[2]);
            if d2 == 6 {
                for i in 0..d1 {
                    let base = i * 6;
                    let s = data[base + 4];
                    if s < score_thresh {
                        continue;
                    }
                    dets.push(map_box(data[base], data[base + 1], data[base + 2], data[base + 3], s, data[base + 5] as i32, scale, ox, oy, iw, ih, img));
                }
            } else if d1 == 6 {
                for i in 0..d2 {
                    let s = data[4 * d2 + i];
                    if s < score_thresh {
                        continue;
                    }
                    dets.push(map_box(data[i], data[d2 + i], data[2 * d2 + i], data[3 * d2 + i], s, data[5 * d2 + i] as i32, scale, ox, oy, iw, ih, img));
                }
            } else {
                // 常规预测头：行 = 4+nc，列 = N（或反过来）
                let (rows, cols, transposed) = if d2 <= 512 && d1 > d2 { (d1, d2, true) } else { (d1, d2, false) };
                let at = |r: usize, c: usize| -> f32 {
                    if transposed { data[c * rows + r] } else { data[r * cols + c] }
                };
                let nc = rows.saturating_sub(4);
                for c in 0..cols {
                    let mut best = 0usize;
                    let mut best_s = 0f32;
                    for k in 0..nc {
                        let s = at(4 + k, c);
                        if s > best_s { best_s = s; best = k; }
                    }
                    if best_s < score_thresh {
                        continue;
                    }
                    dets.push(map_box(at(0, c), at(1, c), at(2, c), at(3, c), best_s, best as i32, scale, ox, oy, iw, ih, img));
                }
                let _ = (nw, nh);
            }
        }
        self.last_ms = t0.elapsed().as_secs_f64() * 1000.0;
        Ok(nms(dets, iou_thresh))
    }
}

#[allow(clippy::too_many_arguments)]
fn map_box(cx: f32, cy: f32, w: f32, h: f32, score: f32, cls: i32, scale: f32, ox: u32, oy: u32, iw: u32, ih: u32, img: &Bgra<'_>) -> Detect {
    // 反 letterbox：先减 padding，再除 scale，最后归一化
    let x0 = (cx - w * 0.5 - ox as f32) / scale;
    let y0 = (cy - h * 0.5 - oy as f32) / scale;
    let x1 = (cx + w * 0.5 - ox as f32) / scale;
    let y1 = (cy + h * 0.5 - oy as f32) / scale;
    let _ = (iw, ih);
    Detect {
        c: cls,
        s: score,
        x: (x0 / img.w as f32).clamp(0.0, 1.0),
        y: (y0 / img.h as f32).clamp(0.0, 1.0),
        w: ((x1 - x0) / img.w as f32).clamp(0.0, 1.0),
        h: ((y1 - y0) / img.h as f32).clamp(0.0, 1.0),
    }
}

fn nms(mut dets: Vec<Detect>, iou_thresh: f32) -> Vec<Detect> {
    dets.sort_by(|a, b| b.s.partial_cmp(&a.s).unwrap_or(std::cmp::Ordering::Equal));
    let mut out: Vec<Detect> = Vec::new();
    for d in dets {
        let mut keep = true;
        for k in &out {
            if k.c != d.c {
                continue;
            }
            let ix0 = k.x.max(d.x);
            let iy0 = k.y.max(d.y);
            let ix1 = (k.x + k.w).min(d.x + d.w);
            let iy1 = (k.y + k.h).min(d.y + d.h);
            if ix1 <= ix0 || iy1 <= iy0 {
                continue;
            }
            let inter = (ix1 - ix0) * (iy1 - iy0);
            let uni = k.w * k.h + d.w * d.h - inter;
            if uni > 0.0 && inter / uni > iou_thresh {
                keep = false;
                break;
            }
        }
        if keep {
            out.push(d);
        }
    }
    out
}

pub struct ModelPaths {
    pub det: String,
    pub rec: String,
    pub dict: String,
    pub yolo: Option<String>,
    pub classes: Option<String>,
}

impl ModelPaths {
    /// 从 models 目录推导：{dir}/v6/PP-OCRv6_det_small.onnx 等（与本仓库 models 布局一致）。
    pub fn from_dir(dir: &str) -> ModelPaths {
        let base = std::path::Path::new(dir);
        ModelPaths {
            det: base.join("v6").join("PP-OCRv6_det_small.onnx").to_string_lossy().into_owned(),
            rec: base.join("v6").join("PP-OCRv6_rec_small.onnx").to_string_lossy().into_owned(),
            dict: base.join("v6").join("ppocrv6_small_dict.txt").to_string_lossy().into_owned(),
            yolo: {
                let p = base.join("yolo").join("yolo26n.onnx");
                if p.exists() { Some(p.to_string_lossy().into_owned()) } else { None }
            },
            classes: {
                let p = base.join("yolo").join("classes.txt");
                if p.exists() { Some(p.to_string_lossy().into_owned()) } else { None }
            },
        }
    }
}

static ENV: std::sync::OnceLock<ort::environment::Environment> = std::sync::OnceLock::new();

/// ort 全进程只允许一个 Environment：OCR 与 YOLO 共用。
pub fn env() -> &'static ort::environment::Environment {
    ENV.get_or_init(|| {
        ort::init()
            .with_name("vision-svc")
            .build()
            .expect("初始化 ONNX Runtime 环境失败")
    })
}

/// 推理后端：cpu / dml（DirectML，需带 DML 的 onnxruntime.dll）
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Ep {
    Cpu,
    Dml,
}

impl Ep {
    pub fn from_name(s: &str) -> Ep {
        match s {
            "dml" | "directml" => Ep::Dml,
            _ => Ep::Cpu,
        }
    }
    pub fn name(&self) -> &'static str {
        match self {
            Ep::Cpu => "cpu",
            Ep::Dml => "dml",
        }
    }
}

fn builder(ep: Ep, threads: usize) -> Result<ort::session::builder::SessionBuilder, Box<dyn std::error::Error>> {
    let b = Session::builder(env())?.with_intra_threads(threads.max(1))?;
    match ep {
        Ep::Cpu => Ok(b),
        Ep::Dml => Ok(b.with_execution_providers([ort::ep::DirectML::default().build()])?),
    }
}

pub struct OcrEngine {
    det: Session,
    rec: Session,
    dict: Vec<String>,
    det_limit: u32,
    pub last_det_ms: f64,
    pub last_rec_ms: f64,
    pub last_boxes: usize,
    pub last_classes: usize,
    pub last_dims: Vec<usize>,
}

impl OcrEngine {
    pub fn new(paths: &ModelPaths, det_limit: u32, threads: usize, ep: Ep) -> Result<OcrEngine, Box<dyn std::error::Error>> {
        let det = builder(ep, threads)?.commit_from_file(&paths.det)?;
        let rec = builder(ep, threads)?.commit_from_file(&paths.rec)?;
        let dict = load_dict(&paths.dict)?;
        Ok(OcrEngine {
            det,
            rec,
            dict,
            det_limit: det_limit.max(320),
            last_det_ms: 0.0,
            last_rec_ms: 0.0,
            last_boxes: 0,
            last_classes: 0,
            last_dims: Vec::new(),
        })
    }

    pub fn dict_len(&self) -> usize {
        self.dict.len()
    }

    /// 全帧/区域 OCR。返回归一化坐标的词块。
    pub fn recognize(&mut self, img: &Bgra<'_>) -> Result<Vec<Word>, Box<dyn std::error::Error>> {
        // ---- det ----
        let t_det = Instant::now();
        let (chw, iw, ih) = det_preprocess(img, self.det_limit);
        let input = Tensor::<f32>::from_array(([1usize, 3, ih as usize, iw as usize], chw))?;
        let outputs = self.det.run(ort::inputs![input])?;
        let (shape, prob) = outputs[0].try_extract_tensor::<f32>()?;
        let dims: Vec<usize> = shape.iter().map(|d| *d as usize).collect();
        let (ph, pw) = if dims.len() >= 4 {
            (dims[dims.len() - 2], dims[dims.len() - 1])
        } else {
            (ih as usize, iw as usize)
        };

        let scale_x = img.w as f32 / iw as f32;
        let scale_y = img.h as f32 / ih as f32;
        let boxes: Vec<DetBox> = db_postprocess(prob, pw, ph, scale_x, scale_y, 0.3, 3, 1.7);
        self.last_det_ms = t_det.elapsed().as_secs_f64() * 1000.0;
        self.last_boxes = boxes.len();

        // ---- rec（按宽度分桶批量推理：PP-OCR rec 是热点，逐框跑会白等）----
        let t_rec = Instant::now();
        let expected = self.dict.len() + 2;
        let mut words: Vec<(DetBox, Word)> = Vec::with_capacity(boxes.len());

        // 1) 每个框先算好目标宽度并按 16 对齐分桶
        struct Item {
            b: DetBox,
            chw: Vec<f32>,
            rw: u32,
            bucket: u32,
        }
        let mut items: Vec<Item> = Vec::with_capacity(boxes.len());
        for b in &boxes {
            let (chw, rw) = rec_preprocess(img, b, 48, 320);
            if rw < 4 {
                continue;
            }
            let bucket = ((rw + 15) / 16) * 16;
            items.push(Item { b: *b, chw, rw, bucket });
        }
        items.sort_by_key(|i| i.bucket);

        let mut k = 0usize;
        while k < items.len() {
            let bucket = items[k].bucket;
            let mut end = k;
            while end < items.len() && items[end].bucket == bucket {
                end += 1;
            }
            // 同一桶内按 8 个一批
            let mut s = k;
            while s < end {
                let n = (end - s).min(8);
                let bw = bucket as usize;
                let mut data = vec![0f32; n * 3 * 48 * bw];
                for i in 0..n {
                    let it = &items[s + i];
                    let src = &it.chw;
                    let w = it.rw as usize;
                    for c in 0..3 {
                        for y in 0..48usize {
                            let src_off = (c * 48 + y) * w;
                            let dst_off = ((i * 3 + c) * 48 + y) * bw;
                            data[dst_off..dst_off + w].copy_from_slice(&src[src_off..src_off + w]);
                        }
                    }
                }
                let input = Tensor::<f32>::from_array(([n, 3usize, 48usize, bw], data))?;
                let outputs = match self.rec.run(ort::inputs![input]) {
                    Ok(o) => o,
                    Err(_) => {
                        s += n;
                        continue;
                    }
                };
                let (shape, logits) = outputs[0].try_extract_tensor::<f32>()?;
                let dims: Vec<usize> = shape.iter().map(|d| *d as usize).collect();
                if dims.len() < 3 {
                    s += n;
                    continue;
                }
                let (d1, d2) = (dims[dims.len() - 2], dims[dims.len() - 1]);
                let (steps, classes, t_major) = if d2 == expected {
                    (d1, d2, true)
                } else if d1 == expected {
                    (d2, d1, false)
                } else {
                    (d1, d2, true)
                };
                self.last_classes = classes;
                self.last_dims = dims.clone();
                let per_sample = steps * classes;
                for i in 0..n {
                    let off = i * per_sample;
                    if off + per_sample > logits.len() {
                        continue;
                    }
                    let slice = &logits[off..off + per_sample];
                    let (text, score) = ctc_decode(slice, steps, classes, &self.dict, t_major);
                    if text.trim().is_empty() {
                        continue;
                    }
                    let it = &items[s + i];
                    words.push((
                        it.b,
                        Word {
                            text,
                            x: it.b.x0 / img.w as f32,
                            y: it.b.y0 / img.h as f32,
                            w: it.b.w() / img.w as f32,
                            h: it.b.h() / img.h as f32,
                            score,
                        },
                    ));
                }
                s += n;
            }
            k = end;
        }

        self.last_rec_ms = t_rec.elapsed().as_secs_f64() * 1000.0;
        // 分桶批量推理会打乱顺序：按阅读顺序（先 y 后 x）还原，探针/日志才稳定
        words.sort_by(|a, b| {
            let ay = a.0.center().1;
            let by = b.0.center().1;
            if (ay - by).abs() > a.0.h().max(b.0.h()) * 0.6 {
                ay.partial_cmp(&by).unwrap_or(std::cmp::Ordering::Equal)
            } else {
                a.0.center().0.partial_cmp(&b.0.center().0).unwrap_or(std::cmp::Ordering::Equal)
            }
        });
        Ok(words.into_iter().map(|(_, w)| w).collect())
    }
}
