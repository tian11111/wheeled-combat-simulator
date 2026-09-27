#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Opt-in, staged throughput profiler for the SCORE_BLOCK PPO environment."""

from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.metadata
import json
import math
import os
import platform
import queue
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

import numpy as np
import psutil
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.monitor import Monitor

from gym_env import ScoreBlockEnv, resolve_dotnet_executable

RESOURCE_INTERVAL_SECONDS = 0.2
JSONL_TIMEOUT_SECONDS = 30.0
TRAIN_SEED = 20260925
WARMUP_STEPS = 100
PPO_WARMUP_UPDATES = 5
MIN_IPC_ROUND_TRIPS = 1000


# This requested script name shadows Python's stdlib ``profile`` module when
# launched by path. PyTorch imports ``cProfile`` during optimizer setup, and
# cProfile expects these two standard-library entry points to exist.
def run(statement, filename=None, sort=-1):
    """Run a statement under cProfile (stdlib compatibility entry point)."""
    import cProfile
    return cProfile.run(statement, filename, sort)


def runctx(statement, globals, locals, filename=None, sort=-1):
    """Run a statement under cProfile with explicit namespaces."""
    import cProfile
    return cProfile.runctx(statement, globals, locals, filename, sort)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return float(ordered[max(0, math.ceil(fraction * len(ordered)) - 1)])


def distribution(values: list[float]) -> dict[str, Any]:
    clean = [float(value) for value in values if math.isfinite(float(value))]
    return {
        "sample_count": len(clean),
        "p50": statistics.median(clean) if clean else None,
        "p95": percentile(clean, 0.95),
        "min": min(clean) if clean else None,
        "max": max(clean) if clean else None,
    }


def compute_ipc_fraction(end_to_end_ms: list[float], in_process_ms: list[float]) -> float | None:
    end_to_end = [float(value) for value in end_to_end_ms
                  if isinstance(value, (int, float)) and math.isfinite(float(value))]
    in_process = [float(value) for value in in_process_ms
                  if isinstance(value, (int, float)) and math.isfinite(float(value))]
    if not end_to_end or not in_process or statistics.median(end_to_end) <= 0:
        return None
    return max(0.0, statistics.median(end_to_end) - statistics.median(in_process)) \
        / statistics.median(end_to_end)


REQUIRED_MANIFEST_FIELDS = (
    "machine", "python", "dotnet_version", "dotnet_executable", "dependencies",
    "scenario", "scenario_sha256", "cli_dll", "cli_dll_sha256",
    "ppo_hyperparameters", "warmup", "effective_parameters", "random_seed",
)


def validate_manifest(manifest: dict[str, Any]) -> list[str]:
    return [key for key in REQUIRED_MANIFEST_FIELDS if key not in manifest]


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unavailable"


class JsonlProcess:
    """Persistent JSONL subprocess with the same write/flush/readline path as gym_env._send."""

    def __init__(self, command: list[str], cwd: Path | None = None):
        self.proc = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None,
            text=True, encoding="utf-8", errors="replace", bufsize=1, cwd=cwd)
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._reader = threading.Thread(target=self._read_stdout, daemon=True)
        self._reader.start()

    def _read_stdout(self) -> None:
        assert self.proc.stdout is not None
        for line in self.proc.stdout:
            self._lines.put(line)
        self._lines.put(None)

    def send(self, payload: dict[str, Any]) -> dict[str, Any]:
        if self.proc.poll() is not None or self.proc.stdin is None:
            raise RuntimeError(f"JSONL process exited with code {self.proc.poll()}")
        self.proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        self.proc.stdin.flush()
        try:
            line = self._lines.get(timeout=JSONL_TIMEOUT_SECONDS)
        except queue.Empty as exc:
            raise TimeoutError(f"JSONL response exceeded {JSONL_TIMEOUT_SECONDS}s") from exc
        if line is None:
            raise RuntimeError(f"JSONL process closed stdout (exit code {self.proc.poll()})")
        reply = json.loads(line)
        if not isinstance(reply, dict):
            raise RuntimeError("JSONL response must be an object")
        return reply

    def close(self) -> None:
        if self.proc.poll() is None:
            if self.proc.stdin is not None:
                try:
                    self.proc.stdin.close()
                except OSError:
                    pass
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.terminate()
                try:
                    self.proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.proc.kill()
                    self.proc.wait()
        for stream in (self.proc.stdin, self.proc.stdout):
            if stream is not None and not stream.closed:
                stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        self.close()


