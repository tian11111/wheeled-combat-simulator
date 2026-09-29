#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MBri yolo-ncnn 推理桥 → 仿真 live 视觉流 JSONL(外部进程源契约)。

用途: 把"真车 YOLO 检测流"作为**外部进程**喂给仿真的活源桥
(`Sim.Cli vision live --process "<命令行>"` → `ExternalProcessStreamSource`
→ `LiveVisionBridge`)。帧契约 = `Sim.Core.VisionStreamFrame`(JSONL 一行一帧,
字段名对齐真车 MBri CSV 列名), 图像永不进入契约。

stdout: **只有** JSONL 帧; stderr: 诊断与元数据(首行是运行参数)。
**逐帧 flush**(本脚本每帧 `flush=True`): 整块缓冲会让帧成簇到达, 桥看到的就不是
"实时流"而是"回放", 帧龄与 stale 率随之失真。用 `python -u` 启动更保险。

模式:
  --stub <csv>     读真车 MBri hunt 方言 CSV, 按**墙钟真实节奏**推帧
                   (帧到达间隔 = vision_timestamp_ms 差; 不依赖权重/相机,
                   本地与 CI 可跑)。默认提前 --lead-ms 发出, 补偿本进程启动
                   与管道延迟 —— 帧源等价确认要求"时间戳 ≤ 锚点+SimT×1000 的帧
                   在对应 classify 之前已经到达"。
  --model-dir <d>  真权重模式**不在仓库内实现**(权重/model.ncnn.* 不进仓库):
                   接入点与素材说明见 tools/yolo-bridge/README.md; 显式退出码 3,
                   绝不静默回退到 stub。

退出码: 0 正常; 1 输入/运行错误(CSV 缺失/契约违例, 零输出); 2 参数错误;
        3 真权重模式未在仓库内实现。

用法示例:
  python -u tools/yolo-bridge/mbri_yolo_bridge.py \
      --stub src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv
  python -u tools/yolo-bridge/mbri_yolo_bridge.py --stub <csv> --lead-ms 300 --limit 50
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
import time
from pathlib import Path

CONTRACT = "vision-stream-v1"
STATUSES = ("target", "no_target", "error", "no_data_or_stale")
# 原始 YOLO 类别 → (target_type, 归一化 label); 契约固定 0=good→buff / 1=bad→debuff。
CLASS_LABELS = {"0": ("good", "buff"), "1": ("bad", "debuff")}
REAL_MODE_HINT = (
    "真权重模式不在仓库内实现(权重/原生扩展不进仓库)。"
    "接入点见 tools/yolo-bridge/README.md: 用户素材 "
    "D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps/"
    "(YOLO26n 320 NCNN full INT8, JSON Lines 输出) → 归一化为本契约字段。"
)


class BridgeError(Exception):
    """输入/契约错误: 打印到 stderr 并以退出码 1 结束(零 stdout 输出)。"""


def parse_float(value: str, field: str, row: int) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        raise BridgeError(f"行 {row}: {field} 不是数值: {value!r}") from None


def is_selected(value: str) -> bool:
    return str(value).strip().lower() in ("1", "true", "yes")


