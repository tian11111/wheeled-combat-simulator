#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Evaluate SCORE_BLOCK PPO checkpoints against the same-seed FSM baseline.

Exit codes: ``0`` results written; ``2`` invalid split/model/CLI configuration;
``3`` a development sweep recorded ``no_qualified_model`` (the blind holdout must
not be opened).

Split discipline (``splits.py`` is the single source of truth):

* ``--split development_v3`` (7001-7020) is this round's model-selection set.
* ``--split final_holdout_v3`` (8001-8050) is the one-shot blind set; it
  requires ``--require-freeze`` so the candidate was frozen beforehand.
* ``--final-holdout`` keeps its historical meaning: 4001-4010, already
  revealed, comparison only.
* ``legacy_development`` (3001-3010), ``development_v2`` (5001-5020) and
  ``final_holdout_v2`` (6001-6050) are already revealed; custom ``--seeds`` are
  exploration. None of them is gate evidence.
* A revealed holdout (``legacy_final_holdout``, ``final_holdout_v2``) additionally
  requires ``--analysis-only`` and its result is written with
  ``gate_evidence_eligible: false`` — a revealed seed set can never be a blind set
  again.

Every model is evaluated with ``deterministic=True`` in a fresh evaluation
environment, and every seed is paired with a built-in-FSM run that starts from
the same ``reset(seed)`` first ``SCORE_BLOCK`` entry. Success claims use only
real referee events: the locked target's own-side ``BlockScore`` and our own
``Drop``. Episode reward and the final score are reported but never substitute
for those events.