class ResourceSampler:
    def __init__(self, interval: float):
        self.interval = interval
        self.rows: list[dict[str, Any]] = []
        self._phase = "idle"
        self._dotnet_pid: int | None = None
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()

    def set_phase(self, phase: str, dotnet_pid: int | None) -> None:
        with self._lock:
            self._phase, self._dotnet_pid = phase, dotnet_pid

    def _sample(self) -> None:
        with self._lock:
            phase, dotnet_pid = self._phase, self._dotnet_pid
        per_cpu = psutil.cpu_percent(interval=None, percpu=True)
        try:
            python_rss = psutil.Process(os.getpid()).memory_info().rss
        except psutil.Error:
            python_rss = None
        try:
            dotnet_rss = psutil.Process(dotnet_pid).memory_info().rss if dotnet_pid else None
        except psutil.Error:
            dotnet_rss = None
        memory = psutil.virtual_memory()
        self.rows.append({
            "utc_epoch_seconds": time.time(),
            "phase": phase,
            "python_rss_bytes": python_rss,
            "dotnet_rss_bytes": dotnet_rss,
            "host_cpu_percent": statistics.mean(per_cpu) if per_cpu else None,
            "logical_cpu_percent": per_cpu,
            "host_memory_used_percent": memory.percent,
            "host_memory_available_bytes": memory.available,
        })

    def _run(self) -> None:
        while not self._stop.wait(self.interval):
            self._sample()

    def __enter__(self):
        psutil.cpu_percent(interval=None, percpu=True)
        self._sample()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *_exc):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self.interval * 2 + 1)
        self._sample()


def find_dotnet_pid(value: Any, depth: int = 0) -> int | None:
    if value is None or depth > 8:
        return None
    proc = getattr(value, "_proc", None)
    if proc is not None and getattr(proc, "pid", None):
        return proc.pid
    for attribute in ("venv", "env", "envs"):
        nested = getattr(value, attribute, None)
        if isinstance(nested, list):
            for item in nested:
                found = find_dotnet_pid(item, depth + 1)
                if found:
                    return found
        elif nested is not None:
            found = find_dotnet_pid(nested, depth + 1)
            if found:
                return found
    return None


def append_round_fault(record: dict[str, Any], exc: BaseException) -> None:
    record["status"] = "invalid"
    record.setdefault("faults", []).append({
        "type": type(exc).__name__, "message": str(exc),
    })


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def write_resources(path: Path, rows: list[dict[str, Any]]) -> None:
    fields = ["utc_epoch_seconds", "phase", "python_rss_bytes", "dotnet_rss_bytes",
              "host_cpu_percent", "logical_cpu_percent", "host_memory_used_percent",
              "host_memory_available_bytes"]
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for row in rows:
            output = dict(row)
            output["logical_cpu_percent"] = json.dumps(row["logical_cpu_percent"])
            writer.writerow(output)


def reset_and_step(env: ScoreBlockEnv, steps: int, seed: int) -> list[dict[str, Any]]:
    samples = []
    env.reset(seed=seed)
    for _ in range(steps):
        _, _, terminated, truncated, _ = env.step((0.0, 0.0))
        if terminated or truncated:
            env.reset(seed=seed)
    return samples


def measure_env_round(env: ScoreBlockEnv, round_index: int, resets: int,
                      steps: int) -> dict[str, Any]:
    record: dict[str, Any] = {
        "stage": "env", "round": round_index, "status": "valid", "faults": [],
        "warmup": {"discard_reset_count": 1, "discard_step_count": WARMUP_STEPS},
        "samples": {"reset_ms": [], "step_ms": []},
    }
    try:
        reset_and_step(env, WARMUP_STEPS, TRAIN_SEED + round_index)
        for index in range(resets):
            started = time.perf_counter()
            env.reset(seed=1000 + round_index * resets + index)
            record["samples"]["reset_ms"].append((time.perf_counter() - started) * 1000)
        env.reset(seed=42)
        for _ in range(steps):
            started = time.perf_counter()
            _, _, terminated, truncated, _ = env.step((0.0, 0.0))
            record["samples"]["step_ms"].append((time.perf_counter() - started) * 1000)
            if terminated or truncated:
                env.reset(seed=42)
    except Exception as exc:
        append_round_fault(record, exc)
    return record


