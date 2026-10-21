#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OCR 误识挖掘器（离线工具，纯标准库，不影响运行中的程序）

用途
----
程序日志里每条 `[OCR原文] a | b | c` 都是一帧的真实识别结果。人工翻日志找误识
（「前往比賽」这种）既慢又容易漏。本工具把日志里的词全部统计一遍，并做两件事：

1) 近似词对：把「编辑距离 ≤ 1」的词归为一组，组内高频词视为正确写法、
   低频词视为误识候选 → 直接给出 (误识 → 正确) 的候选替换对，
   排序按「高频次数 × 低频稀少程度」，最值得先修的在最前面。
2) 繁体单字：命中内置繁简表（賽/階/組/別…）的词单独列出，这类是
   现有 OcrText.Replacements 的主要来源。

产出的候选对人工确认后，追加到
`src/EfootballBot/Core/Vision/OcrRecognizer.cs` 的 `OcrText.Replacements`，
或用单字表验证是否已被覆盖。

用法
----
    python tools/ocr_misspelling_miner.py publish/logs
    python tools/ocr_misspelling_miner.py data/logs --min-major 5 --top 80
    python tools/ocr_misspelling_miner.py a.yml b.yml --no-cluster

参数
----
    --min-major N   高频词至少出现 N 次才作为「正确写法」参与配对（默认 5）
    --min-minor N   低频词至少出现 N 次才报告（默认 1）
    --top N         最多输出多少条（默认 50）
    --no-cluster    只输出繁简单字命中，不做近似词聚类
