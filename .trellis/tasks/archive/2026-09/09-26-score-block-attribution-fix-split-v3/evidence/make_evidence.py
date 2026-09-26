"""Generate the machine-readable evidence bundle for the attribution-fix task.

Reads the archived pre-fix evaluation JSONs and the post-fix re-runs (both under
``%TEMP%``) and writes ``fix-delta.json`` next to this script. Nothing here writes
into the repository except that one evidence file.
"""
import hashlib
import json
import os
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[3]
TEMP = pathlib.Path(os.environ["TEMP"])
ARCHIVE = REPO / ".trellis/tasks/archive/2026-09"
OUT = TEMP / "score-block-attribution-fix"

METRICS = (
    "episodes", "no_score_block_episodes", "episodes_with_locked_target_us_block_score",
    "total_locked_target_us_block_scores", "total_us_block_score_events",
    "total_them_block_score_events", "total_target_block_offs", "total_unowned_block_offs",
    "total_us_drops", "final_score_us_sum", "final_score_them_sum",
    "ambiguous_attribution_episodes",
)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def cell(old, new):
    return {"pre_fix": old, "post_fix": new,
            "changed": [k for k in METRICS if old.get(k) != new.get(k)]}


def main():
    r2_old = json.loads((ARCHIVE / "09-26-score-block-ppo-checkpoint-round/evidence/final-holdout-v2.json")
                        .read_text(encoding="utf-8"))
    r2_new = json.loads((OUT / "final-holdout-v2-after-fix.json").read_text(encoding="utf-8"))
    r1_old = json.loads((ARCHIVE / "09-25-mujoco-score-rl-pilot/evidence/final-holdout.json")
                        .read_text(encoding="utf-8"))
    r1_new = json.loads((OUT / "final-holdout-after-fix.json").read_text(encoding="utf-8"))

    payload = {
        "task": "09-26-score-block-attribution-fix-split-v3",
        "fix": {
            "file": "src/Sim.Core/Physics.cs",
            "function": "PhysicsWorld.FinalizeBlockContacts",
            "before": 'o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";',
            "after": "distinct roles at max contact time; one role -> that role, several -> simultaneous",
            "sim_core_sha256": sha256(REPO / "src/Sim.Core/Physics.cs"),
        },
        "artifacts": {
            "post_fix_cli_dll_sha256": sha256(REPO / "src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll"),
            "pre_fix_cli_dll_sha256": sha256(TEMP / "score-block-prefix-cli/Sim.Cli.dll"),
            "scenario_sha256": sha256(REPO / "scenarios/wushu-ring-2026-mujoco.json"),
            "round2_model_sha256": sha256(
                TEMP / "score-block-rl-v2-500k-20260926/checkpoints/rl_model_307200_steps.zip"),
            "round1_model_sha256": sha256(
                TEMP / "robot-simulator-rl-11d-50k-clean-20260926/ppo_score_block.zip"),
        },
        "projection_from_diagnosis": {
            "final_holdout_v2_policy_locked_target": [5, 9],
            "final_holdout_v2_fsm_locked_target": [8, 8],
            "legacy_final_holdout_policy_locked_target": [1, 1],
            "legacy_final_holdout_fsm_locked_target": [0, 1],
            "unowned_block_offs_all_cells": [">0", 0],
        },
        "measured": {
            "final_holdout_v2_policy": cell(r2_old["models"][0]["policy_summary"],
                                            r2_new["models"][0]["policy_summary"]),
            "final_holdout_v2_fsm": cell(r2_old["fsm_baseline"]["summary"],
                                         r2_new["fsm_baseline"]["summary"]),
            "legacy_final_holdout_policy": cell(r1_old["summary"]["policy"],
                                                r1_new["models"][0]["policy_summary"]),
            "legacy_final_holdout_fsm": cell(r1_old["summary"]["fsm_baseline"],
                                             r1_new["fsm_baseline"]["summary"]),
        },
        "analysis_only_flags": {
            key: r2_new.get(key) for key in
            ("evaluation_split", "split_version", "analysis_only", "gate_evidence_eligible",
             "is_blind_holdout", "is_revealed_holdout", "new_round_blind_gate_passed")},
        "verdicts": {
            "not_gate_evidence": "the revealed-set re-runs are analysis-only and cannot be quoted "
                                  "as gate evidence; the drop condition (our Drop <= FSM) still fails",
        },
    }
    measured = payload["measured"]
    payload["verdicts"]["unowned_block_offs_zero_in_all_four_cells"] = all(
        m["post_fix"]["total_unowned_block_offs"] == 0 for m in measured.values())
    payload["verdicts"]["us_drops_unchanged_in_all_four_cells"] = all(
        m["pre_fix"]["total_us_drops"] == m["post_fix"]["total_us_drops"]
        for m in measured.values())
    payload["verdicts"]["locked_target_projection_matches"] = {
        "final_holdout_v2_policy_expected_9": measured["final_holdout_v2_policy"]["post_fix"][
            "total_locked_target_us_block_scores"] == 9,
        "final_holdout_v2_fsm_expected_8": measured["final_holdout_v2_fsm"]["post_fix"][
            "total_locked_target_us_block_scores"] == 8,
        "legacy_final_holdout_policy_expected_1": measured["legacy_final_holdout_policy"]["post_fix"][
            "total_locked_target_us_block_scores"] == 1,
        "legacy_final_holdout_fsm_expected_1": measured["legacy_final_holdout_fsm"]["post_fix"][
            "total_locked_target_us_block_scores"] == 1,
    }
    (HERE / "fix-delta.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload["verdicts"], ensure_ascii=False, indent=2))
    print("wrote", HERE / "fix-delta.json")


main()
