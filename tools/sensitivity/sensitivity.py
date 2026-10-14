#!/usr/bin/env python3
"""Sim 物理参数 OAT（one-at-a-time）局部敏感度分析工具。

用途：量化 friction/collision/stall/mount 子系统的物理参数对比赛指标的影响
排序（真机采集优先级输入，服务 08-28 任务），不改变模拟器任何行为（纯 tools/）。

复用与实测口径（2026-10-14 实施期实测钉死，git HEAD 1250bb87）：
- 指标解析与场景生成复用 tools/param-optimization/oracle.py 的
  parse_match_output / write_scenario（纯函数直接 import）。
- oracle.run_match 不可直接复用：其按 gb18030 解码子进程输出，而实测
  Sim.Cli `match` 子进程管道输出为 UTF-8 字节（首行 "[我方]" 的字节为
  E6 88 91 E6 96 B9，即 UTF-8 编码；match 输出路径 RunMatch 无显式编码
  设置，Program.cs:42-43 的显式 Console.OutputEncoding=UTF8 仅作用于
  rl-env 命令）——实测 baseline s1 下 gb18030 解码使 [我方]/[对手]
  归属全失效（us_drops 210→0）。本工具自写 run_match_utf8
  （UTF-8 解码 + 复用 oracle.parse_match_output），偏差已写入执行记录。
- parameters 键 wire 名以 SimParameters.FromDictionary 为准：视觉识别
  成功率键是 classifyRate（camelCase；"CLASSIFY_RATE" 抛 Unknown simulation
  parameter——实测确认）。
- vehicles 字段 wire 名以 VehicleProfile + ProtocolJson camelCase 为准
  （latFrictionK / angDamping）；ProtocolJson 对未知字段静默忽略（不抛错），
  生效性由 selftest.py §2 的行为对比实测钉死（正确拼写改变行为、错误拼写
  与基线逐位一致）。

水平口径（偏差如实记录进报告配置头）：PRD R2 "±10%/±25%/±50% 共 4 水平"
中 ±10% 与 4 水平计数矛盾；按 design.md §3 COLLISION_RESTITUTION 例
（0.5×/0.75×/1.25×/1.5×）推广，水平集 = {0.75×, 1.25×, 0.5×, 1.5×}（即
−25%/+25%/−50%/+50%）；有界参数 clamp 到校验域，clamp 后与基线相同（Δp=0）
的水平跳过并记录。classifyRate 阳性对照按 method §2 用显式水平 80%/50%。

COLLISION_RESTITUTION 特例（PRD R1/design §3）：null 基线（速度相关公式）
与显式数值基线 0.75 分两段呈现、不混比（null→数值是模型路径切换）；
1.25×/1.5× clamp 到拟合域上限 0.9，两标签共享同值桶，如实呈现。

预算：实测单场（120s 模拟）wall ≈ 0.23 s（headless 2D 内核），全量 ~400 场
≈ 2 分钟量级，故按 PRD R1 "预算允许才做" 纳入第二梯队 4 参数
（BLOCK_STICK_SPEED / STALL_TIME / STALL_RELEASE / STALL_DISPLACEMENT）。

敏感度定义（method §5）：S(p,l,m) = |mean_m(l) − mean_m(baseline)| /
(|Δp/p| × (std_m(baseline) + ε))，以基线种子噪声的倍数为单位：
S<1 不敏感 · 1≤S<3 弱/存疑 · S≥3 敏感；基线 std=0 且 Δ≠0 时判相变级敏感；
相变（≥2/8 种子 done_reason / mount 状态翻转）单独标注。
"""

from __future__ import annotations

import argparse
import copy
import json
import math
import os
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "param-optimization"))
import oracle  # noqa: E402  复用 parse_match_output / write_scenario

ROOT = HERE.parent.parent
DOTNET_DEFAULT = str(Path.home() / "AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe")
CLI_DLL_DEFAULT = str(ROOT / "src/Sim.Cli/bin/Release/net8.0/Sim.Cli.dll")
BASE_SCENARIO_DEFAULT = str(ROOT / "scenarios/wushu-ring-2026.json")

SEEDS = [1, 2, 3, 4, 5, 6, 7, 8]
DURATION = 120.0
EPS = 1e-12
S_SENSITIVE = 3.0
S_WEAK = 1.0
PHASE_FLIP_MIN = 2  # ≥2/8 种子翻转 = 相变标注

LEVEL_MULTS = [("m25", 0.75), ("p25", 1.25), ("m50", 0.5), ("p50", 1.5)]
NUM_METRICS = ["us_block_scores", "us_drops", "score_margin", "mount_fail"]