"""

import argparse
import os
import re
import sys
from collections import Counter, defaultdict

# 日志行形如：
# - {time: "2026-10-21 17:36:01.123", level: "DEBUG", msg: "[OCR原文] 前往比賽 | 组别 ..."}
OCR_LINE = re.compile(r"\[OCR原文\]\s*(.*?)\"?\}\s*$")

# 繁简/常见误识单字（与 OcrText.Replacements 保持同一份心智模型，新增实锤后再补）
TRAD_TO_SIMP = {
    "賽": "赛", "階": "阶", "組": "组", "別": "别", "聯": "联", "級": "级",
    "標": "标", "閉": "闭", "確": "确", "認": "认", "須": "须", "選": "选",
    "擇": "择", "場": "场", "練": "练", "員": "员", "隊": "队", "積": "积",
    "換": "换", "獎": "奖", "勵": "励", "領": "领", "簽": "签", "約": "约",
    "絕": "绝", "對": "对", "並": "并", "難": "难", "戰": "战", "勝": "胜",
    "負": "负", "進": "进", "奪": "夺", "寶": "宝", "點": "点", "數": "数",
    "據": "据", "練": "练", "總": "总", "結": "结", "計": "计", "劃": "划",
}

# 纯数字/符号类词块（比分、时间、百分比）对误识挖掘无意义
NUMERICISH = re.compile(r"^[\d\s%/:.,+\-–—]+$")


def has_cjk(s):
    return any("\u4e00" <= ch <= "\u9fff" for ch in s)


def iter_tokens(paths, include_ascii=False):
    """从文件或目录里流式产出 OCR 词块。

    GUI 与 Worker 会各自写一份日志，同一帧的 [OCR原文] 行在两种日志里逐字相同
    （时间戳也来自同一条消息）。这里按整行去重，否则所有词频都虚高一倍，
    「只出现一次」的长尾永远为空。
    """
    seen = set()
    for p in paths:
        if os.path.isdir(p):
            for name in sorted(os.listdir(p)):
                full = os.path.join(p, name)
                if os.path.isfile(full) and name.lower().endswith((".yml", ".yaml", ".log", ".txt")):
                    yield from read_tokens(full, seen, include_ascii)
        elif os.path.isfile(p):
            yield from read_tokens(p, seen, include_ascii)


def read_tokens(path, seen, include_ascii):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                if "[OCR原文]" not in line:
                    continue
                raw = line.rstrip("\n")
                if raw in seen:
                    continue
                seen.add(raw)
                m = OCR_LINE.search(raw)
                if not m:
                    continue
                payload = m.group(1).strip().rstrip('"')
                for tok in payload.split("|"):
                    tok = tok.strip().replace(" ", "")
                    if not tok or NUMERICISH.match(tok):
                        continue
                    # 纯拉丁/数字词块（赞助商广告、比分）不是界面判定词，聚类时是纯噪声
                    if not include_ascii and tok.isascii():
                        continue
                    yield tok
    except OSError as e:
        print(f"[跳过] {path}: {e}", file=sys.stderr)


def edit_distance_le1(a, b):
    """是否存在编辑距离 ≤ 1 的关系（等长替换 / 一次增删）。"""
    if a == b:
        return False
    la, lb = len(a), len(b)
    if abs(la - lb) > 1:
        return False
    if la == lb:
        diff = 0
        for x, y in zip(a, b):
            if x != y:
                diff += 1
                if diff > 1:
                    return False
        return True
    # 长度差 1：短的插一个字符等于长的
    if la > lb:
        a, b = b, a
    i = j = 0
    skipped = False
    while i < len(a) and j < len(b):
        if a[i] == b[j]:
            i += 1
            j += 1
            continue
        if skipped:
            return False
        skipped = True
        j += 1
    return True


def main():
    ap = argparse.ArgumentParser(description="从挂机日志里挖掘 OCR 误识候选（离线，只读）")
    ap.add_argument("paths", nargs="+", help="日志目录或日志文件")
    ap.add_argument("--min-major", type=int, default=5, help="高频词最低次数（默认 5）")
    ap.add_argument("--min-minor", type=int, default=1, help="低频词最低次数（默认 1）")
    ap.add_argument("--top", type=int, default=50, help="最多输出条数（默认 50）")
    ap.add_argument("--no-cluster", action="store_true", help="只输出繁简单字命中")
    ap.add_argument("--include-ascii", action="store_true",
                    help="把纯拉丁词块（广告/赞助商/比分）也纳入统计（默认排除，噪声极大）")
    args = ap.parse_args()

    freq = Counter(iter_tokens(args.paths, args.include_ascii))
    if not freq:
        print("没有在给定路径的日志里找到 [OCR原文] 行。确认日志级别为 Debug 且路径正确。")
        return 1

    print(f"共 {sum(freq.values())} 个词块，{len(freq)} 种不同写法。\n")

    # ---- 1. 繁体/异体单字命中 ----
    hit = []
    for tok, n in freq.items():
        bad = [c for c in tok if c in TRAD_TO_SIMP]
        if bad:
            fixed = "".join(TRAD_TO_SIMP.get(c, c) for c in tok)
            hit.append((n, tok, fixed))
    hit.sort(reverse=True)
    print(f"== 繁简/异体单字命中（{len(hit)} 种）==")
    for n, tok, fixed in hit[: args.top]:
        print(f"  {n:5d}×  {tok}  →  {fixed}   （单字: {''.join(c for c in tok if c in TRAD_TO_SIMP)}）")
    if not hit:
        print("  （无）")

    if args.no_cluster:
        return 0

    # ---- 2. 近似词对（编辑距离 ≤ 1）----
    # 按长度分桶，避免 O(n²) 全量比较
    buckets = defaultdict(list)
    for tok in freq:
        buckets[len(tok)].append(tok)

    pairs = []
    for length, toks in buckets.items():
        if length < 2:  # 单字词（日期/比分残片）配对全是噪声
            continue
        if len(toks) > 4000:  # 极端长尾保护
            toks = sorted(toks, key=lambda t: -freq[t])[:4000]
        toks = sorted(toks, key=lambda t: -freq[t])
        for i, a in enumerate(toks):
            for b in toks[i + 1:]:
                if not args.include_ascii and not (has_cjk(a) and has_cjk(b)):
                    continue
                if freq[a] < args.min_major and freq[b] < args.min_major:
                    continue
                if edit_distance_le1(a, b):
                    major, minor = (a, b) if freq[a] >= freq[b] else (b, a)
                    if freq[major] >= args.min_major and freq[minor] >= args.min_minor:
                        pairs.append((major, minor, freq[major], freq[minor]))

    # 同一个误识词可能同时接近多个正确词：只保留最可能的那个（次数最多的 major）
    best = {}
    for major, minor, fm, fn in pairs:
        if minor not in best or fm > best[minor][2]:
            best[minor] = (major, minor, fm, fn)
    pairs = list(best.values())

    # 高分：高频写法明确 + 低频写法稀少（越稀少越像一次性误识）
    pairs.sort(key=lambda p: (-p[2], p[3]))
    print(f"\n== 近似词对候选（编辑距离 ≤ 1，共 {len(pairs)} 对）==")
    print("  说明：左=高频（大概率正确），右=低频（误识候选）。人工确认后加入 OcrText.Replacements。")
    for major, minor, fm, fn in pairs[: args.top]:
        print(f"  {fm:5d}× {major}   ⟵  {fn:4d}× {minor}")

    # ---- 3. 长尾（只出现一次）——新界面/新美术字的第一现场 ----
    # 超过 24 字的「词」基本都是比赛内广告横幅与比分被并成一块的残渣，没有判定价值
    ones = [t for t, n in freq.items() if n == 1 and 2 <= len(t) <= 24]
    print(f"\n== 只出现过 1 次的写法（{len(ones)} 种，前 {min(args.top, len(ones))} 条）==")
    for t in ones[: args.top]:
        print(f"  {t}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
