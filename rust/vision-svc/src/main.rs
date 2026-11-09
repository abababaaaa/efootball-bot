//! vision-svc：eFootball bot 的 Rust 视觉 sidecar。
//!
//! 用法：
//!   vision-svc --pipe <name> [--parent <pid>] [--console]   服务模式（GUI 创建管道 server）
//!   vision-svc --selftest [--hwnd 0x1234] [--out out.bmp]   单帧自检（不需要 GUI）
//!   vision-svc --bench [n] [--hwnd 0x1234]                  抓帧耗时基准
//!
//! 线程模型：读线程（阻塞读命令）→ 主线程（写事件、串行处理命令）→ 捕获线程（只产帧）。

mod capture;
mod capture_wgc;
mod imgio;
mod infer;
mod ocr;
mod pipe;
mod preproc;
mod proto;
mod shm;

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc as StdArc;
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use proto::Msg;
use shm::FrameRing;

const VERSION: &str = env!("CARGO_PKG_VERSION");

struct Args {
    pipe: Option<String>,
    parent: u32,
    hwnd: isize,
    selftest: bool,
    list_outputs: bool,
    pattern: bool,
    backend: String,
    models: Option<String>,
    ocr_test: Option<String>,
    det_limit: u32,
    ep: String,
    bench_preproc: bool,
    bench: usize,
    out: String,
    verbose: bool,
}

impl Args {
    fn parse() -> Args {
        let mut a = Args {
            pipe: None,
            parent: 0,
            hwnd: 0,
            selftest: false,
            list_outputs: false,
            pattern: false,
            backend: "auto".to_string(),
            models: None,
            ocr_test: None,
            det_limit: 960,
            ep: "cpu".to_string(),
            bench_preproc: false,
            bench: 0,
            out: "vision-selftest.bmp".to_string(),
            verbose: false,
        };
        let argv: Vec<String> = std::env::args().collect();
        let mut i = 1;
        while i < argv.len() {
            match argv[i].as_str() {
                "--pipe" if i + 1 < argv.len() => {
                    a.pipe = Some(argv[i + 1].clone());
                    i += 2;
                }
                "--parent" if i + 1 < argv.len() => {
                    a.parent = argv[i + 1].parse().unwrap_or(0);
                    i += 2;
                }
                "--hwnd" if i + 1 < argv.len() => {
                    a.hwnd = parse_hex_or_dec(&argv[i + 1]);
                    i += 2;
                }
                "--out" if i + 1 < argv.len() => {
                    a.out = argv[i + 1].clone();
                    i += 2;
                }
                "--bench" => {
                    let n = argv.get(i + 1).and_then(|s| s.parse::<usize>().ok());
                    a.bench = n.unwrap_or(60);
                    i += if n.is_some() { 2 } else { 1 };
                }
                "--selftest" => {
                    a.selftest = true;
                    i += 1;
                }
                "--list-outputs" => {
                    a.list_outputs = true;
                    i += 1;
                }
                "--pattern" => {
                    a.pattern = true;
                    i += 1;
                }
                "--backend" if i + 1 < argv.len() => {
                    a.backend = argv[i + 1].clone();
                    i += 2;
                }
                "--models" if i + 1 < argv.len() => {
                    a.models = Some(argv[i + 1].clone());
                    i += 2;
                }
                "--ocr-test" if i + 1 < argv.len() => {
                    a.ocr_test = Some(argv[i + 1].clone());
                    i += 2;
                }
                "--det-limit" if i + 1 < argv.len() => {
                    a.det_limit = argv[i + 1].parse().unwrap_or(960);
                    i += 2;
                }
                "--ep" if i + 1 < argv.len() => {
                    a.ep = argv[i + 1].clone();
                    i += 2;
                }
                "--bench-preproc" => {
                    a.bench_preproc = true;
                    i += 1;
                }
                "--console" => {
                    a.verbose = true;
                    i += 1;
                }
                _ => i += 1,
            }
        }
        a
    }
}

fn parse_hex_or_dec(s: &str) -> isize {
    let s = s.trim();
    if let Some(hex) = s.strip_prefix("0x").or_else(|| s.strip_prefix("0X")) {
        isize::from_str_radix(hex, 16).unwrap_or(0)
    } else {
        s.parse().unwrap_or(0)
    }
}

fn main() {
    let args = Args::parse();
    let code = match run(args) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("vision-svc 失败: {e}");
            eprintln!("错误链: {e:?}");
            2
        }
    };
    std::process::exit(code);
}

fn run(args: Args) -> Result<(), Box<dyn std::error::Error>> {
    if args.list_outputs {
        for line in capture::list_outputs()? {
            println!("{line}");
        }
        return Ok(());
    }
    if args.bench_preproc {
        return bench_preproc(&args);
    }
    if let Some(png) = args.ocr_test.clone() {
        return ocr_test(&args, &png);
    }
    if let Some(dir) = args.models.clone() {
        // 允许 --models 只做一次模型自检（不带 --ocr-test 时打印模型信息并退出）
        if args.pipe.is_none() {
            return model_info(&dir);
        }
    }
    if args.selftest {
        return selftest(&args);
    }
    if args.bench > 0 {
        return bench(&args);
    }
    let name = match args.pipe.clone() {
        Some(n) => n,
        None => {
            eprintln!("缺少 --pipe <name>（或使用 --selftest / --bench）");
            return Ok(());
        }
    };
    serve(&args, name)
}

