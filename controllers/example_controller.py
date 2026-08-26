#!/usr/bin/env python3
"""示例外部策略 — 演示 decide(obs) -> {"v": float, "w": float} 协议。

协议（JSONL stdio，与遗留桥一致）:
  1. 每个仿真 tick 从 stdin 读一行观测 JSON（obs）;
  2. 向 stdout 写一行 {"v": .., "w": .., "requestId": <obs.requestId 原样回显>};
  3. 行必须是合法 JSON 且含有限数值 v/w，否则被桥丢弃并按零动作处理。

本策略: 冲向最近的未出局增益块; 无块可用时巡航到擂台中心。
仅作协议演示，非竞技级策略。
"""
import json
import math
import sys

CENTER = (1.9, 1.9)


def decide(obs):
    robot = obs["robot"]
    objects = obs.get("objects") or {}
    buffs = [b for b in (objects.get("buffs") or []) if not b.get("out")]

    if buffs:
        nearest = min(
            buffs,
            key=lambda b: math.hypot(b["x"] - robot["x"], b["y"] - robot["y"]),
        )
        target = (nearest["x"], nearest["y"])
        label = "追击增益块"
    else:
        target = CENTER
        label = "回中心"

    dx = target[0] - robot["x"]
    dy = target[1] - robot["y"]
    distance = math.hypot(dx, dy)
    if distance < 0.05:
        return {"v": 0.0, "w": 0.0, "requestId": obs.get("requestId"), "note": label}

    heading_err = (math.atan2(dy, dx) - robot["th"] + math.pi) % (2 * math.pi) - math.pi
    v = 1.2 if abs(heading_err) < 0.6 else 0.25
    w = max(-4.0, min(4.0, 2.5 * heading_err))
    return {"v": round(v, 3), "w": round(w, 3), "requestId": obs.get("requestId"), "note": label}


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            obs = json.loads(line)
        except json.JSONDecodeError as exc:
            print(json.dumps({"error": f"bad observation json: {exc}"}), file=sys.stderr, flush=True)
            continue
        try:
            action = decide(obs)
        except Exception as exc:  # 策略内部异常也必须回零动作，绝不能崩掉整场评测
            print(json.dumps({"error": str(exc)}), file=sys.stderr, flush=True)
            action = {"v": 0.0, "w": 0.0, "requestId": obs.get("requestId")}
        print(json.dumps(action), flush=True)


if __name__ == "__main__":
    main()
