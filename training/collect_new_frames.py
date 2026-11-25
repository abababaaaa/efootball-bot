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


def _parse_box(box: str):
    """解析 'class,score,x,y,w,h'（x/y 为左上角归一化）；无效则返 None。"""
    parts = box.split(",")
    if len(parts) != 6:
        return None
    name, score_s, x_s, y_s, w_s, h_s = parts
    if name not in CLASS_ID:
        return None
    try:
        score = float(score_s)
        x, y, bw, bh = float(x_s), float(y_s), float(w_s), float(h_s)
    except ValueError:
        return None
    return {
        "raw": box,
        "name": name,
        "score": score,
        "x1": x,
        "y1": y,
        "x2": x + bw,
        "y2": y + bh,
        "cx": x + bw / 2,
        "cy": y + bh / 2,
        "w": bw,
        "h": bh,
    }


def _iou(a: dict, b: dict) -> float:
    ix1, iy1 = max(a["x1"], b["x1"]), max(a["y1"], b["y1"])
    ix2, iy2 = min(a["x2"], b["x2"]), min(a["y2"], b["y2"])
    iw, ih = max(0.0, ix2 - ix1), max(0.0, iy2 - iy1)
    inter = iw * ih
    if inter <= 0:
        return 0.0
    area_a = max(0.0, a["w"]) * max(0.0, a["h"])
    area_b = max(0.0, b["w"]) * max(0.0, b["h"])
    union = area_a + area_b - inter
    return inter / union if union > 0 else 0.0


def validate_boxes(boxes: list[str]) -> tuple[list[str], list[str]]:
    """
    自动标注几何/语义护栏（防止错误预测回灌训练集「自我投毒」）：
      1. 剔除右半屏 btn_back（cx > 0.50）
      2. 剔除左下角返回位 btn_confirm（cx < 0.25 and cy > 0.85）
      3. btn_back 与 btn_confirm 重叠 IoU > 0.45 时仅保留高分框
      4. 存在居中弹窗 btn_confirm（0.30 <= cx <= 0.70 and 0.45 <= cy <= 0.85）时，
         剔除低分（score < 0.75）左下角暗化背景 btn_back
    返回 (保留原始串列表, 过滤原因明细列表)。
    """
    parsed = []
    dropped_reasons: list[str] = []
    for raw in boxes:
        p = _parse_box(raw)
        if p is None:
            dropped_reasons.append(f"无效格式: {raw}")
            continue
        parsed.append(p)

    # 先按单框位置规则过滤
    stage1 = []
    for p in parsed:
        if p["name"] == "btn_back" and p["cx"] > 0.50:
            dropped_reasons.append(f"右半屏误标 btn_back ({p['score']:.2f} @cx={p['cx']:.2f})")
            continue
        if p["name"] == "btn_confirm" and p["cx"] < 0.25 and p["cy"] > 0.85:
            dropped_reasons.append(f"左下角返回位误标 btn_confirm ({p['score']:.2f} @cx={p['cx']:.2f},cy={p['cy']:.2f})")
            continue
        stage1.append(p)

    has_center_confirm = any(
        p["name"] == "btn_confirm" and 0.30 <= p["cx"] <= 0.70 and 0.45 <= p["cy"] <= 0.85
        for p in stage1
    )

    # 居中弹窗遮罩下过滤低分背景 btn_back
    stage2 = []
    for p in stage1:
        if (has_center_confirm and p["name"] == "btn_back"
                and p["cx"] < 0.30 and p["cy"] > 0.75 and p["score"] < 0.75):
            dropped_reasons.append(
                f"居中弹窗下低分背景 btn_back ({p['score']:.2f} @cx={p['cx']:.2f},cy={p['cy']:.2f})")
            continue
        stage2.append(p)

    # btn_back vs btn_confirm 同位重叠互斥：只留高分框
    drop_idx: set[int] = set()
    for i in range(len(stage2)):
        if i in drop_idx:
            continue
        for j in range(i + 1, len(stage2)):
            if j in drop_idx:
                continue
            a, b = stage2[i], stage2[j]
            if {a["name"], b["name"]} == {"btn_back", "btn_confirm"} and _iou(a, b) > 0.45:
                loser_i = j if a["score"] >= b["score"] else i
                winner = a if a["score"] >= b["score"] else b
                loser = stage2[loser_i]
                drop_idx.add(loser_i)
                dropped_reasons.append(
                    f"{loser['name']} 与 {winner['name']} 重叠(IoU={_iou(a, b):.2f})，"
                    f"舍低分 {loser['score']:.2f} 留 {winner['score']:.2f}")
                if loser_i == i:
                    break

    kept_boxes = [p["raw"] for idx, p in enumerate(stage2) if idx not in drop_idx]
    return kept_boxes, dropped_reasons


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

    # 2) 内容筛选 + 护栏校验
    kept, dropped = [], []
    guard_total = 0
    for p in reps:
        rel = p.relative_to(src).as_posix()
        raw_boxes = boxes_by_rel.get(rel, [])
        boxes, guard_reasons = validate_boxes(raw_boxes)
        guard_total += len(guard_reasons)
        m = re.search(r"_\d{6}_\d{3}_([\w]+)", p.name)
        tag = m.group(1) if m else ""
        if any(k in p.name for k in DROP_NAMES):
            dropped.append((rel, tag, "黑屏/失败帧")); continue
        if not boxes and not any(k in tag for k in KEEP_TAGS):
            dropped.append((rel, tag, "护栏过滤后无目标且非困难页" if raw_boxes else "无目标且非困难页")); continue
        kept.append((rel, p, boxes, tag, guard_reasons))

    print(f"源 {len(files)} 张 → 真重复剔除 {dup_removed} 张 → {len(reps)} 张")
    print(f"内容筛选后：保留 {len(kept)} 张 / 剔除 {len(dropped)} 张（[护栏过滤] 拦截伪框 {guard_total} 个）")
    print("\n=== 剔除 ===")
    for rel, tag, why in dropped: print(f"  - {rel} [{tag or '无tag'}] {why}")
    print("\n=== 保留 ===")
    for rel, _, boxes, tag, guard_reasons in kept:
        guard_note = f" | [护栏过滤] {'; '.join(guard_reasons)}" if guard_reasons else ""
        print(f"  + {rel} [{tag or '无tag'}] {len(boxes)}框{guard_note}")

    if args.dry_run:
        print("\n[dry-run] 未写入"); return

    img_dir, lbl_dir = NEG / "images", NEG / "labels"
    img_dir.mkdir(parents=True, exist_ok=True); lbl_dir.mkdir(parents=True, exist_ok=True)
    written = 0
    for rel, p, boxes, tag, _ in kept:
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
