# -*- coding: utf-8 -*-
"""PP-OCRv6 small ONNX int8 量化脚本(离线工具,不随程序发布)。

产出 src/EfootballBot/models/v6/int8/ 下的双模型:
  - PP-OCRv6_rec_small_int8.onnx : 动态量化(MatMul/Gemm 权重 int8,激活运行时量化)
  - PP-OCRv6_det_small_int8.onnx : 静态 QDQ(Conv/MatMul/Gemm,MinMax 校准)
词表/cls 不量化,继续复用 v6 根目录与 v5 cls。

用法(仓库根 tools/ 下):
  python/python.exe quantize_ocr_int8.py \
    --models-dir ../src/EfootballBot/models/v6 \
    --calib-roots ../tests/EfootballBot.Tests/TestData ../training/dataset/images \
    --out-dir ../src/EfootballBot/models/v6/int8

精度把关:量化完成后由 C# 侧全量 RealFrame 测试(默认即加载 int8)+ 关键词对照测试回归。
"""
import argparse
import glob
import os
import sys

import numpy as np
from PIL import Image

from onnxruntime.quantization import (CalibrationDataReader, CalibrationMethod,
                                      QuantFormat, QuantType, quantize_dynamic,
                                      quantize_static, quant_pre_process)
import onnxruntime as ort


def collect_images(roots):
    exts = ("*.png", "*.jpg", "*.jpeg")
    files = []
    for root in roots:
        for e in exts:
            files.extend(glob.glob(os.path.join(root, "**", e), recursive=True))
    return sorted(files)


def det_preprocess(img_bgr_or_rgb, limit_side_len=736):
    """对齐 PP-OCR det 预处理:短边限长 736 等比缩放 + 边长取整到 32 的倍数 + /127.5-1 + CHW。
    边长必须 /32:DBNet 下采样 32x,非对齐尺寸会撞 ORT Resize 节点形状复用 bug
    ({1,96,184,327} != {1,96,184,328} 形态的 FAIL)。"""
    h, w = img_bgr_or_rgb.shape[:2]
    if min(h, w) < limit_side_len:
        if h < w:
            ratio = limit_side_len / float(h)
        else:
            ratio = limit_side_len / float(w)
    else:
        ratio = 1.0
    nh = max(32, int(np.ceil(h * ratio / 32.0)) * 32)
    nw = max(32, int(np.ceil(w * ratio / 32.0)) * 32)
    pil = Image.fromarray(img_bgr_or_rgb).resize((nw, nh), Image.BILINEAR)
    arr = np.asarray(pil, dtype=np.float32)  # HWC RGB
    arr = (arr / 127.5) - 1.0
    chw = arr.transpose(2, 0, 1)[np.newaxis, ...]  # 1,3,H,W
    return np.ascontiguousarray(chw)


class DetCalibReader(CalibrationDataReader):
    def __init__(self, image_files, model_path, limit=200):
        sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
        self.input_name = sess.get_inputs()[0].name
        self.items = [
            {self.input_name: det_preprocess(np.asarray(Image.open(f).convert("RGB")))}
            for f in image_files[:limit]
        ]
        self.idx = 0

    def get_next(self):
        if self.idx >= len(self.items):
            return None
        it = self.items[self.idx]
        self.idx += 1
        return it


def ctc_greedy(logits, dict_chars, blank=0):
    """logits [T, C] -> 贪心 CTC 解码文本(压缩重复、去 blank)。"""
    idx = np.argmax(logits, axis=-1)
    out, prev = [], blank
    for i in idx:
        if i != prev and i != blank:
            if 0 < i < len(dict_chars) + 1:
                out.append(dict_chars[i - 1])
        prev = i
    return "".join(out)


