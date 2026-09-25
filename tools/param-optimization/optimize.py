"""Optuna TPE study over the built-in FSM decision parameters.

Search space (SimParameters whitelist, mujoco backend consumers):
  MOUNT_SPEED    int   500-1500 step 10   reverse-mount speed
  IR_TRIGGER     float 0.15-0.8           search/obstacle trigger
  FALL_THRESHOLD int   80-400 step 5      mount-complete gray level
  EDGE_THRESHOLD int   150-800 step 10    scan edge-avoid trigger
  RECOVER_LIMIT  int   1-8                recovery budget (over-limit = stop)

Screening matches: 60 s, training seeds 1-4. Holdout: seeds 5-8 at 120 s.
Every trial writes its scenario JSON and per-seed metrics next to the study DB.

Usage:
  python optimize.py --n-trials 150 [--dotnet <dotnet.exe>] [--cli-dll <Sim.Cli.dll>]
                     [--out-dir <dir>] [--study-name <name>]
"""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import optuna

import oracle
from objective import aggregate, objective_value

BASE_SCENARIO = Path("scenarios/wushu-ring-2026-mujoco.json")
TRAIN_SEEDS = [1, 2, 3, 4]
SCREEN_DURATION = 60


def default_dotnet() -> str:
    candidates = [
        Path.home() / "AppData/Local/Temp/robot-simulator-dotnet-sdk/dotnet.exe",
        Path("dotnet.exe"),
    ]
    for c in candidates:
        if c.exists():
            return str(c)
    return "dotnet"


def suggest_space(trial: optuna.Trial) -> dict:
    return {
        "MOUNT_SPEED": trial.suggest_int("MOUNT_SPEED", 500, 1500, step=10),
        "IR_TRIGGER": trial.suggest_float("IR_TRIGGER", 0.15, 0.8),
        "FALL_THRESHOLD": trial.suggest_int("FALL_THRESHOLD", 80, 400, step=5),
        "EDGE_THRESHOLD": trial.suggest_int("EDGE_THRESHOLD", 150, 800, step=10),
        "RECOVER_LIMIT": trial.suggest_int("RECOVER_LIMIT", 1, 8),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--n-trials", type=int, default=150)
    parser.add_argument("--duration", type=float, default=SCREEN_DURATION)
    parser.add_argument("--seeds", default=",".join(str(s) for s in TRAIN_SEEDS))
    parser.add_argument("--base-scenario", default=str(BASE_SCENARIO))
    parser.add_argument("--dotnet", default=default_dotnet())
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--out-dir", default="tools/param-optimization/study")
    parser.add_argument("--study-name", default="fsm-param-optimization")
    args = parser.parse_args()

    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    seeds = [int(s) for s in args.seeds.split(",")]
    storage = f"sqlite:///{(out / 'study.db').as_posix()}"

    csv_path = out / "trials.csv"
    write_header = not csv_path.exists()
    csv_file = csv_path.open("a", newline="", encoding="utf-8")
    writer = csv.writer(csv_file)

    def objective(trial: optuna.Trial) -> float:
        nonlocal write_header
        params = suggest_space(trial)
        trial_dir = out / f"trial-{trial.number:04d}"
        scenario = oracle.write_scenario(args.base_scenario, params, trial_dir / "scenario.json")
        metrics = []
        for seed in seeds:
            metrics.append(oracle.run_match(args.dotnet, args.cli_dll, scenario, seed, args.duration))
        agg = aggregate(metrics)
        value = objective_value(agg)
        trial.set_user_attr("block_scores", agg["block_scores"])
        trial.set_user_attr("drops", agg["drops"])
        trial.set_user_attr("recover_limit_ends", agg["recover_limit_ends"])
        trial.set_user_attr("mount_t_values", json.dumps(agg["mount_t_values"]))
        if write_header:
            writer.writerow(["trial", "value", "params", "block_scores", "drops", "recover_limit_ends", "mount_t", "final_scores"])
            write_header = False
        writer.writerow([trial.number, value, json.dumps(params), agg["block_scores"], agg["drops"],
                         agg["recover_limit_ends"], json.dumps(agg["mount_t_values"]), json.dumps(agg["final_scores"])])
        csv_file.flush()
        print(f"trial {trial.number}: value={value:.2f} blocks={agg['block_scores']} drops={agg['drops']} "
              f"params={params}", flush=True)
        return value

    study = optuna.create_study(
        study_name=args.study_name, storage=storage, load_if_exists=True,
        sampler=optuna.samplers.TPESampler(seed=42), direction="maximize",
    )
    study.optimize(objective, n_trials=args.n_trials)

    print("BEST:", study.best_value, study.best_params)
    try:
        importances = optuna.importance.get_param_importances(study)
        print("IMPORTANCES:", importances)
        (out / "importances.txt").write_text(str(importances), encoding="utf-8")
    except Exception as exc:  # importance needs enough completed trials
        print("importances unavailable:", exc)
    csv_file.close()


if __name__ == "__main__":
    main()
