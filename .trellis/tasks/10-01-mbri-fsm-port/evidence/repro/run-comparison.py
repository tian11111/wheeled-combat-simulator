"""MBri vs builtin 行为对照 runner (临时脚本, tmp/ 不入库).

用法: python tmp/mbri-comparison/run-comparison.py
产物: tmp/mbri-comparison/logs/*.txt, tmp/mbri-comparison/summary.json
"""
import json
import os
import re
import statistics
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "tmp", "mbri-comparison")
LOGS = os.path.join(OUT, "logs")
DOTNET = r"C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
CLI = os.path.join(ROOT, "src", "Sim.Cli", "bin", "Debug", "net8.0", "Sim.Cli.dll")
SEEDS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 42]

CONFIGS = {
    "mirror-builtin": {"us": None, "them": None},
    "mirror-mbri": {"us": "mbri", "them": "mbri"},
    "head-us-mbri": {"us": "mbri", "them": None},
    "head-them-mbri": {"us": None, "them": "mbri"},
}


def make_scenarios():
    base = json.load(open(os.path.join(ROOT, "scenarios", "wushu-ring-2026.json"), encoding="utf-8"))
    for name, cfg in CONFIGS.items():
        s = json.loads(json.dumps(base))
        # 双方都要有条目 (Scenario.Validate 要求 us/them 都存在); 无控制器的一端
        # 写空对象 = 默认 builtin 车辆档 (等价于省略 controller 字段)。
        vehicles = {}
        for role in ("us", "them"):
            vehicles[role] = {"controller": cfg[role]} if cfg[role] is not None else {}
        s["vehicles"] = vehicles
        with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8") as f:
            json.dump(s, f, ensure_ascii=False, separators=(",", ":"))


SEED_RE = re.compile(
    r"^seed=(?P<seed>\d+) ticks=(?P<ticks>\d+) score 我方 (?P<us>[-0-9.]+) : (?P<them>[-0-9.]+) 对手"
    r" done=(?P<done>.+?) faults\(us/them\)=(?P<uf>\d+)/(?P<tf>\d+) penalties=(?P<up>[-0-9.]+)/(?P<tp>[-0-9.]+)$"
)
STATS_RE = re.compile(
    r"^stats seed=(?P<seed>\d+) us_falls=(?P<uf>\d+) them_falls=(?P<tf>\d+) "
    r"us_mounts=(?P<um>\d+) them_mounts=(?P<tm>\d+)$"
)
EVENT_RE = re.compile(r"^\[\s*\d+\] t=\s*[-0-9.]+ (?P<type>\S+)\s+(?P<msg>.*)$")


def run_match(config, seed):
    log = os.path.join(LOGS, f"{config}-seed{seed}.txt")
    scenario = os.path.join(OUT, config + ".json")
    cmd = [DOTNET, CLI, "match", "--seed", str(seed), "--scenario", scenario, "--events", "--stats"]
    with open(log, "w", encoding="utf-8", newline="\n") as f:
        proc = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, cwd=ROOT)
    if proc.returncode != 0:
        raise RuntimeError(f"{config} seed={seed} exit={proc.returncode} (see {log})")
    return log


def read_text(path):
    """子进程控制台中文按父控制台代码页写出 (本机 GBK): 先 UTF-8 后 GB18030 兜底。"""
    data = open(path, "rb").read()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("gb18030", errors="replace")


def parse_log(path):
    seed = None
    stats = None
    referee_drops = {"us": 0, "them": 0}
    fsm_drops = {"us": 0, "them": 0}
    mounts = {"us": 0, "them": 0}
    simultaneous = 0
    lines = read_text(path).splitlines()
    for line in lines:
        m = SEED_RE.match(line)
        if m:
            seed = int(m.group("seed"))
            continue
        m = STATS_RE.match(line)
        if m:
            stats = {k: int(m.group(k)) for k in ("seed", "uf", "tf", "um", "tm")}
            continue
        m = EVENT_RE.match(line)
        if not m:
            continue
        etype, msg = m.group("type"), m.group("msg")
        role = "us" if msg.startswith("[我方]") else ("them" if msg.startswith("[对手]") else None)
        if etype == "Drop" and role:
            if "[score]" in msg:
                referee_drops[role] += 1
            elif "[fsm] 掉台" in msg or "[mbri" in msg:
                fsm_drops[role] += 1
        elif etype == "SimultaneousDrop":
            simultaneous += 1
        elif etype == "Mount" and role and "[fsm]" in msg:
            mounts[role] += 1
    # 比分行解析 (末行)
    for raw in reversed(lines):
        m = SEED_RE.match(raw.strip())
        if m:
            return {
                "seed": int(m.group("seed")),
                "ticks": int(m.group("ticks")),
                "score_us": float(m.group("us")),
                "score_them": float(m.group("them")),
                "done": m.group("done"),
                "faults": [int(m.group("uf")), int(m.group("tf"))],
                "penalties": [float(m.group("up")), float(m.group("tp"))],
                "stats": stats,
                "referee_drops": referee_drops,
                "fsm_drops": fsm_drops,
                "mount_events": mounts,
                "simultaneous_drop_events": simultaneous,
            }
    raise RuntimeError(f"no result line in {path}")


def batch_scores(config):
    """batch 交叉核对: 同一场景副本 + 同一 seeds 的 JSONL 结果。"""
    scenario = os.path.join(OUT, config + ".json")
    out_path = os.path.join(OUT, f"batch-{config}.jsonl")
    cmd = [DOTNET, CLI, "batch", "--seeds", ",".join(str(s) for s in SEEDS),
           "--scenario", scenario, "--out", out_path]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", cwd=ROOT)
    if proc.returncode != 0:
        raise RuntimeError(f"batch {config} exit={proc.returncode}: {proc.stderr}")
    rows = [json.loads(l) for l in open(out_path, encoding="utf-8") if l.strip()]
    return {int(r["seed"]): r for r in rows}


def median(values):
    return statistics.median(values) if values else None


def main():
    os.makedirs(LOGS, exist_ok=True)
    make_scenarios()
    results = {}
    for config in CONFIGS:
        results[config] = {}
        for seed in SEEDS:
            log = run_match(config, seed)
            results[config][seed] = parse_log(log)
            print(f"  {config} seed={seed} -> {results[config][seed]['stats']}", flush=True)

    # batch 交叉核对 (比分/ticks/doneReason)
    batch = {config: batch_scores(config) for config in CONFIGS}
    cross = []
    for config in CONFIGS:
        for seed in SEEDS:
            m = results[config][seed]
            b = batch[config].get(seed, {})
            ok = (b.get("scores", {}).get("us") == m["score_us"]
                  and b.get("scores", {}).get("them") == m["score_them"]
                  and b.get("ticks") == m["ticks"]
                  and b.get("doneReason") == m["done"])
            cross.append({"config": config, "seed": seed, "match_vs_batch_ok": ok,
                          "match": [m["score_us"], m["score_them"], m["ticks"], m["done"]],
                          "batch": [b.get("scores"), b.get("ticks"), b.get("doneReason")]})
    if not all(c["match_vs_batch_ok"] for c in cross):
        print("WARNING: match/batch disagreement detected", flush=True)

    # 汇总
    summary = {"seeds": SEEDS, "runs": results, "batch_cross_check": cross}
    with open(os.path.join(OUT, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=1)

    # 控制台速览
    for config in CONFIGS:
        falls = [r["stats"]["uf" if config != "head-them-mbri" else "tf"] for r in results[config].values()]
        print(f"{config}: falls(selected side) median={median(falls)} values={falls}")


if __name__ == "__main__":
    sys.exit(main())
