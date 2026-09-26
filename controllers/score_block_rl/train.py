#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Train the single-environment MuJoCo SCORE_BLOCK PPO pilot.

Task semantics are frozen from the previous round: 11-value privileged
observation (simulator ground-truth block coordinates), reward v2, official
scenario, single environment, SB3 default PPO hyper-parameters.

This round only adds training observability:

* an SB3 CSV logger (``progress.csv``) with PPO optimisation diagnostics;
* a ``CheckpointCallback`` snapshot every ``--checkpoint-interval``
  single-environment steps (default 51,200);
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
import platform
import sys
import time
from pathlib import Path

from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import CheckpointCallback
from stable_baselines3.common.logger import CSVOutputFormat, Logger
from stable_baselines3.common.monitor import Monitor

from gym_env import ScoreBlockEnv, resolve_dotnet_executable
from splits import (
    FINAL_HOLDOUT_V3,
    NAMED_SPLITS,
    REVEALED_HOLDOUT_SPLITS,
    SPLIT_SEEDS,
    SPLIT_VERSION,
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
    sha256_file,
)

TRAIN_SEED = 20260925
TRAIN_SEED_POOL = [42, *range(1000, 2000)]
CHECKPOINT_INTERVAL_STEPS = 51_200
INFO_LOG_FIELDS = (
    "seed", "entry_tick", "policy_ticks", "target_index", "us_block_scores",
    "us_block_score_events", "them_block_score_events", "target_block_offs",
    "unowned_block_offs", "us_drops", "attribution_ambiguous", "done_reason",
    "faults", "score_us", "score_them",
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--steps", type=int, default=500_000)
    parser.add_argument("--out", required=True)
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--checkpoint-interval", type=int, default=CHECKPOINT_INTERVAL_STEPS,
                        help="single-environment steps between CheckpointCallback snapshots")
    args = parser.parse_args()
    if args.steps <= 0:
        parser.error("--steps must be positive")
    if args.checkpoint_interval <= 0:
        parser.error("--checkpoint-interval must be positive")
    if not sys.flags.utf8_mode and locale.getencoding().lower().replace("-", "") != "utf8":
        parser.error("run Python with -X utf8 so SB3 writes the episode CSV in UTF-8")

    repo_root = Path(__file__).resolve().parents[2]
    out = Path(args.out).expanduser().resolve()
    out.mkdir(parents=True, exist_ok=True)
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

    env = ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                        seed_pool=TRAIN_SEED_POOL)
    # Monitor appends '.monitor.csv' unless the filename already has that suffix.
    monitor_path = out / MONITOR_CSV_NAME
    monitored_env = Monitor(env, filename=str(monitor_path), info_keywords=INFO_LOG_FIELDS)
    progress_path = out / PROGRESS_CSV_NAME
    started = time.time()
    config = {
        "status": "running",
        "algorithm": "Stable-Baselines3 PPO MlpPolicy",
        "python": platform.python_version(),
        "platform": platform.platform(),
        "dependencies": {
            "numpy": package_version("numpy"),
            "gymnasium": package_version("gymnasium"),
            "stable-baselines3": package_version("stable-baselines3"),
            "torch": package_version("torch"),
        },
        "total_timesteps_requested": args.steps,
        "train_seed": TRAIN_SEED,
        "train_episode_seed_pool": {"fixed": [42], "inclusive_range": [1000, 1999]},
        "initial_sb3_reset_episode_seed": TRAIN_SEED,
        "split_version": SPLIT_VERSION,
        "training_split": training_pool_manifest(),
        "evaluation_seed_splits": {name: list(SPLIT_SEEDS[name]) for name in NAMED_SPLITS},
        "evaluation_split_usage": {
            name: ("blind holdout" if name == FINAL_HOLDOUT_V3
                   else "revealed holdout; analysis only" if name in REVEALED_HOLDOUT_SPLITS
                   else "non-blind")
            for name in NAMED_SPLITS
        },
        "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario),
        "cli_dll": str(cli_dll),
        "cli_dll_sha256": sha256_file(cli_dll),
        "dotnet_executable": str(Path(dotnet).expanduser().resolve()),
        "monitor_csv": str(monitor_path),
        "progress_csv": str(progress_path),
        "episode_info_fields": list(INFO_LOG_FIELDS),
        "checkpoint_dir": str(checkpoint_dir),
        "checkpoint_interval_single_env_steps": args.checkpoint_interval,
        "checkpoint_callback": {
            "class": "stable_baselines3.common.callbacks.CheckpointCallback",
            "save_freq_env_step_calls": args.checkpoint_interval,
            "n_envs": 1,
            "save_freq_divided_by_n_envs": False,
            "name_prefix": CHECKPOINT_NAME_PREFIX,
            "note": "save_freq counts env.step() calls; with n_envs=1 that equals single-env steps",
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
            "seed": TRAIN_SEED,
            "n_envs": 1,
        },
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
    }
    config_path = out / "run-config.json"
    config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    csv_logger: Logger | None = None
    try:
        model = PPO("MlpPolicy", monitored_env, seed=TRAIN_SEED, verbose=0)
        if int(model.n_envs) != 1:
            raise RuntimeError(
                f"this pilot is single-environment only, got n_envs={model.n_envs}; "
                "the checkpoint interval is expressed in single-env steps")
        csv_logger = Logger(folder=str(out),
                            output_formats=[CSVOutputFormat(str(progress_path))])
        model.set_logger(csv_logger)
        checkpoint_callback = CheckpointCallback(
            save_freq=args.checkpoint_interval,
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
        config["status"] = "completed"
        config["elapsed_seconds"] = round(time.time() - started, 3)
        config["total_timesteps_trained"] = int(model.num_timesteps)
        config["steps_per_second"] = round(model.num_timesteps / config["elapsed_seconds"], 3)
        config["model_zip_sha256"] = sha256_file(out / f"{FINAL_MODEL_STEM}.zip")
        config["episode_log_rows"] = max(0, len(monitor_text.splitlines()) - 2)
        config["checkpoints"] = discover_checkpoints(checkpoint_dir)
        config["checkpoint_count"] = len(config["checkpoints"])
        config["checkpoint_audit"] = audit_checkpoints(config, out)
    except Exception as exc:
        config["status"] = "failed"
        config["elapsed_seconds"] = round(time.time() - started, 3)
        config["error_type"] = type(exc).__name__
        config["error"] = str(exc)
        raise
    finally:
        monitored_env.close()
        if csv_logger is not None:
            try:
                csv_logger.close()
            except Exception:  # noqa: BLE001 - closing twice must never mask the real error
                pass
        config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
    print(json.dumps(config, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