// ---------------------------------------------------------------- 自检 / 基准

fn selftest(args: &Args) -> Result<(), Box<dyn std::error::Error>> {
    if args.pattern {
        return selftest_pattern(args);
    }
    let t0 = Instant::now();
    let mut cap = capture::Capturer::new(&args.backend, args.hwnd)?;
    println!(
        "捕获初始化: {:.1}ms (backend={}, hwnd=0x{:x})",
        t0.elapsed().as_secs_f64() * 1000.0,
        cap.backend_name(),
        args.hwnd
    );

    let t1 = Instant::now();
    let frame = loop {
        if let Some(f) = cap.next_frame(120)? {
            break f;
        }
        if t1.elapsed() > Duration::from_secs(3) {
            return Err("3 秒内没有拿到任何桌面帧（桌面锁定/独占全屏？）".into());
        }
    };
    println!(
        "首帧: {:.1}ms  {}x{} stride={}",
        t1.elapsed().as_secs_f64() * 1000.0,
        frame.w,
        frame.h,
        frame.stride
    );
    println!("诊断: {}", frame.dbg);
    let nz = frame.bgra.iter().filter(|b| **b != 0).count();
    println!(
        "像素非零字节: {}/{} ({:.2}%)",
        nz,
        frame.bgra.len(),
        100.0 * nz as f64 / frame.bgra.len().max(1) as f64
    );

    write_bmp(&args.out, frame.w, frame.h, frame.stride, &frame.bgra)?;
    println!("已写出 BMP: {}（供像素级对照）", args.out);

    let src = preproc::Bgra {
        data: &frame.bgra,
        w: frame.w,
        h: frame.h,
        stride: frame.stride,
    };
    let mut rgb = Vec::new();
    let t2 = Instant::now();
    let (scale, ox, oy, nw, nh) = preproc::letterbox_rgb(&src, 640, 640, 114, &mut rgb);
    println!(
        "letterbox 640x640: {:.2}ms  scale={scale:.4} offset=({ox},{oy}) inner={nw}x{nh}",
        t2.elapsed().as_secs_f64() * 1000.0
    );
    let mut chw = vec![0f32; 640 * 640 * 3];
    let t3 = Instant::now();
    preproc::rgb_to_chw_f32(&rgb, 640, 640, &mut chw);
    println!("CHW 归一化: {:.2}ms", t3.elapsed().as_secs_f64() * 1000.0);
    Ok(())
}

/// 不依赖桌面的自检：生成图案 → 写 BMP → 跑 letterbox/CHW，验证预处理链路与几何约定。
fn selftest_pattern(args: &Args) -> Result<(), Box<dyn std::error::Error>> {
    let (w, h) = (1280u32, 720u32);
    let t = Instant::now();
    let bgra = make_pattern(w, h, 3);
    println!("图案生成 {w}x{h}: {:.2}ms", t.elapsed().as_secs_f64() * 1000.0);
    write_bmp(&args.out, w, h, w * 4, &bgra)?;
    println!("已写出 BMP: {}", args.out);

    let src = preproc::Bgra { data: &bgra, w, h, stride: w * 4 };
    let mut rgb = Vec::new();
    let t2 = Instant::now();
    let (scale, ox, oy, nw, nh) = preproc::letterbox_rgb(&src, 640, 640, 114, &mut rgb);
    println!(
        "letterbox 640x640: {:.2}ms scale={scale:.4} offset=({ox},{oy}) inner={nw}x{nh}",
        t2.elapsed().as_secs_f64() * 1000.0
    );
    let mut chw = vec![0f32; 640 * 640 * 3];
    let t3 = Instant::now();
    preproc::rgb_to_chw_f32(&rgb, 640, 640, &mut chw);
    println!("CHW 归一化: {:.2}ms", t3.elapsed().as_secs_f64() * 1000.0);
    Ok(())
}