def echo_script() -> str:
    return "import sys\nfor line in sys.stdin:\n sys.stdout.write(line)\n sys.stdout.flush()\n"


def measure_ipc_round(root: Path, dotnet: str, cli: Path, scenario: Path,
                      round_index: int, round_trips: int, quick_warmup: int,
                      sampler: ResourceSampler) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    record: dict[str, Any] = {
        "stage": "ipc", "round": round_index, "status": "valid", "faults": [],
        "samples": {"echo_round_trip_ms": [], "reset_round_trip_ms": [],
                    "policy_step_end_to_end_ms": [], "policy_step_tick_ms": []},
        "timing": {"enabled_for_real_rl_env": True, "missing_count": 0,
                   "degraded": False},
    }
    payload = {"op": "step", "v": 0.0, "w": 0.0}
    echo_samples = record["samples"]["echo_round_trip_ms"]
    measured = []
    try:
        with JsonlProcess([sys.executable, "-u", "-c", echo_script()], cwd=root) as echo:
            sampler.set_phase("ipc_echo", None)
            for index in range(quick_warmup + round_trips):
                started = time.perf_counter()
                reply = echo.send(payload)
                elapsed = (time.perf_counter() - started) * 1000
                if reply != payload:
                    raise RuntimeError("JSONL echo response differed from request")
                if index >= quick_warmup:
                    echo_samples.append(elapsed)

        sampler.set_phase("ipc_rl_env", None)
        with JsonlProcess([dotnet, str(cli), "rl-env", "--scenario", str(scenario),
                           "--duration", "120"], cwd=root) as client:
            def request(body: dict[str, Any]) -> tuple[dict[str, Any], float]:
                started = time.perf_counter()
                response = client.send(body)
                return response, (time.perf_counter() - started) * 1000

            response, _ = request({"op": "reset", "seed": TRAIN_SEED + round_index,
                                   "timing": True})
            if response.get("type") != "reset":
                raise RuntimeError(f"expected reset response, got {response.get('type')!r}")
            for _ in range(quick_warmup):
                response, _ = request({"op": "step", "v": 0.0, "w": 0.0, "timing": True})
                if response.get("terminated") or response.get("truncated"):
                    response, _ = request({"op": "reset", "seed": TRAIN_SEED + round_index,
                                           "timing": True})
            response, elapsed = request({"op": "reset", "seed": 2000 + round_index,
                                         "timing": True})
            record["samples"]["reset_round_trip_ms"].append(elapsed)
            if response.get("type") != "reset":
                raise RuntimeError(f"expected reset response, got {response.get('type')!r}")
            sampler.set_phase("ipc_rl_env", client.proc.pid)
            for _ in range(round_trips):
                response, elapsed = request({"op": "step", "v": 0.0, "w": 0.0,
                                             "timing": True})
                if response.get("type") != "step":
                    raise RuntimeError(f"expected step response, got {response.get('type')!r}")
                info = response.get("info")
                timing = info.get("timing") if isinstance(info, dict) else None
                tick_ms = timing.get("tickMs") if isinstance(timing, dict) else None
                valid_tick = isinstance(tick_ms, (int, float)) and math.isfinite(float(tick_ms))
                if valid_tick:
                    record["samples"]["policy_step_tick_ms"].append(float(tick_ms))
                else:
                    record["samples"]["policy_step_tick_ms"].append(None)
                    record["timing"]["missing_count"] += 1
                    record["timing"]["degraded"] = True
                record["samples"]["policy_step_end_to_end_ms"].append(elapsed)
                measured.append({"end_to_end_ms": elapsed,
                                 "tick_ms": float(tick_ms) if valid_tick else None})
                if response.get("terminated") or response.get("truncated"):
                    response, _ = request({"op": "reset", "seed": 2000 + round_index,
                                           "timing": True})
    except Exception as exc:
        append_round_fault(record, exc)
    return record, measured


