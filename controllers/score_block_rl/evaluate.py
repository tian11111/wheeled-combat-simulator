#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Compare deterministic PPO and same-seed FSM at SCORE_BLOCK stage entry."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import platform
from pathlib import Path

from stable_baselines3 import PPO

from gym_env import ScoreBlockEnv, resolve_dotnet_executable

DEV_EVAL_SEEDS = list(range(3001, 3011))
FINAL_HOLDOUT_SEEDS = list(range(4001, 4011))
TRAIN_EPISODE_SEEDS = {42, 20260925, *range(1000, 2000)}
MAX_POLICY_TICKS = 2400


def sha256_file(path: Path) -> str | None:
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "not-installed"


def result_metrics(seed: int, info: dict, total_reward: float) -> dict:
    target_scores = info.get("us_block_scores", 0)
    target_out = bool(info.get("target_out", False))
    contact_role = info.get("target_last_contact_role", "")
    outcome_tick = info.get("target_outcome_tick", -1)
    entry_tick = info.get("phase_entry_tick", info.get("entry_tick", -1))
    entry_pos = (info.get("target_entry_x"), info.get("target_entry_y"))
    final_pos = (info.get("target_x"), info.get("target_y"))
    score_cross_check = None
    if target_scores:
        score_cross_check = (target_out and contact_role == "us"
                             and outcome_tick > entry_tick
                             and all(value is not None for value in (*entry_pos, *final_pos))
                             and entry_pos != final_pos
                             and not bool(info.get("attribution_ambiguous", False)))
    return {
        "seed": seed,
        "phase": info.get("phase", "score_block"),
        "no_score_block": bool(info.get("no_score_block", False)),
        "stage_entry_tick": info.get("phase_entry_tick", info.get("entry_tick", -1)),
        "locked_target_index": info.get("target_index", -1),
        "policy_ticks": info.get("phase_ticks", info.get("policy_ticks", 0)),
        "locked_target_us_block_scores": target_scores,
        "us_block_score_events": info.get("us_block_score_events", 0),
        "them_block_score_events": info.get("them_block_score_events", 0),
        "target_block_offs": info.get("target_block_offs", 0),
        # The referee emits BlockOff only for exits without a valid scoring owner.
        "unowned_block_offs": info.get("unowned_block_offs", 0),
        "us_drops": info.get("us_drops", 0),
        "controller_faults": info.get("faults", 0),
        "final_score_us": info.get("score_us", 0),
        "final_score_them": info.get("score_them", 0),
        "attribution_ambiguous": bool(info.get("attribution_ambiguous", False)),
        "done_reason": info.get("done_reason", "no_score_block" if info.get("no_score_block") else ""),
        "total_reward": round(total_reward, 6),
        "target_entry_position": entry_pos,
        "target_final_position": final_pos,
        "target_out": target_out,
        "target_last_contact_role": contact_role,
        "target_outcome_tick": outcome_tick,
        "position_event_cross_check": score_cross_check,
    }


def run_seed(env: ScoreBlockEnv, seed: int, policy, use_fsm_baseline: bool) -> dict:
    obs, info = env.reset(seed=seed)
    if info.get("no_score_block"):
        return result_metrics(seed, info, 0.0)

    total_reward = 0.0
    for _ in range(MAX_POLICY_TICKS):
        if use_fsm_baseline:
            reply = env._send({"op": "step_fsm"})
        else:
            action, _ = policy.predict(obs, deterministic=True)
            reply = env._send({"op": "step", "v": float(action[0]), "w": float(action[1]) * 2.0})
        if reply.get("type") != "step":
            raise RuntimeError(f"expected step response, got {reply.get('type')!r}")
        obs = env._observation(reply)
        info = env._info(reply)
        total_reward += float(reply.get("reward", 0.0))
        if reply.get("terminated") or reply.get("truncated"):
            break
    else:
        raise RuntimeError(f"seed {seed} exceeded the {MAX_POLICY_TICKS}-tick bridge limit")
    return result_metrics(seed, info, total_reward)