/// 预处理基准：与 C# ImageScaling.DownscaleBilinear 同尺寸同算法，方便 A/B。
fn bench_preproc(_args: &Args) -> Result<(), Box<dyn std::error::Error>> {
    let (w, h) = (3440u32, 1440u32);
    let bgra = make_pattern(w, h, 1);
    let src = preproc::Bgra { data: &bgra, w, h, stride: w * 4 };
    let n = 30usize;

    let mut rgb = Vec::new();
    preproc::letterbox_rgb(&src, 640, 640, 114, &mut rgb); // 预热
    let t0 = Instant::now();
    for _ in 0..n {
        preproc::letterbox_rgb(&src, 640, 640, 114, &mut rgb);
    }
    println!(
        "rust letterbox 3440x1440 -> 640x640 : {:.2} ms/次",
        t0.elapsed().as_secs_f64() * 1000.0 / n as f64
    );

    let mut out = Vec::new();
    preproc::resize_rgb(&src, 1720, 720, &mut out);
    let t1 = Instant::now();
    for _ in 0..n {
        preproc::resize_rgb(&src, 1720, 720, &mut out);
    }
    println!(
        "rust resize   3440x1440 -> 1720x720 : {:.2} ms/次",
        t1.elapsed().as_secs_f64() * 1000.0 / n as f64
    );

    let mut chw = vec![0f32; 640 * 640 * 3];
    let t2 = Instant::now();
    for _ in 0..n {
        preproc::rgb_to_chw_f32(&rgb, 640, 640, &mut chw);
    }
    println!(
        "rust CHW 归一化 640x640             : {:.2} ms/次",
        t2.elapsed().as_secs_f64() * 1000.0 / n as f64
    );
    Ok(())
}

fn bench(args: &Args) -> Result<(), Box<dyn std::error::Error>> {
    let mut cap = capture::Capturer::new(&args.backend, args.hwnd)?;
    for _ in 0..5 {
        let _ = cap.next_frame(500);
    }
    let mut samples = Vec::with_capacity(args.bench);
    for _ in 0..args.bench {
        let t = Instant::now();
        if cap.next_frame(200)?.is_some() {
            samples.push(t.elapsed().as_secs_f64() * 1000.0);
        }
    }
    samples.sort_by(|a, b| a.partial_cmp(b).unwrap());
    if samples.is_empty() {
        println!("没有采到样本（桌面无变化时 Desktop Duplication 不产出新帧，属正常）");
        return Ok(());
    }
    let mean: f64 = samples.iter().sum::<f64>() / samples.len() as f64;
    let p50 = samples[samples.len() / 2];
    let p99 = samples[(samples.len() as f64 * 0.99) as usize % samples.len()];
    println!(
        "抓帧 {} 帧：mean {mean:.2}ms  p50 {p50:.2}ms  p99 {p99:.2}ms  min {:.2}  max {:.2}",
        samples.len(),
        samples[0],
        samples[samples.len() - 1]
    );
    Ok(())
}

/// 写出 32 位 BMP（top-down）。只用于样本导出/像素级对照，不参与运行时。
fn write_bmp(path: &str, w: u32, h: u32, stride: u32, bgra: &[u8]) -> std::io::Result<()> {
    use std::io::Write;
    let image_bytes = (w as usize) * (h as usize) * 4;
    let file_size = 14 + 40 + image_bytes;
    let mut f = std::fs::File::create(path)?;
    let mut header = Vec::with_capacity(54);
    header.extend_from_slice(b"BM");
    header.extend_from_slice(&(file_size as u32).to_le_bytes());
    header.extend_from_slice(&0u32.to_le_bytes());
    header.extend_from_slice(&54u32.to_le_bytes());
    header.extend_from_slice(&40u32.to_le_bytes());
    header.extend_from_slice(&(w as i32).to_le_bytes());
    header.extend_from_slice(&(-(h as i32)).to_le_bytes());
    header.extend_from_slice(&1u16.to_le_bytes());
    header.extend_from_slice(&32u16.to_le_bytes());
    header.extend_from_slice(&0u32.to_le_bytes());
    header.extend_from_slice(&(image_bytes as u32).to_le_bytes());
    header.extend_from_slice(&2835i32.to_le_bytes());
    header.extend_from_slice(&2835i32.to_le_bytes());
    header.extend_from_slice(&0u32.to_le_bytes());
    header.extend_from_slice(&0u32.to_le_bytes());
    f.write_all(&header)?;
    let row_bytes = w as usize * 4;
    for y in 0..h as usize {
        let start = y * stride as usize;
        f.write_all(&bgra[start..start + row_bytes])?;
    }
    f.flush()
}

// ---------------------------------------------------------------- 服务模式

struct Snap {
    bgra: Vec<u8>,
    w: u32,
    h: u32,
    stride: u32,
}

struct Shared {
    hwnd: Mutex<isize>,
    fps: AtomicU32,
    backend: Mutex<String>,
    ring: Arc<Mutex<Option<FrameRing>>>,
    /// 最近一帧（供 ocr/detect 用）；want_frames=0 时不写入，避免白拷。
    last: Mutex<Option<StdArc<Snap>>>,
    want_frames: AtomicBool,
    ocr: Mutex<Option<infer::OcrEngine>>,
    models_dir: Mutex<String>,
    up: Mutex<String>,
    up_reader: Mutex<Option<shm::FrameRingReader>>,
    yolo: Mutex<Option<infer::YoloEngine>>,
}

/// 让 ort 找到 onnxruntime.dll：环境变量优先，其次 exe 同级、models 同级、仓库 .NET 输出目录。
/// ORT 线程数：实机 8 最好（>8 会因 SMT 过度订阅明显变慢：实测 16 线程 2.2s vs 8 线程 0.58s）。
fn ocr_threads() -> usize {
    8
}