def load_frames(csv_path: str) -> tuple[str, list[dict]]:
    """真车 CSV → 契约帧列表(按接收组聚合, 重收帧保留首次接收)。

    口径与 `vision import`(`VisionEvidenceBuilder.ParseSession`)同一语义的最小复刻:
    预热行(无 sequence)丢弃; 接收组 = 连续同 (sequence, vision_timestamp_ms,
    received_age_ms) 的行; 同 sequence 再次出现的接收组是**重收** → 保留首次接收、
    差异计数(不覆盖已发帧, 也不伪造新帧); 每检测一行按 detection_index 聚合;
    选中目标 = selected_target=1 的那一行(至多一个)。sequence 必须严格递增、
    时间戳必须单调非降 —— 违反即输入错误(零 stdout 输出)。
    """
    path = Path(csv_path)
    if not path.is_file():
        raise BridgeError(f"CSV 不存在: {path}")
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        raise BridgeError(f"CSV 为空: {path}")
    required = {"sequence", "vision_timestamp_ms", "vision_status", "detection_count",
                "frame_width", "frame_height", "class_id", "target_type", "confidence",
                "bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2", "center_x", "center_y",
                "offset_x", "offset_y"}
    missing = sorted(required - set(rows[0].keys()))
    if missing:
        raise BridgeError(f"CSV 缺列: {', '.join(missing)} ({path})")

    session = next((row.get("label", "") for row in rows if row.get("label")), path.stem)
    groups: list[tuple[str, list[tuple[int, dict]]]] = []
    seen_sequences: set[str] = set()
    warmup_rows = 0
    duplicates = 0
    previous_sequence: int | None = None
    previous_timestamp: float | None = None
    current_key: tuple[str, str, str] | None = None
    skipping = False  # 当前接收组是重收组(整组丢弃): 其后续检测行不得并入前一个已入组的帧
    for line, row in enumerate(rows, start=2):  # 行号从 2 起(表头是第 1 行)
        sequence = (row.get("sequence") or "").strip()
        if not sequence:
            warmup_rows += 1  # 预热行(无 sequence): 与 import 一致地丢弃
            continue
        number = parse_int(sequence, "sequence", line)
        key = (sequence, (row.get("vision_timestamp_ms") or "").strip(),
               (row.get("received_age_ms") or "").strip())
        if key == current_key:
            if not skipping:
                groups[-1][1].append((line, row))  # 同一接收组的后续检测行
            continue
        timestamp = parse_float(row.get("vision_timestamp_ms", ""), "vision_timestamp_ms", line)
        if sequence in seen_sequences:
            # 同 sequence 的新接收组 = 重收: 整组丢弃(保留首次接收), 只计数。
            # 关键: 该组的检测行也不能被当成"上一组的续行", 否则同一帧会出现
            # 多个 selected_target —— 与 vision import 的"保留首次接收"语义相悖。
            duplicates += 1
            current_key = key
            skipping = True
            continue
        if previous_sequence is not None and number <= previous_sequence:
            raise BridgeError(f"行 {line}: sequence {number} 未严格递增(前一个 {previous_sequence})")
        if previous_timestamp is not None and timestamp < previous_timestamp:
            raise BridgeError(f"行 {line}: vision_timestamp_ms {timestamp} 早于前一帧 {previous_timestamp}")
        seen_sequences.add(sequence)
        previous_sequence = number
        previous_timestamp = timestamp
        current_key = key
        skipping = False
        groups.append((sequence, [(line, row)]))

    frames = [normalize_group(session, sequence, group) for sequence, group in groups]
    if not frames:
        raise BridgeError(f"CSV 不含任何帧行(全部是预热行): {path}")
    # 到达时刻 = (时间戳 − 首帧)/1000 s: 与 CSV 源同一 SimT 缩放语义(审计字段)。
    first_ms = frames[0]["vision_timestamp_ms"]
    for frame in frames:
        frame["arrival_sim_t"] = (frame["vision_timestamp_ms"] - first_ms) / 1000.0
    sys.stderr.write(
        f"[yolo-bridge] csv={path} session={session} frames={len(frames)} "
        f"warmup_rows={warmup_rows} duplicate_receives={duplicates}\n")
    return session, frames


def normalize_group(session: str, sequence: str, group: list[tuple[int, dict]]) -> dict:
    """一组(帧首行 + 每检测一行) → 契约帧 dict(未做跨帧时间换算)。"""
    line, head = group[0]
    status = (head.get("vision_status") or "").strip()
    if status not in STATUSES:
        raise BridgeError(f"行 {line}: vision_status {status!r} 不在允许枚举内 {STATUSES}")
    error = (head.get("vision_error") or "").strip() or None
    timestamp_ms = parse_float(head.get("vision_timestamp_ms", ""), "vision_timestamp_ms", line)
    if timestamp_ms < 0:
        raise BridgeError(f"行 {line}: vision_timestamp_ms 必须非负")
    width = parse_int(head.get("frame_width", ""), "frame_width", line)
    height = parse_int(head.get("frame_height", ""), "frame_height", line)
    if width <= 0 or height <= 0:
        raise BridgeError(f"行 {line}: 帧尺寸 {width}x{height} 必须为正")

    detections: list[dict] = []
    selected: int | None = None
    for detection_line, row in group:
        class_id = (row.get("class_id") or "").strip()
        if not class_id:
            continue  # 该行没有检测(非 target 帧只有帧级字段)
        if class_id not in CLASS_LABELS:
            raise BridgeError(f"行 {detection_line}: class_id {class_id!r} 只允许 0(good)/1(bad)")
        if status != "target":
            raise BridgeError(f"行 {detection_line}: vision_status={status} 但携带检测行")
        raw_type, label = CLASS_LABELS[class_id]
        target_type = (row.get("target_type") or "").strip() or raw_type
        if target_type != raw_type:
            raise BridgeError(
                f"行 {detection_line}: class_id {class_id} 与 target_type {target_type!r} 不一致")
        confidence = parse_float(row.get("confidence", ""), "confidence", detection_line)
        if not 0.0 <= confidence <= 1.0:
            raise BridgeError(f"行 {detection_line}: confidence {confidence} 超出 [0,1]")
        bbox = [parse_float(row.get(name, ""), name, detection_line)
                for name in ("bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2")]
        center = [parse_float(row.get(name, ""), name, detection_line)
                  for name in ("center_x", "center_y")]
        offset = [parse_float(row.get(name, ""), name, detection_line)
                  for name in ("offset_x", "offset_y")]
        if any(not -1.0 <= value <= 1.0 for value in offset):
            raise BridgeError(f"行 {detection_line}: offset 超出 [-1,1]: {offset}")
        if is_selected(row.get("selected_target", "")):
            if selected is not None:
                raise BridgeError(f"行 {detection_line}: 同一帧 selected_target 多于一个")
            selected = len(detections)
        detections.append({
            "class_id": int(class_id),
            "target_type": raw_type,
            "label": label,
            "confidence": confidence,
            "bbox_x1": bbox[0], "bbox_y1": bbox[1], "bbox_x2": bbox[2], "bbox_y2": bbox[3],
            "center_x": center[0], "center_y": center[1],
            "offset_x": offset[0], "offset_y": offset[1],
        })

    declared = (head.get("detection_count") or "").strip()
    if declared and parse_int(declared, "detection_count", line) != len(detections):
        raise BridgeError(
            f"行 {line}: detection_count {declared} 与检测行数 {len(detections)} 不一致")
    if status == "target" and not detections:
        raise BridgeError(f"行 {line}: vision_status=target 但没有检测行")
    if status != "target" and detections:
        raise BridgeError(f"行 {line}: vision_status={status} 不允许携带检测")

    frame = {
        "sequence": int(sequence),
        "vision_timestamp_ms": timestamp_ms,
        "vision_status": status,
        "vision_error": error,
        "selected_target": selected,
        "detection_count": len(detections),
        "frame_width": width,
        "frame_height": height,
        "fps": optional_float(head.get("fps"), "fps", line),
        "inference_ms": optional_float(head.get("inference_ms"), "inference_ms", line),
        "received_age_ms": optional_float(head.get("received_age_ms"), "received_age_ms", line),
        "label": detections[selected]["label"] if selected is not None else None,
        "confidence": detections[selected]["confidence"] if selected is not None else None,
        "detections": detections,
    }
    return frame