def summarize(rows: list[dict]) -> dict:
    return {
        "episodes": len(rows),
        "no_score_block_episodes": sum(row["no_score_block"] for row in rows),
        "episodes_with_locked_target_us_block_score": sum(
            row["locked_target_us_block_scores"] > 0 for row in rows),
        "total_locked_target_us_block_scores": sum(
            row["locked_target_us_block_scores"] for row in rows),
        "total_us_block_score_events": sum(row["us_block_score_events"] for row in rows),
        "total_them_block_score_events": sum(row["them_block_score_events"] for row in rows),
        "total_target_block_offs": sum(row["target_block_offs"] for row in rows),
        "total_unowned_block_offs": sum(row["unowned_block_offs"] for row in rows),
        "total_us_drops": sum(row["us_drops"] for row in rows),
        "total_controller_faults": sum(row["controller_faults"] for row in rows),
        "final_score_us_sum": sum(row["final_score_us"] for row in rows),
        "final_score_them_sum": sum(row["final_score_them"] for row in rows),
        "ambiguous_attribution_episodes": sum(row["attribution_ambiguous"] for row in rows),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--seeds", default=None,
                        help="custom exploratory seeds; results are not AC4 evidence")
    parser.add_argument("--final-holdout", action="store_true",
                        help="evaluate the pre-registered final holdout (4001-4010)")
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    if args.final_holdout and args.seeds is not None:
        parser.error("--final-holdout cannot be combined with custom --seeds")
    try:
        seeds = (FINAL_HOLDOUT_SEEDS if args.final_holdout else
                 [int(item.strip()) for item in args.seeds.split(",") if item.strip()]
                 if args.seeds is not None else DEV_EVAL_SEEDS)
    except ValueError as exc:
        parser.error(f"--seeds must be a comma-separated list of integers: {exc}")
    if len(seeds) < 10 or len(set(seeds)) != len(seeds):
        parser.error("--seeds must contain at least 10 distinct seed values")
    overlap = sorted(set(seeds) & TRAIN_EPISODE_SEEDS)
    if overlap:
        parser.error(f"evaluation seeds overlap the training episode pool: {overlap}")

    if args.final_holdout and seeds != FINAL_HOLDOUT_SEEDS:
        parser.error("final holdout seeds do not match the pre-registered split")
    if args.seeds is not None and set(seeds) & set(DEV_EVAL_SEEDS + FINAL_HOLDOUT_SEEDS):
        parser.error("custom --seeds cannot reuse the development or final holdout split")

    repo_root = Path(__file__).resolve().parents[2]
    model_path = Path(args.model).expanduser().resolve()
    cli_dll = Path(args.cli_dll)
    scenario = Path(args.scenario)
    if not cli_dll.is_absolute():
        cli_dll = repo_root / cli_dll
    if not scenario.is_absolute():
        scenario = repo_root / scenario
    cli_dll = cli_dll.resolve()
    scenario = scenario.resolve()
    dotnet = resolve_dotnet_executable(args.dotnet)
    policy = PPO.load(model_path)
    if policy.observation_space.shape != (11,):
        parser.error(
            f"model observation shape {policy.observation_space.shape} is incompatible; expected (11,). "
            "Models trained with the previous 9-value observation must be retrained.")

    with ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                       seed_pool=seeds) as env:
        policy_rows = [run_seed(env, seed, policy, use_fsm_baseline=False) for seed in seeds]
        fsm_rows = [run_seed(env, seed, None, use_fsm_baseline=True) for seed in seeds]

    policy_summary = summarize(policy_rows)
    fsm_summary = summarize(fsm_rows)
    ac4_metric_gate = {
        "has_at_least_one_locked_target_score": policy_summary["total_locked_target_us_block_scores"] > 0,
        "locked_target_scores_not_below_fsm": policy_summary["total_locked_target_us_block_scores"]
            >= fsm_summary["total_locked_target_us_block_scores"],
        "us_drops_not_above_fsm": policy_summary["total_us_drops"] <= fsm_summary["total_us_drops"],
        "position_event_cross_check_available": all(
            row["position_event_cross_check"] is True
            for row in policy_rows if row["locked_target_us_block_scores"] > 0),
    }
    out_path = Path(args.out).expanduser().resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    results = {
        "protocol": "rl-env JSONL; each row begins at its own reset(seed) SCORE_BLOCK entry",
        "evaluation_split": ("final_holdout" if args.final_holdout else
                             "exploratory" if args.seeds is not None else "development"),
        "ac4_claim_eligible": bool(args.final_holdout),
        "evaluation_mode": "PPO deterministic",
        "privileged_state": True,
        "seeds": seeds,
        "model": str(model_path),
        "model_sha256": sha256_file(model_path if model_path.is_file() else model_path.with_suffix(".zip")),
        "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario),
        "cli_dll": str(cli_dll),
        "cli_dll_sha256": sha256_file(cli_dll),
        "dotnet_executable": str(Path(dotnet).expanduser().resolve()),
        "runtime": {
            "python": platform.python_version(),
            "gymnasium": package_version("gymnasium"),
            "stable-baselines3": package_version("stable-baselines3"),
            "torch": package_version("torch"),
        },
        "per_episode": {"policy": policy_rows, "fsm_baseline": fsm_rows},
        "summary": {"policy": policy_summary, "fsm_baseline": fsm_summary},
        "ac4_metric_gate": ac4_metric_gate,
        "block_off_semantics": "BlockOff is emitted by the referee only when a block exits without a valid scoring owner.",
    }
    out_path.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results["summary"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