fn ensure_ort_dylib() {
    if std::env::var_os("ORT_DYLIB_PATH").is_some() {
        return;
    }
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_default();
    let candidates = [
        exe_dir.join("onnxruntime.dll"),
        exe_dir.join("..").join("..").join("src").join("EfootballBot").join("bin").join("Debug")
            .join("net10.0-windows10.0.19041.0").join("win-x64").join("onnxruntime.dll"),
    ];
    for c in candidates {
        if c.exists() {
            std::env::set_var("ORT_DYLIB_PATH", c);
            return;
        }
    }
}

fn model_info(dir: &str) -> Result<(), Box<dyn std::error::Error>> {
    // 打印 det/rec 的输入输出名与形状，确认输出布局（[1,C,T] 还是 [1,T,C]）
    ensure_ort_dylib();
    {
        let env = infer::env();
        for (tag, path) in [("det", &infer::ModelPaths::from_dir(dir).det), ("rec", &infer::ModelPaths::from_dir(dir).rec)] {
            match ort::session::Session::builder(env)?.commit_from_file(path) {
                Ok(s) => {
                    let ins: Vec<String> = s.inputs().iter().map(|o| format!("{}", o.name())).collect();
                    let outs: Vec<String> = s.outputs().iter().map(|o| format!("{}", o.name())).collect();
                    println!("{tag}: inputs={ins:?} outputs={outs:?}");
                }
                Err(e) => println!("{tag}: 加载失败 {e}"),
            }
        }
    }
    ensure_ort_dylib();
    let paths = infer::ModelPaths::from_dir(dir);
    let t = Instant::now();
    let eng = infer::OcrEngine::new(&paths, 960, 4, infer::Ep::Cpu)?;
    println!(
        "模型加载成功 {:.0}ms  det={}  rec={}  dict={} 条  yolo={:?}",
        t.elapsed().as_secs_f64() * 1000.0,
        paths.det,
        paths.rec,
        eng.dict_len(),
        paths.yolo
    );
    Ok(())
}

fn ocr_test(args: &Args, png: &str) -> Result<(), Box<dyn std::error::Error>> {
    ensure_ort_dylib();
    let dir = args
        .models
        .clone()
        .unwrap_or_else(|| "B:\\Efootball挂机软件\\src\\EfootballBot\\models".to_string());
    let paths = infer::ModelPaths::from_dir(&dir);
    let t0 = Instant::now();
    let mut eng = infer::OcrEngine::new(&paths, args.det_limit, ocr_threads(), infer::Ep::from_name(&args.ep))?;
    println!("模型加载 {:.0}ms", t0.elapsed().as_secs_f64() * 1000.0);

    let (bgra, w, h) = crate::imgio::load_png_bgra(png)?;
    println!("图片 {png} {w}x{h}");
    let src = preproc::Bgra { data: &bgra, w, h, stride: w * 4 };

    // 预热一帧（首次运行含 ORT 内部分配）
    let _ = eng.recognize(&src)?;

    let t1 = Instant::now();
    let words = eng.recognize(&src)?;
    let total = t1.elapsed().as_secs_f64() * 1000.0;
    println!(
        "OCR: {} 词  det {:.1}ms  rec {:.1}ms  合计 {:.1}ms  框 {:.0} 个  rec_out_dims={:?} dict={}",
        words.len(),
        eng.last_det_ms,
        eng.last_rec_ms,
        total,
        eng.last_boxes,
        eng.last_dims,
        eng.dict_len()
    );
    for wdoc in words.iter().take(60) {
        println!(
            "  [{:.3},{:.3} {:.3}x{:.3}] {:.2}  {}",
            wdoc.x, wdoc.y, wdoc.w, wdoc.h, wdoc.score, wdoc.text
        );
    }
    Ok(())
}

