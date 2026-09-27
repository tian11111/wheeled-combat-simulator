#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Measure repeated parallel suites of independent single-env PPO runs.

Each suite launches the five preregistered v4 training seeds concurrently,
keeps per-process logs and synchronized host/process resource samples, and
audits every completed run from its run-config.json before computing a suite
result. A failed run remains in the suite result and makes the suite invalid.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import platform
import statistics
import subprocess
import sys
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import psutil

from train_artifacts import (
    RUN_CONFIG_NAME,
    audit_checkpoints,
    sha256_file,
)

TRAIN_SEEDS = (20260927, 20260928, 20260929, 20260930, 20261001)
TRAIN_SCRIPT = Path(__file__).resolve().with_name("train.py")
REPO_ROOT = TRAIN_SCRIPT.parents[2]
DEFAULT_SCENARIO = REPO_ROOT / "scenarios/wushu-ring-2026-mujoco.json"
DEFAULT_CLI_DLL = REPO_ROOT / "src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll"
DEFAULT_CHECKPOINT_INTERVAL = 51_200
TARGET_STEPS = 500_000
MIN_SUITE_COUNT = 3


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    weight = position - lower
    return ordered[lower] * (1.0 - weight) + ordered[upper] * weight


def process_tree_sample(pid: int, state: dict[str, float]) -> tuple[float, int]:
    """Return sampled process-tree CPU percent and resident bytes."""
    try:
        root = psutil.Process(pid)
        members = [root, *root.children(recursive=True)]
    except psutil.Error:
        return 0.0, 0
    cpu_seconds = 0.0
    rss_bytes = 0
    for process in members:
        try:
            times = process.cpu_times()
            cpu_seconds += times.user + times.system
            rss_bytes += process.memory_info().rss
        except psutil.Error:
            continue
    sampled_at = time.monotonic()
    previous_cpu = state.get("cpu_seconds")
    previous_at = state.get("sampled_at")
    cpu_percent = 0.0
    if previous_cpu is not None and previous_at is not None and sampled_at > previous_at:
        cpu_percent = max(0.0, cpu_seconds - previous_cpu) * 100.0 / (sampled_at - previous_at)
    state["cpu_seconds"] = cpu_seconds
    state["sampled_at"] = sampled_at
    return cpu_percent, rss_bytes


MODEL_STATE_MEMBERS = ("policy.pth", "policy.optimizer.pth", "pytorch_variables.pth")


def model_state_hash(path: Path) -> str | None:
    """Hash learned weights and optimizer state, excluding run-specific metadata."""
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    try:
        with zipfile.ZipFile(path) as archive:
            if not set(MODEL_STATE_MEMBERS).issubset(archive.namelist()):
                return None
            for name in MODEL_STATE_MEMBERS:
                content = archive.read(name)
                digest.update(name.encode("utf-8"))
                digest.update(b"\0")
                digest.update(content)
                digest.update(b"\0")
    except (OSError, zipfile.BadZipFile):
        return None
    return digest.hexdigest()


