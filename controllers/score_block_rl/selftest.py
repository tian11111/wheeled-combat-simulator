#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Targeted self-checks for the SCORE_BLOCK PPO checkpoint round.

Covers the machine-decidable parts of the task's acceptance criteria:

1. split routing: the four named splits resolve to the pre-registered seed lists,
   and the previous ``--final-holdout`` keeps meaning 4001-4010;
2. split/seed mutex: mixing splits or reusing reserved seeds is rejected;
3. checkpoint step parsing + SHA-256, including a cross-check of a real
   ``run-config.json`` registry against the files on disk;
4. CSV readability: ``progress.csv`` (PPO diagnostics) and
   ``episodes.monitor.csv`` (per-episode referee info) parse with the expected
   columns;
5. final blind-run guards: the CLI refuses to run a blind holdout without a
   freeze record and refuses to overwrite an existing result file.

Artifact-backed checks (3/4/5b) need a real training directory and are reported
as ``skipped`` instead of silently passing when ``--train-dir`` is omitted.

Usage:
    python selftest.py [--train-dir DIR] [--dev-sweep JSON] [--freeze JSON] [--out JSON]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import splits
from splits import (
    DEVELOPMENT_V2,
    EXPLORATORY,
    FINAL_HOLDOUT_V2,
    LEGACY_DEVELOPMENT,
    LEGACY_FINAL_HOLDOUT,
    SPLIT_SEEDS,
    SPLIT_VERSION,
    SplitError,
    resolve_selection,
)
from train_artifacts import (
    CHECKPOINT_DIR_NAME,
    FINAL_MODEL_NAME,
    MONITOR_CSV_NAME,
    PROGRESS_CSV_NAME,
    assert_unique_checkpoint_steps,
    audit_checkpoints,
    discover_checkpoints,
    float_column_stats,
    parse_checkpoint_steps,
    read_monitor_csv,
    read_progress_csv,
    sha256_file,
)

SCRIPT_DIR = Path(__file__).resolve().parent
EXPECTED_SPLIT_SEEDS = {
    LEGACY_DEVELOPMENT: list(range(3001, 3011)),
    LEGACY_FINAL_HOLDOUT: list(range(4001, 4011)),
    DEVELOPMENT_V2: list(range(5001, 5021)),
    FINAL_HOLDOUT_V2: list(range(6001, 6051)),
}
#: PPO optimisation diagnostics the CSV logger must expose.
REQUIRED_PROGRESS_COLUMNS = (
    "train/approx_kl",
    "train/clip_fraction",
    "train/explained_variance",
    "train/value_loss",
    "rollout/ep_rew_mean",
    "time/total_timesteps",
)
#: Per-episode referee columns the Monitor CSV must carry.
MONITOR_INFO_FIELDS = (
    "seed", "entry_tick", "policy_ticks", "target_index", "us_block_scores",
    "us_block_score_events", "them_block_score_events", "target_block_offs",
    "unowned_block_offs", "us_drops", "attribution_ambiguous", "done_reason",
    "faults", "score_us", "score_them",
)


class Harness:
    """Collects check outcomes without stopping at the first failure."""

    def __init__(self) -> None:
        self.results: list[dict] = []

    def check(self, name: str, fn) -> None:
        try:
            detail = fn()
            self.results.append({"name": name, "status": "passed", "detail": detail or ""})
        except Exception as exc:  # noqa: BLE001 - a failing check must be reported, not raised
            self.results.append({"name": name, "status": "failed",
                                 "detail": f"{type(exc).__name__}: {exc}"})

    def skip(self, name: str, reason: str) -> None:
        self.results.append({"name": name, "status": "skipped", "detail": reason})

    @property
    def failed(self) -> list[dict]:
        return [row for row in self.results if row["status"] == "failed"]


def expect_split_error(name: str, kwargs: dict, fragment: str) -> None:
    try:
        resolve_selection(**kwargs)
    except SplitError as exc:
        if fragment not in str(exc):
            raise AssertionError(f"{name}: error {str(exc)!r} lacks {fragment!r}") from exc
        return
    raise AssertionError(f"{name}: expected SplitError containing {fragment!r}, got a selection")


