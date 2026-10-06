#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Targeted self-checks for the SCORE_BLOCK PPO training and evaluation path.

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
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import splits
from splits import (
    DEVELOPMENT_V2,
    DEVELOPMENT_V3,
    DEVELOPMENT_V4,
    EXPLORATORY,
    FINAL_HOLDOUT_V2,
    FINAL_HOLDOUT_V3,
    FINAL_HOLDOUT_V4,
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
    DEVELOPMENT_V3: list(range(7001, 7021)),
    FINAL_HOLDOUT_V3: list(range(8001, 8051)),
    DEVELOPMENT_V4: list(range(9001, 9021)),
    FINAL_HOLDOUT_V4: list(range(10001, 10051)),
}
#: Seeds used only as "free range" fixtures: never registered, never in the training pool.
FREE_EXPLORATORY_SEEDS = list(range(11001, 11011))
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
        assert SPLIT_VERSION == "score-block-split-v4", SPLIT_VERSION
        return "8 named splits match the pre-registered seed lists"

    harness.check("split routing: named seed lists", routing)

    def profile_helpers() -> str:
        from profile import (compute_ipc_fraction, distribution, summarize_records,
                             validate_manifest)

        stats = distribution([1.0, 2.0, 3.0, 4.0])
        assert stats["sample_count"] == 4 and stats["p50"] == 2.5 and stats["p95"] == 4.0, stats
        assert compute_ipc_fraction([10.0, 10.0], [7.0, 7.0]) == 0.3
        assert compute_ipc_fraction([10.0], [11.0]) == 0.0, "negative overhead must clamp to zero"
        assert compute_ipc_fraction([], [1.0]) is None
        invalid = [{"stage": "env", "round": 1, "status": "invalid",
                    "faults": [{"type": "TimeoutError", "message": "fixture"}],
                    "samples": {"step_ms": [1.0, 2.0]}}]
        summary = summarize_records(invalid, "env")
        metric = summary["metrics"]["step_ms"]
        assert summary["invalid_rounds"] == [1]
        assert metric["sample_count"] == 0 and metric["invalid_round_sample_count_retained"] == 2
        assert metric["per_round"]["1"]["status"] == "invalid"
        required = ("machine", "python", "dotnet_version", "dotnet_executable", "dependencies",
                    "scenario", "scenario_sha256", "cli_dll", "cli_dll_sha256",
                    "ppo_hyperparameters", "warmup", "effective_parameters", "random_seed")
        assert validate_manifest({key: True for key in required}) == []
        assert "scenario_sha256" in validate_manifest({"machine": {}})
        return "percentiles, IPC clamp, invalid-round retention, and manifest fields"

    harness.check("profiling: summary and contract helpers", profile_helpers)

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
        assert selection.split == DEVELOPMENT_V4, selection.split
        assert selection.seeds == list(range(9001, 9021)), selection.seeds
        assert not selection.is_blind_holdout and not selection.is_exploratory
        return "default split is development_v4 (9001-9020)"

    harness.check("split routing: default is the new development set", defaults)

    def legacy_holdout() -> str:
        selection = resolve_selection(final_holdout=True)
        assert selection.split == LEGACY_FINAL_HOLDOUT, selection.split
        assert selection.seeds == list(range(4001, 4011)), selection.seeds
        assert resolve_selection(split=LEGACY_FINAL_HOLDOUT).seeds == selection.seeds
        return "--final-holdout still resolves to 4001-4010"

    harness.check("split routing: --final-holdout keeps its historical meaning", legacy_holdout)

    def blind_label() -> str:
        selection = resolve_selection(split=FINAL_HOLDOUT_V4)
        assert selection.is_blind_holdout and len(selection.seeds) == 50
        assert selection.manifest()["evaluation_split"] == FINAL_HOLDOUT_V4
        return "final_holdout_v4 is flagged as the one-shot blind split with 50 seeds"

    harness.check("split routing: new blind split label", blind_label)

    def revealed_holdouts_are_not_blind() -> str:
        for split in (LEGACY_FINAL_HOLDOUT, FINAL_HOLDOUT_V2, FINAL_HOLDOUT_V3):
            selection = resolve_selection(split=split)
            assert not selection.is_blind_holdout, f"{split} is still flagged blind"
            assert selection.is_revealed_holdout, f"{split} is not flagged as revealed"
            assert set(selection.seeds) <= splits.REVEALED_SEEDS, f"{split} seeds not revealed"
        assert splits.BLIND_SPLITS == (FINAL_HOLDOUT_V4,), splits.BLIND_SPLITS
        return "all historical holdouts including v3 are revealed and can never be blind again"

    harness.check("split routing: revealed holdouts are analysis-only", revealed_holdouts_are_not_blind)

    def revealed_covers_v3() -> str:
        assert set(SPLIT_SEEDS[DEVELOPMENT_V2]) <= splits.REVEALED_SEEDS
        assert set(SPLIT_SEEDS[FINAL_HOLDOUT_V2]) <= splits.REVEALED_SEEDS
        assert set(SPLIT_SEEDS[DEVELOPMENT_V3]) <= splits.REVEALED_SEEDS
        assert set(SPLIT_SEEDS[FINAL_HOLDOUT_V3]) <= splits.REVEALED_SEEDS
        assert set(SPLIT_SEEDS[DEVELOPMENT_V4]) & splits.REVEALED_SEEDS == set()
        assert set(SPLIT_SEEDS[FINAL_HOLDOUT_V4]) & splits.REVEALED_SEEDS == set()
        return "REVEALED_SEEDS includes v3 and excludes both v4 splits"

    harness.check("split registry: revealed set is complete", revealed_covers_v3)

    def exploratory_label() -> str:
        selection = resolve_selection(custom_seeds=list(FREE_EXPLORATORY_SEEDS))
        assert selection.split == EXPLORATORY and selection.is_exploratory
        assert not selection.is_blind_holdout
        return "custom seeds are labelled exploratory and never gate-eligible"

    harness.check("split routing: custom seeds are exploratory only", exploratory_label)

    def untraceable_score_fails_gate() -> str:
        from evaluate import evaluation_gate

        policy = {"total_locked_target_us_block_scores": 1, "total_us_drops": 0}
        fsm = {"total_locked_target_us_block_scores": 1, "total_us_drops": 0}
        row = {"seed": 7001, "locked_target_us_block_scores": 1,
               "position_event_cross_check": False}
        failed = evaluation_gate(policy, fsm, [row])
        assert failed["gate_passed"] is False
        assert failed["scores_without_position_event_cross_check"] == [7001]
        row["position_event_cross_check"] = True
        passed = evaluation_gate(policy, fsm, [row])
        assert passed["gate_passed"] is True
        return "an untraceable real score cannot pass the blind gate"

    harness.check("selection: untraceable score fails gate", untraceable_score_fails_gate)

    def revealed_v3_is_never_gate_evidence() -> str:
        from evaluate import evaluation_eligibility

        selection = resolve_selection(split=FINAL_HOLDOUT_V3)
        labels = evaluation_eligibility(selection, analysis_only=True)
        assert labels == {
            "is_blind_holdout": False,
            "is_revealed_holdout": True,
            "analysis_only": True,
            "gate_evidence_eligible": False,
        }, labels
        return "v3 analysis result labels it revealed and gate-ineligible"

    harness.check("split output: v3 analysis is gate-ineligible", revealed_v3_is_never_gate_evidence)

    def v4_blind_labels_are_distinct() -> str:
        from evaluate import evaluation_eligibility

        labels = evaluation_eligibility(
            resolve_selection(split=FINAL_HOLDOUT_V4), analysis_only=False)
        assert labels == {
            "is_blind_holdout": True,
            "is_revealed_holdout": False,
            "analysis_only": False,
            "gate_evidence_eligible": True,
        }, labels
        return "v4 is blind before the one-shot run; its consumed state has a separate index"

    harness.check("split output: v4 blind and revealed labels are distinct",
                  v4_blind_labels_are_distinct)

    def freeze_protocol() -> str:
        from evaluate import ConfigError, verify_freeze

        spec = {"path": "candidate.zip", "sha256": "a" * 64, "training_steps": 500000}
        scenario_hash, cli_hash = "b" * 64, "c" * 64
        record = {
            "protocol": "score-block-freeze-v1",
            "split_version": SPLIT_VERSION,
            "development_split": DEVELOPMENT_V4,
            "development_seeds": list(range(9001, 9021)),
            "final_holdout_split": FINAL_HOLDOUT_V4,
            "final_holdout_seeds": list(range(10001, 10051)),
            "frozen_before_final_holdout": True,
            "scenario_sha256": scenario_hash,
            "cli_dll_sha256": cli_hash,
            "candidate": {"sha256": spec["sha256"], "training_steps": spec["training_steps"]},
        }
        checks = verify_freeze(record, spec, scenario_hash, cli_hash)
        assert all(checks.values()), checks
        record["final_holdout_split"] = FINAL_HOLDOUT_V3
        try:
            verify_freeze(record, spec, scenario_hash, cli_hash)
        except ConfigError as exc:
            assert "final_holdout_split_matches" in str(exc), str(exc)
        else:
            raise AssertionError("a v3 freeze record was accepted for v4")
        record["final_holdout_split"] = FINAL_HOLDOUT_V4
        try:
            verify_freeze(record, spec, "different-scenario", cli_hash)
        except ConfigError as exc:
            assert "scenario_sha256_matches" in str(exc), str(exc)
        else:
            raise AssertionError("a mismatched scenario was accepted for v4")
        return "only a complete v4 development freeze authorizes the v4 holdout"

    harness.check("freeze: v4 identity required", freeze_protocol)

    def blind_run_is_one_shot() -> str:
        from evaluate import ConfigError, claim_blind_run, reveal_blind_run

        with tempfile.TemporaryDirectory() as tmp:
            index = Path(tmp) / "blind-index.json"
            freeze_path = Path(tmp) / "freeze.json"
            freeze_path.write_text("{}\n", encoding="utf-8")
            out_path = Path(tmp) / "result.json"
            record = claim_blind_run(index, FINAL_HOLDOUT_V4, freeze_path, out_path)
            started = json.loads(index.read_text(encoding="utf-8"))
            assert started["status"] == "started" and started["revealed"] is True, started
            assert started["freeze_sha256"] is not None, started
            try:
                claim_blind_run(index, FINAL_HOLDOUT_V4, freeze_path, out_path)
            except ConfigError as exc:
                assert "cannot be rerun" in str(exc), str(exc)
            else:
                raise AssertionError("the v4 blind run was claimed twice")
            reveal_blind_run(index, record, gate_passed=False)
            final = json.loads(index.read_text(encoding="utf-8"))
            assert final["status"] == "revealed" and final["revealed"] is True, final
            assert final["gate_passed"] is False, final
            try:
                claim_blind_run(index, FINAL_HOLDOUT_V4, freeze_path, out_path)
            except ConfigError:
                pass
            else:
                raise AssertionError("a failed revealed blind run could be repeated")
        return "failed runs remain revealed and the persistent index rejects every rerun"

    harness.check("blind guard: v4 formal run is one-shot", blind_run_is_one_shot)

    mutex_cases = [
        ("holdout + split", {"split": DEVELOPMENT_V3, "final_holdout": True},
         "cannot be combined with --split"),
        ("holdout + custom seeds", {"final_holdout": True, "custom_seeds": list(FREE_EXPLORATORY_SEEDS)},
         "cannot be combined with custom --seeds"),
        ("split + custom seeds",
         {"split": DEVELOPMENT_V3, "custom_seeds": list(FREE_EXPLORATORY_SEEDS)},
         "--seeds cannot be combined with --split"),
        ("custom seeds reuse historical v3 development split",
         {"custom_seeds": list(range(7001, 7011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse previous development split",
         {"custom_seeds": list(range(5001, 5011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse legacy development split",
         {"custom_seeds": list(range(3001, 3011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse legacy final holdout",
         {"custom_seeds": list(range(4001, 4011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse previous final holdout",
         {"custom_seeds": list(range(6001, 6011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse the new final holdout",
         {"custom_seeds": list(range(8001, 8011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse v4 development",
         {"custom_seeds": list(range(9001, 9011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds reuse v4 final holdout",
         {"custom_seeds": list(range(10001, 10011))}, "cannot reuse pre-registered split seeds"),
        ("custom seeds hit the training pool",
         {"custom_seeds": [42, *FREE_EXPLORATORY_SEEDS[:9]]}, "overlap the training episode pool"),
        ("custom seeds hit the training range",
         {"custom_seeds": list(range(1000, 1010))}, "overlap the training episode pool"),
        ("custom seeds too short", {"custom_seeds": FREE_EXPLORATORY_SEEDS[:5]},
         "at least 10 distinct seeds"),
        ("custom seeds with duplicates", {"custom_seeds": [FREE_EXPLORATORY_SEEDS[0]] * 10},
         "must not repeat"),
        ("unknown split", {"split": "development_v5"}, "unknown split"),
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
        assert train.V4_TRAIN_SEEDS == (20260927, 20260928, 20260929, 20260930, 20261001)
        assert train.TRAIN_SEED_POOL == [42, *range(1000, 2000)], "training pool changed"
        assert train.resolve_vector_config(1) == (2048, 51200)
        assert train.resolve_vector_config(2) == (1024, 25600)
        assert train.resolve_vector_config(4) == (512, 12800)
        for invalid in (0, 3, 128, 1024):
            try:
                train.resolve_vector_config(invalid)
            except ValueError:
                pass
            else:
                raise AssertionError(f"invalid n_envs {invalid} was accepted")
        try:
            train.resolve_vector_config(2, 51201)
        except ValueError:
            pass
        else:
            raise AssertionError("non-divisible global checkpoint interval was accepted")
        return "seed 20260925, single-env defaults, and global rollout/checkpoint divisors"

    harness.check("checkpoint: frozen training defaults", default_interval)

    def config_file_semantics() -> str:
        import train

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "train-config.json"
            path.write_text(json.dumps({
                "steps": 1000, "out": "config-out", "train_seed": 20260927,
                "n_envs": 2, "dotnet": None, "cli_dll": "config-cli.dll",
                "scenario": "config-scenario.json", "reward": "aggression-v1",
                "checkpoint_interval": 25600,
            }), encoding="utf-8")
            parser = train.build_argument_parser(train.load_train_config(path))
            # 显式 CLI 参数覆盖配置值, 未给出的字段取配置值。
            args = parser.parse_args(["--steps", "2000", "--reward", "v4"])
            assert (args.steps, args.reward) == (2000, "v4"), vars(args)
            assert (args.out, args.train_seed) == ("config-out", 20260927), vars(args)
            assert (args.n_envs, args.checkpoint_interval) == (2, 25600), vars(args)
            assert (args.dotnet, args.cli_dll, args.scenario) == (
                None, "config-cli.dll", "config-scenario.json"), vars(args)
            bad = Path(tmp) / "bad.json"
            bad.write_text(json.dumps({"stepz": 1}), encoding="utf-8")
            try:
                train.load_train_config(bad)
            except ValueError as exc:
                assert "stepz" in str(exc), str(exc)
            else:
                raise AssertionError("unknown config key was accepted")
        # 无 --config: 默认值与历史纯 CLI 一致 (--out 仍为必填)。
        baseline = train.build_argument_parser().parse_args(["--out", "run-out"])
        assert (baseline.steps, baseline.train_seed) == (500000, 20260925), vars(baseline)
        assert (baseline.n_envs, baseline.checkpoint_interval) == (1, 51200), vars(baseline)
        assert (baseline.reward, baseline.dotnet) == ("v4", None), vars(baseline)
        return "config defaults + explicit CLI override + unknown-key rejection + no-config baseline"

    harness.check("training: --config file semantics", config_file_semantics)

    def worker_seed_streams() -> str:
        import train

        legacy = train.build_episode_seed_streams(train.TRAIN_SEED, 1)
        assert legacy == [train.TRAIN_SEED_POOL]
        first = train.build_episode_seed_streams(20260927, 4)
        repeated = train.build_episode_seed_streams(20260927, 4)
        changed = train.build_episode_seed_streams(20260928, 4)
        assert first == repeated, "worker episode seeds are not deterministic"
        assert first != changed, "train seed does not affect worker episode streams"
        assert all(first), "worker stream is empty"
        flattened = [seed for stream in first for seed in stream]
        assert len(flattened) == len(set(flattened)), "workers share episode seeds"
        assert sorted(flattened) == sorted(train.build_episode_seed_streams(20260927, 1)[0])
        assert 20260927 not in flattened, "PPO seed was reused as an episode seed"
        run_pools = [train.build_episode_seed_streams(seed, 1)[0]
                     for seed in train.V4_TRAIN_SEEDS]
        combined = [seed for stream in run_pools for seed in stream]
        assert len(combined) == len(set(combined)), "v4 training runs share episode seeds"
        assert sorted(combined) == sorted(train.TRAIN_SEED_POOL)
        assert sorted(map(len, run_pools)) == [200, 200, 200, 200, 201]
        return "fixed v4 seeds and workers receive deterministic disjoint episode streams"

    harness.check("training: isolated deterministic worker episode seed streams", worker_seed_streams)

    def initial_episode_seed_is_separate_from_sb3_seed() -> str:
        import gymnasium as gym
        import numpy as np
        from gym_env import ScoreBlockEnv

        def fake_env(initial_episode_seed: int | None, seed_pool: list[int]):
            env = object.__new__(ScoreBlockEnv)
            gym.Env.__init__(env)
            env._initial_episode_seed = initial_episode_seed
            env._initial_episode_seed_pending = initial_episode_seed is not None
            env._seed_pool = seed_pool
            env._pool_idx = 0
            env._trace = False
            env._closed = False
            env.action_space = gym.spaces.Box(-1.0, 1.0, shape=(2,), dtype=np.float32)
            env.observation_space = gym.spaces.Box(-1.0, 1.0, shape=(11,), dtype=np.float32)
            sent: list[dict[str, object]] = []
            env._send = lambda payload: (
                sent.append(payload) or
                {"type": "reset", "obs": [0.0] * 11, "info": {}})
            return env, sent

        env, sent = fake_env(1234, [2345])
        first_obs, first_info = env.reset(seed=20260927)
        second_obs, second_info = env.reset()
        assert first_obs.shape == second_obs.shape == (11,)
        assert sent[0]["seed"] == first_info["seed"] == 1234
        assert sent[1]["seed"] == second_info["seed"] == 2345

        legacy_env, legacy_sent = fake_env(None, [42])
        legacy_env.reset(seed=20260925)
        assert legacy_sent[0]["seed"] == 20260925
        return "episode seeds are separate from SB3 RNG while the historical default remains stable"

    harness.check("training: scenario reset stream is independent from SB3 seed",
                  initial_episode_seed_is_separate_from_sb3_seed)

    def crashed_bridge_close() -> str:
        from types import SimpleNamespace

        from gym_env import ScoreBlockEnv

        class BrokenPipe:
            def close(self) -> None:
                raise OSError("child exited before pipe close")

        env = object.__new__(ScoreBlockEnv)
        env._closed = False
        env._proc = SimpleNamespace(poll=lambda: 1, stdin=BrokenPipe(), stdout=BrokenPipe())
        env.close()
        assert env._closed
        return "broken Windows child pipes do not mask the original training failure"

    harness.check("training: crashed bridge close preserves failure record", crashed_bridge_close)

    def global_checkpoint_audit() -> str:
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            checkpoints = out / CHECKPOINT_DIR_NAME
            checkpoints.mkdir()
            for step in (51200, 102400):
                (checkpoints / f"rl_model_{step}_steps.zip").write_bytes(str(step).encode("ascii"))
            (out / FINAL_MODEL_NAME).write_bytes(b"model")
            run_config = {
                "checkpoint_interval_transitions": 51200,
                "actual_global_transitions": 128000,
                "model_zip_sha256": sha256_file(out / FINAL_MODEL_NAME),
                "checkpoints": discover_checkpoints(checkpoints),
            }
            audit = audit_checkpoints(run_config, out)
            assert audit["ok"], audit["issues"]
            run_config["actual_global_transitions"] = 153600
            audit = audit_checkpoints(run_config, out)
            assert not audit["ok"] and any("global-transition cadence" in issue
                                           for issue in audit["issues"]), audit
        return "checkpoint filenames are audited against actual global transitions"

    harness.check("checkpoint: global transition cadence audit", global_checkpoint_audit)

    def output_collision_guard() -> str:
        import train

        with tempfile.TemporaryDirectory() as temporary:
            fresh = Path(temporary) / "fresh-run"
            assert train.prepare_output_directory(fresh) == fresh.resolve()
            (fresh / "run-config.json").write_text("fixture", encoding="utf-8")
            try:
                train.prepare_output_directory(fresh)
            except FileExistsError:
                pass
            else:
                raise AssertionError("non-empty training output directory was reused")
        return "empty output created; non-empty output rejected"

    harness.check("training: output directory collision guard", output_collision_guard)


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
        assert manifest.get("legacy_default_initial_sb3_reset_episode_seed") == 20260925, manifest
        assert manifest.get("initial_episode_seeds_for_this_run"), manifest
        train_seed = run_config.get("train_seed")
        assert isinstance(train_seed, int) and 0 <= train_seed <= 0xFFFFFFFF, train_seed
        assert int(run_config.get("actual_global_transitions", 0)) >= int(
            run_config.get("total_timesteps_requested", 1)), run_config
        assert run_config.get("faults_total") == 0, run_config.get("faults_total")
        code = run_config.get("code_identity") or {}
        assert code.get("git_commit") and isinstance(code.get("git_worktree_dirty"), bool), code
        assert all(isinstance(code.get(path), str) and len(code[path]) == 64 for path in (
            "controllers/score_block_rl/train.py",
            "controllers/score_block_rl/gym_env.py",
            "controllers/score_block_rl/train_artifacts.py",
            "controllers/score_block_rl/splits.py",
        )), code
        hardware = run_config.get("hardware_identity") or {}
        assert hardware.get("hostname") and hardware.get("logical_cpu_count"), hardware
        assert (run_config.get("dotnet_runtime") or {}).get("executable"), run_config.get(
            "dotnet_runtime")
        interval = run_config.get("checkpoint_interval_transitions",
                                  run_config.get("checkpoint_interval_single_env_steps"))
        assert isinstance(interval, int) and interval > 0, interval
        callback = run_config.get("checkpoint_callback") or {}
        n_envs = int((run_config.get("ppo_parameters") or {}).get("n_envs", 1))
        assert callback.get("save_freq_env_step_calls") * n_envs == interval, callback
        assert callback.get("n_envs") == n_envs, callback
        assert callback.get("global_transition_interval") == interval, callback
        assert callback.get("name_prefix") == "rl_model", callback
        n_steps = int((run_config.get("ppo_parameters") or {}).get("n_steps", 2048))
        assert n_steps * n_envs == run_config.get("global_rollout_transitions_per_update", 2048)
        assert (run_config.get("best_model_selection") or {}).get(
            "uses_eval_callback_mean_reward") is False
        assert (run_config.get("observation") or {}).get("privileged_state") is True
        assert (run_config.get("observation") or {}).get("dimension") == 11
        for row in run_config.get("checkpoints") or []:
            assert row["training_steps"] % interval == 0, \
                f"checkpoint {row['filename']} is not a multiple of global interval {interval}"
        return (f"split version, interval {interval} consistent with "
                f"{len(run_config.get('checkpoints') or [])} global-transition checkpoints, "
                "no EvalCallback best model")

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
        result = run_cli(["--model", "does-not-matter.zip", "--split", FINAL_HOLDOUT_V4,
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "blind holdout ran without a freeze record"
        assert "--require-freeze" in (result.stderr + result.stdout), result.stderr
        return "blind holdout without --require-freeze is rejected"

    harness.check("CLI guard: blind holdout needs a freeze record", no_freeze)

    def freeze_wrong_split() -> str:
        result = run_cli(["--model", "does-not-matter.zip", "--split", DEVELOPMENT_V4,
                          "--require-freeze", "freeze.json",
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "--require-freeze accepted outside final_holdout_v4"
        assert "only valid with" in (result.stderr + result.stdout), result.stderr
        v3 = run_cli(["--model", "does-not-matter.zip", "--split", FINAL_HOLDOUT_V3,
                      "--analysis-only", "--require-freeze", "freeze.json",
                      "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert v3.returncode != 0, "a v3 analysis run accepted a freeze record"
        assert "only valid with" in (v3.stderr + v3.stdout), v3.stderr
        return "--require-freeze outside the blind split is rejected"

    harness.check("CLI guard: --require-freeze split scoping", freeze_wrong_split)

    def revealed_holdout_needs_analysis_only() -> str:
        for split in (LEGACY_FINAL_HOLDOUT, FINAL_HOLDOUT_V2, FINAL_HOLDOUT_V3):
            result = run_cli(["--model", "does-not-matter.zip", "--split", split,
                              "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
            assert result.returncode != 0, f"{split} ran without --analysis-only"
            assert "--analysis-only" in (result.stderr + result.stdout), result.stderr
        with tempfile.TemporaryDirectory() as tmp:
            v3_analysis = run_cli(["--model", "does-not-matter.zip", "--split", FINAL_HOLDOUT_V3,
                                   "--analysis-only", "--out", str(Path(tmp) / "analysis.json")])
        assert "model file not found" in (v3_analysis.stderr + v3_analysis.stdout), \
            v3_analysis.stderr
        result = run_cli(["--model", "does-not-matter.zip", "--split", DEVELOPMENT_V4,
                          "--analysis-only",
                          "--out", str(Path(tempfile.gettempdir()) / "score-block-unused.json")])
        assert result.returncode != 0, "--analysis-only accepted on a non-revealed split"
        assert "only meaningful" in (result.stderr + result.stdout), result.stderr
        return "revealed holdouts require --analysis-only; the flag is rejected elsewhere"

    harness.check("CLI guard: revealed holdouts are analysis-only", revealed_holdout_needs_analysis_only)

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
            result = run_cli(["--model", "does-not-matter.zip", "--split", DEVELOPMENT_V4,
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
            assert record["protocol"] == "score-block-freeze-v1"
            assert record["split_version"] == SPLIT_VERSION
            assert record["development_split"] == DEVELOPMENT_V4
            assert record["development_seeds"] == list(range(9001, 9021))
            assert record["final_holdout_split"] == FINAL_HOLDOUT_V4
            assert record["final_holdout_seeds"] == list(range(10001, 10051))
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

    def profiler_quick() -> str:
        out_argument = Path(args.out).expanduser().resolve() if args.out else Path.cwd() / "selftest.json"
        profile_out = out_argument.parent / f"profile-quick-{os.getpid()}-{time.time_ns()}"
        result = subprocess.run(
            [sys.executable, "-X", "utf8", str(SCRIPT_DIR / "profile.py"),
             "--quick", "--out", str(profile_out), "--dotnet", dotnet,
             "--cli-dll", str(cli_dll), "--scenario", str(scenario)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            cwd=str(SCRIPT_DIR.parents[1]), timeout=300)
        assert result.returncode == 0, (
            f"profiler --quick exited {result.returncode}: {result.stdout}\n{result.stderr}")
        manifest_path, summary_path = profile_out / "manifest.json", profile_out / "summary.json"
        assert manifest_path.is_file() and summary_path.is_file(), "quick run omitted manifest/summary"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        assert not __import__("profile").validate_manifest(manifest)
        assert all(summary["stages"][stage]["valid_round_count"] == 1
                   for stage in ("env", "ipc", "ppo")), summary["stages"]
        for stage, name in (("env", "env-round-001.json"),
                            ("ipc", "ipc-round-001.json"),
                            ("ppo", "ppo-round-001.json")):
            assert (profile_out / "raw" / name).is_file(), f"missing {stage} raw sample"
        assert (profile_out / "resources.csv").is_file(), "missing synchronized resource samples"
        return (f"profile --quick completed; output={profile_out}; "
                f"samples={summary['stages']}")

    harness.check("profiling: --quick end-to-end", profiler_quick)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--train-dir", default=None,
                        help="training output dir (enables checkpoint/CSV artifact checks)")
    parser.add_argument("--dev-sweep", default=None,
        help="development_v4 sweep JSON (enables selection/freeze checks)")
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