fn serve(args: &Args, name: String) -> Result<(), Box<dyn std::error::Error>> {
    let mut conn = pipe::PipeConn::connect(&name, Duration::from_secs(20))?;
    pipe::spawn_parent_watchdog(args.parent);

    let mut ready = Msg::ev("ready");
    ready.pid = Some(std::process::id());
    ready.version = Some(VERSION.to_string());
    conn.send(&ready.to_line())?;
    if args.verbose {
        eprintln!(
            "vision-svc {VERSION} 已连接管道 {name}，PID {}",
            std::process::id()
        );
    }

    ensure_ort_dylib();
    let (ev_tx, ev_rx) = mpsc::channel::<Msg>();
    let shared = Arc::new(Shared {
        hwnd: Mutex::new(args.hwnd),
        fps: AtomicU32::new(30),
        backend: Mutex::new(args.backend.clone()),
        ring: Arc::new(Mutex::new(None)),
        last: Mutex::new(None),
        want_frames: AtomicBool::new(false),
        ocr: Mutex::new(None),
        models_dir: Mutex::new(args.models.clone().unwrap_or_default()),
        up: Mutex::new(String::new()),
        up_reader: Mutex::new(None),
        yolo: Mutex::new(None),
    });
    let mut cap_stop: Option<Arc<AtomicBool>> = None;

    loop {
        // 每轮先把捕获线程产出的事件全部写出去（写不与读互相阻塞，见 pipe.rs 顶部说明）
        while let Ok(ev) = ev_rx.try_recv() {
            conn.send(&ev.to_line())?;
        }

        let mut shutdown = false;
        let mut next = conn.poll_line()?;
        while let Some(line) = next {
            if args.verbose {
                eprintln!("recv: {line}");
            }

            let msg: Msg = match serde_json::from_str(&line) {
                Ok(m) => m,
                Err(e) => {
                    conn.send(&Msg::err(None, format!("JSON 解析失败: {e}")).to_line())?;
                    next = conn.poll_line()?;
                    continue;
                }
            };

            let cmd = msg.cmd.clone().unwrap_or_default();
            let id = msg.id;
            match cmd.as_str() {
                "ping" => {
                    let mut pong = Msg::ev("pong");
                    pong.id = id;
                    conn.send(&pong.to_line())?;
                }
                "init" => {
                    let shm_name = msg
                        .shm
                        .clone()
                        .unwrap_or_else(|| format!("Local\\EfootballVision-v1-{name}"));
                    if let Some(dir) = msg.models.clone() {
                        *shared.models_dir.lock().unwrap() = dir;
                    }
                    if let Some(up) = msg.up.clone() {
                        *shared.up.lock().unwrap() = up;
                        *shared.up_reader.lock().unwrap() = None;
                    }
                    let slots = msg.slots.unwrap_or(2).max(2);
                    // 槽位按「请求尺寸 ∪ 桌面最大输出」开：DXGI 抓的是整屏，
                    // 窗口全屏时帧比请求尺寸大，槽位太小会被 publish 截断成废帧。
                    let (out_w, out_h) = capture::desktop_size().unwrap_or((0, 0));
                    let w = msg.w.unwrap_or(1920).max(out_w);
                    let h = msg.h.unwrap_or(1080).max(out_h);
                    let slot_bytes = (w as usize) * (h as usize) * 4;
                    if args.verbose {
                        eprintln!("init: {w}x{h} slots={slots} slot_bytes={slot_bytes} name={shm_name}");
                    }
                    match FrameRing::create(&shm_name, slot_bytes, slots) {
                        Ok(ring) => {
                            *shared.ring.lock().unwrap() = Some(ring);
                            let mut ack = Msg::ack(id, None);
                            ack.version = Some(VERSION.to_string());
                            ack.shm = Some(shm_name.clone());
                            ack.w = Some(w);
                            ack.h = Some(h);
                            ack.slots = Some(slots);
                            conn.send(&ack.to_line())?;
                        }
                        Err(e) => {
                            conn.send(&Msg::err(id, e.to_string()).to_line())?;
                        }
                    }
                }
                "start" => {
                    if let Some(hwnd) = msg.hwnd {
                        *shared.hwnd.lock().unwrap() = hwnd as isize;
                    }
                    if let Some(b) = msg.backend.clone() {
                        *shared.backend.lock().unwrap() = b;
                    }
                    if let Some(fps) = msg.fps {
                        shared.fps.store(fps.clamp(1, 120), Ordering::Relaxed);
                    }
                    if shared.ring.lock().unwrap().is_none() {
                        conn.send(&Msg::err(id, "未 init：帧环不存在").to_line())?;
                    } else {
                        if let Some(stop) = cap_stop.take() {
                            stop.store(true, Ordering::Release);
                        }
                        let stop = Arc::new(AtomicBool::new(false));
                        spawn_capture_thread(shared.clone(), stop.clone(), ev_tx.clone(), args.verbose);
                        cap_stop = Some(stop);
                        conn.send(&Msg::ack(id, None).to_line())?;
                    }
                }
                "stop" => {
                    if let Some(stop) = cap_stop.take() {
                        stop.store(true, Ordering::Release);
                    }
                    conn.send(&Msg::ack(id, None).to_line())?;
                }
                "grab" => {
                    let hwnd = msg
                        .hwnd
                        .map(|v| v as isize)
                        .unwrap_or(*shared.hwnd.lock().unwrap());
                    let t = Instant::now();
                    let backend = msg.backend.clone().unwrap_or_else(|| shared.backend.lock().unwrap().clone());
                    match capture::Capturer::new(&backend, hwnd) {
                        Ok(mut cap) => {
                            let mut got = None;
                            for _ in 0..30 {
                                if let Some(f) = cap.next_frame(60)? {
                                    got = Some(f);
                                    break;
                                }
                            }
                            // auto 模式：DXGI 抓到全黑且窗口有效 → 换 WGC 再抓一次
                            if backend == "auto" && hwnd != 0 {
                                if let Some(f) = &got {
                                    if capture::mostly_black(f) {
                                        if let Ok(mut w) = capture::Capturer::new("wgc", hwnd) {
                                            if let Ok(Some(f2)) = w.next_frame(300) {
                                                got = Some(f2);
                                            }
                                        }
                                    }
                                }
                            }
                            match got {
                                Some(f) => {
                                    let seq = {
                                        let mut g = shared.ring.lock().unwrap();
                                        match g.as_mut() {
                                            Some(ring) => ring.publish(f.w, f.h, f.stride, &f.bgra),
                                            None => 0,
                                        }
                                    };
                                    let mut ev = Msg::ev("frame");
                                    ev.id = id;
                                    ev.seq = Some(seq);
                                    ev.w = Some(f.w);
                                    ev.h = Some(f.h);
                                    ev.stride = Some(f.stride);
                                    ev.slot = Some((((seq / 2) % 2) as u32).min(1));
                                    ev.ms = Some(t.elapsed().as_secs_f64() * 1000.0);
                                    ev.dbg = Some(f.dbg);
                                    conn.send(&ev.to_line())?;
                                }
                                None => {
                                    conn.send(&Msg::err(id, "60ms×30 次内没有新帧").to_line())?;
                                }
                            }
                        }
                        Err(e) => {
                            conn.send(&Msg::err(id, e.to_string()).to_line())?;
                        }
                    }
                }
                "inject" => {
                    let w = msg.w.unwrap_or(640);
                    let h = msg.h.unwrap_or(360);
                    let photo = make_pattern(w, h, 1);
                    let seq = {
                        let mut g = shared.ring.lock().unwrap();
                        match g.as_mut() {
                            Some(ring) => ring.publish(w, h, w * 4, &photo),
                            None => 0,
                        }
                    };
                    let mut ev = Msg::ev("frame");
                    ev.id = id;
                    ev.seq = Some(seq);
                    ev.w = Some(w);
                    ev.h = Some(h);
                    ev.stride = Some(w * 4);
                    ev.slot = Some((((seq / 2) % 2) as u32).min(1));
                    ev.ms = Some(0.0);
                    conn.send(&ev.to_line())?;
                }
                "ocr" => {
                    let t = Instant::now();
                    let dir = shared.models_dir.lock().unwrap().clone();
                    if dir.is_empty() {
                        conn.send(&Msg::err(id, "未提供 models 目录（init.models）").to_line())?;
                    } else {
                        {
                            let mut eng = shared.ocr.lock().unwrap();
                            if eng.is_none() {
                                let paths = infer::ModelPaths::from_dir(&dir);
                                match infer::OcrEngine::new(&paths, 960, ocr_threads(), infer::Ep::from_name(&args.ep)) {
                                    Ok(e) => *eng = Some(e),
                                    Err(e) => {
                                        conn.send(&Msg::err(id, format!("模型加载失败: {e}")).to_line())?;
                                        next = conn.poll_line()?;
                                        continue;
                                    }
                                }
                            }
                        }
                        // src=capture：直接用 sidecar 刚捕获的帧（省一次上传拷贝）；
                        // 否则用 C# 上传的帧（任意 FrameData / 裁切块），再退回最近捕获帧
                        let from_capture = msg.src.as_deref() == Some("capture");
                        // src=capture：直接读 sidecar 自己帧环里的最新帧（零上传、零额外快照）
                        let from_ring: Option<(Vec<u8>, u32, u32, u32)> = if from_capture {
                            let mut g = shared.ring.lock().unwrap();
                            g.as_mut().and_then(|r| r.read_latest())
                        } else {
                            None
                        };
                        let uploaded: Option<(Vec<u8>, u32, u32, u32)> = if from_capture {
                            None
                        } else {
                            let up_name = shared.up.lock().unwrap().clone();
                            if up_name.is_empty() {
                                None
                            } else {
                                let mut guard = shared.up_reader.lock().unwrap();
                                if guard.is_none() {
                                    match shm::FrameRingReader::open(&up_name) {
                                        Ok(r) => *guard = Some(r),
                                        Err(_) => {}
                                    }
                                }
                                guard.as_mut().and_then(|r| r.read(msg.up_seq.unwrap_or(0)))
                            }
                        };
                        let snap = match from_ring.or(uploaded) {
                            Some((bgra, w, h, stride)) => Some((bgra, w, h, stride)),
                            None => shared
                                .last
                                .lock()
                                .unwrap()
                                .clone()
                                .map(|s| (s.bgra.clone(), s.w, s.h, s.stride)),
                        };
                        shared.want_frames.store(true, Ordering::Relaxed);
                        match snap {
                            None => {
                                conn.send(&Msg::err(id, "还没有可用帧（先 start/grab，或通过 up 上传帧）").to_line())?;
                            }
                            Some(s) => {
                                let img = preproc::Bgra { data: &s.0, w: s.1, h: s.2, stride: s.3 };
                                let mut eng = shared.ocr.lock().unwrap();
                                let engine = eng.as_mut().unwrap();
                                let res = engine.recognize(&img);
                                let (det_ms, rec_ms) = (engine.last_det_ms, engine.last_rec_ms);
                                match res {
                                    Ok(words) => {
                                        let mut ev = Msg::ev("ocr");
                                        ev.id = id;
                                        ev.ms = Some(t.elapsed().as_secs_f64() * 1000.0);
                                        ev.w = Some(s.1);
                                        ev.h = Some(s.2);
                                        ev.text = Some(format!("det {det_ms:.1}ms rec {rec_ms:.1}ms"));
                                        ev.words = Some(
                                            words
                                                .iter()
                                                .map(|w| proto::Word {
                                                    t: w.text.clone(),
                                                    x: w.x,
                                                    y: w.y,
                                                    w: w.w,
                                                    h: w.h,
                                                    s: w.score,
                                                })
                                                .collect(),
                                        );
                                        conn.send(&ev.to_line())?;
                                    }
                                    Err(e) => {
                                        conn.send(&Msg::err(id, format!("OCR 失败: {e}")).to_line())?;
                                    }
                                }
                            }
                        }
                    }
                }
                "detect" => {
                    let t = Instant::now();
                    let dir = shared.models_dir.lock().unwrap().clone();
                    let from_capture = msg.src.as_deref() == Some("capture");
                    let from_ring: Option<(Vec<u8>, u32, u32, u32)> = if from_capture {
                        let mut g = shared.ring.lock().unwrap();
                        g.as_mut().and_then(|r| r.read_latest())
                    } else {
                        None
                    };
                    let snap = {
                        let up_name = shared.up.lock().unwrap().clone();
                        let uploaded: Option<(Vec<u8>, u32, u32, u32)> = if from_capture || up_name.is_empty() {
                            None
                        } else {
                            let mut guard = shared.up_reader.lock().unwrap();
                            if guard.is_none() {
                                if let Ok(r) = shm::FrameRingReader::open(&up_name) {
                                    *guard = Some(r);
                                }
                            }
                            guard.as_mut().and_then(|r| r.read(msg.up_seq.unwrap_or(0)))
                        };
                        from_ring.or(uploaded).or_else(|| {
                            shared.last.lock().unwrap().clone().map(|s| (s.bgra.clone(), s.w, s.h, s.stride))
                        })
                    };
                    match (snap, dir.is_empty()) {
                        (None, _) => {
                            conn.send(&Msg::err(id, "还没有可用帧").to_line())?;
                        }
                        (Some(_), true) => {
                            conn.send(&Msg::err(id, "未提供 models 目录（init.models）").to_line())?;
                        }
                        (Some(s), false) => {
                            {
                                let mut y = shared.yolo.lock().unwrap();
                                if y.is_none() {
                                    let paths = infer::ModelPaths::from_dir(&dir);
                                    let model = match paths.yolo.clone() {
                                        Some(m) => m,
                                        None => {
                                            conn.send(&Msg::err(id, "models/yolo/yolo26n.onnx 不存在").to_line())?;
                                            next = conn.poll_line()?;
                                            continue;
                                        }
                                    };
                                    match infer::YoloEngine::new(&model, paths.classes.as_deref(), ocr_threads(), infer::Ep::from_name(&args.ep)) {
                                        Ok(e) => *y = Some(e),
                                        Err(e) => {
                                            conn.send(&Msg::err(id, format!("YOLO 加载失败: {e}")).to_line())?;
                                            next = conn.poll_line()?;
                                            continue;
                                        }
                                    }
                                }
                            }
                            let img = preproc::Bgra { data: &s.0, w: s.1, h: s.2, stride: s.3 };
                            let mut y = shared.yolo.lock().unwrap();
                            let engine = y.as_mut().unwrap();
                            let res = engine.detect(&img, 0.25, 0.45);
                            let ms = engine.last_ms;
                            match res {
                                Ok(dets) => {
                                    let mut ev = Msg::ev("detect");
                                    ev.id = id;
                                    ev.ms = Some(t.elapsed().as_secs_f64() * 1000.0);
                                    ev.text = Some(format!(
                                        "yolo {ms:.1}ms {} 框 dims={:?} max_score={:.3}",
                                        dets.len(),
                                        engine.last_dims,
                                        engine.last_max_score
                                    ));
                                    ev.boxes = Some(
                                        dets.iter()
                                            .map(|d| proto::Detection {
                                                c: d.c,
                                                s: d.s,
                                                x: d.x,
                                                y: d.y,
                                                w: d.w,
                                                h: d.h,
                                            })
                                            .collect(),
                                    );
                                    conn.send(&ev.to_line())?;
                                }
                                Err(e) => {
                                    conn.send(&Msg::err(id, format!("YOLO 失败: {e}")).to_line())?;
                                }
                            }
                        }
                    }
                }
                "preview" => {
                    conn.send(&Msg::ack(id, None).to_line())?;
                }
                "shutdown" => {
                    if let Some(stop) = cap_stop.take() {
                        stop.store(true, Ordering::Release);
                    }
                    conn.send(&Msg::ack(id, None).to_line())?;
                    shutdown = true;
                }
                other => {
                    conn.send(
                        &Msg::err(id, format!("未知命令 {other}（vision-svc {VERSION}）")).to_line(),
                    )?;
                }
            }

            if shutdown {
                break;
            }
            next = conn.poll_line()?;
        }
        if shutdown {
            return Ok(());
        }

        std::thread::sleep(Duration::from_millis(2));
    }
}

