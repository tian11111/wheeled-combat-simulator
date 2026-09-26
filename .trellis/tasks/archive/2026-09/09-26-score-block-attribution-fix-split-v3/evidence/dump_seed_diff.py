"""Dump the rows around the first behavioural divergence for one seed (ad-hoc)."""
import gzip
import json
import os
import pathlib
import sys

TEMP = pathlib.Path(os.environ["TEMP"])
SEED = int(sys.argv[1]) if len(sys.argv) > 1 else 6047
PRE = TEMP / "score-block-rl-diagnose-20260926" / "traces" / f"final_holdout_v2-policy-{SEED}.jsonl.gz"
POST = TEMP / "score-block-attribution-fix" / "diag-postfix" / "traces" / f"postfix-policy-{SEED}.jsonl.gz"


def load(path):
    rows = []
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        for line in fh:
            if line.strip():
                rows.append(json.loads(line))
    return rows


def beh(row):
    t = row["trace"]
    return (
        tuple(round(t["us"][k], 9) for k in ("x", "y", "th", "v", "w", "vx", "vy")),
        tuple(round(t["them"][k], 9) for k in ("x", "y", "th", "v", "w", "vx", "vy")),
        tuple((b["name"], b["kind"], round(b["x"], 9), round(b["y"], 9), b["out"], b["was_on"])
              for b in t["blocks"]),
        tuple((e["kind"], e.get("robot"), e.get("block"), e.get("reason"), e.get("tick"))
              for e in t["events"]),
    )


pre, post = load(PRE), load(POST)
i = next(i for i in range(min(len(pre), len(post))) if beh(pre[i]) != beh(post[i]))
print(f"seed {SEED}: first behavioural divergence at row {i} "
      f"tick {pre[i]['trace']['tick']} / {post[i]['trace']['tick']}")
for j in range(max(0, i - 3), min(len(pre), len(post), i + 3)):
    for tag, rows in (("pre ", pre), ("post", post)):
        t = rows[j]["trace"]
        roles = [b["last_contact_role"] for b in t["blocks"]]
        events = [(e["kind"], e.get("reason"), e.get("block")) for e in t["events"]]
        print(f"  {tag} row{j:>4} tick={t['tick']:>5} them=({t['them']['v']:>6},{t['them']['w']:>6}) "
              f"us=({t['us']['v']:>6},{t['us']['w']:>6}) roles={roles} events={events}")
    print("  " + "-" * 100)
