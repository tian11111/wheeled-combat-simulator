#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MBri RobotController ↔ 武术擂台仿真 JSONL 适配器(官方示例)。

协议(docs/CONTROLLER_PROTOCOL.md): 每帧(0.05 s)收 Observation JSON,
回 {"v": m/s, "w": rad/s, "requestId": N}。

运行模式(--mode):
  smoke       协议检查: 不导入车代码、不合成任何输入, 恒回零动作。
  oracle      决策检查: 导入 MBri RobotController 并以合成红外 + 仿真真值视觉
              (显式标记, 非真机传感器)驱动车端决策; 未提供 --k/--b 时动作恒为零。
  calibrated  运动映射: 同 oracle, 但必须显式提供 --k 与 --track-width 才做
              v/w 换算; 缺失时启动即报错(拒绝猜测系数)。

stdout 只写动作 JSONL(回显 requestId); 诊断与元数据全部写 stderr(首行为
运行参数与 calibrated/estimated 标记)。

用法示例:
  python controllers/mbri_adapter.py --mode smoke
  python controllers/mbri_adapter.py --mode oracle \
      --mbri-path "D:/project/robocup/2026/MBri" --ir-threshold 0.3
  python controllers/mbri_adapter.py --mode calibrated \
      --mbri-path "D:/project/robocup/2026/MBri" \
      --k 0.00055 --track-width 0.18 --ir-threshold 0.3