def quick_compare(fp32_path, int8_path, sample_files, kind, dict_path=None):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    s32 = ort.InferenceSession(fp32_path, so, providers=["CPUExecutionProvider"])
    s8 = ort.InferenceSession(int8_path, providers=["CPUExecutionProvider"])
    chars = None
    if dict_path and os.path.exists(dict_path):
        with open(dict_path, "r", encoding="utf-8") as fh:
            chars = [line.rstrip("\n\r") or " " for line in fh.readlines()]
    print(f"== {kind}: fp32 vs int8 输出对比 ==")
    for f in sample_files[:3]:
        img = np.asarray(Image.open(f).convert("RGB"))
        inp = det_preprocess(img) if kind == "det" else np.random.RandomState(0).randn(1, 3, 48, 320).astype(np.float32)
        o32 = s32.run(None, {s32.get_inputs()[0].name: inp})[0]
        o8 = s8.run(None, {s8.get_inputs()[0].name: inp})[0]
        if o32.shape != o8.shape:
            print(f"  [警告] 形状不一致 {o32.shape} vs {o8.shape}")
            continue
        diff = float(np.abs(o32.astype(np.float64) - o8.astype(np.float64)).mean())
        line = f"  {os.path.basename(f)}: 形状{o32.shape} 平均|Δ|={diff:.5f}"
        if kind == "rec" and chars is not None:
            t32 = ctc_greedy(o32[0], chars)
            t8 = ctc_greedy(o8[0], chars)
            line += f" 文本一致={t32 == t8}"
        print(line)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models-dir", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--calib-roots", nargs="*", default=[])
    ap.add_argument("--calib-limit", type=int, default=200)
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    det32 = os.path.join(args.models_dir, "PP-OCRv6_det_small.onnx")
    rec32 = os.path.join(args.models_dir, "PP-OCRv6_rec_small.onnx")
    dict_path = os.path.join(args.models_dir, "ppocrv6_small_dict.txt")
    det8 = os.path.join(args.out_dir, "PP-OCRv6_det_small_int8.onnx")
    rec8 = os.path.join(args.out_dir, "PP-OCRv6_rec_small_int8.onnx")

    # quant_pre_process:形状推断+图优化,产出量化器友好底模。PP-OCRv6 det 的动态形状
    # 走不完符号形状推断(Incomplete symbolic shape inference),降级 skip_symbolic_shape,
    # 再不行直接用原模型(靠 per_channel=False 绕开 per-channel 反量化节点的 opset 兼容问题)。
    # ★prep 目录必须在 ASCII 路径下:onnx 预处理自写自读临时文件,中文路径会 FileNotFoundError
    prep = os.path.join(os.environ.get("TEMP", os.getcwd()), "ocrq_prep")
    os.makedirs(prep, exist_ok=True)
    det32p = os.path.join(prep, "det_pre.onnx")
    rec32p = os.path.join(prep, "rec_pre.onnx")
    print("== 0/3 quant_pre_process 预处理底模 ==")
    try:
        quant_pre_process(det32, det32p, skip_symbolic_shape=True)
        quant_pre_process(rec32, rec32p, skip_symbolic_shape=True)
        det32, rec32 = det32p, rec32p
    except Exception as ex:
        print(f"  预处理失败,改用原模型量化: {ex}")

    print("== 1/3 rec 动态量化 ==")
    quantize_dynamic(rec32, rec8, weight_type=QuantType.QInt8,
                     per_channel=True,
                     extra_options={"WeightSymmetric": True})
    print("  ->", rec8, f"{os.path.getsize(rec8)/1e6:.1f} MB")

    print("== 2/3 det 静态 QDQ(MinMax 校准) ==")
    files = collect_images(args.calib_roots)
    print(f"  校准素材 {min(len(files), args.calib_limit)}/{len(files)} 张")
    quantize_static(
        det32, det8, DetCalibReader(files, det32, args.calib_limit),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QUInt8,
        weight_type=QuantType.QInt8,
        per_channel=False,
        op_types_to_quantize=["Conv", "MatMul", "Gemm"],
        calibrate_method=CalibrationMethod.MinMax)
    print("  ->", det8, f"{os.path.getsize(det8)/1e6:.1f} MB")

    print("== 3/3 数值对比 ==")
    sample = collect_images(args.calib_roots)[:3]
    quick_compare(det32, det8, sample, "det")
    quick_compare(rec32, rec8, sample, "rec", dict_path)
    print("完成。fp32 vs int8 体积:",
          f"{os.path.getsize(det32)/1e6:.1f}->{os.path.getsize(det8)/1e6:.1f} MB(det),",
          f"{os.path.getsize(rec32)/1e6:.1f}->{os.path.getsize(rec8)/1e6:.1f} MB(rec)")


if __name__ == "__main__":
    sys.exit(main())
