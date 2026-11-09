# rust/ — vision-svc（Rust 视觉 sidecar）

## 构建

```bat
cd rust
cargo build --release
```

产物：`rust/target/release/vision-svc.exe`（单文件，无托管运行时）。

部署时把它放到主程序 exe 同级（发布脚本会自动拷贝），或配置 `VisionSidecar.ExePath`。

## 三种运行方式

```bat
:: 1) 单帧自检：抓主屏一帧 → 写 BMP → 跑一遍 letterbox/CHW 冒烟
vision-svc.exe --selftest --out selftest.bmp

:: 2) 抓帧基准（Desktop Duplication 只在画面变化时出帧，测试时让鼠标动起来）
vision-svc.exe --bench 120

:: 3) 服务模式：由 GUI/Worker 建命名管道 server 后拉起
vision-svc.exe --pipe EfootballBot-Vision-xxx --parent 12345 --console
```

## 协议

JSON Lines over 命名管道（`\\.\pipe\EfootballBot-Vision-<guid>`），一条一行 UTF-8。
所有字段可空，与 C# `VisionMsg` 一一对应；帧像素走共享内存，不占管道带宽。

| 命令 | 参数 | 响应 |
|---|---|---|
| `ping` | — | `ev=pong` |
| `init` | `shm` `slots` `w` `h` `hwnd` | `ev=ack`（回带 shm/w/h/slots） |
| `start` | `hwnd` `fps` | `ev=ack`，之后持续 `ev=frame` |
| `stop` | — | `ev=ack` |
| `grab` | `hwnd?` | `ev=frame`（一次性） |
| `shutdown` | — | `ev=ack` 后进程退出 |

事件：`ready`（连上）/ `frame`（seq,w,h,stride,slot,ms）/ `log`（level,text）/ `error`（id,text）。

## 共享内存帧环（`src/shm.rs`）

```text
off  0 u32 magic 0x5649534E   off  4 u32 version 1
off  8 u32 width               off 12 u32 height
off 16 u32 stride              off 20 u32 slot_count
off 24 u32 slot_bytes          off 28 u32 flags
off 32 u64 seq（奇数=写入中）  off 40 u64 ts_ms
off 48 u64 captures           off 56 u64 drops
off 64 槽位 0..N-1
```

读侧（C# `SharedFrameRing`）：只读偶数 seq → 按 `(seq/2) % slots` 取槽 → 复核 seq 未变。
写快于读时读到旧帧而不是撕裂帧。

## 已实现 / 未实现

- [x] DXGI Desktop Duplication 捕获（含 R10G10B10A2 / FP16 HDR 降位）
- [x] 客户区裁剪（只传目标窗口那一块，不搬整屏）
- [x] 命名共享内存双缓冲帧环 + JSON Lines 管道协议
- [x] 双线性缩放 / letterbox / CHW 归一化（含单测）
- [x] C# 客户端：进程拉起、握手、帧环读取、`ICaptureProvider` 适配、BotEngine 配置开关 + 失败回退
- [x] 设置页开关 + 引擎接入（`VisionSidecar.Enabled`）
- [ ] ONNX Runtime（`ort`）接入：PP-OCR det/cls/rec + YOLO26n（下一里程碑，见 docs/plans/2026-11-08-rust-vision-sidecar.md）
- [ ] GPU EP（DirectML / TensorRT）与 GPU 预处理
