#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""mbri_yolo_bridge 契约自测: 真车 CSV → live 视觉流 JSONL, 无需权重/相机/仿真器。

运行: py -3.12 tools/yolo-bridge/selftest.py
覆盖: CSV 归一化口径(预热行/重收/多检测/选中目标)、字段契约与枚举、逐帧 flush
与墙钟节奏、坏输入与真权重占位模式的退出码(零 stdout 输出)。
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import mbri_yolo_bridge as bridge  # noqa: E402

FIXTURE = (HERE.parents[1] / "src" / "Sim.Tests" / "fixtures" / "mbri-vision-mini"
           / "hunt_drive_20260817_095205.csv")
# 重收结构夹具: 同一 sequence 有多个接收组(age 不同, 各带自己的检测行与 selected_target),
# 是"重收帧整组丢弃"语义的回归用例(hunt_drive 没有重收结构, 覆盖不到)。
REFIXTURE = (HERE.parents[1] / "src" / "Sim.Tests" / "fixtures" / "mbri-vision-mini"
             / "good_recheck_20260816_223647_excerpt.csv")


def run_bridge(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HERE / "mbri_yolo_bridge.py"), *args],
        capture_output=True, text=True, encoding="utf-8", timeout=60)


def test_fixture_normalization() -> None:
    session, frames = bridge.load_frames(str(FIXTURE))
    assert session == "hunt_drive", session
    assert len(frames) == 101, f"夹具应归一化为 101 帧, 得到 {len(frames)}"
    assert [frame["sequence"] for frame in frames] == list(range(1, 102))
    assert all(frame["frame_width"] == 640 and frame["frame_height"] == 480 for frame in frames)
    statuses = {frame["vision_status"] for frame in frames}
    assert statuses == {"target", "no_target"}, statuses
    targets = [frame for frame in frames if frame["vision_status"] == "target"]
    assert targets, "夹具必须含 target 帧(否则契约没被真实验证)"
    for frame in targets:
        assert frame["detection_count"] == len(frame["detections"]) > 0
        assert frame["selected_target"] is not None
        detection = frame["detections"][frame["selected_target"]]
        assert detection["label"] in ("buff", "debuff")
        assert detection["target_type"] in ("good", "bad")
        assert detection["class_id"] == (0 if detection["target_type"] == "good" else 1)
    # 到达时刻 = (时间戳 − 首帧)/1000 s(SimT 缩放语义, 与 CSV 源同一时基)。
    assert frames[0]["arrival_sim_t"] == 0.0
    assert frames[1]["arrival_sim_t"] > 0
    print(f"OK fixture normalization ({len(frames)} frames, {len(targets)} target)")