def pure_checks(harness: Harness) -> None:
    def routing() -> str:
        for split, expected in EXPECTED_SPLIT_SEEDS.items():
            actual = splits.seeds_for(split)
            assert actual == expected, f"{split}: {actual} != {expected}"
        assert SPLIT_SEEDS == EXPECTED_SPLIT_SEEDS, "registry drifted from the pre-registered lists"
        assert SPLIT_VERSION == "score-block-split-v2", SPLIT_VERSION
        return "4 named splits match the pre-registered seed lists"

    harness.check("split routing: named seed lists", routing)

    def disjoint() -> str:
        splits.assert_registry_is_disjoint()
        seen: dict[int, str] = {}
        for split, seeds in SPLIT_SEEDS.items():
            for seed in seeds:
                assert seed not in seen, f"seed {seed} in {seen.get(seed)} and {split}"
                seen[seed] = split
        assert not (set(seen) & splits.TRAIN_EPISODE_SEEDS), "split/training overlap"
        return f"{len(seen)} split seeds, disjoint from the training pool"

    harness.check("split registry: disjointness", disjoint)

    def defaults() -> str:
        selection = resolve_selection()
        assert selection.split == DEVELOPMENT_V2, selection.split
        assert selection.seeds == list(range(5001, 5021)), selection.seeds
        assert not selection.is_blind_holdout and not selection.is_exploratory
        return "default split is development_v2 (5001-5020)"

    harness.check("split routing: default is the new development set", defaults)

    def legacy_holdout() -> str:
        selection = resolve_selection(final_holdout=True)
        assert selection.split == LEGACY_FINAL_HOLDOUT, selection.split
        assert selection.seeds == list(range(4001, 4011)), selection.seeds
        assert resolve_selection(split=LEGACY_FINAL_HOLDOUT).seeds == selection.seeds
        return "--final-holdout still resolves to 4001-4010"

    harness.check("split routing: --final-holdout keeps its historical meaning", legacy_holdout)

    def blind_label() -> str:
        selection = resolve_selection(split=FINAL_HOLDOUT_V2)
        assert selection.is_blind_holdout and len(selection.seeds) == 50
        assert selection.manifest()["evaluation_split"] == FINAL_HOLDOUT_V2
        return "final_holdout_v2 is flagged as the one-shot blind split with 50 seeds"

    harness.check("split routing: new blind split label", blind_label)

    def exploratory_label() -> str:
        selection = resolve_selection(custom_seeds=list(range(7001, 7011)))
        assert selection.split == EXPLORATORY and selection.is_exploratory
        assert not selection.is_blind_holdout
        return "custom seeds are labelled exploratory and never gate-eligible"

    harness.check("split routing: custom seeds are exploratory only", exploratory_label)

    mutex_cases = [
        ("holdout + split", {"split": DEVELOPMENT_V2, "final_holdout": True},
         "cannot be combined with --split"),
        ("holdout + custom seeds", {"final_holdout": True, "custom_seeds": list(range(7001, 7011))},
         "cannot be combined with custom --seeds"),
        ("split + custom seeds",
         {"split": DEVELOPMENT_V2, "custom_seeds": list(range(7001, 7011))},
         "--seeds cannot be combined with --split"),
        ("custom seeds reuse new development split",
         {"custom_seeds": list(range(5001, 5011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse legacy development split",
         {"custom_seeds": list(range(3001, 3011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse legacy final holdout",
         {"custom_seeds": list(range(4001, 4011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds hit the training pool",
         {"custom_seeds": [42, *range(7001, 7010)]}, "overlap the training episode pool"),
        ("custom seeds hit the training range",
         {"custom_seeds": list(range(1000, 1010))}, "overlap the training episode pool"),
        ("custom seeds too short", {"custom_seeds": list(range(7001, 7006))},
         "at least 10 distinct seeds"),
        ("custom seeds with duplicates", {"custom_seeds": [7001] * 10},
         "must not repeat"),
        ("unknown split", {"split": "development_v3"}, "unknown split"),
    ]
    for name, kwargs, fragment in mutex_cases:
        harness.check(f"mutex: {name}", (lambda k=kwargs, f=fragment, n=name: (
            expect_split_error(n, k, f) or f"rejected with {f!r}")))


def checkpoint_checks(harness: Harness) -> None:
    def parser_case() -> str:
        assert parse_checkpoint_steps("rl_model_51200_steps.zip") == 51200
        assert parse_checkpoint_steps("rl_model_460800_steps.zip") == 460800
        assert parse_checkpoint_steps("ppo_score_block.zip") is None
        assert parse_checkpoint_steps("rl_model_51200.zip") is None
        assert parse_checkpoint_steps("rl_model_abc_steps.zip") is None
        return "checkpoint filename step parsing"

    harness.check("checkpoint: step parsing", parser_case)

    def discovery() -> str:
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            payload = {51200: b"first", 102400: b"second"}
            for steps, blob in payload.items():
                (folder / f"rl_model_{steps}_steps.zip").write_bytes(blob)
            (folder / "unrelated.zip").write_bytes(b"ignored")
            (folder / "rl_model__steps.zip").write_bytes(b"glob-matches-but-not-the-pattern")
            rows = discover_checkpoints(folder)
            assert [row["training_steps"] for row in rows] == [51200, 102400], rows
            for row in rows:
                assert row["sha256"] == sha256_file(folder / row["filename"])
        # The uniqueness invariant is enforced on every discovery; exercise it
        # directly because the current filename scheme cannot collide by itself.
        try:
            assert_unique_checkpoint_steps([
                {"training_steps": 51200, "filename": "rl_model_51200_steps.zip"},
                {"training_steps": 51200, "filename": "rl_model_51200_steps-copy.zip"},
            ])
        except ValueError as exc:
            assert "duplicate checkpoint step count" in str(exc), str(exc)
            return "discovery + SHA-256 + step-uniqueness invariant"
        raise AssertionError("duplicate checkpoint steps were accepted")

    harness.check("checkpoint: discovery, SHA-256, step uniqueness", discovery)

    def default_interval() -> str:
        # Local import keeps the pure-logic checks independent of the SB3 venv layout.
        import train

        assert train.CHECKPOINT_INTERVAL_STEPS == 51200, train.CHECKPOINT_INTERVAL_STEPS
        assert train.TRAIN_SEED == 20260925, train.TRAIN_SEED
        assert train.TRAIN_SEED_POOL == [42, *range(1000, 2000)], "training pool changed"
        return "default snapshot interval 51200 single-env steps; train seed 20260925"

    harness.check("checkpoint: frozen training defaults", default_interval)


def artifact_checks(harness: Harness, train_dir: Path | None) -> None:
    if train_dir is None:
        for name in ("checkpoint: run-config registry vs disk",
                     "run-config: split version and checkpoint interval",
                     "progress.csv: PPO diagnostics readable",
                     "episodes.monitor.csv: referee info readable",
                     "run-config: checkpoint audit ok"):
            harness.skip(name, "no --train-dir given")
        return

    run_config_path = train_dir / "run-config.json"
    if not run_config_path.is_file():
        for name in ("checkpoint: run-config registry vs disk",
                     "run-config: split version and checkpoint interval",
                     "progress.csv: PPO diagnostics readable",
                     "episodes.monitor.csv: referee info readable",
                     "run-config: checkpoint audit ok"):
            harness.skip(name, f"{run_config_path} not found")
        return

    run_config = json.loads(run_config_path.read_text(encoding="utf-8"))

    def registry() -> str:
        registered = run_config.get("checkpoints") or []
        assert registered, "run-config has no checkpoint registry"
        on_disk = discover_checkpoints(train_dir / CHECKPOINT_DIR_NAME)
        assert len(registered) == len(on_disk), f"{len(registered)} registered vs {len(on_disk)} on disk"
        by_steps = {row["training_steps"]: row for row in on_disk}
        for row in registered:
            disk_row = by_steps.get(row["training_steps"])
            assert disk_row is not None, f"registered step {row['training_steps']} missing on disk"
            assert row["sha256"] == disk_row["sha256"], f"sha256 mismatch at {row['training_steps']}"
            assert row["filename"] == disk_row["filename"]
        return f"{len(registered)} checkpoints, steps and SHA-256 match disk"

    harness.check("checkpoint: run-config registry vs disk", registry)

    def split_meta() -> str:
        assert run_config.get("split_version") == SPLIT_VERSION, run_config.get("split_version")
        manifest = run_config.get("training_split") or {}
        assert manifest.get("fixed") == [42], manifest
        assert manifest.get("inclusive_range") == [1000, 1999], manifest
        interval = run_config.get("checkpoint_interval_single_env_steps")
        assert isinstance(interval, int) and interval > 0, interval
        callback = run_config.get("checkpoint_callback") or {}
        assert callback.get("save_freq_env_step_calls") == interval, callback
        assert callback.get("n_envs") == 1 and callback.get("save_freq_divided_by_n_envs") is False
        assert callback.get("name_prefix") == "rl_model", callback
        assert (run_config.get("best_model_selection") or {}).get(
            "uses_eval_callback_mean_reward") is False
        assert (run_config.get("observation") or {}).get("privileged_state") is True
        assert (run_config.get("observation") or {}).get("dimension") == 11
        for row in run_config.get("checkpoints") or []:
            assert row["training_steps"] % interval == 0, \
                f"checkpoint {row['filename']} is not a multiple of the interval {interval}"
        return (f"split version, interval {interval} consistent with "
                f"{len(run_config.get('checkpoints') or [])} checkpoints, no EvalCallback best model")

    harness.check("run-config: split version and checkpoint interval", split_meta)

    def progress() -> str:
        data = read_progress_csv(train_dir / PROGRESS_CSV_NAME)
        assert data["row_count"] > 0, "progress.csv has no data rows"
        missing = [column for column in REQUIRED_PROGRESS_COLUMNS if column not in data["header"]]
        assert not missing, f"progress.csv lacks columns: {missing}"
        for column in REQUIRED_PROGRESS_COLUMNS:
            stats = float_column_stats(data["rows"], column)
            assert stats["parsed"] > 0, f"{column} has no parseable value"
            assert stats["invalid"] == 0, f"{column} has {stats['invalid']} invalid values"
        return f"{data['row_count']} rows, {len(data['header'])} columns"

    harness.check("progress.csv: PPO diagnostics readable", progress)

    def monitor() -> str:
        data = read_monitor_csv(train_dir / MONITOR_CSV_NAME)
        assert data["row_count"] > 0, "Monitor CSV has no data rows"
        missing = [column for column in MONITOR_INFO_FIELDS if column not in data["header"]]
        assert not missing, f"Monitor CSV lacks info columns: {missing}"
        for column in ("r", "l", "t"):
            assert float_column_stats(data["rows"], column)["invalid"] == 0, column
        expected_rows = run_config.get("episode_log_rows")
        if expected_rows is not None:
            assert int(expected_rows) == data["row_count"], \
                f"run-config episode_log_rows={expected_rows} but CSV has {data['row_count']}"
        return f"{data['row_count']} episode rows; info fields present"

    harness.check("episodes.monitor.csv: referee info readable", monitor)

    def audit() -> str:
        result = audit_checkpoints(run_config, train_dir)
        assert result["ok"], result["issues"]
        return (f"audit ok: {result['registered_checkpoints']} checkpoints, "
                f"progress rows={result['progress_csv_rows']}, "
                f"monitor rows={result['monitor_csv_rows']}")

    harness.check("run-config: checkpoint audit ok", audit)


def cli_guard_checks(harness: Harness) -> None:
    def run_cli(extra: list[str]) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(SCRIPT_DIR / "evaluate.py"), *extra],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            cwd=str(SCRIPT_DIR))

    def no_freeze() -> str:
        result = run_cli(["--model", "does-not-matter.zip", "--split", FINAL_HOLDOUT_V2,
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "blind holdout ran without a freeze record"
        assert "--require-freeze" in (result.stderr + result.stdout), result.stderr
        return "blind holdout without --require-freeze is rejected"

    harness.check("CLI guard: blind holdout needs a freeze record", no_freeze)

    def freeze_wrong_split() -> str:
        result = run_cli(["--model", "does-not-matter.zip", "--split", DEVELOPMENT_V2,
                          "--require-freeze", "freeze.json",
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "--require-freeze accepted outside final_holdout_v2"
        assert "only valid with" in (result.stderr + result.stdout), result.stderr
        return "--require-freeze outside the blind split is rejected"

    harness.check("CLI guard: --require-freeze split scoping", freeze_wrong_split)

    def mixed_split_flags() -> str:
        result = run_cli(["--model", "does-not-matter.zip", "--final-holdout",
                          "--split", LEGACY_DEVELOPMENT,
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "--final-holdout plus --split was accepted"
        assert "cannot be combined" in (result.stderr + result.stdout), result.stderr
        return "--final-holdout plus --split is rejected"

    harness.check("CLI guard: split mixing rejected", mixed_split_flags)

    def reuses_split_seeds() -> str:
        result = run_cli(["--model", "does-not-matter.zip", "--seeds",
                          ",".join(str(seed) for seed in range(5001, 5011)),
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "custom seeds reusing development_v2 were accepted"
        assert "cannot reuse pre-registered split seeds" in (result.stderr + result.stdout), \
            result.stderr
        return "custom seeds reusing a named split are rejected"

    harness.check("CLI guard: custom seeds cannot reuse a named split", reuses_split_seeds)

    def existing_out() -> str:
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "already.json"
            out.write_text("{}", encoding="utf-8")
            result = run_cli(["--model", "does-not-matter.zip", "--split", DEVELOPMENT_V2,
                              "--out", str(out)])
            assert result.returncode != 0, "existing --out was overwritten without --force"
            assert "already exists" in (result.stderr + result.stdout), result.stderr
        return "an existing result file is not overwritten without --force"

    harness.check("CLI guard: pre-registered split runs once", existing_out)


def sweep_checks(harness: Harness, dev_sweep: Path | None, freeze: Path | None) -> None:
    if dev_sweep is None:
        harness.skip("selection: freeze matches the development sweep",
                     "no --dev-sweep given")
        return

    def selection_and_freeze() -> str:
        sweep = json.loads(dev_sweep.read_text(encoding="utf-8"))
        selection = sweep.get("selection") or {}
        candidate = selection.get("candidate")
        if candidate is None:
            assert selection.get("stop_reason") == "no_qualified_model", selection
            assert sweep.get("freeze_record_written") is False, "no candidate but a freeze was written"
            return f"no qualified model recorded ({selection.get('stop_reason')}); no freeze written"
        window = next(row for row in sweep["models"] if row["path"] == candidate["path"])
        for key in ("has_at_least_one_locked_target_score",
                    "locked_target_scores_not_below_fsm", "us_drops_not_above_fsm"):
            assert window["gate"][key] is True, f"candidate fails {key}"
        assert window["gate"]["traceability_ok"] is True, "candidate scores are not traceable"
        qualified = selection["ranking"] or []
        assert qualified and qualified[0]["path"] == candidate["path"], "ranking head != candidate"
        if freeze is not None:
            record = json.loads(freeze.read_text(encoding="utf-8"))
            assert record["candidate"]["sha256"] == candidate["sha256"], "freeze/model sha256 drift"
            assert record["candidate"]["training_steps"] == candidate["training_steps"]
            assert record["split_version"] == SPLIT_VERSION
            assert record["final_holdout_split"] == FINAL_HOLDOUT_V2
            assert record["frozen_before_final_holdout"] is True
            assert sha256_file(Path(candidate["path"])) == candidate["sha256"]
        return (f"candidate {candidate['filename']} at {candidate['training_steps']} steps "
                f"frozen with matching SHA-256")

    harness.check("selection: freeze matches the development sweep", selection_and_freeze)


def env_checks(harness: Harness, args) -> None:
    """Gymnasium contract + reset/action determinism (opt-in, needs the built CLI)."""
    names = ("gym: check_env contract", "gym: seed 42 reset/action determinism")
    if not args.gym_check:
        for name in names:
            harness.skip(name, "no --gym-check given")
        return

    import math

    from gym_env import ScoreBlockEnv, resolve_dotnet_executable

    repo_root = SCRIPT_DIR.parents[1]
    cli_dll = Path(args.cli_dll) if Path(args.cli_dll).is_absolute() else repo_root / args.cli_dll
    scenario = Path(args.scenario) if Path(args.scenario).is_absolute() else repo_root / args.scenario
    cli_dll, scenario = cli_dll.resolve(), scenario.resolve()
    if not cli_dll.is_file():
        for name in names:
            harness.skip(name, f"CLI dll not built: {cli_dll}")
        return
    dotnet = resolve_dotnet_executable(args.dotnet)
    # Deterministic action sequence (no RNG); seed 42 is a determinism probe, not a split.
    actions = [(math.sin(index * 0.7), math.cos(index * 1.3)) for index in range(100)]
    trace_keys = ("seed", "policy_ticks", "us_block_scores", "us_drops", "done_reason",
                  "score_us", "score_them", "attribution_ambiguous")

    def contract() -> str:
        from gymnasium.utils.env_checker import check_env
        with ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                           seed_pool=[42]) as env:
            check_env(env, skip_render_check=True)
            shape = env.observation_space.shape
        return f"Gymnasium check_env passed; observation_space={shape}"

    harness.check(names[0], contract)

    def determinism() -> str:
        def rollout() -> list:
            with ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                               seed_pool=[42]) as env:
                obs, info = env.reset(seed=42)
                trace = [([float(value) for value in obs], None, False, False,
                          {key: info.get(key) for key in trace_keys})]
                for action in actions:
                    obs, reward, terminated, truncated, info = env.step(action)
                    trace.append(([float(value) for value in obs], float(reward),
                                  bool(terminated), bool(truncated),
                                  {key: info.get(key) for key in trace_keys}))
                    if terminated or truncated:
                        break
                return trace

        first, second = rollout(), rollout()
        assert len(first) == len(second), f"step counts differ: {len(first)} vs {len(second)}"
        for index, (left, right) in enumerate(zip(first, second, strict=True)):
            assert left == right, f"frame {index} differs: {left} != {right}"
        return f"{len(first)} frames bit-identical across two seed-42 episodes"

    harness.check(names[1], determinism)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--train-dir", default=None,
                        help="training output dir (enables checkpoint/CSV artifact checks)")
    parser.add_argument("--dev-sweep", default=None,
                        help="development_v2 sweep JSON (enables selection/freeze checks)")
    parser.add_argument("--freeze", default=None, help="freeze record JSON")
    parser.add_argument("--out", default=None, help="write the check report as JSON")
    parser.add_argument("--gym-check", action="store_true",
                        help="run Gymnasium check_env and the seed-42 determinism probe")
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    args = parser.parse_args()

    harness = Harness()
    pure_checks(harness)
    checkpoint_checks(harness)
    artifact_checks(harness, Path(args.train_dir).resolve() if args.train_dir else None)
    cli_guard_checks(harness)
    env_checks(harness, args)
    sweep_checks(harness,
                 Path(args.dev_sweep).resolve() if args.dev_sweep else None,
                 Path(args.freeze).resolve() if args.freeze else None)

    report = {
        "checks": harness.results,
        "passed": sum(row["status"] == "passed" for row in harness.results),
        "failed": sum(row["status"] == "failed" for row in harness.results),
        "skipped": sum(row["status"] == "skipped" for row in harness.results),
    }
    for row in harness.results:
        print(f"[{row['status']:>7}] {row['name']}: {row['detail']}")
    print(f"\n{report['passed']} passed, {report['failed']} failed, {report['skipped']} skipped")
    if args.out:
        out = Path(args.out).expanduser().resolve()
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 1 if report["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
