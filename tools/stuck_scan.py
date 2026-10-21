#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
卡死扫描器（离线工具，纯标准库，只读日志）

用途
----
用户报「走到不动 / 死循环」时，日志里最直接的证据是：**同一屏的 OCR 原文连续重复**。
本工具把一次会话的 `[OCR原文]` 序列按时间排好，找出：

1) 长时间停在同一个画面（同一份词表连续出现 ≥ N 次）——「卡死」；
2) 两个画面来回横跳（A,B,A,B…）——「死循环」；
3) 每个卡死点前后最近保存的异常帧（data/failures/<日期>/），方便直接看图。

用法
----
    python tools/stuck_scan.py publish/logs
    python tools/stuck_scan.py data/logs/bot-20261021-173601-24108.yml --min-repeat 3
    python tools/stuck_scan.py publish/logs --show-tokens

输出
----
每段列出：起止时间、持续时长、重复次数、该画面的代表性词块。
配合 `tools/ocr_misspelling_miner.py` 与 data\\failures 的帧，就能离线复现用户现场。
"""

import argparse
import os
import re
import sys
from collections import Counter

LINE_TIME = re.compile(r'time:\s*"([0-9\-]{10} [0-9:.]{8,12})"')
OCR_PAYLOAD = re.compile(r"\[OCR原文\]\s*(.*?)\"?\}\s*$")


def parse_dump_time(line):
    m = LINE_TIME.search(line)
    return m.group(1) if m else ""


def read_dumps(paths):
    """返回 [(time, signature, tokens)]，按出现顺序（同一条重复行只算一次）。"""
    seen = set()
    out = []
    for p in paths:
        files = []
        if os.path.isdir(p):
            files = [os.path.join(p, n) for n in sorted(os.listdir(p))
                     if n.lower().endswith((".yml", ".yaml", ".log", ".txt"))]
        else:
            files = [p]
        for f in files:
            if not os.path.isfile(f):
                continue
            try:
                with open(f, "r", encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if "[OCR原文]" not in line:
                            continue
                        key = line.rstrip("\n")
                        if key in seen:      # GUI 与 Worker 双份日志：按整行去重
                            continue
                        seen.add(key)
                        m = OCR_PAYLOAD.search(key)
                        if not m:
                            continue
                        tokens = [t.strip().replace(" ", "") for t in m.group(1).split("|")]
                        tokens = [t for t in tokens if t]
                        if not tokens:
                            continue
                        # 签名 = 去重后排序的词集合（顺序抖动/漏读一个词不算换屏）
                        sig = "|".join(sorted(set(tokens)))
                        out.append((parse_dump_time(key), sig, tokens))
            except OSError as e:
                print(f"[跳过] {f}: {e}", file=sys.stderr)
    out.sort(key=lambda x: x[0])
    return out


def similar(a, b, threshold=0.8):
    """两份词表的 Jaccard 相似度是否达到阈值（容 OCR 抖动）。"""
    sa, sb = set(a.split("|")), set(b.split("|"))
    if not sa or not sb:
        return False
    return len(sa & sb) / len(sa | sb) >= threshold


def main():
    ap = argparse.ArgumentParser(description="从挂机日志里找卡死/死循环（离线，只读）")
    ap.add_argument("paths", nargs="+", help="日志目录或日志文件")
    ap.add_argument("--min-repeat", type=int, default=4, help="同一画面连续出现多少次算卡死（默认 4）")
    ap.add_argument("--min-minutes", type=float, default=0.0,
                    help="只报告持续至少多少分钟的卡死（默认 0 = 都报）")
    ap.add_argument("--show-tokens", action="store_true", help="打印每段卡死画面的词块")
    args = ap.parse_args()

    dumps = read_dumps(args.paths)
    if not dumps:
        print("没有找到 [OCR原文] 行。确认日志级别为 Debug、路径正确。")
        return 1

    print(f"共 {len(dumps)} 次画面识别（首条 {dumps[0][0]}，末条 {dumps[-1][0]}）\n")

    # ---- 1) 连续同屏 ----
    runs = []
    i = 0
    while i < len(dumps):
        j = i
        while j + 1 < len(dumps) and similar(dumps[j][1], dumps[j + 1][1]):
            j += 1
        if j - i + 1 >= args.min_repeat:
            runs.append((i, j))
        i = j + 1

    def minutes(a, b):
        try:
            from datetime import datetime
            t1 = datetime.strptime(a[:19], "%Y-%m-%d %H:%M:%S")
            t2 = datetime.strptime(b[:19], "%Y-%m-%d %H:%M:%S")
            return (t2 - t1).total_seconds() / 60.0
        except Exception:
            return 0.0

    print(f"== 长时间停在同一画面（连续 ≥ {args.min_repeat} 次，{len(runs)} 段）==")
    for i, j in runs:
        dur = minutes(dumps[i][0], dumps[j][0])
        if dur < args.min_minutes:
            continue
        print(f"  {dumps[i][0]} → {dumps[j][0]}  {dur:.1f} 分钟 · 重复 {j - i + 1} 次")
        if args.show_tokens:
            print(f"    词块：{' | '.join(dumps[i][2][:20])}")
        print(f"    提示：看 data\\failures\\{dumps[i][0][:10].replace('-', '')}\\ 有没有这段时间的帧")
    if not runs:
        print("  （无）")

    # ---- 2) A,B,A,B 死循环 ----
    cycles = []
    k = 0
    while k + 3 < len(dumps):
        a, b, c, d = (dumps[k + t][1] for t in range(4))
        if similar(a, c) and similar(b, d) and not similar(a, b):
            start = k
            while k + 2 < len(dumps) and similar(dumps[k][1], dumps[k + 2][1]) \
                    and not similar(dumps[k][1], dumps[k + 1][1]):
                k += 1
            cycles.append((start, k + 2))
        k += 1

    print(f"\n== 两个画面来回横跳（{len(cycles)} 段）==")
    for i, j in cycles:
        print(f"  {dumps[i][0]} → {dumps[j][0]}  {minutes(dumps[i][0], dumps[j][0]):.1f} 分钟 · {j - i + 1} 次识别")
        if args.show_tokens:
            print(f"    A：{' | '.join(dumps[i][2][:12])}")
            print(f"    B：{' | '.join(dumps[i + 1][2][:12])}")
    if not cycles:
        print("  （无）")

    # ---- 3) 高频重复画面（哪一屏耗掉最多时间）----
    sig_count = Counter(d[1] for d in dumps)
    print("\n== 出现次数最多的 5 个画面 ==")
    for sig, n in sig_count.most_common(5):
        sample = sig.split("|")[:10]
        print(f"  {n:4d} 次  {' | '.join(sample)}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
