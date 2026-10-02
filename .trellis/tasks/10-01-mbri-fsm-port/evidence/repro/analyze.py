"""读取 summary.json + 原始日志, 产出对照表 (markdown 片段) 到 stdout。"""
import json
import os
import re
import statistics

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "tmp", "mbri-comparison")
SEEDS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 42]
CONFIGS = ["mirror-builtin", "mirror-mbri", "head-us-mbri", "head-them-mbri"]
summary = json.load(open(os.path.join(OUT, "summary.json"), encoding="utf-8"))
runs = summary["runs"]


def read_text(path):
    data = open(path, "rb").read()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("gb18030", errors="replace")


def last_event_tick(config, seed):
    """最后一个非 End/Timeout 事件的 tick (展示时间已 ×10: Js.ToFixed(simT,1) 既有怪癖)。"""
    text = read_text(os.path.join(OUT, "logs", f"{config}-seed{seed}.txt"))
    last = 0
    for line in text.splitlines():
        m = re.match(r"^\[\s*\d+\] t=\s*([-0-9.]+)\s+(\S+)\s+", line)
        if m and m.group(2) not in ("End", "Timeout"):
            last = float(m.group(1))
    return last


def median(values):
    return statistics.median(values)


print("## 逐 seed 掉台/上台/比分 (match --stats)\n")
for config in CONFIGS:
    print(f"\n### {config}\n")
    print("| seed | us_falls | them_falls | us_mounts | them_mounts | 比分 us:them | done | ticks | 最后事件 t |")
    print("|---|---|---|---|---|---|---|---|---|")
    for seed in SEEDS:
        r = runs[config][str(seed)]
        s = r["stats"]
        print(f"| {seed} | {s['uf']} | {s['tf']} | {s['um']} | {s['tm']} | "
              f"{r['score_us']:g}:{r['score_them']:g} | {r['done']} | {r['ticks']} | {last_event_tick(config, seed):g} |")

print("\n## 汇总 (每场每角色 = 一条观测)\n")
print("| 组 | 角色/控制器 | n | 掉台中位数 | 掉台均值 | 掉台范围 | 上台中位数 | 上台均值 |")
print("|---|---|---|---|---|---|---|---|")
for config in CONFIGS:
    rows = [runs[config][str(seed)] for seed in SEEDS]
    # 角色 -> 控制器
    mapping = {
        "mirror-builtin": {"us": "builtin", "them": "builtin"},
        "mirror-mbri": {"us": "mbri", "them": "mbri"},
        "head-us-mbri": {"us": "mbri", "them": "builtin"},
        "head-them-mbri": {"us": "builtin", "them": "mbri"},
    }[config]
    for role in ("us", "them"):
        falls = [r["stats"]["uf" if role == "us" else "tf"] for r in rows]
        mounts = [r["stats"]["um" if role == "us" else "tm"] for r in rows]
        print(f"| {config} | {role}={mapping[role]} | {len(rows)} | {median(falls):g} | "
              f"{statistics.mean(falls):.2f} | {min(falls)}-{max(falls)} | {median(mounts):g} | {statistics.mean(mounts):.2f} |")

# 控制器维度合并 (mirror 两组各 22 条, 双向 head 各 22 条)
def collect(configs, controller):
    out_falls, out_mounts, out_scores = [], [], []
    for config in configs:
        mapping = {
            "mirror-builtin": {"us": "builtin", "them": "builtin"},
            "mirror-mbri": {"us": "mbri", "them": "mbri"},
            "head-us-mbri": {"us": "mbri", "them": "builtin"},
            "head-them-mbri": {"us": "builtin", "them": "mbri"},
        }[config]
        for seed in SEEDS:
            r = runs[config][str(seed)]
            for role in ("us", "them"):
                if mapping[role] != controller:
                    continue
                out_falls.append(r["stats"]["uf" if role == "us" else "tf"])
                out_mounts.append(r["stats"]["um" if role == "us" else "tm"])
                out_scores.append(r["score_us"] if role == "us" else r["score_them"])
    return out_falls, out_mounts, out_scores


print("\n## 控制器维度合并\n")
print("| 数据面 | 控制器 | n | 掉台中位数 | 掉台均值 | 掉台范围 | 上台中位数 | 得分中位数 | 零掉台场次 |")
print("|---|---|---|---|---|---|---|---|---|")
for label, configs in [
    ("mirror 双份 (双方同控制器)", ["mirror-builtin", "mirror-mbri"]),
    ("同场配对 (双向角色互换)", ["head-us-mbri", "head-them-mbri"]),
]:
    for controller in ("builtin", "mbri"):
        falls, mounts, scores = collect(configs, controller)
        print(f"| {label} | {controller} | {len(falls)} | {median(falls):g} | {statistics.mean(falls):.2f} | "
              f"{min(falls)}-{max(falls)} | {median(mounts):g} | {median(scores):g} | {sum(1 for f in falls if f == 0)} |")

# stats 掉台 vs 裁判 Drop 事件交叉核对
print("\n## 掉台交叉核对 (stats 迁移 vs 裁判 Drop 事件)\n")
mismatch = 0
for config in CONFIGS:
    for seed in SEEDS:
        r = runs[config][str(seed)]
        if (r["stats"]["uf"], r["stats"]["tf"]) != (r["referee_drops"]["us"], r["referee_drops"]["them"]):
            mismatch += 1
            print(f"MISMATCH {config} seed={seed}: stats={r['stats']} referee={r['referee_drops']} fsm={r['fsm_drops']}")
print(f"| 配置×seed | 一致 | 不一致 |")
print(f"|---|---|---|")
print(f"| 44 | {44 - mismatch} | {mismatch} |")

# match vs batch 比分交叉
bad = [c for c in summary["batch_cross_check"] if not c["match_vs_batch_ok"]]
print(f"\nmatch vs batch: {len(summary['batch_cross_check'])} 行, 不一致 {len(bad)}")
for c in bad:
    print("  ", c)
