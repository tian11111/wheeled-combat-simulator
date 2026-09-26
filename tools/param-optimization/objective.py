"""Objective aggregation for FSM parameter optimization.

J(theta) = sum over training seeds of [1.0 * our BlockScore - 1.0 * our Drop]
           - 10 * (matches ended by recovery-limit)
Crashes / non-finite states fail the trial (caller raises -> Optuna marks it failed).
"""

from __future__ import annotations

RECOVER_LIMIT_MARKER = "恢复次数超限"


def aggregate(metrics: list[dict]) -> dict:
    return {
        "block_scores": sum(m["us_block_scores"] for m in metrics),
        "drops": sum(m["us_drops"] for m in metrics),
        "recover_limit_ends": sum(1 for m in metrics if RECOVER_LIMIT_MARKER in m.get("done_reason", "")),
        "mount_t_values": [m["mount_t"] for m in metrics if m["mount_t"] is not None],
        "final_scores": [m["final_score"] for m in metrics if m["final_score"] is not None],
    }


def objective_value(agg: dict) -> float:
    value = 1.0 * agg["block_scores"] - 1.0 * agg["drops"]
    value -= 10.0 * agg["recover_limit_ends"]
    return value