CR_NAME = "COLLISION_RESTITUTION"
# (段名, 段基线, [(水平标签, clamp 后值)])；两段的 0.9 桶共享（同场景字节）。
CR_SEGMENTS = [
    ("null→数值（模型路径切换，效应按噪声倍数呈现，不参与跨参数 S 排序）", None,
     [("m25", 0.75), ("p25", 0.9), ("m50", 0.5), ("p50", 0.9)]),
    ("数值基线 0.75（固定恢复系数路径，S 与其他参数可比）", 0.75,
     [("m25", 0.5625), ("p25", 0.9), ("m50", 0.375), ("p50", 0.9)]),
]


@dataclass
class Spec:
    name: str
    channel: str  # "parameters" | "vehicles"
    subsystem: str
    baseline: float | None
    domain: tuple[float, float] | None = None
    levels_override: list | None = None
    tier: int = 1  # 1=核心/阳性对照, 2=第二梯队


PARAMS = [
    Spec("latFrictionK", "vehicles", "friction", 8.0, (0.5, 60.0)),
    Spec("BLOCK_MU_K", "parameters", "friction", 0.5),
    Spec("BLOCK_STICK_SPEED", "parameters", "friction", 0.02, tier=2),
    Spec("angDamping", "vehicles", "collision", 3.0, (0.0, 40.0)),
    Spec(CR_NAME, "parameters", "collision", None, (0.0, 0.9)),
    Spec("STALL_SPEED", "parameters", "stall", 0.03),
    Spec("STALL_TIME", "parameters", "stall", 0.4, tier=2),
    Spec("STALL_RELEASE", "parameters", "stall", 0.06, tier=2),
    Spec("STALL_DISPLACEMENT", "parameters", "stall", 0.006, tier=2),
    Spec("MOUNT_V_MIN", "parameters", "mount", 0.3, (0.0, 2.0)),
    Spec("MOUNT_ANGLE_MAX", "parameters", "mount", 0.26, (0.0, 1.2)),
    Spec("classifyRate", "parameters", "vision-阳性对照", 100.0, (0.0, 100.0),
         levels_override=[("rate80", 80.0), ("rate50", 50.0)]),
]


# ---------------------------------------------------------------- 基础设施

def clamp(v: float, domain) -> float:
    if domain is None:
        return v
    lo, hi = domain
    return min(max(v, lo), hi)


def atomic_write_json(path: Path, obj) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=1), encoding="utf-8")
    os.replace(tmp, path)


def git_head() -> str:
    try:
        p = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True,
                           text=True, cwd=str(ROOT), timeout=30)
        return p.stdout.strip() or "unknown"
    except Exception:
        return "unknown"