def parse_int(value: str, field: str, line: int) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        raise BridgeError(f"行 {line}: {field} 不是整数: {value!r}") from None


def optional_float(value: str | None, field: str, line: int) -> float | None:
    if value is None or str(value).strip() == "":
        return None
    parsed = parse_float(value, field, line)
    if not math.isfinite(parsed):
        raise BridgeError(f"行 {line}: {field} 必须是有限数值: {value!r}")
    return parsed


def emit(frame: dict) -> None:
    """一行 JSONL + 立即 flush(逐帧 flush 是子进程契约的硬要求)。"""
    sys.stdout.write(json.dumps(frame, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def run_stub(csv_path: str, lead_ms: float, limit: int) -> int:
    session, frames = load_frames(csv_path)
    if limit > 0:
        frames = frames[:limit]
    sys.stderr.write(
        f"[yolo-bridge] mode=stub contract={CONTRACT} session={session} frames={len(frames)} "
        f"lead_ms={lead_ms} flush=per-frame\n")
    started = time.monotonic()
    for frame in frames:
        due = frame["arrival_sim_t"] - lead_ms / 1000.0
        wait = due - (time.monotonic() - started)
        if wait > 0:
            time.sleep(wait)
        emit(frame)
    return 0


def run_real(model_dir: str, source: str | None) -> int:
    """真权重模式占位: 仓库内只保留接口与文档(权重/原生扩展不进仓库)。"""
    sys.stderr.write(
        f"[yolo-bridge] mode=real model_dir={model_dir} input={source or '(相机默认)'}\n"
        f"[yolo-bridge] {REAL_MODE_HINT}\n")
    return 3


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="MBri yolo-ncnn 推理桥 → 仿真 live 视觉流 JSONL(外部进程源契约)。")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--stub", metavar="CSV",
                      help="stub 模式: 真车 CSV 按墙钟节奏推帧(不依赖权重)")
    mode.add_argument("--model-dir", metavar="DIR",
                      help="真权重模式(仓库内不实现; 见 tools/yolo-bridge/README.md)")
    parser.add_argument("--lead-ms", type=float, default=200.0,
                        help="stub 模式提前量(ms), 补偿进程启动/管道延迟(默认 200)")
    parser.add_argument("--limit", type=int, default=0,
                        help="stub 模式只推前 N 帧(0=全部)")
    parser.add_argument("--input", default=None,
                        help="真权重模式的输入(相机/视频; 占位参数)")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    # 契约要求 stdout 为 UTF-8(JSONL); Windows 控制台默认 GBK 会破坏 vision_error 等文本。
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass
    try:
        if args.model_dir is not None:
            return run_real(args.model_dir, args.input)
        if args.limit < 0:
            raise BridgeError(f"--limit 必须 >= 0, 得到 {args.limit}")
        return run_stub(args.stub, args.lead_ms, args.limit)
    except BridgeError as error:
        sys.stderr.write(f"[yolo-bridge] 输入错误: {error}\n")
        return 1
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main())
