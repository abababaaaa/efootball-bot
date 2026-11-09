//! GUI/Worker ↔ vision-svc 的 JSON Lines 协议（与 C# 侧 `VisionSidecarClient` 一一对应）。
//!
//! 设计原则：单条扁平消息结构（全部字段可选），与现有 `Ipc.PipeProtocol.Msg` 同风格，
//! C# 用 System.Text.Json 直接反序列化，不做联合类型判别。

use serde::{Deserialize, Serialize};

fn is_false(b: &bool) -> bool {
    !*b
}

/// 一条消息（命令或事件）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Msg {
    // ---- 命令（GUI → vision-svc）----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cmd: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<u32>,

    // ---- 事件（vision-svc → GUI）----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ev: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub level: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,

    // ---- init / 帧环 ----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shm: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub slots: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fps: Option<u32>,
    /// C# 上传帧的共享内存名（OCR/detect 作用在任意 FrameData 上时用）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub up: Option<String>,
    /// 帧来源："capture" = 直接用 sidecar 刚捕获的那帧（跳过上传拷贝）；缺省 = 上传帧
    #[serde(skip_serializing_if = "Option::is_none")]
    pub src: Option<String>,
    /// 上传帧的序号（0/缺省 = 用 sidecar 最近捕获帧）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub up_seq: Option<u64>,
    /// models 目录（v6/*.onnx 与 yolo/*）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub models: Option<String>,
    /// 捕获后端：auto / dxgi / wgc
    #[serde(skip_serializing_if = "Option::is_none")]
    pub backend: Option<String>,

    // ---- 帧/推理结果 ----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seq: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub w: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub h: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stride: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub slot: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ts: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ms: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,

    // ---- OCR ----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub words: Option<Vec<Word>>,

    // ---- YOLO ----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub boxes: Option<Vec<Detection>>,

    // ---- 区域参数（ocr/detect 请求）----
    #[serde(skip_serializing_if = "Option::is_none")]
    pub region: Option<[f32; 4]>,

    /// 诊断文本（捕获后处理信息：行距/指针/非零样本数等）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dbg: Option<String>,

    // ---- 预览 ----
    #[serde(default, skip_serializing_if = "is_false")]
    pub on: bool,
}

/// 一个 OCR 词块，坐标归一化到 0..1（与 C# `OcrWord` 同语义）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Word {
    /// 文本
    pub t: String,
    /// 左上角 + 宽高，归一化
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    /// 置信度
    #[serde(default)]
    pub s: f32,
}

/// 一个检测框，坐标归一化到 0..1（与 C# `YoloDetection` 同语义）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Detection {
    /// 类别 id
    pub c: i32,
    /// 置信度
    pub s: f32,
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

impl Msg {
    pub fn ev(name: &str) -> Msg {
        Msg { ev: Some(name.to_string()), ..Default::default() }
    }

    pub fn log(level: &str, text: impl Into<String>) -> Msg {
        Msg {
            ev: Some("log".into()),
            level: Some(level.into()),
            text: Some(text.into()),
            ..Default::default()
        }
    }

    pub fn ack(id: Option<u32>, ms: Option<f64>) -> Msg {
        Msg { ev: Some("ack".into()), id, ms, ..Default::default() }
    }

    pub fn err(id: Option<u32>, message: impl Into<String>) -> Msg {
        Msg {
            ev: Some("error".into()),
            id,
            text: Some(message.into()),
            ..Default::default()
        }
    }

    pub fn to_line(&self) -> String {
        let mut s = serde_json::to_string(self).unwrap_or_else(|_| "{\"ev\":\"error\"}".into());
        s.push('\n');
        s
    }
}
