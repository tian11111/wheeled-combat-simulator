#!/usr/bin/env python
"""Calibrate the edge-risk weight w over the existing seed-behavior diagnosis traces.

Replays risk_t = -w * max(0, v_out_t - v0) * 1[dist_t < d0] over the 60 policy
episodes (3 training-seed models x 20 development_v4 episodes) collected for
09-28-rl-v4-seed-behavior-diagnosis, with the pre-registered exemption
(risk = 0 while our robot contacts the target block in the same tick).

Pre-registered selection criteria (implement.md step 1):
  L = mean per-tick penalty inside the 25 ticks preceding each failing-model drop
  W = winner model's overall mean per-tick penalty
  M = max per-episode |total risk| across all episodes
  choose w from {0.005, 0.01, 0.02} with L >= 3*W and M <= 1.0; no solution -> No-Go.
"""

from __future__ import annotations

import gzip
import json
import statistics
from pathlib import Path

TRACES = Path(__file__).resolve().parents[4] / ".sim_runs/score-block-v4-seed-behavior-diagnosis-20260928"
D0 = 0.27
V0 = 0.2
TICK_SECONDS = 0.05
WINDOW = 25
WS = (0.005, 0.01, 0.02)
FAILING = ("20260927", "20260928")
WINNER = "20260929"


def episode_rows(path: Path):
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        for line in fh:
            row = json.loads(line)
            trace = row.get("info", {}).get("trace") or row.get("trace")
            if trace is not None:
                yield trace


def replay(path: Path, w: float):
    """Return (total_risk, per_tick_risk list, us_drop_ticks set)."""
    prev_dist = None
    prev_tick = None
    per_tick = []
    total = 0.0
    drop_ticks = set()
    for tr in episode_rows(path):
        tick = tr["tick"]
        dist = tr["us"]["edge_distance"]
        idx = tr["target_index"]
        contacts = set()
        if idx is not None and 0 <= idx < len(tr["blocks"]):
            contacts = {c["r"] for c in (tr["blocks"][idx].get("contacts") or [])}
        for evt in tr["events"]:
            if evt.get("kind") == "Drop" and evt.get("is_us"):
                drop_ticks.add(tick)
        risk = 0.0
        if (prev_dist is not None and prev_tick is not None and tick == prev_tick + 1
                and dist is not None and dist < D0 and "us" not in contacts):
            v_out = max(0.0, (prev_dist - dist) / TICK_SECONDS)
            risk = -w * max(0.0, v_out - V0)
        per_tick.append((tick, risk))
        total += risk
        prev_dist, prev_tick = dist, tick
    return total, per_tick, drop_ticks


def main() -> int:
    files = sorted(TRACES.glob("seed-*/traces/*policy*.jsonl.gz"))
    by_seed = {s: [f for f in files if f"seed-{s}" in str(f)] for s in (*FAILING, WINNER)}
    print(f"policy episodes: failing={len(by_seed['20260927'])+len(by_seed['20260928'])} "
          f"winner={len(by_seed[WINNER])}")

    results = {}
    for w in WS:
        drop_window_rates = []      # per-tick penalty in the 25 ticks before each failing drop
        winner_rate_samples = []    # winner per-tick penalties (all ticks)
        max_episode_total = 0.0
        for f in by_seed["20260927"] + by_seed["20260928"]:
            total, per_tick, drop_ticks = replay(f, w)
            max_episode_total = max(max_episode_total, abs(total))
            tmap = dict(per_tick)
            for dt in drop_ticks:
                window = [tmap[t] for t in range(dt - WINDOW, dt) if t in tmap]
                if window:
                    drop_window_rates.append(sum(window) / len(window))
        for f in by_seed[WINNER]:
            total, per_tick, _ = replay(f, w)
            max_episode_total = max(max_episode_total, abs(total))
            winner_rate_samples.extend(r for _, r in per_tick)
        L = statistics.mean(drop_window_rates) if drop_window_rates else 0.0
        W = statistics.mean(winner_rate_samples) if winner_rate_samples else 0.0
        ok = (W < 0 and L <= 3 * W) and max_episode_total <= 1.0
        results[w] = (L, W, max_episode_total, ok)
        print(f"w={w:<6} L(掉台前窗口每tick罚值)={L:.6f}  W(胜出全程每tick)={W:.6f}  "
              f"L>=3W:{L <= 3 * W if W < 0 else False}  max|单场累计|={max_episode_total:.4f}  "
              f"判据{'✅' if ok else '❌'}")

    chosen = [w for w, (_, _, _, ok) in results.items() if ok]
    print("\nchosen w:", chosen if chosen else "NONE -> No-Go (不训练)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