def run_match_utf8(dotnet: str, cli_dll: str, scenario, seed: int, duration: float) -> dict:
    """跑一场 match --events 并解析（UTF-8 解码 + 复用 oracle.parse_match_output）。

    编码口径见模块 docstring：实测 Sim.Cli match 子进程管道输出为 UTF-8
    字节（RunMatch 无显式编码设置，Program.cs:42-43 的显式 UTF-8 仅作用
    于 rl-env；管道 UTF-8 为 .NET 8 运行时重定向默认），oracle.run_match
    的 gb18030 解码实测归属全失效，故此处自写。
    """
    proc = subprocess.run(
        [dotnet, cli_dll, "match", "--seed", str(seed), "--scenario", str(scenario),
         "--duration", str(duration), "--events"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
    if proc.returncode != 0:
        raise RuntimeError(f"match seed={seed} exited {proc.returncode}: {proc.stderr[-400:]}")
    return oracle.parse_match_output(proc.stdout)


def derived(m: dict) -> dict:
    """oracle 五指标 + 派生 score_margin / mount_fail。"""
    d = dict(m)
    fs = m.get("final_score")
    d["score_margin"] = (fs[0] - fs[1]) if fs is not None else None
    d["mount_fail"] = 1 if m.get("mount_t") is None else 0
    return d


def level_values(spec: Spec) -> list[tuple[str, float]]:
    """该参数的扰动水平 [(tag, clamp 后值)]；classifyRate 用显式水平。"""
    if spec.levels_override:
        return [(t, v) for t, v in spec.levels_override]
    vals = [(t, clamp(spec.baseline * m, spec.domain)) for t, m in LEVEL_MULTS]
    seen, out = set(), []
    for t, v in vals:
        if v not in seen:
            seen.add(v)
            out.append((t, v))
    return out


def spec_job_values(spec: Spec) -> list[float]:
    """该参数需要实际跑场的目标值集合（去重；CR 为两段并集）。"""
    if spec.name == CR_NAME:
        vals: list[float] = []
        for _, _, levels in CR_SEGMENTS:
            for _, v in levels:
                if v not in vals:
                    vals.append(v)
        return vals
    out = []
    for _, v in level_values(spec):
        if spec.baseline is not None and abs(v - spec.baseline) < 1e-15:
            continue  # clamp 后与基线相同（Δp=0），无扰动意义，跳过
        out.append(v)
    return out


def write_variant(base_scenario, spec: Spec | None, value, out_path: Path) -> Path:
    """生成变体场景：parameters 级复用 oracle.write_scenario；vehicles 级自写
    （深拷贝 + us/them 同值覆盖指定字段；部分字段反序列化时其余取默认）。"""
    if spec is None:  # 基线：空 parameters 覆盖（FromDictionary 空表 → 全默认）
        return oracle.write_scenario(base_scenario, {}, out_path)
    if spec.channel == "parameters":
        return oracle.write_scenario(base_scenario, {spec.name: value}, out_path)
    base = json.loads(Path(base_scenario).read_text(encoding="utf-8"))
    v = copy.deepcopy(base)
    v["vehicles"] = {"us": {spec.name: value}, "them": {spec.name: value}}
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(v, ensure_ascii=False), encoding="utf-8")
    return out_path


def run_key(param: str, value, seed: int) -> str:
    v = "" if value is None else f"{value:.6g}"
    return f"{param}|{v}|{seed}"


# ---------------------------------------------------------------- 扫描

def run_scan(args, specs: list[Spec], seeds: list[int]) -> dict:
    out_dir = Path(args.out_dir)
    variants_dir = Path(args.variants_dir) if args.variants_dir else out_dir / "variants"
    results_path = out_dir / "sensitivity-results.json"
    out_dir.mkdir(parents=True, exist_ok=True)
    variants_dir.mkdir(parents=True, exist_ok=True)

    results = {"config": {}, "runs": []}
    if results_path.exists():
        try:
            results = json.loads(results_path.read_text(encoding="utf-8"))
        except Exception as e:  # 损坏则重跑（断点续跑以 runs 键为准）
            print(f"[warn] results.json 读取失败({e})，从头开始")
            results = {"config": {}, "runs": []}
    existing = {run_key(r["param"], r["value"], r["seed"]) for r in results["runs"]}

    cfg = {
        "git_head": git_head(),
        "dotnet": str(Path(args.dotnet).resolve()),
        "cli_dll": str(Path(args.cli_dll).resolve()),
        "base_scenario": str(Path(args.base_scenario).resolve()),
        "seeds": seeds,
        "duration": DURATION,
        "level_mults": dict(LEVEL_MULTS),
        "params": [
            {"name": s.name, "channel": s.channel, "subsystem": s.subsystem,
             "baseline": s.baseline, "domain": s.domain, "tier": s.tier,
             "levels": level_values(s) if s.name != CR_NAME
             else [{"segment": seg, "baseline": b, "levels": lv}
                   for seg, b, lv in CR_SEGMENTS]}
            for s in specs
        ],
        "skipped_noop_levels": [],  # clamp 后与基线相同被跳过的水平
    }
    for s in specs:
        if s.name != CR_NAME and s.baseline is not None:
            for t, v in level_values(s):
                if abs(v - s.baseline) < 1e-15:
                    cfg["skipped_noop_levels"].append({"param": s.name, "tag": t, "value": v})
    results["config"] = cfg

    # 作业表：(spec|None=基线, value)
    jobs: list[tuple[Spec | None, float | None]] = [(None, None)]
    for s in specs:
        for v in spec_job_values(s):
            jobs.append((s, v))
    total = len(jobs) * len(seeds)
    done = skipped = 0
    print(f"[scan] {len(jobs)} 个 (参数,值) 桶 × {len(seeds)} 种子 = {total} 场；"
          f"已存在 {len(existing)} 条（跳过）", flush=True)

    for spec, value in jobs:
        param = spec.name if spec else "__baseline__"
        vtag = "baseline" if value is None else f"{value:.6g}"
        variant_path = variants_dir / f"{param}-{vtag}.json"
        if not variant_path.exists():
            write_variant(args.base_scenario, spec, value, variant_path)
        for seed in seeds:
            key = run_key(param, value, seed)
            if key in existing:
                skipped += 1
                continue
            t0 = time.time()
            m = derived(run_match_utf8(args.dotnet, args.cli_dll, variant_path, seed, DURATION))
            wall = time.time() - t0
            entry = {"param": param, "value": value, "seed": seed, "metrics": m, "wall": round(wall, 3)}
            results["runs"].append(entry)
            atomic_write_json(results_path, results)  # 每场完成即原子增量落盘
            existing.add(key)
            done += 1
            print(f"[{done + skipped}/{total}] {param}={vtag} s{seed} wall={wall:.2f}s "
                  f"margin={m['score_margin']} drops={m['us_drops']} "
                  f"mount_t={m['mount_t']} done={m['done_reason']}", flush=True)
    print(f"[scan] 完成：新跑 {done} 场，跳过 {skipped} 场", flush=True)
    return results


# ---------------------------------------------------------------- 聚合

def _stats(values: list) -> dict:
    vals = [v for v in values if v is not None]
    if not vals:
        return {"mean": None, "std": 0.0, "n": 0}
    return {"mean": statistics.fmean(vals), "std": statistics.pstdev(vals), "n": len(vals)}


def _s_value(diff: float, dp_rel: float | None, base_std: float) -> float:
    """S = |Δmean| / (|Δp/p| × (std+ε))；dp_rel=None 时效应=噪声倍数（无 Δp 归一）。"""
    if base_std == 0.0:
        return math.inf if diff > 0 else 0.0
    denom = (dp_rel if dp_rel is not None else 1.0) * (base_std + EPS)
    if denom <= 0:
        return math.inf if diff > 0 else 0.0
    return diff / denom


def _bucket(runs: dict, param: str, value, seeds: list[int]) -> dict[int, dict]:
    return {r["seed"]: r["metrics"] for r in runs if r["param"] == param
            and ((r["value"] is None and value is None)
                 or (r["value"] is not None and value is not None
                     and abs(r["value"] - value) < 1e-12))
            and r["seed"] in seeds}


def _flips(bucket: dict[int, dict], base_bucket: dict[int, dict], field: str) -> int:
    n = 0
    for seed, m in bucket.items():
        bm = base_bucket.get(seed)
        if bm is None:
            continue
        a, b = str(m.get(field)), str(bm.get(field))
        if a != b:
            n += 1
    return n


def _fmt_s(s: float) -> str:
    if math.isinf(s):
        return "∞"
    return f"{s:.2f}"


def verdict_of(s_max: float, phase: bool) -> str:
    if s_max >= S_SENSITIVE:
        v = "敏感"
    elif s_max >= S_WEAK:
        v = "弱/存疑"
    else:
        v = "不敏感"
    if phase:
        v += "+相变"
    return v


def capture_advice(v: str, s_max: float) -> str:
    """对 08-28 的采集优先级建议（证据行，不拍板）。"""
    if s_max >= S_SENSITIVE:
        return "高优先级：OAT 邻域内显著影响比赛指标，建议 08-28 优先安排对应受控采集"
    if s_max >= S_WEAK:
        return "中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测"
    return "低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）"


def aggregate(results: dict, specs: list[Spec], seeds: list[int]) -> dict:
    runs = results.get("runs", [])
    buckets = {(r["param"], r["value"]): _bucket(runs, r["param"], r["value"], seeds)
                for r in runs}
    base_bucket = buckets.get(("__baseline__", None), {})
    if len(base_bucket) < len(seeds):
        raise RuntimeError(f"基线桶不完整：{len(base_bucket)}/{len(seeds)} 种子")

    base_stats = {m: _stats([base_bucket[s].get(m) for s in seeds if s in base_bucket])
                  for m in NUM_METRICS}
    base_mount = [base_bucket[s].get("mount_t") for s in seeds if s in base_bucket]
    base_mounted = [t for t in base_mount if t is not None]
    agg = {
        "baseline": {
            "per_metric": base_stats,
            "mount_t": {"mounted": len(base_mounted), "total": len(base_mount),
                        "mean_mounted": (statistics.fmean(base_mounted)
                                         if base_mounted else None)},
            "done_reasons": {r: sum(1 for s in seeds if s in base_bucket
                                    and str(base_bucket[s].get("done_reason")) == r)
                             for r in {str(base_bucket[s].get("done_reason"))
                                       for s in seeds if s in base_bucket}},
        },
        "params": [],
    }

    for spec in specs:
        entry = {"name": spec.name, "channel": spec.channel, "subsystem": spec.subsystem,
                 "tier": spec.tier, "baseline": spec.baseline,
                 "domain": spec.domain, "levels": [], "S_max": 0.0,
                 "phase_any": False, "verdict": "", "advice": ""}
        if spec.name == CR_NAME:
            for seg_name, seg_base, seg_levels in CR_SEGMENTS:
                for tag, v in seg_levels:
                    lvl = _level_agg(buckets, base_bucket, base_stats, seeds,
                                     spec, tag, v, seg_base, seg_name)
                    entry["levels"].append(lvl)
                    if seg_base is not None:  # 数值段参与跨参数 S 排序
                        entry["S_max"] = max(entry["S_max"], lvl["max_S"])
                    entry["phase_any"] = entry["phase_any"] or lvl["phase"]
        else:
            for tag, v in level_values(spec):
                if abs(v - spec.baseline) < 1e-15:
                    entry["levels"].append({"tag": tag, "value": v, "skipped_noop": True})
                    continue
                lvl = _level_agg(buckets, base_bucket, base_stats, seeds,
                                 spec, tag, v, spec.baseline, None)
                entry["levels"].append(lvl)
                entry["S_max"] = max(entry["S_max"], lvl["max_S"])
                entry["phase_any"] = entry["phase_any"] or lvl["phase"]
        entry["verdict"] = verdict_of(entry["S_max"], entry["phase_any"])
        entry["advice"] = capture_advice(entry["verdict"], entry["S_max"])
        agg["params"].append(entry)

    control = next((p for p in agg["params"] if p["name"] == "classifyRate"), None)
    if control is not None:
        agg["control_ok"] = control["S_max"] >= S_SENSITIVE or control["phase_any"]
    else:  # 子集模式（selftest/--params）：classifyRate 不在集合内，不做门禁判定
        agg["control_ok"] = None
    return agg


def _level_agg(buckets, base_bucket, base_stats, seeds, spec, tag, value,
               seg_base, seg_name) -> dict:
    # 段基线对照（PRD R1 "分两条基线呈现，不混比"）：仅 CR 数值段
    # （seg_name 非 None）启用——对照桶与噪声尺度改用该段基线值的桶
    # （同模型路径内的扰动），不得混入 null→数值的模型路径切换效应。
    # 普通参数（seg_base=spec.baseline 仅供 dp_rel）与 CR null 段均用全局基线桶。
    if seg_name is not None and seg_base is not None:
        seg_bucket = buckets.get((spec.name, seg_base), {})
        seg_missing = [s for s in seeds if s not in seg_bucket]
        if seg_missing:
            raise RuntimeError(f"{spec.name} 段基线 {seg_base} 桶缺种子 {seg_missing}")
        ref_bucket = seg_bucket
        ref_stats = {m: _stats([seg_bucket[s].get(m) for s in seeds])
                     for m in NUM_METRICS}
    else:
        ref_bucket = base_bucket
        ref_stats = base_stats
    bucket = buckets.get((spec.name, value), {})
    missing = [s for s in seeds if s not in bucket]
    if missing:
        raise RuntimeError(f"{spec.name}={value} 桶缺种子 {missing}")
    dp_rel = None
    if seg_base is not None:
        dp_rel = abs(value - seg_base) / abs(seg_base) if seg_base else None
    per_metric = {}
    for m in NUM_METRICS:
        st = _stats([bucket[s].get(m) for s in seeds])
        bs = ref_stats[m]
        if st["mean"] is None or bs["mean"] is None:
            per_metric[m] = {"mean": None, "std": st["std"], "S": None}
            continue
        s_val = _s_value(abs(st["mean"] - bs["mean"]), dp_rel, bs["std"])
        per_metric[m] = {"mean": st["mean"], "std": st["std"], "S": s_val}
    finite = [mm["S"] for mm in per_metric.values()
              if mm["S"] is not None and not math.isinf(mm["S"])]
    inf_any = any(mm["S"] is not None and math.isinf(mm["S"]) for mm in per_metric.values())
    max_s = math.inf if inf_any else (max(finite) if finite else 0.0)
    done_flips = _flips(bucket, ref_bucket, "done_reason")
    mount_flips = _flips(bucket, ref_bucket, "mount_fail")
    phase = done_flips >= PHASE_FLIP_MIN or mount_flips >= PHASE_FLIP_MIN
    mount = [bucket[s].get("mount_t") for s in seeds]
    mounted = [t for t in mount if t is not None]
    return {
        "segment": seg_name, "tag": tag, "value": value, "dp_rel": dp_rel,
        "per_metric": per_metric, "max_S": max_s,
        "phase": phase, "done_flips": done_flips, "mount_flips": mount_flips,
        "mount_t": {"mounted": len(mounted), "total": len(mount),
                    "mean_mounted": statistics.fmean(mounted) if mounted else None},
        "done_reasons": {r: sum(1 for s in seeds if str(bucket[s].get("done_reason")) == r)
                         for r in {str(bucket[s].get("done_reason")) for s in seeds}},
    }


# ---------------------------------------------------------------- 报告

def render_report(results: dict, agg: dict) -> str:
    cfg = results["config"]
    bs = agg["baseline"]
    lines = []
    a = lines.append
    a("# Sim 物理参数敏感性报告（OAT 局部敏感度）")
    a("")
    a("> 生成时间：" + time.strftime("%Y-%m-%d %H:%M:%S"))
    a("")
    a("## 配置头")
    a("")
    a(f"- git HEAD：`{cfg['git_head']}`")
    a(f"- dotnet：`{cfg['dotnet']}`")
    a(f"- Sim.Cli.dll：`{cfg['cli_dll']}`")
    a(f"- 基础场景：`{cfg['base_scenario']}`（vehicles/parameters 均为默认基线）")
    a(f"- 种子：s{cfg['seeds'][0]}–s{cfg['seeds'][-1]} 全集（{len(cfg['seeds'])} 个）；"
      f"比赛时长 {cfg['duration']:.0f}s（正式判读口径）")
    a("- 水平：相对基线 ×{0.75, 1.25, 0.5, 1.5}（−25%/+25%/−50%/+50%），"
      "有界参数 clamp 到校验域；**口径偏差**：PRD R2 写 “±10%/±25%/±50% 共 4 水平”，"
      "±10% 与 4 水平计数矛盾，按 design §3 唯一具体水平集（0.5×/0.75×/1.25×/1.5×）推广执行")
    a("- 阳性对照 classifyRate 用显式水平 80%/50%（method §2 指定；"
      "wire 名实测为 `classifyRate`，`CLASSIFY_RATE` 抛 Unknown simulation parameter）")
    a("- 第二梯队（BLOCK_STICK_SPEED/STALL_TIME/STALL_RELEASE/STALL_DISPLACEMENT）："
      "纳入。依据：实测单场 wall ≈ 0.23s，预算远低于 method §6 估计（1–1.5h），"
      "符合 PRD R1 “预算允许才做”")
    a("- 编码口径：实测 Sim.Cli match 子进程管道输出为 UTF-8 字节（首行 "
      "\"[我方]\" 字节 E6 88 91 E6 96 B9；match 输出路径无显式编码设置，"
      "Program.cs:42-43 的显式 UTF-8 仅作用于 rl-env 命令）；"
      "oracle.run_match 的 gb18030 解码实测使 [我方]/[对手] 归属全失效（baseline s1 "
      "us_drops 210→0），故本工具自写 UTF-8 解码、指标解析复用 oracle.parse_match_output")
    a("- vehicles 部分字段覆盖（latFrictionK/angDamping camelCase wire 名）："
      "ProtocolJson 对未知字段静默忽略，生效性由行为对比实测钉死"
      "（正确拼写改变行为、错误拼写与基线逐位一致）")
    noop = cfg.get("skipped_noop_levels") or []
    if noop:
        a(f"- 被跳过的无效水平（clamp 后与基线相同，Δp=0）：{noop}")
    a(f"- 基线跨种子指标：us_block_scores {bs['per_metric']['us_block_scores']['mean']:.2f}"
      f"±{bs['per_metric']['us_block_scores']['std']:.2f}，"
      f"us_drops {bs['per_metric']['us_drops']['mean']:.2f}"
      f"±{bs['per_metric']['us_drops']['std']:.2f}，"
      f"score_margin {bs['per_metric']['score_margin']['mean']:.2f}"
      f"±{bs['per_metric']['score_margin']['std']:.2f}，"
      f"mount_fail {bs['per_metric']['mount_fail']['mean']:.2f}"
      f"±{bs['per_metric']['mount_fail']['std']:.2f}；"
      f"登台 {bs['mount_t']['mounted']}/{bs['mount_t']['total']}")
    a("")
    a("## 阳性对照（classifyRate）")
    a("")
    control = next(p for p in agg["params"] if p["name"] == "classifyRate")
    if agg["control_ok"]:
        a(f"**敏感 ⇒ 方法学有效**（S_max={_fmt_s(control['S_max'])}，"
          f"裁决：{control['verdict']}）。视觉识别成功率扰动显著改变比赛指标，"
          "扫描/解析/聚合链路按预期响应参数变化。")
    else:
        a(f"**⚠ 不敏感 ⇒ 整表作废，方法学失效**（S_max={_fmt_s(control['S_max'])}）。"
          "按 PRD 验收标准，下表全部结论不可信，需停下排查扫描/解析/聚合链路。")
    a("")
    a("## 逐参数总表")
    a("")
    a("| 参数 | 子系统 | 通道 | 基线 | 梯队 | 各水平 max-S | S_max | 相变 | 裁决 |")
    a("|---|---|---|---|---|---|---|---|---|")
    for p in agg["params"]:
        if p["name"] == CR_NAME:
            base = "null→速度相关公式；数值基线 0.75（双段）"
            lvl_txt = "; ".join(
                f"{l['segment'].split('（')[0]} {l['tag']}={l['value']:.6g}:"
                f"{'效应' + _fmt_s(max((mm['S'] or 0) for mm in l['per_metric'].values())) if l.get('dp_rel') is None else _fmt_s(l['max_S'])}"
                for l in p["levels"])
            tier = "1"
        else:
            base = f"{p['baseline']:.6g}"
            lvl_txt = "; ".join(
                f"{l['tag']}={l['value']:.6g}:{_fmt_s(l['max_S'])}"
                for l in p["levels"] if not l.get("skipped_noop"))
            tier = str(p["tier"])
        a(f"| `{p['name']}` | {p['subsystem']} | {p['channel']} | {base} | {tier} "
          f"| {lvl_txt} | {_fmt_s(p['S_max'])} | {'是' if p['phase_any'] else '否'} "
          f"| {p['verdict']} |")
    a("")
    a("> S 定义见 method §5：|Δmean| / (|Δp/p| × (基线跨种子 std+ε))，以基线种子噪声"
      "倍数为单位；S<1 不敏感 · 1≤S<3 弱/存疑 · S≥3 敏感；∞ = 基线 std=0 但均值"
      "偏移非 0（相变级）。S_max = 各水平各指标最大 S（保守排序）。")
    a("")
    a("## 各参数 × 各指标 max-S 全表（不隐藏弱指标）")
    a("")
    a("| 参数 | 指标 | 基线 mean±std | max-S |")
    a("|---|---|---|---|")
    for p in agg["params"]:
        for m in NUM_METRICS:
            bs_m = bs["per_metric"][m]
            lvls = [l for l in p["levels"] if not l.get("skipped_noop")]
            s_vals = [l["per_metric"][m]["S"] for l in lvls
                      if l["per_metric"].get(m, {}).get("S") is not None]
            mx = max(s_vals) if s_vals else None
            mx_txt = _fmt_s(mx) if mx is not None else "—"
            a(f"| `{p['name']}` | {m} | {bs_m['mean']:.2f}±{bs_m['std']:.2f} | {mx_txt} |")
    a("")
    a("## COLLISION_RESTITUTION 双段明细（PRD R1：null 与数值基线不混比）")
    a("")
    a("> null 段（模型路径切换）的效应以全局 null 基线噪声倍数呈现、不参与跨参数 S 排序；"
      "数值段（固定恢复系数路径内扰动）的对照桶与噪声尺度均为 **0.75 段基线桶**"
      "（不混入 null→0.75 的模型路径切换效应）。")
    a("")
    cr = next(p for p in agg["params"] if p["name"] == CR_NAME)
    for seg_name, _, _ in CR_SEGMENTS:
        a(f"### {seg_name}")
        a("")
        a("| 水平 | 值 | Δp/p | us_block_scores | us_drops | score_margin | mount_fail | "
          "max-S/效应 | 相变 |")
        a("|---|---|---|---|---|---|---|---|---|")
        for l in [x for x in cr["levels"] if x["segment"] == seg_name]:
            cells = []
            for m in NUM_METRICS:
                pm = l["per_metric"][m]
                cells.append(_fmt_s(pm["S"]) if pm["S"] is not None else "—")
            dpp = f"{l['dp_rel']:.3f}" if l["dp_rel"] is not None else "—（路径切换）"
            head = _fmt_s(l["max_S"]) if l["dp_rel"] is not None else f"效应 {_fmt_s(l['max_S'])}"
            a(f"| {l['tag']} | {l['value']:.6g} | {dpp} | {cells[0]} | {cells[1]} "
              f"| {cells[2]} | {cells[3]} | {head} | "
              f"{'是(done:%d,mount:%d)' % (l['done_flips'], l['mount_flips']) if l['phase'] else '否'} |")
        a("")
    a("## 相变记录（done_reason / mount 状态翻转明细）")
    a("")
    flips = [(p, l) for p in agg["params"] for l in p["levels"]
             if not l.get("skipped_noop") and l.get("phase")]
    if flips:
        a("| 参数 | 水平 | 值 | done 翻转 | mount 翻转 |")
        a("|---|---|---|---|---|")
        for p, l in flips:
            a(f"| `{p['name']}` | {l['tag']} | {l['value']:.6g} "
              f"| {l['done_flips']}/{len(cfg['seeds'])} | {l['mount_flips']}/{len(cfg['seeds'])} |")
    else:
        a("无（所有参数所有水平下 done_reason 与 mount 状态翻转均 < 2/8 种子）。")
    a("")
    a("## 方法边界")
    a("")
    a("- OAT 只测**局部敏感度**（基线邻域 ±25%/±50%），不能外推参数大范围行为；"
      "交互项不显式测度，但跨水平非线性会显形（若 −25% 与 −50% 的 S 差异大 = 非线性提示）。")
    a("- 敏感度以基线跨种子 std 为噪声尺度；S<1 仅意味 “±50% 邻域内该参数效应淹没"
      "于种子噪声”，不排除更大范围或交互效应。")
    a("- mount_t 数值不进 S 排序（None=未登台是离散相变，由 mount_fail/登台数呈现）；"
      "登台种子上的 mount_t 均值见 sensitivity-results.json。")
    a("- 合成数据不入任何标定/fidelity 晋升链（Calibrator 制度封死 synthetic）；"
      "本报告仅为 08-28 采集优先级提供证据行。")
    a("")
    a("## 对 08-28 的采集优先级建议（证据行，不替 08-28 拍板）")
    a("")
    a("| 参数 | 子系统 | S_max | 裁决 | 建议 |")
    a("|---|---|---|---|---|")
    for p in sorted(agg["params"], key=lambda x: -(x["S_max"] if not math.isinf(x["S_max"]) else 1e18)):
        a(f"| `{p['name']}` | {p['subsystem']} | {_fmt_s(p['S_max'])} | {p['verdict']} | {p['advice']} |")
    a("")
    a("> 完整原始指标与逐水平聚合见同目录 `sensitivity-results.json`；"
      "变体场景 JSON 见 `variants/`（可由脚本确定性再生成，不入库）。")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- 主入口

def main() -> int:
    ap = argparse.ArgumentParser(description="Sim 物理参数 OAT 敏感性分析")
    ap.add_argument("--params", default=None, help="逗号分隔参数名子集（默认全部）")
    ap.add_argument("--seeds", default=",".join(str(s) for s in SEEDS))
    ap.add_argument("--report-only", action="store_true", help="只聚合已有 results 生成报告")
    ap.add_argument("--out-dir", default=str(HERE / "report"))
    ap.add_argument("--variants-dir", default=None)
    ap.add_argument("--dotnet", default=DOTNET_DEFAULT)
    ap.add_argument("--cli-dll", default=CLI_DLL_DEFAULT)
    ap.add_argument("--base-scenario", default=BASE_SCENARIO_DEFAULT)
    ap.add_argument("--no-xref", action="store_true",
                    help="不写 08-28 交叉引用附注（selftest/子集跑用）")
    args = ap.parse_args()

    specs = PARAMS
    if args.params:
        wanted = {s.strip() for s in args.params.split(",") if s.strip()}
        unknown = wanted - {s.name for s in PARAMS}
        if unknown:
            ap.error(f"未知参数: {unknown}（可选: {[s.name for s in PARAMS]}）")
        specs = [s for s in PARAMS if s.name in wanted]
    seeds = [int(s) for s in args.seeds.split(",") if s.strip()]

    if not args.report_only:
        results = run_scan(args, specs, seeds)
    else:
        rp = Path(args.out_dir) / "sensitivity-results.json"
        results = json.loads(rp.read_text(encoding="utf-8"))

    agg = aggregate(results, specs, seeds)
    results["aggregate"] = agg
    out_dir = Path(args.out_dir)
    atomic_write_json(out_dir / "sensitivity-results.json", results)
    report = render_report(results, agg)
    (out_dir / "sensitivity-report.md").write_text(report, encoding="utf-8")
    print(f"[report] {out_dir / 'sensitivity-report.md'} 生成完毕", flush=True)

    # 阳性对照门禁：classifyRate 必须在扫描范围内且敏感，否则整表作废声明 + 非 0 退出
    if any(s.name == "classifyRate" for s in specs) and not agg["control_ok"]:
        print("[FATAL] 阳性对照 classifyRate 不敏感 ⇒ 方法学失效，整表作废"
              "（报告已含作废声明）；按 implement.md 停下排查，不许静默继续", flush=True)
        return 2

    # 08-28 交叉引用附注（仅全量跑且未显式关闭时写）
    if not args.no_xref and not args.params and len(seeds) == len(SEEDS):
        xref_path = ROOT / ".trellis/tasks/08-28-real-telemetry-capture-first-promotion/research/sensitivity-input.md"
        xref_path.parent.mkdir(parents=True, exist_ok=True)
        rows = "\n".join(
            f"- `{p['name']}`（{p['subsystem']}）：S_max={_fmt_s(p['S_max'])}，"
            f"裁决 {p['verdict']} —— {p['advice']}"
            for p in sorted(agg["params"],
                            key=lambda x: -(x["S_max"] if not math.isinf(x["S_max"]) else 1e18)))
        xref = (
            "# 敏感性分析输入（交叉引用，10-14 任务附注）\n\n"
            "> 证据来源：`.trellis/tasks/10-14-sim-physical-sensitivity`（OAT 局部敏感度，"
            "git HEAD " + results["config"]["git_head"] + "）；完整数据见其 "
            "`tools/sensitivity/report/sensitivity-results.json` 与 `sensitivity-report.md`。"
            "本文件仅为采集优先级建议摘要（证据行），**不替 08-28 拍板、不改其 PRD**。\n\n"
            "## 采集优先级建议摘要（按 S_max 降序）\n\n" + rows + "\n\n"
            "## 注意事项\n\n"
            "- OAT 只测局部敏感度（±25%/±50% 邻域），S<1 不排除大范围非线性；"
            "敏感参数建议对应受控实验（碰撞 before/after 法向速度、摩擦、堵转、登台成败）优先采集。\n"
            "- 合成数据不入标定/fidelity 晋升链（Calibrator 制度封死 synthetic）。\n"
        )
        xref_path.write_text(xref, encoding="utf-8")
        print(f"[xref] {xref_path} 已写", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
