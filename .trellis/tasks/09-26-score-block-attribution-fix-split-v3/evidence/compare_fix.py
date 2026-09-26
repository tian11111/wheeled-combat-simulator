"""Compare archived (pre-fix) and post-fix evaluation results per seed.

Read-only analysis used to write the attribution-fix report. It prints, for every
seed, the fields that changed, and summarises which seeds diverged.
"""
import json
import pathlib
import sys

ARCHIVE = pathlib.Path(".trellis/tasks/archive/2026-09")
R2_OLD = ARCHIVE / "09-26-score-block-ppo-checkpoint-round/evidence/final-holdout-v2.json"
OUT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")

SUMMARY_KEYS = (
    "episodes", "no_score_block_episodes", "episodes_with_locked_target_us_block_score",
    "total_locked_target_us_block_scores", "total_us_block_score_events",
    "total_them_block_score_events", "total_target_block_offs", "total_unowned_block_offs",
    "total_us_drops", "final_score_us_sum", "final_score_them_sum",
    "ambiguous_attribution_episodes",
)
ROW_KEYS = (
    "locked_target_us_block_scores", "us_block_score_events", "them_block_score_events",
    "target_block_offs", "unowned_block_offs", "us_drops", "final_score_us", "final_score_them",
    "policy_ticks", "total_reward", "target_last_contact_role", "target_out",
    "target_outcome_tick", "done_reason", "stage_entry_tick",
)


def rows_by_seed(payload, which):
    if which == "policy":
        source = payload["models"][0]["per_episode_policy"]
    else:
        source = payload["fsm_baseline"]["per_episode"]
    return {row["seed"]: row for row in source}


def summary(payload, which):
    if which == "policy":
        return payload["models"][0]["policy_summary"]
    return payload["fsm_baseline"]["summary"]


def compare(tag, old, new, which):
    print(f"\n===== {tag} / {which} =====")
    os_, ns_ = summary(old, which), summary(new, which)
    for key in SUMMARY_KEYS:
        a, b = os_.get(key), ns_.get(key)
        flag = "  " if a == b else "**"
        print(f" {flag} {key:46s} {a!r:>8} -> {b!r}")
    orows, nrows = rows_by_seed(old, which), rows_by_seed(new, which)
    assert set(orows) == set(nrows), "seed sets differ"
    changed = []
    for seed in sorted(orows):
        diff = {k: (orows[seed].get(k), nrows[seed].get(k))
                for k in ROW_KEYS if orows[seed].get(k) != nrows[seed].get(k)}
        if diff:
            changed.append((seed, diff))
    print(f" per-seed rows changed: {len(changed)}/{len(orows)}")
    for seed, diff in changed:
        # Only report the scoring/attribution-relevant movement plus the first divergence.
        keys = {k: v for k, v in diff.items()}
        print(f"  seed {seed}: {keys}")


def main():
    old2 = json.loads(R2_OLD.read_text(encoding="utf-8"))
    new2 = json.loads((OUT / "final-holdout-v2-after-fix.json").read_text(encoding="utf-8"))
    compare("final_holdout_v2 6001-6050", old2, new2, "policy")
    compare("final_holdout_v2 6001-6050", old2, new2, "fsm")

    old1 = json.loads((ARCHIVE / "09-25-mujoco-score-rl-pilot/evidence/final-holdout.json")
                      .read_text(encoding="utf-8"))
    new1 = json.loads((OUT / "final-holdout-after-fix.json").read_text(encoding="utf-8"))
    print("\n===== legacy_final_holdout 4001-4010 (summary only: archived schema is older) =====")
    for which in ("policy", "fsm_baseline"):
        old_s = old1["summary"][which]
        new_s = (new1["models"][0]["policy_summary"] if which == "policy"
                 else new1["fsm_baseline"]["summary"])
        print(f" -- {which}")
        for key in SUMMARY_KEYS:
            a, b = old_s.get(key), new_s.get(key)
            flag = "  " if a == b else "**"
            print(f"  {flag} {key:46s} {a!r:>8} -> {b!r}")


main()
