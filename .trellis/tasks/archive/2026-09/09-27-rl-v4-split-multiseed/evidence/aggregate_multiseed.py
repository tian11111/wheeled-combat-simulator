#!/usr/bin/env python
"""Aggregate the five per-seed development_v4 sweeps into the multiseed baseline summary.

Inputs : .sim_runs/score-block-v4-split-multiseed-20260928/dev-sweep-seed-<seed>.json
         (produced by controllers/score_block_rl/evaluate.py --split development_v4
          --select-candidate, one invocation per training seed, no --freeze)
Output : summary JSON next to this script (multiseed-summary.json)

Ranking follows the task design.md exactly:
  per training seed : qualifying models (gate_passed && traceability_ok) sorted by
                      (locked-target BlockScore - FSM) desc,
                      (FSM Drop - our Drop) desc,
                      checkpoint step asc
  across seeds      : same order, ties broken by training RNG seed asc
Pass gate: a training seed passes iff it has at least one qualifying model.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
OUT_DIR = REPO / ".sim_runs/score-block-v4-split-multiseed-20260928"
SEEDS = [20260927, 20260928, 20260929, 20260930, 20261001]
PASS_THRESHOLD = 4  # at least 4/5 training seeds must pass


def per_seed_candidate(result: dict) -> dict | None:
    """design.md ordering; FSM totals are constant within one invocation."""
    fsm = result["fsm_baseline"]["summary"]
    fsm_score = fsm["total_locked_target_us_block_scores"]
    fsm_drops = fsm["total_us_drops"]
    qualified = [
        m for m in result["models"]
        if m["gate"]["gate_passed"] and m["gate"]["traceability_ok"]
    ]
    if not qualified:
        return None
    qualified.sort(key=lambda m: (
        -(m["policy_summary"]["total_locked_target_us_block_scores"] - fsm_score),
        -(fsm_drops - m["policy_summary"]["total_us_drops"]),
        m["training_steps"],
    ))
    return qualified[0]


def main() -> int:
    rows = []
    for seed in SEEDS:
        path = OUT_DIR / f"dev-sweep-seed-{seed}.json"
        if not path.exists():
            print(f"missing result: {path}", file=sys.stderr)
            return 2
        result = json.loads(path.read_text(encoding="utf-8"))
        fsm = result["fsm_baseline"]["summary"]
        cand = per_seed_candidate(result)
        sel = result.get("selection") or {}
        tool_cand = (sel.get("candidate") or {}).get("filename") if sel else None
        rows.append({
            "train_seed": seed,
            "fsm_locked_target_scores": fsm["total_locked_target_us_block_scores"],
            "fsm_us_drops": fsm["total_us_drops"],
            "passed": cand is not None,
            "candidate": None if cand is None else {
                "filename": cand["filename"],
                "sha256": cand["sha256"],
                "training_steps": cand["training_steps"],
                "locked_target_scores": cand["policy_summary"]["total_locked_target_us_block_scores"],
                "us_drops": cand["policy_summary"]["total_us_drops"],
                "controller_faults": cand["policy_summary"]["total_controller_faults"],
                "ambiguous_attribution_episodes": cand["policy_summary"]["ambiguous_attribution_episodes"],
            },
            "tool_candidate_filename": tool_cand,
            "qualified_model_count": sum(
                1 for m in result["models"]
                if m["gate"]["gate_passed"] and m["gate"]["traceability_ok"]),
            "evaluated_models": len(result["models"]),
            "result_path": str(path),
        })

    rows.sort(key=lambda r: (
        -(r["candidate"]["locked_target_scores"] - r["fsm_locked_target_scores"]) if r["candidate"] else 0,
        0 if not r["candidate"] else -(r["fsm_us_drops"] - r["candidate"]["us_drops"]),
        r["train_seed"],
    ))
    passed = sum(1 for r in rows if r["passed"])
    summary = {
        "protocol": "score-block-multiseed-summary-v1",
        "suite": ".sim_runs/score-block-v4-independent-throughput-20260927/suite-01",
        "development_split": "development_v4 (9001-9020)",
        "pass_threshold": f"{PASS_THRESHOLD}/5",
        "passed_seeds": passed,
        "direct_freeze_go": passed >= PASS_THRESHOLD,
        "rows": rows,
    }
    out = Path(__file__).resolve().parent / "multiseed-summary.json"
    out.write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(json.dumps({k: summary[k] for k in
                      ("passed_seeds", "direct_freeze_go")}, ensure_ascii=False))
    for r in rows:
        cand = r["candidate"]
        cand_txt = (f"ckpt={cand['filename']} steps={cand['training_steps']} "
                    f"score={cand['locked_target_scores']} drop={cand['us_drops']} "
                    f"fault={cand['controller_faults']}") if cand else "no qualified model"
        agree = "" if (cand is None) == (r["tool_candidate_filename"] is None) else "  [tool disagrees]"
        print(f"seed {r['train_seed']} pass={r['passed']} fsm(score={r['fsm_locked_target_scores']},"
              f"drop={r['fsm_us_drops']}) {cand_txt}{agree}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