def ppo_update(model: PPO, callback: BaseCallback, total_timesteps: int) -> tuple[float, float]:
    assert model.env is not None
    started = time.perf_counter()
    collected = model.collect_rollouts(
        model.env, callback, model.rollout_buffer, n_rollout_steps=model.n_steps)
    rollout_seconds = time.perf_counter() - started
    if not collected:
        raise RuntimeError("SB3 callback stopped rollout before the full n_steps")
    model._update_current_progress_remaining(model.num_timesteps, total_timesteps)
    started = time.perf_counter()
    model.train()
    train_seconds = time.perf_counter() - started
    return rollout_seconds, train_seconds


def ppo_snapshot(model: PPO) -> dict[str, Any]:
    return {
        "policy": "MlpPolicy", "seed": TRAIN_SEED, "n_envs": model.n_envs,
        "n_steps": model.n_steps, "batch_size": model.batch_size,
        "n_epochs": model.n_epochs, "gamma": model.gamma,
        "gae_lambda": model.gae_lambda, "clip_range": float(model.clip_range(1.0)),
        "normalize_advantage": model.normalize_advantage,
        "ent_coef": model.ent_coef, "vf_coef": model.vf_coef,
        "max_grad_norm": model.max_grad_norm,
        "learning_rate_initial": float(model.lr_schedule(1.0)),
        "target_kl": model.target_kl, "use_sde": model.use_sde,
        "device": str(model.device),
    }


