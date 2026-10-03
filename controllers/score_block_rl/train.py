#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Train the MuJoCo SCORE_BLOCK PPO pilot.

Task semantics are frozen from the previous round: 11-value privileged
observation (simulator ground-truth block coordinates), reward v2, official
scenario, and SB3 PPO hyper-parameters. Single environment remains the default;
subprocess vectorization is explicit through ``--n-envs``.

The default remains the frozen single-environment PPO configuration. Explicit
training seeds and subprocess environments are available for the gated v4
throughput experiment:

* an SB3 CSV logger (``progress.csv``) with PPO optimisation diagnostics;
* a ``CheckpointCallback`` snapshot every ``--checkpoint-interval`` global
  transitions (default 51,200);
* ``run-config.json`` recording the split version, every checkpoint's training
  steps and SHA-256, and the scenario/CLI/dependency hashes.

No ``EvalCallback``: its default "best model" rule ranks by mean episode
reward, which is not this project's gate (locked-target real ``BlockScore`` and
our own ``Drop`` count). See design.md.
"""

from __future__ import annotations

import argparse
import json
import locale
import os
import platform
import subprocess
import sys
import time
import uuid
from pathlib import Path

from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import CheckpointCallback
from stable_baselines3.common.logger import CSVOutputFormat, Logger
from stable_baselines3.common.monitor import Monitor
from stable_baselines3.common.vec_env import SubprocVecEnv, VecMonitor

from gym_env import ScoreBlockEnv, resolve_dotnet_executable
from splits import (
    NAMED_SPLITS,
    SPLIT_SEEDS,
    SPLIT_USAGE,
    SPLIT_VERSION,
    TRAIN_EPISODE_SEEDS,
    training_pool_manifest,
)
from train_artifacts import (
    CHECKPOINT_DIR_NAME,
    CHECKPOINT_NAME_PREFIX,
    FINAL_MODEL_STEM,
    MONITOR_CSV_NAME,
    PROGRESS_CSV_NAME,
    audit_checkpoints,
    discover_checkpoints,
    package_version,
    read_monitor_csv,
    sha256_file,
)

TRAIN_SEED = 20260925
V4_TRAIN_SEEDS = (20260927, 20260928, 20260929, 20260930, 20261001)
TRAIN_SEED_POOL = sorted(TRAIN_EPISODE_SEEDS - {TRAIN_SEED})
CHECKPOINT_INTERVAL_STEPS = 51_200
ROLLOUT_TRANSITIONS = 2_048
EPISODE_SEED_PARTITION = 0x53434F52
EPISODE_SEED_DOMAIN = 0x45504953
INFO_LOG_FIELDS = (
    "seed", "entry_tick", "policy_ticks", "target_index", "us_block_scores",
    "us_block_score_events", "them_block_score_events", "target_block_offs",
    "unowned_block_offs", "us_drops", "attribution_ambiguous", "done_reason",
    "faults", "score_us", "score_them",
)


def resolve_vector_config(n_envs: int, checkpoint_interval: int = CHECKPOINT_INTERVAL_STEPS
                          ) -> tuple[int, int]:
    """Return rollout steps and callback calls while preserving global counts."""
    if n_envs < 1 or n_envs > len(TRAIN_SEED_POOL):
        raise ValueError(f"--n-envs must be between 1 and {len(TRAIN_SEED_POOL)}")
    smallest_v4_worker_pool = len(TRAIN_SEED_POOL) // len(V4_TRAIN_SEEDS)
    if n_envs > smallest_v4_worker_pool // 2:
        raise ValueError("--n-envs must leave at least two registered episode seeds per worker")
    if ROLLOUT_TRANSITIONS % n_envs:
        raise ValueError(f"--n-envs must divide {ROLLOUT_TRANSITIONS} global rollout transitions")
    if checkpoint_interval < 1 or checkpoint_interval % n_envs:
        raise ValueError("--checkpoint-interval must be positive and divisible by --n-envs")
    return ROLLOUT_TRANSITIONS // n_envs, checkpoint_interval // n_envs


def build_episode_seed_streams(train_seed: int, n_envs: int,
                               seed_pool: list[int] | None = None) -> list[list[int]]:
    """Build isolated per-run/per-worker episode streams from registered seeds.

    The five preregistered RL-v4 training seeds receive disjoint partitions of
    the registered training pool. The historical default keeps its old stream.
    Other explicit seeds get a deterministic, domain-separated pool ordering.
    """
    pool = list(TRAIN_SEED_POOL if seed_pool is None else seed_pool)
    if n_envs < 1 or n_envs > len(pool):
        raise ValueError(f"n_envs must be between 1 and the {len(pool)} episode seeds")
    if train_seed == TRAIN_SEED and n_envs == 1:
        return [pool]  # Preserve the established single-env episode order.
    import numpy as np

    if train_seed in V4_TRAIN_SEEDS:
        shuffled = np.random.default_rng(EPISODE_SEED_PARTITION).permutation(pool).tolist()
        run_pool = shuffled[V4_TRAIN_SEEDS.index(train_seed)::len(V4_TRAIN_SEEDS)]
    else:
        # A separate seed domain avoids reusing the PPO seed as the episode RNG.
        episode_rng = np.random.default_rng(
            np.random.SeedSequence([EPISODE_SEED_DOMAIN, train_seed]))
        run_pool = episode_rng.permutation(pool).tolist()
    streams = [run_pool[worker::n_envs] for worker in range(n_envs)]
    if any(len(stream) < 2 for stream in streams):
        raise ValueError("--n-envs must leave an initial and a subsequent episode seed per worker")
    return streams


def prepare_output_directory(path: Path) -> Path:
    """Create a fresh run directory and refuse paths that could overwrite a run."""
    out = path.expanduser().resolve()
    if out.exists():
        if not out.is_dir() or any(out.iterdir()):
            raise FileExistsError(f"training output directory already exists and is not empty: {out}")
    else:
        out.mkdir(parents=True)
    return out


def code_identity(repo_root: Path) -> dict[str, object]:
    files = ("controllers/score_block_rl/train.py",
             "controllers/score_block_rl/gym_env.py",
             "controllers/score_block_rl/train_artifacts.py",
             "controllers/score_block_rl/splits.py")
    result: dict[str, object] = {name: sha256_file(repo_root / name) for name in files}
    try:
        result["git_commit"] = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=repo_root, check=True,
            capture_output=True, text=True, encoding="utf-8").stdout.strip()
        dirty = subprocess.run(
            ["git", "status", "--porcelain", "--", "controllers/score_block_rl",
             "src/Sim.Cli", "src/Sim.Core", "src/Sim.Mujoco", "src/Sim.Protocol"],
            cwd=repo_root, check=True, capture_output=True, text=True, encoding="utf-8")
        result["git_worktree_dirty"] = bool(dirty.stdout.strip())
        result["git_dirty_paths"] = [line[3:].strip() for line in dirty.stdout.splitlines()]
    except (OSError, subprocess.CalledProcessError):
        result["git_commit"] = None
        result["git_worktree_dirty"] = None
        result["git_dirty_paths"] = None
    return result


def hardware_identity() -> dict[str, object]:
    identity: dict[str, object] = {
        "hostname": platform.node(), "platform": platform.platform(),
        "processor": platform.processor(), "logical_cpu_count": os.cpu_count(),
    }
    try:
        import psutil
        identity["memory_total_bytes"] = int(psutil.virtual_memory().total)
    except ImportError:
        identity["memory_total_bytes"] = None
    return identity


def dotnet_identity(executable: str) -> dict[str, object]:
    """Capture SDK and runtime versions for the host used by the rl-env bridge."""
    identity: dict[str, object] = {"executable": str(Path(executable).expanduser().resolve())}
    for field, arguments in (("sdk_version", ["--version"]),
                             ("installed_runtimes", ["--list-runtimes"])):
        try:
            result = subprocess.run(
                [executable, *arguments], check=True, capture_output=True,
                text=True, encoding="utf-8", timeout=10)
            identity[field] = result.stdout.strip()
        except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
            identity[field] = None
            identity[f"{field}_error"] = str(exc)
    return identity


def make_env_factory(dotnet: str, cli_dll: Path, scenario: Path,
                     worker_seeds: list[int], initial_episode_seed: int | None = None,
                     reward: str = "v4"):
    """Return a cloudpickle-compatible subprocess worker initializer."""
    def create():
        return ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                             seed_pool=worker_seeds, initial_episode_seed=initial_episode_seed,
                             reward=reward)
    return create


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--steps", type=int, default=500_000)
    parser.add_argument("--out", required=True)
    parser.add_argument("--train-seed", type=int, default=TRAIN_SEED)
    parser.add_argument("--n-envs", type=int, default=1,
                        help="opt-in subprocess environments; 1 preserves the legacy path")
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--reward", default="v4", choices=["v4", "aggression-v1", "aggression-v2", "aggression-v3"],
                        help="reward variant; v4 (default) keeps the frozen v4 terms byte-identical")
    parser.add_argument("--checkpoint-interval", type=int, default=CHECKPOINT_INTERVAL_STEPS,
                        help="global transitions between CheckpointCallback snapshots")
    args = parser.parse_args()
    if args.steps <= 0:
        parser.error("--steps must be positive")
    if args.checkpoint_interval <= 0:
        parser.error("--checkpoint-interval must be positive")
    if args.train_seed < 0 or args.train_seed > 0xFFFFFFFF:
        parser.error("--train-seed must be an unsigned 32-bit integer")
    try:
        n_steps, checkpoint_calls = resolve_vector_config(args.n_envs, args.checkpoint_interval)
    except ValueError as exc:
        parser.error(str(exc))
    if not sys.flags.utf8_mode and locale.getencoding().lower().replace("-", "") != "utf8":
        parser.error("run Python with -X utf8 so SB3 writes the episode CSV in UTF-8")

    repo_root = Path(__file__).resolve().parents[2]
    try:
        out = prepare_output_directory(Path(args.out))
    except (OSError, FileExistsError) as exc:
        parser.error(str(exc))
    dotnet = resolve_dotnet_executable(args.dotnet)
    cli_dll = Path(args.cli_dll)
    scenario = Path(args.scenario)
    if not cli_dll.is_absolute():
        cli_dll = repo_root / cli_dll
    if not scenario.is_absolute():
        scenario = repo_root / scenario
    scenario = scenario.resolve()
    cli_dll = cli_dll.resolve()
    checkpoint_dir = out / CHECKPOINT_DIR_NAME

    episode_seed_streams = build_episode_seed_streams(args.train_seed, args.n_envs)
    legacy_seed_compat = args.train_seed == TRAIN_SEED and args.n_envs == 1
    initial_episode_seeds = ([args.train_seed] if legacy_seed_compat else
                             [stream[0] for stream in episode_seed_streams])
    subsequent_episode_seed_streams = (
        episode_seed_streams if legacy_seed_compat else
        [stream[1:] for stream in episode_seed_streams]
    )
    registered_training_split = training_pool_manifest()
    legacy_initial_seed = registered_training_split.pop("initial_sb3_reset_episode_seed", None)
    registered_training_split["legacy_default_initial_sb3_reset_episode_seed"] = legacy_initial_seed
    registered_training_split["initial_episode_seeds_for_this_run"] = initial_episode_seeds
    # Monitor appends '.monitor.csv' unless the filename already has that suffix.
    monitor_path = out / MONITOR_CSV_NAME
    if args.n_envs == 1:
        env = ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                            seed_pool=subsequent_episode_seed_streams[0],
                            initial_episode_seed=(None if legacy_seed_compat else
                                                  initial_episode_seeds[0]),
                            reward=args.reward)
        monitored_env = Monitor(env, filename=str(monitor_path), info_keywords=INFO_LOG_FIELDS)
    else:
        vector_env = SubprocVecEnv(
            [make_env_factory(dotnet, cli_dll, scenario,
                              subsequent_episode_seed_streams[index],
                              initial_episode_seeds[index],
                              reward=args.reward)
             for index in range(args.n_envs)],
            start_method="spawn")
        monitored_env = VecMonitor(vector_env, filename=str(monitor_path),
                                   info_keywords=INFO_LOG_FIELDS)
    progress_path = out / PROGRESS_CSV_NAME
    started = time.time()
    config = {
        "run_id": str(uuid.uuid4()),
        "status": "running",
        "cli_arguments": list(sys.argv),
        "algorithm": "Stable-Baselines3 PPO MlpPolicy",
        "python": platform.python_version(),
        "platform": platform.platform(),
        "dependencies": {
            "numpy": package_version("numpy"),
            "gymnasium": package_version("gymnasium"),
            "stable-baselines3": package_version("stable-baselines3"),
            "torch": package_version("torch"),
        },
        "dotnet_runtime": dotnet_identity(dotnet),
        "total_timesteps_requested": args.steps,
        "train_seed": args.train_seed,
        "n_envs": args.n_envs,
        "train_episode_seed_pool": {"fixed": [42], "inclusive_range": [1000, 1999]},
        "legacy_seed_compatibility": legacy_seed_compat,
        "sb3_env_reset_seeds": [args.train_seed + worker for worker in range(args.n_envs)],
        "initial_episode_seeds": initial_episode_seeds,
        "episode_seed_streams": {
            "scheme": ("legacy_training_pool_order" if legacy_seed_compat else
                       "disjoint_registered_pool_partitions_for_v4_training_seeds" if
                       args.train_seed in V4_TRAIN_SEEDS else
                       "domain_separated_deterministic_registered_pool_permutation"),
            "workers": [{"worker_index": worker,
                         "initial_episode_seed": initial_episode_seeds[worker],
                         "subsequent_seeds": subsequent_episode_seed_streams[worker]}
                        for worker in range(len(episode_seed_streams))],
        },
        "split_version": SPLIT_VERSION,
        "training_split": registered_training_split,
        "evaluation_seed_splits": {name: list(SPLIT_SEEDS[name]) for name in NAMED_SPLITS},
        "evaluation_split_usage": {name: SPLIT_USAGE[name] for name in NAMED_SPLITS},
        "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario),
        "reward_variant": args.reward,
        "cli_dll": str(cli_dll),
        "cli_dll_sha256": sha256_file(cli_dll),
        "dotnet_executable": str(Path(dotnet).expanduser().resolve()),
        "monitor_csv": str(monitor_path),
        "progress_csv": str(progress_path),
        "episode_info_fields": list(INFO_LOG_FIELDS),
        "checkpoint_dir": str(checkpoint_dir),
        "checkpoint_interval_transitions": args.checkpoint_interval,
        "checkpoint_callback": {
            "class": "stable_baselines3.common.callbacks.CheckpointCallback",
            "save_freq_env_step_calls": checkpoint_calls,
            "n_envs": args.n_envs,
            "save_freq_divided_by_n_envs": args.n_envs > 1,
            "global_transition_interval": args.checkpoint_interval,
            "name_prefix": CHECKPOINT_NAME_PREFIX,
            "note": "save_freq counts vector env.step() calls; num_timesteps advances by n_envs",
        },
        "logger": {
            "class": "stable_baselines3.common.logger.Logger + CSVOutputFormat",
            "progress_csv": str(progress_path),
            "note": "SB3's default configure_logger installs no writers at verbose=0 "
                    "without tensorboard_log, so the CSV logger is set explicitly",
        },
        "best_model_selection": {
            "uses_eval_callback_mean_reward": False,
            "note": "EvalCallback's default best_model rule ranks by mean episode reward; "
                    "this pilot selects models on referee events in evaluate.py instead",
        },
        "ppo_parameters": {
            "policy": "MlpPolicy",
            "defaults_from_installed_sb3_version": True,
            "seed": args.train_seed,
            "n_envs": args.n_envs,
            "n_steps": n_steps,
        },
        "global_rollout_transitions_per_update": n_steps * args.n_envs,
        "observation": {
            "dimension": 11,
            "privileged_state": True,
            "fields": [
                "target_relative_forward/platform_side",
                "target_relative_left/platform_side",
                "target_x/platform_side",
                "target_y/platform_side",
                "us_forward_speed/vehicle_max_speed",
                "us_yaw_rate/vehicle_max_turn_rate",
                "us_on_platform",
                "target_on_platform",
                "remaining_match_time_ratio",
                "us_x_from_platform_center/platform_half_side",
                "us_y_from_platform_center/platform_half_side",
            ],
            "block_coordinates_are_simulator_ground_truth": True,
        },
        "action": "Box(-1,1)^2 -> v=action[0] m/s, w=2*action[1] rad/s",
        "code_identity": code_identity(repo_root),
        "hardware_identity": hardware_identity(),
    }
    config_path = out / "run-config.json"
    config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    csv_logger: Logger | None = None
    try:
        model = PPO("MlpPolicy", monitored_env, seed=args.train_seed,
                    n_steps=n_steps, verbose=0)
        if int(model.n_envs) != args.n_envs:
            raise RuntimeError(f"requested {args.n_envs} environments, got n_envs={model.n_envs}")
        csv_logger = Logger(folder=str(out),
                            output_formats=[CSVOutputFormat(str(progress_path))])
        model.set_logger(csv_logger)
        checkpoint_callback = CheckpointCallback(
            save_freq=checkpoint_calls,
            save_path=str(checkpoint_dir),
            name_prefix=CHECKPOINT_NAME_PREFIX,
            verbose=0,
        )
        config["ppo_parameters"].update({
            "n_steps": model.n_steps,
            "batch_size": model.batch_size,
            "n_epochs": model.n_epochs,
            "gamma": model.gamma,
            "gae_lambda": model.gae_lambda,
            "clip_range": float(model.clip_range(1.0)),
            "normalize_advantage": model.normalize_advantage,
            "ent_coef": model.ent_coef,
            "vf_coef": model.vf_coef,
            "max_grad_norm": model.max_grad_norm,
            "learning_rate_initial": float(model.lr_schedule(1.0)),
            "target_kl": model.target_kl,
            "use_sde": model.use_sde,
            "device": str(model.device),
        })
        config["logger"]["log_interval"] = int(getattr(model, "log_interval", 1))
        config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
        model.learn(total_timesteps=args.steps, progress_bar=False,
                    callback=checkpoint_callback)
        model.save(out / FINAL_MODEL_STEM)
        monitored_env.close()  # flush Monitor's episode CSV before counting rows
        csv_logger.close()  # flush the PPO diagnostics CSV
        # Python UTF-8 mode makes SB3 Monitor write portable UTF-8 directly.
        monitor_text = monitor_path.read_text(encoding="utf-8")
        monitor_data = read_monitor_csv(monitor_path)
        fault_values: list[int] = []
        for row in monitor_data["rows"]:
            try:
                fault_values.append(int(row["faults"]))
            except (KeyError, TypeError, ValueError) as exc:
                raise RuntimeError("monitor CSV has a missing or invalid faults value") from exc
        config["faults_total"] = sum(fault_values)
        if config["faults_total"] != 0:
            raise RuntimeError(f"training recorded {config['faults_total']} controller faults")
        config["status"] = "completed"
        config["elapsed_seconds"] = round(time.time() - started, 3)
        config["total_timesteps_trained"] = int(model.num_timesteps)
        config["actual_global_transitions"] = int(model.num_timesteps)
        config["steps_per_second"] = round(model.num_timesteps / config["elapsed_seconds"], 3)
        config["model_zip_sha256"] = sha256_file(out / f"{FINAL_MODEL_STEM}.zip")
        config["episode_log_rows"] = max(0, len(monitor_text.splitlines()) - 2)
        config["checkpoints"] = discover_checkpoints(checkpoint_dir)
        config["checkpoint_count"] = len(config["checkpoints"])
        config["artifact_hashes"] = {
            "final_model": sha256_file(out / f"{FINAL_MODEL_STEM}.zip"),
            "monitor_csv": sha256_file(monitor_path),
            "progress_csv": sha256_file(progress_path),
        }
        config["checkpoint_audit"] = audit_checkpoints(config, out)
        if not config["checkpoint_audit"]["ok"]:
            config["status"] = "failed"
            raise RuntimeError("training artifact audit failed: " +
                               "; ".join(config["checkpoint_audit"]["issues"]))
    except Exception as exc:
        config["status"] = "failed"
        config["elapsed_seconds"] = round(time.time() - started, 3)
        config["error_type"] = type(exc).__name__
        config["error"] = str(exc)
        raise
    finally:
        active_exception = sys.exc_info()[0] is not None
        cleanup_error: Exception | None = None
        try:
            monitored_env.close()
        except Exception as exc:  # noqa: BLE001 - preserve a final failure record
            config["status"] = "failed"
            config["cleanup_error_type"] = type(exc).__name__
            config["cleanup_error"] = str(exc)
            if not active_exception:
                cleanup_error = exc
        finally:
            if csv_logger is not None:
                try:
                    csv_logger.close()
                except Exception:  # noqa: BLE001 - closing twice must never mask the real error
                    pass
            config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n",
                                   encoding="utf-8")
        if cleanup_error is not None:
            raise cleanup_error
    print(json.dumps(config, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