"""

from __future__ import annotations

import argparse
import json
import sys

TICK_SECONDS = 0.05
IN_PLACE_V_THRESHOLD = 0.02  # |CmdV| 判定原地转向(与 mujoco 执行器边界一致)


def clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


class MbriAdapter:
    """Observation JSON → MBri RobotController → {"v","w","requestId"}。

    转换契约(design.md):
      灰度    仿真 0-1000 → 车端 ADC 0-10000(接口量纲换算, 非光学等效);
              缺失必需通道 → 该帧报错并回零动作, 不静默制造有效信号。
      数字红外 官方场景无六路 GPIO 等价通道; oracle/calibrated 模式下由
              uL/uR/r/f 按阈值合成, 运行元数据显式标记"仿真近似"。
      视觉    objects 为仿真真值: oracle/calibrated 模式包装成 MBri detections,
              confidence 恒为 1.0 并带 oracle 标记, 不伪称 YOLO 置信度。
      电机    车端左右轮命令 L/R(自造单位)→ v=(L+R)k/2、w=(R−L)k/b;
              k/b 必须来自真机标定, 未提供时动作恒为零(estimated)。
      时间    now = obs.t(仿真时间), 校验 tick 单调与步长; 重复/跳帧记协议故障。
    """

    GRAY_SCALE = 10.0  # 仿真 0-1000 → 车端 ADC 0-10000
    GRAY_KEYS = {"gF": "front", "gB": "rear", "gL": "left", "gR": "right"}

    def __init__(self, mode: str, k: float | None, track_width: float | None,
                 ir_threshold: float, mbri_path: str | None):
        if mode not in ("smoke", "oracle", "calibrated"):
            raise ValueError(f"unknown mode: {mode}")
        if mode == "calibrated" and (k is None or k <= 0 or not track_width or track_width <= 0):
            raise ValueError(
                "mode=calibrated requires --k (> 0, m/s per wheel-command unit) "
                "and --track-width (> 0, m); refusing to guess motion scaling")
        self.mode = mode
        self.k = k
        self.track_width = track_width
        self.ir_threshold = ir_threshold
        self.mbri_path = mbri_path
        self.controller = None
        self.faults = 0
        self._last_tick: int | None = None
        if mode in ("oracle", "calibrated"):
            self._import_car(mbri_path)

    def _import_car(self, mbri_path: str | None) -> None:
        if not mbri_path:
            raise ValueError("mode=oracle/calibrated requires --mbri-path (MBri checkout)")
        if mbri_path not in sys.path:
            sys.path.insert(0, mbri_path)
        try:
            from main import RobotController  # noqa: PLC0415 — 显式路径导入车代码
        except Exception as exc:
            raise RuntimeError(f"cannot import MBri RobotController from {mbri_path}: {exc}") from exc
        self.controller = RobotController()

    def metadata_line(self) -> str:
        motion = "calibrated" if (self.mode == "calibrated" and self.k) else "estimated/zero-motion"
        return (f"[mbri-adapter] mode={self.mode} motion={motion} k={self.k} "
                f"track_width={self.track_width} ir_threshold={self.ir_threshold} "
                f"ir_source=synthesized(仿真近似) vision_source=simulation-truth(特权真值)")

    def handle(self, obs: dict) -> dict:
        """Process one Observation dict; returns the action reply dict."""
        request_id = int(obs.get("requestId") or 0)
        if self.mode == "smoke":
            return {"v": 0.0, "w": 0.0, "requestId": request_id}

        tick = obs.get("tick")
        fault = self._check_tick(tick)
        try:
            gray_raw, digi, analog = self._car_sensor_inputs(obs)
            vision = self._car_vision_input(obs)
            now = float(obs.get("t") or 0.0)  # 仿真时间注入, 禁用墙钟
        except KeyError as exc:
            self.faults += 1
            print(f"[mbri-adapter] frame fault: missing channel {exc}", file=sys.stderr)
            return {"v": 0.0, "w": 0.0, "requestId": request_id}

        try:
            result = self.controller.update(gray_raw, digi, analog, shovel=None,
                                            vision=vision, now=now, healthy=not fault)
        except Exception as exc:
            self.faults += 1
            print(f"[mbri-adapter] car decision fault: {exc}", file=sys.stderr)
            return {"v": 0.0, "w": 0.0, "requestId": request_id}

        left = float(result["left"])
        right = float(result["right"])
        if self.mode == "calibrated" and self.k and self.track_width:
            vehicle = obs.get("vehicle") or {}
            v_limit = float(vehicle.get("maxSpeed", 1.5))
            w_limit = float(vehicle.get("maxTurnRate", 4.0))
            v = (left + right) / 2.0 * self.k
            w = (right - left) * self.k / self.track_width
            reply = {"v": clamp(v, -v_limit, v_limit),
                     "w": clamp(w, -w_limit, w_limit),
                     "requestId": request_id}
        else:
            # oracle 模式: 决策可跑但无标定系数 → 拒绝物理动作映射, 动作恒为零。
            reply = {"v": 0.0, "w": 0.0, "requestId": request_id}
        return reply

    def _check_tick(self, tick) -> bool:
        """返回 True=本帧带故障(重复/非单调/步长异常); 仍按零动作应答。"""
        if tick is None:
            return False
        if self._last_tick is not None:
            if tick <= self._last_tick:
                print(f"[mbri-adapter] protocol fault: tick {tick} not increasing "
                      f"(last {self._last_tick})", file=sys.stderr)
                self.faults += 1
                return True
            if tick - self._last_tick != 1:
                print(f"[mbri-adapter] protocol fault: tick jump {self._last_tick} -> {tick}",
                      file=sys.stderr)
                self.faults += 1
        self._last_tick = tick
        return False

    def _car_sensor_inputs(self, obs: dict):
        gray = obs.get("sensors") or {}
        raw = obs.get("rawSensors") or {}
        gray_raw: dict[str, float] = {}
        for sim_key, car_key in self.GRAY_KEYS.items():
            if sim_key not in gray:
                raise KeyError(f"gray channel {sim_key} missing from observation")
            gray_raw[car_key] = clamp(float(gray[sim_key]), 0.0, 1000.0) * self.GRAY_SCALE
        if self.mode == "smoke":
            return gray_raw, {}, {}
        # 数字红外合成: 阈值化仿真近距/铲下通道(显式标记的仿真近似)。
        digi = {
            "left_rear": 1 if float(raw.get("uL", 0.0)) > self.ir_threshold else 0,
            "right_rear": 1 if float(raw.get("uR", 0.0)) > self.ir_threshold else 0,
            "rear": 1 if float(raw.get("r", 0.0)) > self.ir_threshold else 0,
            "front": 1 if float(raw.get("f", 0.0)) > self.ir_threshold else 0,
        }
        digi["left_front"] = digi["front"]
        digi["right_front"] = digi["front"]
        digi["valid"] = 1
        # 模拟对角红外: 仿真 0-1.2 → 车端 ADC 0-10000。
        analog = {
            "left": clamp(float(raw.get("dLB", 0.0)), 0.0, 1.2) * (10000.0 / 1.2),
            "right": clamp(float(raw.get("dRB", 0.0)), 0.0, 1.2) * (10000.0 / 1.2),
            "valid": 1,
        }
        return gray_raw, digi, analog

    def _car_vision_input(self, obs: dict):
        objects = obs.get("objects")
        if not objects:
            return None
        detections = []
        for b in objects.get("buffs") or []:
            detections.append({"type": "good", "x": b.get("X"), "y": b.get("Y"),
                               "confidence": 1.0, "oracle": True})
        if objects.get("debuff"):
            d = objects["debuff"]
            detections.append({"type": "bad", "x": d.get("X"), "y": d.get("Y"),
                               "confidence": 1.0, "oracle": True})
        return {"sequence": int(obs.get("requestId") or 0),
                "status": "target" if detections else "no_target",
                "detections": detections,
                "target": detections[0] if detections else None}


def main() -> None:
    parser = argparse.ArgumentParser(description="MBri RobotController ↔ sim JSONL adapter")
    parser.add_argument("--mode", choices=["smoke", "oracle", "calibrated"], default="smoke")
    parser.add_argument("--mbri-path", default=None, help="MBri checkout root (main.py 所在目录)")
    parser.add_argument("--k", type=float, default=None,
                        help="标定系数: 每轮命令单位对应的 m/s(未标定时不得提供)")
    parser.add_argument("--track-width", type=float, default=None, help="实测轮距 (m)")
    parser.add_argument("--ir-threshold", type=float, default=0.3,
                        help="合成数字红外的距离阈值(仿真近似)")
    args = parser.parse_args()

    try:
        adapter = MbriAdapter(mode=args.mode, k=args.k, track_width=args.track_width,
                              ir_threshold=args.ir_threshold, mbri_path=args.mbri_path)
    except ValueError as exc:
        print(f"[mbri-adapter] startup error: {exc}", file=sys.stderr)
        sys.exit(2)

    print(adapter.metadata_line(), file=sys.stderr, flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            obs = json.loads(line)
        except json.JSONDecodeError as exc:
            adapter.faults += 1
            print(f"[mbri-adapter] bad line: {exc}", file=sys.stderr)
            print(json.dumps({"v": 0.0, "w": 0.0}), flush=True)
            continue
        reply = adapter.handle(obs)
        sys.stdout.write(json.dumps(reply) + "\n")
        sys.stdout.flush()
    print(f"[mbri-adapter] exit; faults={adapter.faults}", file=sys.stderr)


if __name__ == "__main__":
    main()