def run_ppo(root: Path, dotnet: str, cli: Path, scenario: Path,
            rounds: int, updates_per_round: int, warmup_updates: int,
            sampler: ResourceSampler, raw_dir: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    env = ScoreBlockEnv(dotnet, str(cli), str(scenario), duration=120.0,
                        seed_pool=[42, *range(1000, 2000)])
    monitored = Monitor(env)
    model = PPO("MlpPolicy", monitored, seed=TRAIN_SEED, verbose=0)
    if model.n_envs != 1 or model.n_steps != 2048:
        raise RuntimeError(f"PPO defaults drifted: n_envs={model.n_envs}, n_steps={model.n_steps}")
    total_updates = warmup_updates + rounds * updates_per_round
    total_timesteps = total_updates * model.n_steps
    _, callback = model._setup_learn(total_timesteps, callback=None, reset_num_timesteps=True,
                                     tb_log_name="profile", progress_bar=False)
    callback.on_training_start(locals(), globals())
    records: list[dict[str, Any]] = []
    try:
        sampler.set_phase("ppo_warmup", find_dotnet_pid(model.env))
        for _ in range(warmup_updates):
            ppo_update(model, callback, total_timesteps)
        for round_index in range(1, rounds + 1):
            sampler.set_phase("ppo", find_dotnet_pid(model.env))
            record: dict[str, Any] = {
                "stage": "ppo", "round": round_index, "status": "valid", "faults": [],
                "warmup_updates_before_first_round": warmup_updates,
                "samples": {"rollout_seconds": [], "train_seconds": [],
                            "transitions": []},
            }
            try:
                for _ in range(updates_per_round):
                    rollout_seconds, train_seconds = ppo_update(model, callback, total_timesteps)
                    record["samples"]["rollout_seconds"].append(rollout_seconds)
                    record["samples"]["train_seconds"].append(train_seconds)
                    record["samples"]["transitions"].append(model.n_steps * model.n_envs)
            except Exception as exc:
                append_round_fault(record, exc)
            records.append(record)
            write_json(raw_dir / f"ppo-round-{round_index:03}.json", record)
            if record["status"] != "valid":
                break
    finally:
        callback.on_training_end()
        monitored.close()
    return records, ppo_snapshot(model)


def summarize_records(records: list[dict[str, Any]], stage: str) -> dict[str, Any]:
    fields = sorted({key for row in records if row.get("stage") == stage
                     for key, values in row.get("samples", {}).items()
                     if isinstance(values, list)})
    result: dict[str, Any] = {"round_count": sum(row.get("stage") == stage for row in records),
                              "valid_round_count": sum(row.get("stage") == stage and
                                                       row.get("status") == "valid"
                                                       for row in records),
                              "invalid_rounds": [row["round"] for row in records
                                  if row.get("stage") == stage and row.get("status") != "valid"]}
    result["metrics"] = {}
    for field in fields:
        by_round: dict[str, Any] = {}
        combined: list[float] = []
        for row in records:
            if row.get("stage") != stage:
                continue
            values = row.get("samples", {}).get(field, [])
            clean = [float(item) for item in values
                     if isinstance(item, (float, int)) and math.isfinite(float(item))]
            if row.get("status") == "valid":
                combined.extend(clean)
            by_round[str(row["round"])] = {
                **distribution(clean), "status": row.get("status", "unknown"),
                "raw_sample_count": len(values),
            }
        invalid_samples = sum(len(row.get("samples", {}).get(field, []))
                              for row in records if row.get("stage") == stage
                              and row.get("status") != "valid")
        result["metrics"][field] = {**distribution(combined), "per_round": by_round,
                                     "invalid_round_sample_count_retained": invalid_samples}
    return result


def summarize_resources(rows: list[dict[str, Any]]) -> dict[str, Any]:
    def vals(key: str, phase: str | None = None) -> list[float]:
        return [float(row[key]) for row in rows if (phase is None or row["phase"] == phase)
                and isinstance(row.get(key), (int, float))]
    width = max((len(row.get("logical_cpu_percent", [])) for row in rows), default=0)
    cpu_per_logical = []
    for index in range(width):
        samples = [row["logical_cpu_percent"][index] for row in rows
                   if len(row.get("logical_cpu_percent", [])) > index]
        cpu_per_logical.append(distribution(samples))
    host_cpu = vals("host_cpu_percent")
    host_memory = vals("host_memory_used_percent")
    return {
        "sample_count": len(rows), "sampling_interval_seconds": RESOURCE_INTERVAL_SECONDS,
        "tool": "psutil", "psutil_version": package_version("psutil"),
        "python_rss_bytes": {"peak": max(vals("python_rss_bytes"), default=None)},
        "dotnet_rss_bytes": {"peak": max(vals("dotnet_rss_bytes"), default=None)},
        "host_cpu_percent": distribution(host_cpu),
        "host_cpu_saturated": bool(host_cpu and statistics.median(host_cpu) >= 85.0),
        "logical_cpu_percent": cpu_per_logical,
        "memory_pressure": {
            "host_memory_used_percent": distribution(host_memory),
            "host_memory_pressure": bool(host_memory and max(host_memory) >= 85.0),
            "python_rss_peak_bytes": max(vals("python_rss_bytes"), default=None),
            "dotnet_rss_peak_bytes": max(vals("dotnet_rss_bytes"), default=None),
            "minimum_host_memory_available_bytes": min(
                vals("host_memory_available_bytes"), default=None),
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, help="absolute output directory")
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--rounds", type=int, default=5)
    parser.add_argument("--resets", type=int, default=100)
    parser.add_argument("--steps", type=int, default=1000)
    parser.add_argument("--ipc-round-trips", type=int, default=MIN_IPC_ROUND_TRIPS)
    parser.add_argument("--ppo-updates", type=int, default=50)
    parser.add_argument("--quick", action="store_true", help="small end-to-end validation run")
    args = parser.parse_args()
    out = Path(args.out).expanduser()
    if not out.is_absolute():
        parser.error("--out must be an absolute path")
    for name in ("rounds", "resets", "steps", "ipc_round_trips", "ppo_updates"):
        if getattr(args, name) < 1:
            parser.error(f"--{name.replace('_', '-')} must be positive")
    if not args.quick and args.ipc_round_trips < MIN_IPC_ROUND_TRIPS:
        parser.error(f"--ipc-round-trips must be at least {MIN_IPC_ROUND_TRIPS} (or use --quick)")
    return args


def main() -> int:
    args = parse_args()
    root = Path(__file__).resolve().parents[2]
    out = Path(args.out).expanduser().resolve()
    if out.exists() and any(out.iterdir()):
        raise SystemExit(f"refusing to overwrite non-empty output directory: {out}")
    out.mkdir(parents=True, exist_ok=True)
    raw_dir = out / "raw"
    raw_dir.mkdir(exist_ok=True)
    dotnet = str(Path(resolve_dotnet_executable(args.dotnet)).expanduser().resolve())
    cli = Path(args.cli_dll)
    scenario = Path(args.scenario)
    cli = (cli if cli.is_absolute() else root / cli).resolve()
    scenario = (scenario if scenario.is_absolute() else root / scenario).resolve()
    for required in (Path(dotnet), cli, scenario):
        if not required.is_file():
            raise SystemExit(f"required file not found: {required}")

    rounds, resets, steps = args.rounds, args.resets, args.steps
    ipc_round_trips, ppo_updates = args.ipc_round_trips, args.ppo_updates
    echo_warmup = WARMUP_STEPS
    real_warmup = WARMUP_STEPS
    ppo_warmup = PPO_WARMUP_UPDATES
    if args.quick:
        rounds, resets, steps = 1, min(resets, 2), min(steps, 8)
        ipc_round_trips = min(ipc_round_trips, 10)
        ppo_updates = min(ppo_updates, 1)
        echo_warmup = real_warmup = 2
        ppo_warmup = 1

    try:
        dotnet_version = subprocess.run([dotnet, "--version"], capture_output=True,
                                        text=True, encoding="utf-8", errors="replace",
                                        timeout=15, check=True).stdout.strip()
    except (subprocess.SubprocessError, OSError) as exc:
        dotnet_version = f"unavailable: {type(exc).__name__}: {exc}"
    manifest: dict[str, Any] = {
        "protocol": "score-block-rl-profile-v1", "created_utc": time.strftime(
            "%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "machine": {"name": platform.node(), "model": platform.processor() or platform.machine(),
                    "cpu_logical_count": psutil.cpu_count(logical=True),
                    "os": platform.platform()},
        "python": platform.python_version(), "dotnet_version": dotnet_version,
        "dotnet_executable": dotnet,
        "dependencies": {name: package_version(name) for name in
                         ("numpy", "gymnasium", "stable-baselines3", "torch", "psutil")},
        "torch_version": torch.__version__, "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario), "cli_dll": str(cli),
        "cli_dll_sha256": sha256_file(cli),
        "ppo_hyperparameters": {"seed": TRAIN_SEED, "n_envs": 1, "n_steps": 2048,
                                "defaults_from_installed_sb3": True},
        "warmup": {"env_each_round": {"discard_resets": 1, "discard_steps": WARMUP_STEPS},
                   "ipc_each_round": {"echo_discard_round_trips": echo_warmup,
                                      "real_rl_env_discard_steps": real_warmup},
                   "ppo_before_first_measured_update": ppo_warmup},
        "requested_parameters": {"rounds": args.rounds, "resets_per_round": args.resets,
                                 "steps_per_round": args.steps,
                                 "ipc_round_trips_per_round": args.ipc_round_trips,
                                 "ppo_updates_per_round": args.ppo_updates,
                                 "quick": args.quick},
        "effective_parameters": {"rounds": rounds, "resets_per_round": resets,
                                 "steps_per_round": steps,
                                 "ipc_round_trips_per_round": ipc_round_trips,
                                 "ppo_updates_per_round": ppo_updates,
                                 "ppo_warmup_updates": ppo_warmup},
        "random_seed": TRAIN_SEED,
        "resource_sampler": {"tool": "psutil", "interval_seconds": RESOURCE_INTERVAL_SECONDS},
        "output_layout": {"manifest": "manifest.json", "summary": "summary.json",
                          "raw": "raw/", "resources": "resources.csv"},
    }
    write_json(out / "manifest.json", manifest)

    records: list[dict[str, Any]] = []
    resource_rows: list[dict[str, Any]] = []
    with ResourceSampler(RESOURCE_INTERVAL_SECONDS) as sampler:
        with ScoreBlockEnv(dotnet, str(cli), str(scenario)) as env:
            sampler.set_phase("env", env._proc.pid)
            for round_index in range(1, rounds + 1):
                record = {"stage": "env", "round": round_index, "status": "valid", "faults": []}
                try:
                    record = measure_env_round(env, round_index, resets, steps)
                except Exception as exc:
                    append_round_fault(record, exc)
                records.append(record)
                write_json(raw_dir / f"env-round-{round_index:03}.json", record)

        ipc_measured: list[dict[str, Any]] = []
        for round_index in range(1, rounds + 1):
            record = {"stage": "ipc", "round": round_index, "status": "valid", "faults": [],
                      "samples": {"echo_round_trip_ms": [], "reset_round_trip_ms": [],
                                  "policy_step_end_to_end_ms": [], "policy_step_tick_ms": []},
                      "timing": {"degraded": False, "missing_count": 0}}
            try:
                record, pairs = measure_ipc_round(root, dotnet, cli, scenario, round_index,
                                                  ipc_round_trips, real_warmup, sampler)
                ipc_measured.extend(pairs)
            except Exception as exc:
                append_round_fault(record, exc)
            records.append(record)
            write_json(raw_dir / f"ipc-round-{round_index:03}.json", record)

        ppo_records, ppo_params = run_ppo(root, dotnet, cli, scenario, rounds,
                                          ppo_updates, ppo_warmup, sampler, raw_dir)
        records.extend(ppo_records)
        manifest["ppo_hyperparameters"] = ppo_params
        write_json(out / "manifest.json", manifest)
        resource_rows = list(sampler.rows)
    write_resources(out / "resources.csv", resource_rows)

    summaries = {stage: summarize_records(records, stage) for stage in ("env", "ipc", "ppo")}
    e2e = summaries["ipc"]["metrics"].get("policy_step_end_to_end_ms", {})
    in_process = summaries["ipc"]["metrics"].get("policy_step_tick_ms", {})
    end_median, tick_median = e2e.get("p50"), in_process.get("p50")
    ipc_fraction = None
    if end_median is not None and tick_median is not None and end_median > 0:
        ipc_fraction = max(0.0, end_median - tick_median) / end_median
    measured_resource_rows = [row for row in resource_rows
                              if row["phase"] in {"env", "ppo_warmup", "ppo"}]
    host_resources = summarize_resources(measured_resource_rows)
    host_resources["all_phase_sample_count"] = len(resource_rows)
    host_resources["per_phase"] = {
        phase: summarize_resources([row for row in resource_rows if row["phase"] == phase])
        for phase in ("env", "ppo_warmup", "ppo")
    }
    missing_timing = sum(row.get("timing", {}).get("missing_count", 0)
                         for row in records if row.get("stage") == "ipc")
    cpu_saturated = host_resources["host_cpu_saturated"]
    go_batch = ipc_fraction is not None and ipc_fraction >= 0.25 and not cpu_saturated
    summary = {
        "protocol": "score-block-rl-profile-v1", "created_utc": time.strftime(
            "%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "stages": summaries, "resources": host_resources,
        "ipc_fraction": {"value": ipc_fraction,
                          "formula": "max(0, median(end_to_end_policy_step_ms) - "
                                    "median(in_process_tick_ms)) / median(end_to_end_policy_step_ms)",
                          "end_to_end_p50_ms": end_median,
                          "in_process_tick_p50_ms": tick_median,
                          "timing_missing_count": missing_timing,
                          "degraded": missing_timing > 0 or ipc_fraction is None},
        "native_dotnet_batch_decision": (
            "Go: 建议尝试原生 .NET batch" if go_batch else "No-Go 原生 batch"),
        "decision_inputs": {"ipc_fraction_at_least_0_25": bool(
            ipc_fraction is not None and ipc_fraction >= 0.25),
            "host_cpu_saturated_median_at_least_85_percent": cpu_saturated},
        "raw_round_status": [{"stage": row["stage"], "round": row["round"],
                              "status": row["status"], "faults": row.get("faults", [])}
                             for row in records],
        "ppo_collection_method": {
            "description": "SB3 _setup_learn, collect_rollouts, progress update, then train per update",
            "equivalence": "Matches OnPolicyAlgorithm.learn loop order and RNG-consuming calls; "
                           "only wall-clock boundaries are added. No callbacks or logging alter actions.",
            "transitions_per_update": 2048,
        },
    }
    write_json(out / "summary.json", summary)
    print(json.dumps({"out": str(out), "effective_parameters": manifest["effective_parameters"],
                      "stage_sample_counts": {
                          stage: {name: metric["sample_count"] for name, metric in
                                  data["metrics"].items()} for stage, data in summaries.items()},
                      "resource_samples": host_resources["sample_count"],
                      "ipc_fraction": ipc_fraction,
                      "native_dotnet_batch_decision": summary["native_dotnet_batch_decision"]},
                     ensure_ascii=False, indent=2))
    return 0 if all(row.get("status") == "valid" for row in records) else 1


if __name__ == "__main__":
    raise SystemExit(main())
