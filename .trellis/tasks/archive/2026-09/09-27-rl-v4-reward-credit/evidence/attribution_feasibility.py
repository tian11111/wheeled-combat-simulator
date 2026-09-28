#!/usr/bin/env python
"""Feasibility study: can per-tick target-block edge progress be attributed
to {us, them, both, none} from the rl-env diagnostic trace?

For every tick t of every episode (target block only):
  progress_t = edge_distance(t-1) - edge_distance(t)   (>0 = toward edge)
  class_t    = set of contact roles recorded for the target block at tick t:
               "us" / "them" / "both" / "none"

Reports per-mode totals: tick counts, positive/negative progress sums per class,
both-role ambiguity rate, and how much none-class positive progress follows a
us-contact within K ticks (window analysis, informational only).
"""

from __future__ import annotations

import gzip
import json
import sys
from collections import defaultdict
from pathlib import Path

TRACE_DIR = Path(__file__).resolve().parents[4] / ".sim_runs/score-block-v4-attribution-feasibility-20260928/traces"
EPS = 1e-9
CLASSES = ("us", "them", "both", "none")


def classify(roles: set[str]) -> str:
    if roles == {"us"}:
        return "us"
    if roles == {"them"}:
        return "them"
    if roles == {"us", "them"}:
        return "both"
    if not roles:
        return "none"
    return "both"  # defensive: unexpected role set counts as contested


def main() -> int:
    stats = {mode: {c: {"ticks": 0, "pos": 0.0, "neg": 0.0} for c in CLASSES}
             for mode in ("policy", "fsm")}
    none_after_us = {mode: {k: 0.0 for k in ("within1", "within3", "within5", "total")}
                     for mode in ("policy", "fsm")}
    episodes = 0
    for path in sorted(TRACE_DIR.glob("*.jsonl.gz")):
        mode = "policy" if "-policy-" in path.name else "fsm"
        episodes += 1
        prev = None          # (edge_distance,) of previous tick
        last_us_contact = -10**9
        with gzip.open(path, "rt", encoding="utf-8") as fh:
            for line in fh:
                row = json.loads(line)
                trace = row.get("info", {}).get("trace") or row.get("trace")
                if trace is None:
                    continue
                idx = trace["target_index"]
                if idx is None or idx < 0:
                    prev = None
                    continue
                block = trace["blocks"][idx]
                edge = block["edge_distance"]
                if prev is not None:
                    progress = prev - edge
                    roles = {c["r"] for c in (block.get("contacts") or [])}
                    cls = classify(roles)
                    bucket = stats[mode][cls]
                    bucket["ticks"] += 1
                    if progress > EPS:
                        bucket["pos"] += progress
                        if cls == "none":
                            gap = trace["tick"] - last_us_contact
                            none_after_us[mode]["total"] += progress
                            if gap <= 1:
                                none_after_us[mode]["within1"] += progress
                            if gap <= 3:
                                none_after_us[mode]["within3"] += progress
                            if gap <= 5:
                                none_after_us[mode]["within5"] += progress
                    elif progress < -EPS:
                        bucket["neg"] += progress
                if any(c["r"] == "us" for c in (block.get("contacts") or [])):
                    last_us_contact = trace["tick"]
                prev = edge

    print(f"episodes: {episodes}")
    for mode in ("policy", "fsm"):
        total_pos = sum(stats[mode][c]["pos"] for c in CLASSES)
        total_neg = sum(stats[mode][c]["neg"] for c in CLASSES)
        print(f"\n[{mode}] total positive progress {total_pos:.4f}, negative {total_neg:.4f}")
        for c in CLASSES:
            s = stats[mode][c]
            share = s["pos"] / total_pos * 100 if total_pos else 0.0
            print(f"  {c:5s} ticks={s['ticks']:>5}  pos={s['pos']:9.4f} ({share:5.1f}%)  neg={s['neg']:9.4f}")
        w = none_after_us[mode]
        if w["total"]:
            print(f"  none-class positive progress following a us-contact: "
                  f"<=1 tick {w['within1']/w['total']*100:.1f}%, <=3 {w['within3']/w['total']*100:.1f}%, "
                  f"<=5 {w['within5']/w['total']*100:.1f}% (of {w['total']:.4f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
