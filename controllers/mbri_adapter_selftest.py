#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""mbri_adapter 转换契约自测(AC2): 固定 Observation 夹具, 无需仿真器/MBri 检出。

运行: python controllers/mbri_adapter_selftest.py
覆盖: 灰度量纲与边界、缺通道报错、IR 极性/合成标记、oracle 视觉格式、
smoke 恒零、calibrated 缺 k 启动报错、左右轮方向与限幅、requestId 回显、
now=obs.t 与 tick 故障。
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from mbri_adapter import MbriAdapter  # noqa: E402


def base_obs() -> dict:
    return {
        "requestId": 7,
        "tick": 10,
        "t": 0.5,
        "sensors": {"gF": 500, "gB": 1000, "gL": 0, "gR": 250},
        "rawSensors": {"uL": 1.0, "uR": 0.0, "r": 0.5, "f": 0.1,
                       "dLB": 0.6, "dRB": 1.2},
        "objects": {"buffs": [{"X": 1.35, "Y": 1.35}],
                    "debuff": {"X": 1.6, "Y": 2.4}},
        "vehicle": {"maxSpeed": 1.5, "maxTurnRate": 4.0},
    }


class StubController:
    """替代真实 RobotController: 记录输入, 回放预设左右轮命令。"""

    def __init__(self, left: float = 0.0, right: float = 0.0):
        self.left = left
        self.right = right
        self.last_args = None

    def update(self, gray_raw, digi, analog, shovel=None, vision=None,
               now=None, healthy=True):
        self.last_args = {"gray_raw": gray_raw, "digi": digi, "analog": analog,
                          "vision": vision, "now": now, "healthy": healthy}
        return {"left": self.left, "right": self.right,
                "mode": "stub", "state": "stub", "reason": ""}


def make_oracle_adapter(stub: StubController) -> MbriAdapter:
    adapter = MbriAdapter.__new__(MbriAdapter)  # 跳过 MBri 导入
    adapter.mode = "oracle"
    adapter.k = None
    adapter.track_width = None
    adapter.ir_threshold = 0.3
    adapter.mbri_path = None
    adapter.controller = stub
    adapter.faults = 0
    adapter._last_tick = None
    return adapter


def test_gray_scaling_and_boundaries():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    obs = base_obs()
    reply = adapter.handle(obs)
    gray = stub.last_args["gray_raw"]
    assert gray["front"] == 5000.0, gray   # 500 × 10
    assert gray["rear"] == 10000.0, gray   # 1000 × 10 = ADC 满量程
    assert gray["left"] == 0.0, gray
    obs_edge = base_obs() | {"sensors": {"gF": 1000, "gB": 0, "gL": 1000, "gR": 0}}
    adapter.handle(obs_edge)
    assert stub.last_args["gray_raw"]["front"] == 10000.0
    print("OK gray scaling/boundaries")


def test_missing_gray_channel_fails_frame():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    obs = base_obs() | {"sensors": {"gF": 500}}  # 缺 gB/gL/gR
    reply = adapter.handle(obs)
    assert reply == {"v": 0.0, "w": 0.0, "requestId": 7}
    assert adapter.faults == 1
    print("OK missing gray channel -> zero action + fault")


def test_digi_ir_synthesis_and_polarity():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    adapter.handle(base_obs())
    digi = stub.last_args["digi"]
    # uL=1.0 > 0.3 → left_rear=1; uR=0.0 → right_rear=0; r=0.5 > 0.3 → rear=1
    assert digi["left_rear"] == 1 and digi["right_rear"] == 0, digi
    assert digi["rear"] == 1 and digi["front"] == 0, digi
    assert digi["valid"] == 1
    print("OK digi IR synthesis/polarity (synthesized, marked approximate)")


def test_oracle_vision_format_and_privilege_marking():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    adapter.handle(base_obs())
    vision = stub.last_args["vision"]
    assert vision["status"] == "target"
    assert vision["detections"][0] == {"type": "good", "x": 1.35, "y": 1.35,
                                       "confidence": 1.0, "oracle": True}
    assert vision["detections"][1]["type"] == "bad"
    print("OK oracle vision format (simulation truth, confidence=1.0 + oracle mark)")


def test_time_injection_uses_obs_t():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    adapter.handle(base_obs())
    assert stub.last_args["now"] == 0.5  # obs.t, 非 time.monotonic()
    print("OK time injection now=obs.t")


def test_tick_fault_on_non_monotonic():
    stub = StubController()
    adapter = make_oracle_adapter(stub)
    adapter.handle(base_obs())  # tick 10
    obs = base_obs() | {"tick": 10, "requestId": 8}  # 重复 tick
    reply = adapter.handle(obs)
    assert adapter.faults == 1 and reply["requestId"] == 8
    print("OK non-monotonic tick -> fault")


def test_request_id_echo_and_calibrated_motion():
    stub = StubController(1000, 2000)
    adapter = make_oracle_adapter(stub)
    adapter.mode = "calibrated"
    adapter.k = 0.00055
    adapter.track_width = 0.18
    reply = adapter.handle(base_obs())
    # v = (1000+2000)/2 × 0.00055 = 0.825; w = (2000−1000)×0.00055/0.18 ≈ 3.056
    assert abs(reply["v"] - 0.825) < 1e-9, reply
    assert abs(reply["w"] - 3.0555555555555556) < 1e-9, reply
    assert reply["requestId"] == 7
    print("OK calibrated motion mapping v/w + requestId echo")


def test_calibrated_motion_clamped_to_vehicle_limits():
    stub = StubController(5000, 9000)  # 超限轮速
    adapter = make_oracle_adapter(stub)
    adapter.mode = "calibrated"
    adapter.k = 0.00055
    adapter.track_width = 0.18
    reply = adapter.handle(base_obs())
    assert reply["v"] == 1.5 and reply["w"] == 4.0, reply  # 车辆限幅
    print("OK vehicle clamp (maxSpeed/maxTurnRate)")


def test_calibrated_without_k_fails_startup():
    try:
        MbriAdapter(mode="calibrated", k=None, track_width=None,
                    ir_threshold=0.3, mbri_path=None)
        raise AssertionError("expected startup error")
    except ValueError as exc:
        assert "requires --k" in str(exc)
    print("OK calibrated without k/b -> explicit startup error")


def test_smoke_mode_zero_action_no_car_import():
    adapter = MbriAdapter(mode="smoke", k=None, track_width=None,
                          ir_threshold=0.3, mbri_path=None)
    assert adapter.controller is None  # 不导入车代码
    reply = adapter.handle(base_obs())
    assert reply == {"v": 0.0, "w": 0.0, "requestId": 7}
    print("OK smoke mode zero action, no car import")


if __name__ == "__main__":
    test_gray_scaling_and_boundaries()
    test_missing_gray_channel_fails_frame()
    test_digi_ir_synthesis_and_polarity()
    test_oracle_vision_format_and_privilege_marking()
    test_time_injection_uses_obs_t()
    test_tick_fault_on_non_monotonic()
    test_request_id_echo_and_calibrated_motion()
    test_calibrated_motion_clamped_to_vehicle_limits()
    test_calibrated_without_k_fails_startup()
    test_smoke_mode_zero_action_no_car_import()
    print("ALL ADAPTER SELFTESTS PASSED")
