#!/usr/bin/env python3
"""Measure full Gym reset and strategy step on one persistent rl-env process."""

from __future__ import annotations

import argparse
import json
import math
import platform
import statistics
import time
from pathlib import Path

from gym_env import ScoreBlockEnv, resolve_dotnet_executable


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--resets", type=int, default=100)
    parser.add_argument("--steps", type=int, default=1000)
    args = parser.parse_args()
    if args.resets < 1 or args.steps < 1:
        parser.error("--resets and --steps must be positive")

    root = Path(__file__).resolve().parents[2]
    dotnet = resolve_dotnet_executable(args.dotnet)
    cli = Path(args.cli_dll)
    scenario = Path(args.scenario)
    cli = (cli if cli.is_absolute() else root / cli).resolve()
    scenario = (scenario if scenario.is_absolute() else root / scenario).resolve()
    reset_ms: list[float] = []
    step_ms: list[float] = []
    no_score_block = 0
    with ScoreBlockEnv(dotnet, str(cli), str(scenario)) as env:
        for index in range(args.resets):
            start = time.perf_counter()
            _, info = env.reset(seed=1000 + index)
            reset_ms.append((time.perf_counter() - start) * 1000)
            no_score_block += bool(info.get("no_score_block"))

        env.reset(seed=42)
        for _ in range(args.steps):
            start = time.perf_counter()
            _, _, terminated, truncated, _ = env.step((0.0, 0.0))
            step_ms.append((time.perf_counter() - start) * 1000)
            if terminated or truncated:
                env.reset(seed=42)

    output = {
        "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "os": platform.platform(),
        "python": platform.python_version(),
        "processor": platform.processor(),
        "scenario": str(scenario),
        "cli_dll": str(cli),
        "method": "persistent process; full reset includes FSM pre-roll; each step is one 0.05 s tick; reconnect/reset overhead excluded from step samples",
        "reset_seed_range": [1000, 1000 + args.resets - 1],
        "no_score_block_resets": no_score_block,
        "fullResetMs": reset_ms,
        "stepMs": step_ms,
        "fullResetP50Ms": statistics.median(reset_ms),
        "fullResetP95Ms": percentile(reset_ms, 0.95),
        "stepP50Ms": statistics.median(step_ms),
        "stepP95Ms": percentile(step_ms, 0.95),
        "stepsPerSecond": len(step_ms) / (sum(step_ms) / 1000),
    }
    path = Path(args.out).expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in output.items()
                      if key not in {"fullResetMs", "stepMs"}}, indent=2))


if __name__ == "__main__":
    main()