def load_run_result(run: dict[str, Any], requested_steps: int) -> dict[str, Any]:
    config_path = run["out_dir"] / RUN_CONFIG_NAME
    result: dict[str, Any] = {
        "train_seed": run["train_seed"],
        "pid": run["pid"],
        "started_at_utc": run["started_at_utc"],
        "ended_at_utc": run.get("ended_at_utc"),
        "elapsed_seconds": round(run.get("elapsed_seconds", 0.0), 3),
        "exit_code": run["exit_code"],
        "stdout_log": str(run["log_path"].resolve()),
        "run_config": str(config_path.resolve()),
        "run_config_sha256": sha256_file(config_path),
        "max_sampled_process_tree_rss_bytes": run["max_rss_bytes"],
        "max_sampled_process_tree_cpu_percent": round(run["max_cpu_percent"], 3),
        "faults_total": None,
        "actual_global_transitions": None,
        "model_sha256": None,
        "model_state_sha256": model_state_hash(run["out_dir"] / "ppo_score_block.zip"),
        "checkpoint_count": None,
        "checkpoint_hashes": [],
        "audit": {"ok": False, "issues": ["run-config.json missing"]},
        "valid": False,
    }
    if not config_path.is_file():
        result["status"] = "missing_run_config"
        return result

    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
        audit = audit_checkpoints(config, run["out_dir"])
    except (OSError, ValueError, KeyError, TypeError) as exc:
        result["status"] = "invalid_run_config"
        result["audit"] = {"ok": False, "issues": [f"manifest audit raised {type(exc).__name__}: {exc}"]}
        return result

    checkpoints = config.get("checkpoints") or []
    result.update({
        "status": config.get("status"),
        "faults_total": config.get("faults_total"),
        "actual_global_transitions": config.get("actual_global_transitions"),
        "model_sha256": config.get("model_zip_sha256"),
        "model_state_sha256": model_state_hash(run["out_dir"] / "ppo_score_block.zip"),
        "checkpoint_count": len(checkpoints),
        "checkpoint_hashes": [
            {"training_steps": row.get("training_steps"), "sha256": row.get("sha256")}
            for row in checkpoints
        ],
        "scenario_sha256": config.get("scenario_sha256"),
        "cli_dll_sha256": config.get("cli_dll_sha256"),
        "code_identity": config.get("code_identity"),
        "hardware_identity": config.get("hardware_identity"),
        "dependencies": config.get("dependencies"),
        "dotnet_runtime": config.get("dotnet_runtime"),
        "train_seed_recorded": config.get("train_seed"),
        "n_envs": config.get("n_envs"),
        "ppo_parameters": config.get("ppo_parameters"),
        "episode_seed_streams": config.get("episode_seed_streams"),
        "audit": audit,
    })
    issues: list[str] = []
    if run["exit_code"] != 0:
        issues.append(f"training process exit code was {run['exit_code']}")
    if config.get("status") != "completed":
        issues.append(f"training status was {config.get('status')!r}")
    if config.get("train_seed") != run["train_seed"]:
        issues.append("run-config train_seed does not match the requested seed")
    if config.get("n_envs") != 1:
        issues.append(f"n_envs was {config.get('n_envs')!r}, expected 1")
    actual_steps = config.get("actual_global_transitions")
    if not isinstance(actual_steps, int) or actual_steps < requested_steps:
        issues.append(f"actual transitions {actual_steps!r} below {requested_steps}")
    if config.get("faults_total") != 0:
        issues.append(f"faults_total was {config.get('faults_total')!r}")
    if not audit.get("ok"):
        issues.extend(audit.get("issues") or ["checkpoint/artifact audit failed"])
    if not result["model_sha256"]:
        issues.append("final model hash is missing")
    result["valid"] = not issues
    result["validation_issues"] = issues
    return result


def compare_run_identities(runs: list[dict[str, Any]]) -> list[str]:
    """Require equivalent runtime/build identity within a suite."""
    fields = (
        "scenario_sha256", "cli_dll_sha256", "code_identity", "hardware_identity",
        "dependencies", "dotnet_runtime",
    )
    issues: list[str] = []
    completed = [run for run in runs if run.get("scenario_sha256") is not None]
    if len(completed) != len(runs):
        issues.append("one or more runs have no identity-bearing run-config.json")
    if completed:
        baseline = completed[0]
        for other in completed[1:]:
            for field in fields:
                if other.get(field) != baseline.get(field):
                    issues.append(f"seed {other['train_seed']} has a different {field}")
    return issues


