"""Per-tick A/B of pre-fix vs post-fix traces for the revealed set 6001-6050.

Inputs (never committed, both live under %TEMP%):

* pre-fix  traces: ``score-block-rl-diagnose-20260926/traces/final_holdout_v2-policy-<seed>.jsonl.gz``
  (collected during the diagnosis task, before the attribution fix)
* post-fix traces: ``score-block-attribution-fix/diag-postfix/traces/postfix-policy-<seed>.jsonl.gz``

Each JSONL line is one ``rl-env`` call; the simulator snapshot lives under ``trace``.

For every seed the script reports the first step at which the two runs diverge, split into

* ``label``     - a block's ``last_contact_role`` (the corrected field itself)
* ``behaviour`` - robot pose/velocity, block pose/velocity, ``out``/``was_on``, or events
* ``outcome``   - the returned ``reward``/``terminated``/``truncated``

A seed with a label change but no behavioural change proves the fix is inert until an
actually-unattributed block exit flips a score. A seed with a behavioural change is a
genuine feedback divergence, and its first tick must not precede the label change.
"""
import gzip
import json
import os
import pathlib
import sys

TEMP = pathlib.Path(os.environ.get("TEMP", "."))
PRE_DIR = TEMP / "score-block-rl-diagnose-20260926" / "traces"
POST_ROOT = (pathlib.Path(sys.argv[1]) if len(sys.argv) > 1
             else TEMP / "score-block-attribution-fix" / "diag-postfix")
POST_DIR = POST_ROOT / "traces"
BLOCK_STATE_KEYS = ("x", "y", "vx", "vy", "out", "was_on", "edge_distance")
ROBOT_KEYS = ("x", "y", "th", "v", "w", "vx", "vy")


def load(path):
    rows = []
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def behaviour(row):
    t = row["trace"]
    return (
        tuple(round(t["us"][k], 9) for k in ROBOT_KEYS),
        tuple(round(t["them"][k], 9) for k in ROBOT_KEYS),
        tuple((b["name"], b["kind"], *[round(b[k], 9) for k in BLOCK_STATE_KEYS])
              for b in t["blocks"]),
        tuple((e["kind"], e.get("robot"), e.get("neutral"), e.get("tick"),
               e.get("block"), e.get("reason"), e.get("msg")) for e in t["events"]),
    )


def labels(row):
    return tuple(b["last_contact_role"] for b in row["trace"]["blocks"])


def outcome(row):
    return (row.get("kind"), round(row.get("reward", 0.0), 9),
            row.get("terminated"), row.get("truncated"))


def first_diff(pre, post, project, skip=0):
    """First index whose projection differs; rows without a trace (the final
    no_score_block response) are compared by response kind instead."""
    for i in range(skip, min(len(pre), len(post))):
        ta, tb = pre[i].get("trace"), post[i].get("trace")
        if ta is None or tb is None:
            if (ta is None) != (tb is None) or pre[i].get("kind") != post[i].get("kind"):
                return i, None
            continue
        if project(pre[i]) != project(post[i]):
            return i, ta["tick"]
    if skip == 0 and len(pre) != len(post):
        return min(len(pre), len(post)), None
    return None, None


def describe(project, a, b):
    pa, pb = project(a), project(b)
    if isinstance(pa, tuple) and len(pa) == 4 and isinstance(pa[2], tuple):
        out = []
        for name, xa, xb in zip(("us", "them", "blocks", "events"), pa, pb):
            if xa != xb:
                out.append(f"{name}: {xa if name != 'blocks' else ''}"
                           f"{'' if name != 'blocks' else '...'}"
                           f" -> {xb if name != 'blocks' else ''}")
        return "; ".join(out)[:220]
    return f"{pa} -> {pb}"


def main():
    seeds = sorted(int(p.name.rsplit("-", 1)[1].split(".")[0])
                   for p in PRE_DIR.glob("final_holdout_v2-policy-*.jsonl.gz"))
    print(f"seeds with a pre-fix trace: {len(seeds)}")
    print(f"{'seed':>5} {'steps p/f':>10} {'label@':>7} {'behav@':>7} {'outcome@':>9}  note")
    behavioural, label_only, identical = [], [], []
    for seed in seeds:
        post_path = POST_DIR / f"postfix-policy-{seed}.jsonl.gz"
        if not post_path.exists():
            print(f"{seed:>5}  missing post-fix trace")
            continue
        pre, post = load(PRE_DIR / f"final_holdout_v2-policy-{seed}.jsonl.gz"), load(post_path)
        li, ltick = first_diff(pre, post, labels)
        bi, btick = first_diff(pre, post, behaviour)
        oi, otick = first_diff(pre, post, outcome)
        note = ""
        if li is not None:
            if pre[li].get("trace") and post[li].get("trace"):
                detail = [(i, x, y)
                          for i, (x, y) in enumerate(zip(labels(pre[li]), labels(post[li])))
                          if x != y]
                note = "label " + ", ".join(f"blk{i}:{x}->{y}" for i, x, y in detail)
            else:
                note = "length/kind divergence"
        if bi is not None:
            note += f" | behav {describe(behaviour, pre[bi], post[bi])}"
            behavioural.append((seed, ltick, btick, note))
        elif li is not None:
            label_only.append(seed)
        else:
            identical.append(seed)
        print(f"{seed:>5} {f'{len(pre)}/{len(post)}':>10} {str(ltick):>7} {str(btick):>7} "
              f"{str(otick):>9}  {note[:150]}")
    print(f"\nbit-identical (no label change at all): {len(identical)} -> {identical}")
    print(f"label-only change (behaviour identical): {len(label_only)} -> {label_only}")
    print(f"behavioural divergence: {len(behavioural)}")
    for seed, ltick, btick, note in behavioural:
        print(f"  seed {seed}: label@{ltick} behaviour@{btick} {note[:200]}")


main()