def test_jsonl_contract_and_pacing() -> None:
    """子进程端到端: 逐帧 flush + 墙钟节奏 + 逐行契约。"""
    proc = subprocess.Popen(
        [sys.executable, "-u", str(HERE / "mbri_yolo_bridge.py"),
         "--stub", str(FIXTURE), "--limit", "3", "--lead-ms", "0"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    received: list[tuple[dict, float]] = []
    try:
        start = time.monotonic()
        for _ in range(3):
            line = proc.stdout.readline()
            assert line, f"子进程提前结束(exit={proc.poll()}): 只收到 {len(received)} 行"
            received.append((json.loads(line), time.monotonic() - start))
    finally:
        proc.communicate(timeout=30)
    assert proc.returncode == 0, proc.returncode

    first_frame, first_at = received[0]
    # 逐帧 flush + 墙钟节奏: 若子进程整块缓冲到退出才吐, 三行会在毫秒内挤作一团,
    # 帧间隔 ≈ 0; 夹具前两帧时间戳差 ~215ms, 真实节奏下必须看到这个间隔。
    gap = received[1][1] - received[0][1]
    assert 0.1 <= gap <= 2.0, f"第 1→2 帧墙钟间隔异常: {gap:.3f}s (flush 或节奏被破坏)"
    assert first_at < 2.0, f"第 1 帧到达过晚: {first_at:.3f}s"

    for frame, _ in received:
        assert set(frame) == {
            "sequence", "vision_timestamp_ms", "vision_status", "vision_error",
            "selected_target", "detection_count", "frame_width", "frame_height",
            "fps", "inference_ms", "received_age_ms", "label", "confidence",
            "arrival_sim_t", "detections",
        }, sorted(frame)
        assert frame["vision_status"] in bridge.STATUSES
        assert frame["detection_count"] == len(frame["detections"])
    assert [frame["sequence"] for frame, _ in received] == [1, 2, 3]
    assert received[1][0]["vision_timestamp_ms"] > first_frame["vision_timestamp_ms"]
    print(f"OK jsonl contract + per-frame flush/pacing (帧间隔 {gap * 1000:.0f}ms)")


def test_duplicate_receive_groups_collapse_to_the_first_receive() -> None:
    """重收帧(同 sequence 多个接收组): 整组丢弃、保留首次接收。

    评审缺陷回归: 重收组的**检测行**曾被并进前一个已入组的首次接收组, 使同一帧出现
    多个 selected_target → 输入错误 exit 1(整条 CLI 冒烟失败)。此夹具第 6 帧起即有
    多接收组(age 8.117/372.881/393.013)。
    """
    session, frames = bridge.load_frames(str(REFIXTURE))
    assert session == "good_recheck", session
    assert len(frames) == 245, f"重收折叠后应为 245 帧, 得到 {len(frames)}"
    # 与 vision import 的统计口径一致: 358 行、58 预热行、26 个重收接收组。
    assert sum(1 for frame in frames if frame["vision_status"] == "target") == 25
    assert sum(1 for frame in frames
               if frame["vision_status"] == "target" and frame["selected_target"] is None) == 4
    assert all(frame["detection_count"] == len(frame["detections"]) for frame in frames)
    assert all(frame["selected_target"] is None
               or 0 <= frame["selected_target"] < len(frame["detections"]) for frame in frames)

    # 全量推流(一次性)必须成功: 整条流能通过同一套校验, 且每帧至多一个选中检测。
    result = run_bridge("--stub", str(REFIXTURE), "--lead-ms", "60000")
    assert result.returncode == 0, result.stderr
    assert "frames=245" in result.stderr and "duplicate_receives=26" in result.stderr, result.stderr
    lines = result.stdout.splitlines()
    assert len(lines) == 245, len(lines)
    emitted = [json.loads(line) for line in lines]
    assert sum(1 for frame in emitted if frame["selected_target"] is not None) == 21
    timestamps = [frame["vision_timestamp_ms"] for frame in emitted]
    assert timestamps == sorted(timestamps), "帧时间戳必须单调非降(sidecar 证据包要求)"
    print("OK duplicate receive groups collapse to the first receive (245 frames, 26 re-receives)")


def test_bad_inputs_fail_loud_and_empty() -> None:
    missing = run_bridge("--stub", "no/such/file.csv")
    assert missing.returncode == 1, missing.returncode
    assert missing.stdout == "", "坏输入不得产出任何帧"
    assert "输入错误" in missing.stderr, missing.stderr

    with tempfile.TemporaryDirectory() as work:
        bad = Path(work) / "bad.csv"
        header = ("sequence,vision_timestamp_ms,received_age_ms,vision_status,vision_error,"
                  "frame_width,frame_height,fps,inference_ms,detection_count,detection_index,"
                  "selected_target,class_id,target_type,confidence,bbox_x1,bbox_y1,bbox_x2,"
                  "bbox_y2,center_x,center_y,offset_x,offset_y,label\n")
        # class_id 与 target_type 矛盾: 契约硬违约, 必须在源头失败。
        bad.write_text(header + "1,1000,5,target,,640,480,5,10,1,0,1,0,bad,0.9,"
                                 "10,10,20,20,15,15,0.1,0.1,x\n", encoding="utf-8")
        result = run_bridge("--stub", str(bad))
        assert result.returncode == 1, result.returncode
        assert result.stdout == ""
        assert "class_id" in result.stderr, result.stderr

        # 非 target 帧携带检测行: 同样拒绝。
        bad.write_text(header + "1,1000,5,no_target,,640,480,5,10,1,0,,0,good,0.9,"
                                 "10,10,20,20,15,15,0.1,0.1,x\n", encoding="utf-8")
        result = run_bridge("--stub", str(bad))
        assert result.returncode == 1 and "携带检测行" in result.stderr, result.stderr
    print("OK bad inputs -> exit 1, zero stdout, explicit reason")


def test_real_mode_is_an_explicit_placeholder() -> None:
    result = run_bridge("--model-dir", "D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps")
    assert result.returncode == 3, result.returncode
    assert result.stdout == "", "真权重占位模式不得产出任何帧"
    assert "README.md" in result.stderr and "接入点" in result.stderr, result.stderr
    # 模式二选一: --stub 与 --model-dir 同时给出 = 参数错误(argparse 退出码 2)。
    both = run_bridge("--stub", str(FIXTURE), "--model-dir", "x")
    assert both.returncode == 2, both.returncode
    print("OK real-weights mode -> exit 3 placeholder (no silent stub fallback)")


def test_full_stream_emission_and_metadata() -> None:
    result = run_bridge("--stub", str(FIXTURE), "--limit", "0", "--lead-ms", "60000")
    assert result.returncode == 0
    lines = result.stdout.splitlines()
    assert len(lines) == 101, len(lines)
    assert "frames=101" in result.stderr, result.stderr
    # 提前量大于整段时长 ⇒ 一次性推完(用于快照/CI 链路, 不用于实时对齐)。
    assert "flush=per-frame" in result.stderr
    assert json.loads(lines[-1])["sequence"] == 101
    print("OK full stream emission + stderr metadata")


if __name__ == "__main__":
    assert FIXTURE.is_file(), f"夹具缺失: {FIXTURE}"
    assert REFIXTURE.is_file(), f"夹具缺失: {REFIXTURE}"
    test_fixture_normalization()
    test_jsonl_contract_and_pacing()
    test_duplicate_receive_groups_collapse_to_the_first_receive()
    test_bad_inputs_fail_loud_and_empty()
    test_real_mode_is_an_explicit_placeholder()
    test_full_stream_emission_and_metadata()
    print("ALL YOLO BRIDGE SELFTESTS PASSED")