def run_suite(suite_index: int, args, out_root: Path) -> dict[str, Any]:
    suite_dir = out_root / f"suite-{suite_index:02d}"
    suite_dir.mkdir()
    logs_dir = suite_dir / "logs"
    logs_dir.mkdir()
    resources_path = suite_dir / "resources.csv"
    started_at = utc_now()
    started_mono = time.monotonic()
    processes: list[dict[str, Any]] = []

    # Keep every child independent and on the explicitly frozen single-env path.
    for train_seed in TRAIN_SEEDS:
        run_dir = suite_dir / f"seed-{train_seed}"
        log_path = logs_dir / f"seed-{train_seed}.log"
        command = [
            sys.executable, "-X", "utf8", str(TRAIN_SCRIPT),
            "--steps", str(args.steps),
            "--train-seed", str(train_seed),
            "--n-envs", "1",
            "--checkpoint-interval", str(args.checkpoint_interval),
            "--dotnet", args.dotnet,
            "--cli-dll", str(args.cli_dll),
            "--scenario", str(args.scenario),
            "--out", str(run_dir),
        ]
        run_started = utc_now()
        run_started_mono = time.monotonic()
        with log_path.open("w", encoding="utf-8", newline="") as log_stream:
            process = subprocess.Popen(
                command,
                cwd=REPO_ROOT,
                stdout=log_stream,
                stderr=subprocess.STDOUT,
            )
        processes.append({
            "train_seed": train_seed,
            "pid": process.pid,
            "process": process,
            "command": command,
            "out_dir": run_dir,
            "log_path": log_path,
            "started_at_utc": run_started,
            "started_mono": run_started_mono,
            "ended_at_utc": None,
            "elapsed_seconds": 0.0,
            "exit_code": None,
            "max_rss_bytes": 0,
            "max_cpu_percent": 0.0,
            "cpu_samples": [],
            "rss_samples": [],
            "cpu_sample_state": {},
        })

    host_cpu_samples: list[float] = []
    host_memory_samples: list[float] = []
    available_memory_samples: list[int] = []
    psutil.cpu_percent(interval=None)  # prime the host CPU sampler
    with resources_path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=(
            "timestamp_utc", "suite_elapsed_seconds", "train_seed", "pid", "process_state",
            "process_tree_cpu_percent", "process_tree_rss_bytes", "host_cpu_percent",
            "host_memory_percent", "host_available_bytes",
        ))
        writer.writeheader()
        while True:
            timestamp = utc_now()
            suite_elapsed = time.monotonic() - started_mono
            host_cpu = psutil.cpu_percent(interval=None)
            memory = psutil.virtual_memory()
            host_cpu_samples.append(host_cpu)
            host_memory_samples.append(memory.percent)
            available_memory_samples.append(int(memory.available))
            all_exited = True
            for run in processes:
                process = run["process"]
                cpu_percent, rss_bytes = process_tree_sample(run["pid"], run["cpu_sample_state"])
                run["max_cpu_percent"] = max(run["max_cpu_percent"], cpu_percent)
                run["max_rss_bytes"] = max(run["max_rss_bytes"], rss_bytes)
                if process.poll() is None:
                    state = "running"
                    all_exited = False
                    run["cpu_samples"].append(cpu_percent)
                    run["rss_samples"].append(rss_bytes)
                else:
                    state = "exited"
                    if run["exit_code"] is None:
                        run["exit_code"] = process.returncode
                        run["ended_at_utc"] = timestamp
                        run["elapsed_seconds"] = time.monotonic() - run["started_mono"]
                writer.writerow({
                    "timestamp_utc": timestamp,
                    "suite_elapsed_seconds": round(suite_elapsed, 3),
                    "train_seed": run["train_seed"],
                    "pid": run["pid"],
                    "process_state": state,
                    "process_tree_cpu_percent": round(cpu_percent, 3),
                    "process_tree_rss_bytes": rss_bytes,
                    "host_cpu_percent": host_cpu,
                    "host_memory_percent": memory.percent,
                    "host_available_bytes": memory.available,
                })
            stream.flush()
            if all_exited:
                break
            time.sleep(args.sample_interval)

    ended_at = utc_now()
    elapsed_seconds = time.monotonic() - started_mono
    for run in processes:
        if run["exit_code"] is None:
            run["exit_code"] = run["process"].wait()
            run["ended_at_utc"] = ended_at
            run["elapsed_seconds"] = time.monotonic() - run["started_mono"]
        del run["process"]
        del run["started_mono"]

    run_results = [load_run_result(run, args.steps) for run in processes]
    identity_issues = compare_run_identities(run_results)
    valid = len(run_results) == len(TRAIN_SEEDS) and all(run["valid"] for run in run_results)
    valid = valid and not identity_issues
    all_episode_seeds = [
        seed
        for run in run_results
        for worker in (run.get("episode_seed_streams") or {}).get("workers", [])
        for seed in ([worker.get("initial_episode_seed")] + list(worker.get("subsequent_seeds") or []))
        if isinstance(seed, int)
    ]
    seed_isolation_issues = []
    if len(all_episode_seeds) != len(set(all_episode_seeds)):
        seed_isolation_issues.append("episode seed streams overlap across training seeds")
    if {run.get("train_seed_recorded") for run in run_results} != set(TRAIN_SEEDS):
        seed_isolation_issues.append("the completed run set does not contain each preregistered training seed once")
    valid = valid and not seed_isolation_issues
    result = {
        "suite_index": suite_index,
        "status": "valid" if valid else "invalid",
        "started_at_utc": started_at,
        "ended_at_utc": ended_at,
        "wall_seconds": round(elapsed_seconds, 3),
        "requested_steps_per_seed": args.steps,
        "train_seeds": list(TRAIN_SEEDS),
        "run_count": len(run_results),
        "valid_run_count": sum(run["valid"] for run in run_results),
        "resource_samples": len(host_cpu_samples),
        "host_cpu_percent": {
            "median": statistics.median(host_cpu_samples) if host_cpu_samples else None,
            "p95": percentile(host_cpu_samples, 0.95),
            "max": max(host_cpu_samples) if host_cpu_samples else None,
        },
        "host_memory_percent": {
            "median": statistics.median(host_memory_samples) if host_memory_samples else None,
            "max": max(host_memory_samples) if host_memory_samples else None,
            "minimum_available_bytes": min(available_memory_samples) if available_memory_samples else None,
        },
        "resources_csv": str(resources_path.resolve()),
        "identity_issues": identity_issues,
        "seed_isolation_issues": seed_isolation_issues,
        "unique_episode_seed_count": len(set(all_episode_seeds)),
        "episode_seed_count": len(all_episode_seeds),
        "runs": run_results,
    }
    result_path = suite_dir / "suite-result.json"
    result_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "suite_index": suite_index,
        "status": result["status"],
        "wall_seconds": result["wall_seconds"],
        "valid_runs": result["valid_run_count"],
        "host_cpu_median": result["host_cpu_percent"]["median"],
        "result": str(result_path.resolve()),
    }, ensure_ascii=False), flush=True)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out-root", required=True, help="fresh directory for all suite artifacts")
    parser.add_argument("--dotnet", required=True, help="absolute .NET SDK/runtime executable")
    parser.add_argument("--steps", type=int, default=500_000)
    parser.add_argument("--suite-count", type=int, default=3)
    parser.add_argument("--first-suite", type=int, default=1)
    parser.add_argument("--checkpoint-interval", type=int, default=DEFAULT_CHECKPOINT_INTERVAL)
    parser.add_argument("--sample-interval", type=float, default=1.0)
    parser.add_argument("--scenario", type=Path, default=DEFAULT_SCENARIO)
    parser.add_argument("--cli-dll", type=Path, default=DEFAULT_CLI_DLL)
    args = parser.parse_args()
    if args.steps <= 0 or args.suite_count <= 0 or args.first_suite < 1:
        parser.error("steps/suite-count must be positive and first-suite must be >= 1")
    if args.checkpoint_interval <= 0 or args.sample_interval <= 0:
        parser.error("checkpoint-interval and sample-interval must be positive")

    out_root = Path(args.out_root).expanduser().resolve()
    if out_root.exists():
        if not out_root.is_dir() or any(out_root.iterdir()):
            parser.error(f"output root already exists and is not empty: {out_root}")
    else:
        out_root.mkdir(parents=True)
    args.scenario = args.scenario.expanduser().resolve()
    args.cli_dll = args.cli_dll.expanduser().resolve()
    if not Path(args.dotnet).is_file():
        parser.error(f".NET executable does not exist: {args.dotnet}")
    if not args.scenario.is_file() or not args.cli_dll.is_file():
        parser.error("scenario and CLI DLL must exist before a throughput suite starts")

    runner_config = {
        "runner": str(Path(__file__).resolve()),
        "runner_sha256": sha256_file(Path(__file__)),
        "python": sys.version,
        "platform": platform.platform(),
        "logical_cpu_count": os.cpu_count(),
        "scenario": str(args.scenario),
        "scenario_sha256": sha256_file(args.scenario),
        "cli_dll": str(args.cli_dll),
        "cli_dll_sha256": sha256_file(args.cli_dll),
        "dotnet": str(Path(args.dotnet).resolve()),
        "steps_per_seed": args.steps,
        "suite_count": args.suite_count,
        "first_suite": args.first_suite,
        "train_seeds": list(TRAIN_SEEDS),
        "n_envs_per_process": 1,
        "sample_interval_seconds": args.sample_interval,
    }
    (out_root / "runner-config.json").write_text(
        json.dumps(runner_config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    suites = []
    for suite_index in range(args.first_suite, args.first_suite + args.suite_count):
        suites.append(run_suite(suite_index, args, out_root))
        aggregate = {
            "status": "complete" if len(suites) == args.suite_count else "running",
            "requested_suite_count": args.suite_count,
            "completed_suite_count": len(suites),
            "valid_suite_count": sum(suite["status"] == "valid" for suite in suites),
            "suite_wall_seconds": [suite["wall_seconds"] for suite in suites],
            "suite_wall_seconds_median": statistics.median(
                suite["wall_seconds"] for suite in suites),
            "suites": suites,
        }
        (out_root / "aggregate.json").write_text(
            json.dumps(aggregate, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    model_hashes_by_seed: dict[str, list[str | None]] = {}
    for suite in suites:
        for run in suite["runs"]:
            model_hashes_by_seed.setdefault(str(run["train_seed"]), []).append(
                run.get("model_state_sha256"))
    fixed_seed_reproducible = (
        len(model_hashes_by_seed) == len(TRAIN_SEEDS)
        and all(len(hashes) == args.suite_count and len(set(hashes)) == 1 and hashes[0]
                for hashes in model_hashes_by_seed.values())
    )
    all_valid = len(suites) == args.suite_count and all(
        suite["status"] == "valid" for suite in suites)
    aggregate["all_suites_valid"] = all_valid
    aggregate["fixed_seed_model_state_hashes_match_across_suites"] = fixed_seed_reproducible
    aggregate["model_state_hashes_by_seed"] = model_hashes_by_seed
    aggregate["model_state_hash_algorithm"] = list(MODEL_STATE_MEMBERS)
    spec_conformant = (
        args.steps == TARGET_STEPS
        and args.suite_count >= MIN_SUITE_COUNT
        and args.first_suite == 1
    )
    aggregate["spec_conformant_measurement_size"] = spec_conformant
    aggregate["acceptance_gate"] = (
        "GO" if (all_valid and fixed_seed_reproducible and spec_conformant
                 and aggregate["suite_wall_seconds_median"] <= 3600) else "NO-GO")
    (out_root / "aggregate.json").write_text(
        json.dumps(aggregate, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": aggregate["status"],
        "valid_suites": aggregate["valid_suite_count"],
        "median_wall_seconds": aggregate["suite_wall_seconds_median"],
        "acceptance_gate": aggregate["acceptance_gate"],
        "aggregate": str((out_root / "aggregate.json").resolve()),
    }, ensure_ascii=False), flush=True)
    return 0 if all_valid else 1


if __name__ == "__main__":
    sys.exit(main())
