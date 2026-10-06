#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""train.py 的 CLI 与 JSON 训练配置文件解析（仅标准库, 可在无 SB3 环境下单测）。

``--config <json>`` 约定 (design.md 批1 §1c):

* 配置字段 = 现有 CLI 参数的 snake_case 形式, 只接受 TRAIN_CONFIG_FIELDS 中的键,
  出现未知键直接报错拒绝 —— 拼写错误绝不静默失效;
* 配置值只作为 argparse 默认值, 显式 CLI 参数永远覆盖配置值;
* 无 ``--config`` 时 ``build_argument_parser({})`` 与历史纯 CLI 解析逐位一致
  (同样的默认值 / required / choices), 既有训练命令行为不变。

解析逻辑单独成模块, 让 Sim.Tests 能在没有 stable-baselines3/gymnasium 的环境里
直接 ``python -c "import train_config"`` 断言解析语义; train.py 是唯一产品消费方。
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

#: 配置文件允许出现的字段 (snake_case, 与 CLI 参数一一对应)。
TRAIN_CONFIG_FIELDS = (
    "steps", "out", "train_seed", "n_envs", "dotnet", "cli_dll", "scenario",
    "reward", "checkpoint_interval",
)

#: 与 train.py --reward 的 choices 保持一致 (argparse 不校验默认值, 这里显式校验)。
REWARD_CHOICES = ("v4", "aggression-v1", "aggression-v2", "aggression-v3")

#: 冻结的历史默认值 (单环境 PPO 档), 与 train.py 原常量同值。
TRAIN_SEED = 20260925
CHECKPOINT_INTERVAL_STEPS = 51_200
DEFAULT_STEPS = 500_000
DEFAULT_CLI_DLL = "src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll"
DEFAULT_SCENARIO = "scenarios/wushu-ring-2026-mujoco.json"

_INT_FIELDS = ("steps", "train_seed", "n_envs", "checkpoint_interval")
_STR_FIELDS = ("out", "cli_dll", "scenario", "reward")


def load_train_config(path: Path) -> dict[str, object]:
    """读取并校验训练配置文件, 返回可直接作为 argparse 默认值的字段表。

    ValueError 覆盖所有语义错误 (非法 JSON / 根不是对象 / 未知键 / 类型不符 /
    reward 越域), 由调用方转成 CLI 报错; OSError 保留文件系统原因。
    """
    text = Path(path).read_text(encoding="utf-8")
    try:
        payload = json.loads(text)
    except json.JSONDecodeError as exc:
        raise ValueError(f"不是合法 JSON: {exc}") from exc
    if not isinstance(payload, dict):
        raise ValueError("根节点必须是 JSON 对象")
    unknown = sorted(set(payload) - set(TRAIN_CONFIG_FIELDS))
    if unknown:
        raise ValueError("未知字段 " + ", ".join(repr(key) for key in unknown)
                         + " (允许: " + ", ".join(TRAIN_CONFIG_FIELDS) + ")")
    values: dict[str, object] = {}
    for field, raw in payload.items():
        if field in _INT_FIELDS:
            # JSON 的 true/false 在 Python 里也是 int, 必须排除。
            if isinstance(raw, bool) or not isinstance(raw, int):
                raise ValueError(f"字段 {field!r} 必须是整数, 收到 {type(raw).__name__}")
        elif field in _STR_FIELDS:
            if not isinstance(raw, str) or not raw.strip():
                raise ValueError(f"字段 {field!r} 必须是非空字符串")
        elif raw is not None and not isinstance(raw, str):
            raise ValueError("字段 'dotnet' 必须是字符串或 null")
        values[field] = raw
    reward = values.get("reward")
    if reward is not None and reward not in REWARD_CHOICES:
        raise ValueError(f"字段 'reward' 必须是 {', '.join(REWARD_CHOICES)} 之一, 收到 {reward!r}")
    return values


def build_argument_parser(config: dict[str, object] | None = None) -> argparse.ArgumentParser:
    """train.py 完整参数解析器; config 值作为可被显式 CLI 参数覆盖的默认值。"""
    values = dict(config or {})
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=None,
                        help="JSON 训练配置文件; 显式 CLI 参数覆盖配置字段")
    parser.add_argument("--steps", type=int, default=values.get("steps", DEFAULT_STEPS))
    if "out" in values:
        # 配置文件给了 out 就不再要求 CLI 必填; 显式 --out 仍然覆盖。
        parser.add_argument("--out", default=values["out"])
    else:
        parser.add_argument("--out", required=True)
    parser.add_argument("--train-seed", type=int, default=values.get("train_seed", TRAIN_SEED))
    parser.add_argument("--n-envs", type=int, default=values.get("n_envs", 1),
                        help="opt-in subprocess environments; 1 preserves the legacy path")
    parser.add_argument("--dotnet", default=values.get("dotnet"))
    parser.add_argument("--cli-dll", default=values.get("cli_dll", DEFAULT_CLI_DLL))
    parser.add_argument("--scenario", default=values.get("scenario", DEFAULT_SCENARIO))
    parser.add_argument("--reward", default=values.get("reward", "v4"),
                        choices=list(REWARD_CHOICES),
                        help="reward variant; v4 (default) keeps the frozen v4 terms byte-identical")
    parser.add_argument("--checkpoint-interval", type=int,
                        default=values.get("checkpoint_interval", CHECKPOINT_INTERVAL_STEPS),
                        help="global transitions between CheckpointCallback snapshots")
    return parser