The previous round's AC4 (4001-4010) is closed as failed; ``ac4_claim_eligible``
is therefore always ``false`` and new results can never be claimed as that AC4.
"""

from __future__ import annotations

import argparse
import json
import platform
import sys
from datetime import datetime, timezone
from pathlib import Path

from stable_baselines3 import PPO

from gym_env import MAX_POLICY_TICKS, ScoreBlockEnv, resolve_dotnet_executable
from splits import (
    DEVELOPMENT_V3,
    FINAL_HOLDOUT_V3,
    LEGACY_DEVELOPMENT,
    LEGACY_FINAL_HOLDOUT,
    REVEALED_HOLDOUT_SPLITS,
    SPLIT_VERSION,
    SplitError,
    resolve_selection,
    seeds_for,
    training_pool_manifest,
)
from train_artifacts import (
    FINAL_MODEL_NAME,
    discover_checkpoints,
    package_version,
    parse_checkpoint_steps,
    sha256_file,
)

PROTOCOL = "rl-env JSONL; each row begins at its own reset(seed) SCORE_BLOCK entry"
PAIRED_BY = "same seed, same first SCORE_BLOCK entry (reset pre-roll)"
EXPECTED_OBSERVATION_SHAPE = (11,)
AC4_CLAIM_NOTE = (
    "the previous round's AC4 on the revealed 4001-4010 holdout is closed as failed; "
    "no later run can be claimed as that AC4"
)
#: Legacy split names used before split v2, kept for report continuity.
LEGACY_ALIASES = {
    LEGACY_DEVELOPMENT: "development",
    LEGACY_FINAL_HOLDOUT: "final_holdout",
}


class ConfigError(ValueError):
    """Raised for invalid model paths or evaluation configuration."""


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
        "stage_entry_tick": entry_tick,
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


def run_episode(env: ScoreBlockEnv, seed: int, policy, use_fsm_baseline: bool) -> dict:
    """Run one episode for ``seed`` and return its referee-event metrics."""
    obs, info = env.reset(seed=seed)
    if info.get("no_score_block"):
        return result_metrics(seed, info, 0.0)

    total_reward = 0.0
    for _ in range(MAX_POLICY_TICKS):
        if use_fsm_baseline:
            obs, reward, terminated, truncated, info = env.step_fsm()
        else:
            action, _ = policy.predict(obs, deterministic=True)
            obs, reward, terminated, truncated, info = env.step(action)
        total_reward += float(reward)
        if terminated or truncated:
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


def evaluation_gate(policy_summary: dict, fsm_summary: dict, policy_rows: list[dict]) -> dict:
    """Project gate: real referee events only, never reward or final score."""
    scoring_rows = [row for row in policy_rows if row["locked_target_us_block_scores"] > 0]
    untraceable = [row["seed"] for row in scoring_rows
                   if row["position_event_cross_check"] is not True]
    project_gate = {
        "has_at_least_one_locked_target_score":
            policy_summary["total_locked_target_us_block_scores"] > 0,
        "locked_target_scores_not_below_fsm":
            policy_summary["total_locked_target_us_block_scores"]
            >= fsm_summary["total_locked_target_us_block_scores"],
        "us_drops_not_above_fsm":
            policy_summary["total_us_drops"] <= fsm_summary["total_us_drops"],
    }
    return {
        **project_gate,
        "gate_passed": all(project_gate.values()),
        "traceability_ok": not untraceable,
        "scores_without_position_event_cross_check": untraceable,
        "note": "gate uses locked-target real BlockScore and our own Drop only; "
                "reward and final score are not gate evidence",
    }


def paired_per_seed(policy_rows: list[dict], fsm_rows: list[dict]) -> list[dict]:
    """Explicit same-seed pairing of policy and FSM referee metrics."""
    fsm_by_seed = {row["seed"]: row for row in fsm_rows}
    paired = []
    for policy_row in policy_rows:
        fsm_row = fsm_by_seed[policy_row["seed"]]
        paired.append({
            "seed": policy_row["seed"],
            "policy_locked_target_us_block_scores": policy_row["locked_target_us_block_scores"],
            "fsm_locked_target_us_block_scores": fsm_row["locked_target_us_block_scores"],
            "policy_us_drops": policy_row["us_drops"],
            "fsm_us_drops": fsm_row["us_drops"],
            "policy_no_score_block": policy_row["no_score_block"],
            "fsm_no_score_block": fsm_row["no_score_block"],
            "policy_position_event_cross_check": policy_row["position_event_cross_check"],
        })
    return paired


def ranking_key(report: dict) -> tuple:
    """More locked-target scores, then fewer drops, then more training steps."""
    return (
        -int(report["policy_summary"]["total_locked_target_us_block_scores"]),
        int(report["policy_summary"]["total_us_drops"]),
        -int(report["training_steps"]),
    )


def select_candidate(reports: list[dict]) -> dict:
    eligible = [report for report in reports
                if report["gate"]["gate_passed"] and report["gate"]["traceability_ok"]]
    eligible_paths = {report["path"] for report in eligible}
    ranked = sorted(eligible, key=ranking_key)
    ranked_manifest = [{
        "filename": report["filename"],
        "path": report["path"],
        "training_steps": report["training_steps"],
        "sha256": report["sha256"],
        "total_locked_target_us_block_scores":
            report["policy_summary"]["total_locked_target_us_block_scores"],
        "total_us_drops": report["policy_summary"]["total_us_drops"],
        "ranking_key": list(ranking_key(report)),
    } for report in ranked]
    rejected = [{
        "filename": report["filename"],
        "gates": {key: report["gate"][key] for key in (
            "has_at_least_one_locked_target_score",
            "locked_target_scores_not_below_fsm",
            "us_drops_not_above_fsm",
            "traceability_ok")},
        "total_locked_target_us_block_scores":
            report["policy_summary"]["total_locked_target_us_block_scores"],
        "total_us_drops": report["policy_summary"]["total_us_drops"],
        "scores_without_position_event_cross_check":
            report["gate"]["scores_without_position_event_cross_check"],
    } for report in reports if report["path"] not in eligible_paths]
    return {
        "rule": "qualified = (>=1 locked-target real BlockScore) AND (scores >= FSM) AND "
                "(us drops <= FSM) AND (all scores traceable); then sort by more target "
                "scores, fewer drops, more training steps",
        "evaluated_models": len(reports),
        "qualified_models": len(ranked),
        "ranking": ranked_manifest,
        "rejected_models": rejected,
        "candidate": ranked_manifest[0] if ranked_manifest else None,
        "stop_reason": None if ranked_manifest else "no_qualified_model",
    }


def collect_model_specs(args, repo_root: Path) -> list[dict]:
    specs: list[dict] = []
    seen: set[str] = set()

    def add(path: Path) -> None:
        resolved = path.expanduser()
        if not resolved.is_absolute():
            resolved = repo_root / resolved
        resolved = resolved.resolve()
        if not resolved.is_file():
            raise ConfigError(f"model file not found: {resolved}")
        key = str(resolved)
        if key in seen:
            return
        seen.add(key)
        filename = resolved.name
        step_from_name = parse_checkpoint_steps(filename)
        specs.append({
            "path": key,
            "filename": filename,
            "training_steps_from_filename": step_from_name,
            "kind": "checkpoint" if step_from_name is not None else "final_or_named_model",
            "sha256": sha256_file(resolved),
        })

    for raw in (args.model or []):
        add(Path(raw))
    if args.checkpoints_dir:
        checkpoint_dir = Path(args.checkpoints_dir)
        if not checkpoint_dir.is_absolute():
            checkpoint_dir = repo_root / checkpoint_dir
        checkpoint_dir = checkpoint_dir.resolve()
        if not checkpoint_dir.is_dir():
            raise ConfigError(f"--checkpoints-dir is not a directory: {checkpoint_dir}")
        for row in discover_checkpoints(checkpoint_dir):
            add(Path(row["path"]))
        final_model = checkpoint_dir.parent / FINAL_MODEL_NAME
        if final_model.is_file():
            add(final_model)
    if not specs:
        raise ConfigError("provide at least one --model, or --checkpoints-dir with checkpoints")
    return specs


def load_freeze(path: Path) -> dict:
    if not path.is_file():
        raise ConfigError(f"freeze record not found: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ConfigError(f"freeze record is not valid JSON: {exc}") from exc


def resolve_model_identity(specs: list[dict]) -> None:
    """Validate the observation contract and resolve the training-step tie-breaker."""
    for spec in specs:
        policy = PPO.load(spec["path"])
        shape = tuple(policy.observation_space.shape)
        if shape != EXPECTED_OBSERVATION_SHAPE:
            raise ConfigError(
                f"{spec['filename']}: observation shape {shape} is incompatible; "
                f"expected {EXPECTED_OBSERVATION_SHAPE}. Models trained with the previous "
                "9-value observation must be retrained.")
        spec["model_num_timesteps"] = int(policy.num_timesteps)
        spec["training_steps"] = (spec["training_steps_from_filename"]
                                  if spec["training_steps_from_filename"] is not None
                                  else spec["model_num_timesteps"])
        spec["training_steps_source"] = ("checkpoint_filename"
                                        if spec["training_steps_from_filename"] is not None
                                        else "model_num_timesteps")
        spec["filename_steps_match_model"] = (
            spec["training_steps_from_filename"] is None
            or spec["training_steps_from_filename"] == spec["model_num_timesteps"])
        del policy


def verify_freeze(freeze: dict, spec: dict) -> dict:
    candidate = freeze.get("candidate") or {}
    checks = {
        "split_version_matches": freeze.get("split_version") == SPLIT_VERSION,
        "final_holdout_split_matches": freeze.get("final_holdout_split") == FINAL_HOLDOUT_V3,
        "candidate_sha256_matches": candidate.get("sha256") == spec["sha256"],
        "candidate_training_steps_matches":
            candidate.get("training_steps") == spec["training_steps"],
    }
    failed = [name for name, ok in checks.items() if not ok]
    if failed:
        raise ConfigError(
            f"freeze record does not match the evaluated model: {failed}; "
            f"freeze_candidate={candidate} model={ {k: spec[k] for k in ('path', 'training_steps', 'sha256')} }")
    return checks


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", action="append", default=None,
                        help="model/checkpoint path; repeatable")
    parser.add_argument("--checkpoints-dir", default=None,
                        help="directory of rl_model_<steps>_steps.zip files; the final "
                             "ppo_score_block.zip next to it is included automatically")
    parser.add_argument("--split", default=None,
                        help="legacy_development | legacy_final_holdout | development_v2 | "
                             "final_holdout_v2 | development_v3 | final_holdout_v3 | "
                             "exploratory (default: development_v3)")
    parser.add_argument("--final-holdout", action="store_true",
                        help="historical final holdout 4001-4010 (already revealed); "
                             "equivalent to --split legacy_final_holdout")
    parser.add_argument("--analysis-only", action="store_true",
                        help="acknowledge that an already-revealed holdout is used for "
                             "analysis only; required for legacy_final_holdout/final_holdout_v2")
    parser.add_argument("--seeds", default=None,
                        help="custom exploratory seeds; results are never gate evidence")
    parser.add_argument("--select-candidate", action="store_true",
                        help="rank the evaluated models and freeze one candidate")
    parser.add_argument("--freeze", default=None,
                        help="write the frozen-candidate JSON for the development_v3 selection")
    parser.add_argument("--require-freeze", default=None,
                        help="required for final_holdout_v3: the freeze record to verify")
    parser.add_argument("--force", action="store_true",
                        help="allow overwriting an existing --out file")
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    parser.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    custom_seeds = None
    if args.seeds is not None:
        try:
            custom_seeds = [int(item.strip()) for item in args.seeds.split(",") if item.strip()]
        except ValueError as exc:
            parser.error(f"--seeds must be a comma-separated list of integers: {exc}")
    try:
        selection = resolve_selection(split=args.split, custom_seeds=custom_seeds,
                                     final_holdout=args.final_holdout)
    except SplitError as exc:
        parser.error(str(exc))

    if args.select_candidate and selection.split != DEVELOPMENT_V3:
        parser.error(f"--select-candidate requires --split {DEVELOPMENT_V3}")
    if args.freeze and selection.split != DEVELOPMENT_V3:
        parser.error(f"--freeze requires --split {DEVELOPMENT_V3}")
    if selection.split == FINAL_HOLDOUT_V3 and not args.require_freeze:
        parser.error(f"--split {FINAL_HOLDOUT_V3} requires --require-freeze <freeze.json>; "
                     "freeze the candidate on the development split first")
    if args.require_freeze and selection.split != FINAL_HOLDOUT_V3:
        parser.error(f"--require-freeze is only valid with --split {FINAL_HOLDOUT_V3}")
    if selection.is_revealed_holdout and not args.analysis_only:
        parser.error(
            f"--split {selection.split} is an already-revealed holdout and can never be a blind "
            "set again; pass --analysis-only to confirm this run is analysis, not gate evidence")
    if args.analysis_only and not selection.is_revealed_holdout:
        parser.error("--analysis-only is only meaningful for an already-revealed holdout "
                     f"({'/'.join(REVEALED_HOLDOUT_SPLITS)})")

    out_path = Path(args.out).expanduser().resolve()
    if out_path.exists() and not args.force:
        parser.error(f"{out_path} already exists; a pre-registered split runs once "
                     "(pass --force to overwrite deliberately)")
    out_path.parent.mkdir(parents=True, exist_ok=True)

    repo_root = Path(__file__).resolve().parents[2]
    try:
        specs = collect_model_specs(args, repo_root)
        resolve_model_identity(specs)
    except (ConfigError, SplitError) as exc:
        parser.error(str(exc))

    cli_dll = Path(args.cli_dll)
    scenario = Path(args.scenario)
    if not cli_dll.is_absolute():
        cli_dll = repo_root / cli_dll
    if not scenario.is_absolute():
        scenario = repo_root / scenario
    cli_dll = cli_dll.resolve()
    scenario = scenario.resolve()
    if not cli_dll.is_file():
        parser.error(f"CLI dll not found: {cli_dll} (build RobotSimulator.sln first)")
    if not scenario.is_file():
        parser.error(f"scenario not found: {scenario}")
    dotnet = resolve_dotnet_executable(args.dotnet)

    freeze = None
    freeze_checks = None
    if args.require_freeze:
        freeze_path = Path(args.require_freeze)
        if not freeze_path.is_absolute():
            freeze_path = repo_root / freeze_path
        freeze = load_freeze(freeze_path.resolve())
        if len(specs) != 1:
            parser.error("a blind final evaluation must load exactly one frozen model")
        try:
            freeze_checks = verify_freeze(freeze, specs[0])
        except ConfigError as exc:
            parser.error(str(exc))

    models: list[dict] = []
    with ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=120.0,
                       seed_pool=selection.seeds) as env:
        # The built-in FSM path never consumes the policy, so one baseline run
        # per seed is valid evidence for every model in this sweep.
        fsm_rows = [run_episode(env, seed, None, use_fsm_baseline=True)
                    for seed in selection.seeds]
        fsm_summary = summarize(fsm_rows)
        for spec in specs:
            policy = PPO.load(spec["path"])
            policy_rows = [run_episode(env, seed, policy, use_fsm_baseline=False)
                           for seed in selection.seeds]
            del policy
            policy_summary = summarize(policy_rows)
            models.append({
                **{key: spec[key] for key in (
                    "path", "filename", "kind", "sha256", "training_steps",
                    "training_steps_source", "training_steps_from_filename",
                    "model_num_timesteps", "filename_steps_match_model")},
                "policy_summary": policy_summary,
                "gate": evaluation_gate(policy_summary, fsm_summary, policy_rows),
                "paired_per_seed": paired_per_seed(policy_rows, fsm_rows),
                "per_episode_policy": policy_rows,
            })

    selection_result = select_candidate(models) if args.select_candidate else None
    results = {
        "protocol": PROTOCOL,
        "split_version": SPLIT_VERSION,
        "evaluation_split": selection.split,
        "evaluation_split_usage": selection.usage,
        "evaluation_split_legacy_name": LEGACY_ALIASES.get(selection.split),
        "seeds": list(selection.seeds),
        "seed_count": len(selection.seeds),
        "is_blind_holdout": selection.is_blind_holdout,
        "is_exploratory": selection.is_exploratory,
        "is_revealed_holdout": selection.is_revealed_holdout,
        "analysis_only": bool(args.analysis_only),
        "gate_evidence_eligible": bool(selection.is_blind_holdout),
        "ac4_claim_eligible": False,
        "ac4_claim_eligible_note": AC4_CLAIM_NOTE,
        "previous_round_ac4_split": LEGACY_FINAL_HOLDOUT,
        "new_round_blind_gate_passed": None,
        "blind_run_index": None,
        "frozen_candidate": (freeze or {}).get("candidate"),
        "freeze_checks": freeze_checks,
        "evaluation_mode": "PPO deterministic",
        "deterministic_actions": True,
        "privileged_state": True,
        "privileged_state_note": "the 11-value observation includes simulator ground-truth "
                                 "block coordinates; this is not a real-robot sensor policy",
        "paired_by": PAIRED_BY,
        "fsm_baseline_is_model_independent": True,
        "training_seed_pool": training_pool_manifest(),
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
        "models": models,
        "fsm_baseline": {"summary": fsm_summary, "per_episode": fsm_rows},
        "selection": selection_result,
        "evaluated_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "block_off_semantics": "BlockOff is emitted by the referee only when a block exits "
                               "without a valid scoring owner.",
    }

    if args.freeze:
        if selection_result is None:
            parser.error("--freeze requires --select-candidate")
        candidate = selection_result["candidate"]
        if candidate is None:
            results["freeze_record_written"] = False
            results["freeze_record_skip_reason"] = selection_result["stop_reason"]
        else:
            model = next(row for row in models if row["path"] == candidate["path"])
            freeze_record = {
                "protocol": "score-block-freeze-v1",
                "split_version": SPLIT_VERSION,
                "frozen_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "development_split": selection.split,
                "development_seeds": list(selection.seeds),
                "candidate": {
                    "path": candidate["path"],
                    "filename": candidate["filename"],
                    "training_steps": candidate["training_steps"],
                    "sha256": candidate["sha256"],
                },
                "ranking_key": candidate["ranking_key"],
                "gate": {key: model["gate"][key] for key in (
                    "has_at_least_one_locked_target_score",
                    "locked_target_scores_not_below_fsm",
                    "us_drops_not_above_fsm",
                    "traceability_ok", "gate_passed")},
                "development_summary": {
                    "policy": model["policy_summary"],
                    "fsm_baseline": fsm_summary,
                },
                "scenario": str(scenario),
                "scenario_sha256": sha256_file(scenario),
                "cli_dll": str(cli_dll),
                "cli_dll_sha256": sha256_file(cli_dll),
                "final_holdout_split": FINAL_HOLDOUT_V3,
                "final_holdout_seeds": seeds_for(FINAL_HOLDOUT_V3),
                "frozen_before_final_holdout": True,
                "note": "freeze record written before any final_holdout_v3 run",
            }
            freeze_path = Path(args.freeze)
            if not freeze_path.is_absolute():
                freeze_path = repo_root / freeze_path
            freeze_path = freeze_path.resolve()
            freeze_path.parent.mkdir(parents=True, exist_ok=True)
            freeze_path.write_text(json.dumps(freeze_record, ensure_ascii=False, indent=2) + "\n",
                                   encoding="utf-8")
            results["freeze_record_written"] = True
            results["freeze_record_path"] = str(freeze_path)
            results["freeze_record"] = freeze_record

    if selection.split == FINAL_HOLDOUT_V3:
        results["blind_run_index"] = 1
        results["new_round_blind_gate_passed"] = (bool(models[0]["gate"]["gate_passed"])
                                                 if len(models) == 1 else None)

    out_path.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n",
                        encoding="utf-8")
    printable = {
        "evaluation_split": results["evaluation_split"],
        "split_version": results["split_version"],
        "seed_count": results["seed_count"],
        "models": [{"filename": model["filename"],
                    "training_steps": model["training_steps"],
                    "gate_passed": model["gate"]["gate_passed"]} for model in models],
        "fsm_baseline": fsm_summary,
        "selection": selection_result["candidate"] if selection_result else None,
        "new_round_blind_gate_passed": results["new_round_blind_gate_passed"],
        "ac4_claim_eligible": False,
    }
    print(json.dumps(printable, ensure_ascii=False, indent=2))
    # Exit code 3 is the machine-readable "recorded failure, stop here" signal:
    # a development sweep that finds no qualified model must not open the
    # pre-registered blind holdout.
    if selection_result is not None and selection_result["candidate"] is None:
        print(f"\nno qualified model on {selection.split}: "
              f"{selection_result['stop_reason']}; do not run the final holdout",
              file=sys.stderr)
        sys.exit(3)


if __name__ == "__main__":
    main()
