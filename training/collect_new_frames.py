#!/usr/bin/env python3
"""把桌面发布目录采集的照片精细筛选、去重、自动标注后并入 training/negatives/。

用法：
    python training/collect_new_frames.py --source "C:\\Users\\20603\\Desktop\\新建文件夹" --dry-run
    python training/collect_new_frames.py --source "..."       # 实际写入

筛选原则（用户 2026-11-23 要求：只留有训练价值的，剔除没用的）：
  1. 真重复（像素差异 < 0.5%，即同一次写盘的多目录副本）：每组只留一张，
     优先顺序 trainingframes(主动采集) > suspicious(带诊断tag) > captures > dump
  2. 有细微差异的帧（0.5%~8%）**全部保留** —— 同页面不同焦点/闪烁相位/动画状态，
     正是模型要学的困难样本（用户特别提醒：别把略有差异的当重复删掉）
  3. 纯黑屏/加载/比赛画面（无 6 类目标且非已知困难页）：剔除（无训练信息）
  4. 保留帧用 YOLO 高分框(>=0.6)生成标注（坐标已核对与标注规范一致）

只读源文件，绝不修改/删除桌面原文件。写入 training/negatives/。
"""
import argparse, os, re, shutil, itertools
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
NEG = ROOT / "training" / "negatives"
# YOLO 类别顺序（与 training/label.txt + models/yolo/classes.txt 一致）
CLASS_ORDER = ["player_card", "btn_back", "btn_confirm", "coin_bar", "main_menu", "item_row"]
CLASS_ID = {name: i for i, name in enumerate(CLASS_ORDER)}


def to_yolo_label(box: str) -> str:
    """批量检测的 'class,score,x,y,w,h'（x/y 为左上角归一化）→ YOLO 'id cx cy w h'。"""
    parts = box.split(",")
    if len(parts) != 6: return ""
    name, score, x, y, bw, bh = parts
    if name not in CLASS_ID: return ""
    try:
        cx = float(x) + float(bw) / 2
        cy = float(y) + float(bh) / 2
    except ValueError:
        return ""
    return f"{CLASS_ID[name]} {cx:.4f} {cy:.4f} {float(bw):.4f} {float(bh):.4f}"

IMG_EXTS = {".jpg", ".jpeg", ".png"}
# 困难负样本页 tag：即使无 YOLO 目标也保留（这些页正是要教模型「别检出」的）
KEEP_TAGS = ("weekly_cards", "tournament", "popup_over_menu", "main_menu",
             "match_hub", "subpage_timeout", "tile_not_found", "menu_tile_ocr_incomplete")
# 明显的空帧关键词（黑屏/加载/失败）：直接剔除
DROP_NAMES = ("unknown_live", "failure", "观察循环连续异常")

DUP_THRESHOLD = 0.005  # 像素差异率 < 0.5% 判为真重复


def fingerprint(p: Path, size=(64, 36)):
    im = Image.open(p).convert("RGB").resize(size)
    return list(im.getdata())


def diff_ratio(a, b, threshold=24):
    n = len(a); diff = 0
    for (r1, g1, b1), (r2, g2, b2) in zip(a, b):
        if abs(r1-r2) + abs(g1-g2) + abs(b1-b2) > threshold:
            diff += 1
    return diff / n if n else 0


def parse_boxes() -> dict[str, list[str]]:
    tmp = Path(os.environ.get("TEMP", "/tmp")) / "yolo_batch_boxes.txt"
    result = {}
    if not tmp.exists():
        return result
    for line in tmp.read_text(encoding="utf-8").splitlines():
        if "|" not in line: continue
        rel, boxes = line.split("|", 1)
        result[rel.replace("\\", "/")] = [b for b in boxes.split(";") if b.strip()]
    return result


def priority(rel):
    parts = rel.split("/")
    if "trainingframes" in parts: return 0
    if "suspicious" in parts: return 1
    if "captures" in parts: return 2
    return 3


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    src = Path(args.source)
    if not src.is_dir(): raise SystemExit(f"源目录不存在: {src}")
    boxes_by_rel = parse_boxes()
    if not boxes_by_rel: raise SystemExit("缺 yolo_batch_boxes.txt（先跑 BatchInspect）")

    files = [p for p in src.rglob("*") if p.suffix.lower() in IMG_EXTS]
    fps = {p: fingerprint(p) for p in files}

    # 1) 真重复分组（差异 < 0.5%），每组只留优先级最高的一张
    parent = {p: p for p in files}
    def find(x):
        while parent[x] != x: parent[x] = parent[parent[x]]; x = parent[x]
        return x
    for a, b in itertools.combinations(files, 2):
        if diff_ratio(fps[a], fps[b]) < DUP_THRESHOLD:
            ra, rb = find(a), find(b)
            if ra != rb: parent[rb] = ra
    groups = {}
    for p in files: groups.setdefault(find(p), []).append(p)
    reps = []
    for g in groups.values():
        reps.append(sorted(g, key=lambda p: (priority(p.relative_to(src).as_posix()), str(p)))[0])
    dup_removed = len(files) - len(reps)

    # 2) 内容筛选
    kept, dropped = [], []
    for p in reps:
        rel = p.relative_to(src).as_posix()
        boxes = boxes_by_rel.get(rel, [])
        m = re.search(r"_\d{6}_\d{3}_([\w]+)", p.name)
        tag = m.group(1) if m else ""
        if any(k in p.name for k in DROP_NAMES):
            dropped.append((rel, tag, "黑屏/失败帧")); continue
        if not boxes and not any(k in tag for k in KEEP_TAGS):
            dropped.append((rel, tag, "无目标且非困难页")); continue
        kept.append((rel, p, boxes, tag))

    print(f"源 {len(files)} 张 → 真重复剔除 {dup_removed} 张 → {len(reps)} 张")
    print(f"内容筛选后：保留 {len(kept)} 张 / 剔除 {len(dropped)} 张")
    print("\n=== 剔除 ===")
    for rel, tag, why in dropped: print(f"  - {rel} [{tag or '无tag'}] {why}")
    print("\n=== 保留 ===")
    for rel, _, boxes, tag in kept: print(f"  + {rel} [{tag or '无tag'}] {len(boxes)}框")

    if args.dry_run:
        print("\n[dry-run] 未写入"); return

    img_dir, lbl_dir = NEG / "images", NEG / "labels"
    img_dir.mkdir(parents=True, exist_ok=True); lbl_dir.mkdir(parents=True, exist_ok=True)
    written = 0
    for rel, p, boxes, tag in kept:
        # rel 形如 "captures/20261123/xxx.jpg"（as_posix 开头无斜杠），用路径段判断
        parts = rel.split("/")
        prefix = ("trf" if "trainingframes" in parts else "sus" if "suspicious" in parts
                  else "cap" if "captures" in parts else "dmp")
        name = f"new_{prefix}_{p.stem}.jpg"
        dst = img_dir / name
        if dst.exists(): continue
        shutil.copy2(p, dst)
        # boxes 是 'class,score,x,y,w,h' 原始串，必须转成 YOLO 'id cx cy w h'（用户 2026-11-23 发现格式错误）
        yolo = [to_yolo_label(b) for b in boxes]
        yolo = [b for b in yolo if b]  # 过滤无法转换的空行
        (lbl_dir / (dst.stem + ".txt")).write_text(
            "\n".join(yolo) + ("\n" if yolo else ""), encoding="utf-8")
        written += 1
    print(f"\n已写入 {written} 张 → {img_dir} / {lbl_dir}")
    print("下一步：python training/prepare_dataset.py --dry-run 校验")


if __name__ == "__main__":
    main()