/// 测试图案：斜向渐变 + 随帧滚动的竖条（用于在没有可用桌面复制的环境里
/// 验证「管道 → 帧环 → C# 读帧 → 预处理」整条链路，不依赖 GPU/显示器）。
fn make_pattern(w: u32, h: u32, phase: u32) -> Vec<u8> {
    let mut v = vec![0u8; (w as usize) * (h as usize) * 4];
    for y in 0..h {
        for x in 0..w {
            let i = ((y * w + x) * 4) as usize;
            let bar = ((x + phase * 8) / 64) % 2 == 0;
            let r = if bar { (x * 255 / w.max(1)) as u8 } else { 32 };
            let g = (y * 255 / h.max(1)) as u8;
            let b = if bar { 160 } else { 16 };
            v[i] = b;
            v[i + 1] = g;
            v[i + 2] = r;
            v[i + 3] = 255;
        }
    }
    v
}

fn spawn_capture_thread(
    shared: Arc<Shared>,
    stop: Arc<AtomicBool>,
    ev_tx: Sender<Msg>,
    verbose: bool,
) {
    std::thread::spawn(move || {
        let hwnd = *shared.hwnd.lock().unwrap();
        let requested = shared.backend.lock().unwrap().clone();

        let mut cap = match capture::Capturer::new(&requested, hwnd) {
            Ok(c) => c,
            Err(e) => {
                let _ = ev_tx.send(Msg::log("Error", format!("捕获初始化失败: {e}")));
                return;
            }
        };

        // auto 模式：DXGI 出一片黑（虚拟显示器/远程会话）就切 WGC 窗口捕获
        let auto = requested != "dxgi" && requested != "wgc";
        if auto && matches!(cap, capture::Capturer::Dxgi(_)) && hwnd != 0 {
            let mut black = 0u32;
            for _ in 0..4 {
                match cap.next_frame(120) {
                    Ok(Some(f)) => {
                        if capture::mostly_black(&f) {
                            black += 1;
                        } else {
                            break;
                        }
                    }
                    _ => {}
                }
            }
            if black >= 2 {
                let _ = ev_tx.send(Msg::log(
                    "Warn",
                    "DXGI 抓到的画面接近全黑（虚拟显示器/远程会话/被遮挡），自动切 WGC 窗口捕获".to_string(),
                ));
                if let Ok(w) = capture::Capturer::new("wgc", hwnd) {
                    cap = w;
                }
            }
        }

        if verbose {
            let _ = ev_tx.send(Msg::log(
                "Info",
                format!("捕获线程启动 backend={} hwnd=0x{hwnd:x}", cap.backend_name()),
            ));
        }

        while !stop.load(Ordering::Acquire) {
            let fps = shared.fps.load(Ordering::Relaxed).max(1);
            let timeout = (1000 / fps).max(1);
            match cap.next_frame(timeout) {
                Ok(Some(f)) => {
                    let seq = {
                        let mut g = shared.ring.lock().unwrap();
                        match g.as_mut() {
                            Some(ring) => ring.publish(f.w, f.h, f.stride, &f.bgra),
                            None => 0,
                        }
                    };
                    if shared.want_frames.load(Ordering::Relaxed) {
                        *shared.last.lock().unwrap() = Some(StdArc::new(Snap {
                            bgra: f.bgra.clone(),
                            w: f.w,
                            h: f.h,
                            stride: f.stride,
                        }));
                    }
                    let mut ev = Msg::ev("frame");
                    ev.seq = Some(seq);
                    ev.w = Some(f.w);
                    ev.h = Some(f.h);
                    ev.stride = Some(f.stride);
                    ev.slot = Some((((seq / 2) % 2) as u32).min(1));
                    ev.backend = Some(cap.backend_name().to_string());
                    if verbose {
                        ev.dbg = Some(f.dbg.clone());
                    }
                    if ev_tx.send(ev).is_err() {
                        break;
                    }
                }
                Ok(None) => {}
                Err(e) => {
                    let _ = ev_tx.send(Msg::log("Warn", format!("捕获异常，重建: {e}")));
                    let hwnd2 = *shared.hwnd.lock().unwrap();
                    let req = shared.backend.lock().unwrap().clone();
                    match capture::Capturer::new(&req, hwnd2) {
                        Ok(c) => cap = c,
                        Err(e2) => {
                            let _ = ev_tx.send(Msg::log("Error", format!("重建失败: {e2}")));
                            std::thread::sleep(Duration::from_millis(500));
                        }
                    }
                }
            }
        }
        let _ = ev_tx.send(Msg::log("Info", "捕获线程退出".to_string()));
    });
}
