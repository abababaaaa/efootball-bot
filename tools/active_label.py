#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
主动学习选帧器（离线工具，需要 PIL；项目自带 tools\\python 可用）

为什么需要它
------------
训练集靠人肉从 captures 里翻，翻到的往往是**和训练集几乎一样的帧**（同一屏重复上千张），
真正值得标的「新界面 / 稀有界面 / 出错界面」反而被淹没。

本工具做三件事：
1. **感知去重**：dHash（8×8 差分哈希）+ 汉明距离，几乎一样的帧只留一张；
2. **剔除已见**：和 training/dataset/images 里已有样本距离 ≤ 阈值的一律跳过，不浪费标注；
3. **按价值排序**：异常帧（data/failures）优先，其次是与训练集差异最大的新画面。

输出
----
data/label_queue/  按序号命名的待标帧（可直接拖进标注工具）
data/label_queue/queue.csv  清单：序号、来源、与训练集最近距离、选择原因

用法
----
    tools\\python\\python.exe tools/active_label.py
    tools\\python\\python.exe tools/active_label.py --top 200 --root publish
    tools\\python\\python.exe tools/active_label.py --include-testdata
"""

import argparse
import csv
import os
import shutil
import sys

try:
    from PIL import Image
except ImportError:
    print("需要 PIL。用项目自带的解释器：tools\\python\\python.exe tools/active_label.py")
    sys.exit(2)

IMAGE_EXT = (".jpg", ".jpeg", ".png", ".bmp")

# 仓库根（本脚本在 tools/ 下）：训练集默认从仓库根找，而不是从 --root 找
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DATASET = os.path.join(REPO_ROOT, "training", "dataset", "images")


def dhash(path, size=8):
    """差分感知哈希：64 位整数。对缩放/轻微明暗变化稳定。"""
    with Image.open(path) as im:
        im = im.convert("L").resize((size + 1, size), Image.Resampling.BILINEAR)
        px = im.tobytes()
    bits = 0
    idx = 0
    for row in range(size):
        base = row * (size + 1)
        for col in range(size):
            left = px[base + col]
            right = px[base + col + 1]
            if left > right:
                bits |= 1 << idx
            idx += 1
    return bits


def hamming(a, b):
    return bin(a ^ b).count("1")


def walk_images(root):
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            if name.lower().endswith(IMAGE_EXT):
                yield os.path.join(dirpath, name)


def main():
    ap = argparse.ArgumentParser(description="从存帧里挑出值得标注的新画面（离线）")
    ap.add_argument("--root", default=".", help="程序目录（含 captures/ 与 data/），默认当前目录")
    ap.add_argument("--dataset", default=DEFAULT_DATASET, help="已标注训练集目录（默认仓库 training/dataset/images）")
    ap.add_argument("--out", default="data/label_queue", help="输出目录")
    ap.add_argument("--top", type=int, default=120, help="最多挑多少张（默认 120）")
    ap.add_argument("--min-dataset-distance", type=int, default=6,
                    help="与训练集最近距离小于该值的帧直接跳过（默认 6，0~64）")
    ap.add_argument("--dedupe-distance", type=int, default=3, help="候选之间去重阈值（默认 3）")
    ap.add_argument("--include-testdata", action="store_true", help="把 tests/TestData 也算进已见集合")
    args = ap.parse_args()

    root = os.path.abspath(args.root)
    captures = os.path.join(root, "captures")
    failures = os.path.join(root, "data", "failures")
    dataset = args.dataset if os.path.isabs(args.dataset) else os.path.abspath(args.dataset)
    testdata = os.path.join(REPO_ROOT, "tests", "EfootballBot.Tests", "TestData")

    # ---- 1. 已见集合 ----
    seen = []
    seen_sources = []
    if os.path.isdir(dataset):
        seen_sources.append(dataset)
    if args.include_testdata and os.path.isdir(testdata):
        seen_sources.append(testdata)

    for src in seen_sources:
        for p in walk_images(src):
            try:
                seen.append(dhash(p))
            except Exception:
                continue
    print(f"已见样本：{len(seen)} 张（{', '.join(os.path.basename(s) for s in seen_sources) or '无'}）")

    # ---- 2. 收集候选（失败帧优先）----
    candidates = []  # (priority, path, reason)
    for base, prio, reason in ((failures, 0, "异常帧"), (captures, 1, "存帧")):
        if not os.path.isdir(base):
            continue
        for p in walk_images(base):
            candidates.append((prio, p, reason))
    print(f"候选帧：{len(candidates)} 张")

    # ---- 3. 逐个算距离 + 去重 ----
    chosen = []       # (distance, priority, path, reason)
    kept_hashes = []
    skipped_seen = 0
    unreadable = 0
    for prio, path, reason in candidates:
        try:
            h = dhash(path)
        except Exception:
            unreadable += 1
            continue

        nearest_seen = min((hamming(h, s) for s in seen), default=64)
        if nearest_seen < args.min_dataset_distance:
            skipped_seen += 1
            continue

        if any(hamming(h, k) <= args.dedupe_distance for k in kept_hashes):
            continue

        kept_hashes.append(h)
        chosen.append((nearest_seen, prio, path, reason))

    print(f"与训练集过近跳过：{skipped_seen} 张；不可读：{unreadable} 张；去重后候选：{len(chosen)} 张")

    # ---- 4. 排序：异常帧优先，其次差异最大 ----
    chosen.sort(key=lambda x: (x[1], -x[0]))
    chosen = chosen[: args.top]

    out_dir = os.path.join(root, args.out) if not os.path.isabs(args.out) else args.out
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir, ignore_errors=True)
    os.makedirs(out_dir, exist_ok=True)

    rows = []
    for i, (dist, prio, path, reason) in enumerate(chosen, 1):
        ext = os.path.splitext(path)[1].lower()
        name = f"{i:04d}_{reason}{ext}"
        try:
            shutil.copy2(path, os.path.join(out_dir, name))
        except Exception:
            continue
        rows.append([name, path, dist, reason])

    with open(os.path.join(out_dir, "queue.csv"), "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(["文件名", "原路径", "与训练集最近距离", "来源"])
        w.writerows(rows)

    print(f"\n已输出 {len(rows)} 张 → {out_dir}")
    print("排序说明：异常帧在前；同一来源内「与训练集最近距离」越大越靠前（越可能是新界面）。")
    print("标注完成后把图放进 training/dataset/images，标签放入 labels（格式见 training/labeling_guide.md）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
