#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SCORE_BLOCK failure diagnosis: why we drop, and why block exits go unowned.

Diagnosis only. This script never trains, never gates, never re-runs a blind set
as evidence, and never touches ``Sim.Core``. It consumes the *opt-in* ``rl-env``
diagnostic trace (``{"op":"reset","trace":true}``) on **already revealed** seeds,
which are analysis input only.

Two stages:

``collect``  Run one episode per seed and stream the per-tick trace to
             ``<out>/traces/<tag>-<mode>-<seed>.jsonl.gz``.
``analyze``  Read those traces and classify every our-side ``Drop`` and every
             ``BlockOff`` from the raw contact records.
``all``      Both stages.

The decisive evidence for unowned exits is ``blocks[].contacts``: an append-only
list of ``(role, substep-time)`` contact records for the referee tick. The
referee derives ``LastContactRole`` from the records at the largest contact time,
and treats *any* count other than one as ``"simultaneous"`` — including several
records of the *same* robot. This script checks that directly.
"""

from __future__ import annotations

import argparse
import gzip
import json
import math
import statistics
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

from stable_baselines3 import PPO

from gym_env import MAX_POLICY_TICKS, ScoreBlockEnv, resolve_dotnet_executable
from splits import (
    SPLIT_VERSION,
    SplitError,
    resolve_selection,
    training_pool_manifest,
)
from train_artifacts import sha256_file

REPO_ROOT = Path(__file__).resolve().parents[2]

#: Forward-looking window used to describe the run-up to a drop (50 Hz ticks).
DROP_WINDOW = 25
#: MuJoCo SCORE_BLOCK FSM anti-drop thresholds, for same-scale comparison.
FSM_EDGE_GUARD = 0.27
FSM_SLOW_EDGE = 0.45
#: Contact times are quantised per substep; equality is exact by construction.
CONTACT_TIME_EPS = 1e-9

INFO_KEYS = (
    "seed", "entry_tick", "phase_entry_tick", "policy_ticks", "phase", "no_score_block",
    "target_index", "target_name", "target_out", "target_last_contact_role",
    "target_outcome_tick", "target_edge_distance", "target_x", "target_y",
    "us_block_scores", "us_block_score_events", "them_block_score_events",
    "target_block_offs", "unowned_block_offs", "us_drops", "attribution_ambiguous",
    "target_scored", "target_lost_not_ours", "us_dropped",
    "score_us", "score_them", "done_reason", "faults",
)

CLASSIFICATION_RULES = [
    "block never had any contact record in the episode -> no_valid_contact",
    "last contact tick: exactly one record at the max contact time -> single_contact_<role>",
    "last contact tick: several records at the max contact time, all one role -> "
    "multi_point_same_robot_<role>  (referee labels this 'simultaneous' -> no score)",
    "last contact tick: both roles at the max contact time -> genuine_two_robot_contest",
]


class DiagnosisError(RuntimeError):
    """Raised when the traces or the diagnostic trace contract are unusable."""


# --------------------------------------------------------------------------- #
# field geometry (for the outward direction at a drop)
# --------------------------------------------------------------------------- #

def load_field(scenario_path: Path) -> dict:
    data = json.loads(scenario_path.read_text(encoding="utf-8"))
    field = data["field"]
    platform = field["platform"]
    pose = field.get("pose") or {}
    return {
        "min": (float(platform["minX"]), float(platform["minY"])),
        "max": (float(platform["maxX"]), float(platform["maxY"])),
        "pose": (float(pose.get("x", 0.0)), float(pose.get("y", 0.0)), float(pose.get("th", 0.0))),
    }


def outward_normal(x: float, y: float, field: dict) -> tuple[tuple[float, float], float]:
    """World-space outward unit normal of the nearest platform edge, and its signed distance."""
    px, py, th = field["pose"]
    dx, dy = x - px, y - py
    c, s = math.cos(-th), math.sin(-th)
    lx, ly = c * dx - s * dy, s * dx + c * dy
    minx, miny = field["min"]
    maxx, maxy = field["max"]
    distances = {"-x": lx - minx, "+x": maxx - lx, "-y": ly - miny, "+y": maxy - ly}
    axis = min(distances, key=distances.get)
    local = {"-x": (-1.0, 0.0), "+x": (1.0, 0.0), "-y": (0.0, -1.0), "+y": (0.0, 1.0)}[axis]
    c2, s2 = math.cos(th), math.sin(th)
    return (c2 * local[0] - s2 * local[1], s2 * local[0] + c2 * local[1]), distances[axis]


def distance(a: dict, x: float, y: float) -> float:
    return math.hypot(a["x"] - x, a["y"] - y)


# --------------------------------------------------------------------------- #
# collect
# --------------------------------------------------------------------------- #

def slim_info(info: dict) -> dict:
    return {key: info.get(key) for key in INFO_KEYS}


def run_episode(env: ScoreBlockEnv, seed: int, mode: str, model) -> list[dict]:
    rows: list[dict] = []
    obs, info = env.reset(seed=seed)
    if info.get("trace") is None:
        raise DiagnosisError(
            "rl-env returned no 'trace' object; rebuild Sim.Cli (the opt-in trace is missing)"
        )
    rows.append({"kind": "reset", "seed": seed, "info": slim_info(info), "trace": info["trace"]})
    if info.get("no_score_block"):
        rows.append({"kind": "episode", "seed": seed, "no_score_block": True,
                     "total_reward": 0.0, "info": slim_info(info)})
        return rows

    terminated = truncated = False
    total_reward = 0.0
    for _ in range(MAX_POLICY_TICKS):
        action = None
        if mode == "policy":
            action, _ = model.predict(obs, deterministic=True)
            obs, reward, terminated, truncated, info = env.step(action)
            action = [float(action[0]), float(action[1])]
        else:
            obs, reward, terminated, truncated, info = env.step_fsm()
        total_reward += float(reward)
        if info.get("trace") is None:
            raise DiagnosisError(f"seed {seed}: trace disappeared mid-episode")
        rows.append({
            "kind": "tick", "seed": seed, "action": action, "reward": float(reward),
            "terminated": bool(terminated), "truncated": bool(truncated),
            "info": slim_info(info), "trace": info["trace"],
        })
        if terminated or truncated:
            break
    else:
        raise DiagnosisError(f"seed {seed} exceeded the {MAX_POLICY_TICKS}-tick bridge limit")

    rows.append({"kind": "episode", "seed": seed, "no_score_block": False,
                 "total_reward": round(total_reward, 6), "info": slim_info(info)})
    return rows


def collect(args: argparse.Namespace) -> dict:
    out_dir = Path(args.out).resolve()
    traces_dir = out_dir / "traces"
    traces_dir.mkdir(parents=True, exist_ok=True)

    try:
        selection = resolve_selection(split=args.split)
    except SplitError as exc:
        raise DiagnosisError(str(exc)) from exc
    seeds = selection.seeds
    if args.max_episodes:
        seeds = seeds[: args.max_episodes]

    modes = ["policy", "fsm"] if args.mode == "both" else [args.mode]
    model = None
    if "policy" in modes:
        model_path = Path(args.model) if args.model else None
        if model_path is None:
            raise DiagnosisError("--model is required for policy traces")
        if not model_path.is_absolute():
            model_path = (REPO_ROOT / model_path).resolve()
        if not model_path.is_file():
            raise DiagnosisError(f"model not found: {model_path}")
        model = PPO.load(str(model_path), device="cpu")

    cli_dll = Path(args.cli_dll)
    if not cli_dll.is_absolute():
        cli_dll = (REPO_ROOT / cli_dll).resolve()
    if not cli_dll.is_file():
        raise DiagnosisError(f"CLI dll not found: {cli_dll} (build RobotSimulator.sln first)")
    scenario = Path(args.scenario)
    if not scenario.is_absolute():
        scenario = (REPO_ROOT / scenario).resolve()

    dotnet = resolve_dotnet_executable(args.dotnet)
    tag = args.tag or selection.split
    manifest = {
        "stage": "collect",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "split": selection.split,
        "split_version": SPLIT_VERSION,
        "split_usage": selection.usage,
        "analysis_only_revealed_sets": True,
        "modes": modes,
        "seeds": seeds,
        "tag": tag,
        "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario),
        "cli_dll": str(cli_dll),
        "cli_dll_sha256": sha256_file(cli_dll),
        "dotnet": dotnet,
        "model": str(model_path) if "policy" in modes else None,
        "model_sha256": sha256_file(model_path) if "policy" in modes else None,
        "training_pool": training_pool_manifest(),
        "drop_window_ticks": DROP_WINDOW,
        "classification_rules": CLASSIFICATION_RULES,
    }

    written = []
    for mode in modes:
        for seed in seeds:
            path = traces_dir / f"{tag}-{mode}-{seed}.jsonl.gz"
            if path.exists() and not args.force:
                raise DiagnosisError(f"{path} exists; pass --force to overwrite")
            env = ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=args.duration,
                                seed_pool=[seed], trace=True)
            try:
                rows = run_episode(env, seed, mode, model)
            finally:
                env.close()
            with gzip.open(path, "wt", encoding="utf-8", newline="\n") as handle:
                for row in rows:
                    handle.write(json.dumps(row, separators=(",", ":"), ensure_ascii=False) + "\n")
            episode = next(r for r in rows if r["kind"] == "episode")
            info = episode["info"]
            written.append({
                "mode": mode, "seed": seed, "path": str(path),
                "no_score_block": bool(info.get("no_score_block")),
                "locked_target_us_block_scores": info.get("us_block_scores", 0),
                "us_drops": info.get("us_drops", 0),
                "unowned_block_offs": info.get("unowned_block_offs", 0),
                "target_block_offs": info.get("target_block_offs", 0),
                "policy_ticks": info.get("policy_ticks", 0),
                "final_score_us": info.get("score_us", 0),
                "final_score_them": info.get("score_them", 0),
            })
            print(f"[collect] {tag}/{mode} seed={seed} ticks={info.get('policy_ticks', 0)} "
                  f"scores={info.get('us_block_scores', 0)} drops={info.get('us_drops', 0)} "
                  f"unowned={info.get('unowned_block_offs', 0)}", flush=True)

    manifest["episodes"] = written
    (out_dir / f"collect-{tag}.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return manifest


# --------------------------------------------------------------------------- #
# analyze
# --------------------------------------------------------------------------- #

def read_episode(path: Path) -> list[dict]:
    rows = []
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def episode_index(rows: list[dict]) -> dict:
    """Per-block contact history and the tick frames of one episode."""
    reset = rows[0]
    trace0 = reset.get("trace") or {}
    blocks0 = trace0.get("blocks") or []
    frames = [r for r in rows if r["kind"] == "tick"]
    history: list[list[dict]] = [[] for _ in blocks0]
    ever_contacted = [False] * len(blocks0)
    for row in frames:
        trace = row["trace"]
        for idx, block in enumerate(trace.get("blocks") or []):
            if idx >= len(history):
                continue
            contacts = block.get("contacts") or []
            history[idx].append({
                "tick": trace["tick"],
                "contacts": contacts,
                "last_contact_role": block.get("last_contact_role", ""),
                "edge_distance": block.get("edge_distance"),
                "x": block["x"], "y": block["y"],
            })
            if contacts:
                ever_contacted[idx] = True
    return {
        "reset": reset,
        "trace0": trace0,
        "blocks0": blocks0,
        "frames": frames,
        "history": history,
        "ever_contacted": ever_contacted,
        "target_index": trace0.get("target_index", -1),
        "episode": next((r for r in rows if r["kind"] == "episode"), {}),
    }


def classify_exit(index: dict, block_index: int, tick: int) -> dict:
    """Classify one block exit from the raw contact records."""
    history = index["history"][block_index]
    if not index["ever_contacted"][block_index]:
        return {"classification": "no_valid_contact", "detail": "block never had a contact record"}
    entry = None
    for record in reversed(history):
        if record["tick"] <= tick and record["contacts"]:
            entry = record
            break
    if entry is None:
        return {"classification": "no_valid_contact", "detail": "no contact record at or before exit"}
    contacts = entry["contacts"]
    max_t = max(c["t"] for c in contacts)
    group = [c for c in contacts if abs(c["t"] - max_t) <= CONTACT_TIME_EPS]
    roles = sorted({c["r"] for c in group})
    if len(roles) == 1 and len(group) == 1:
        classification = f"single_contact_{roles[0]}"
    elif len(roles) == 1:
        classification = f"multi_point_same_robot_{roles[0]}"
    else:
        classification = "genuine_two_robot_contest"
    return {
        "classification": classification,
        "last_contact_tick": entry["tick"],
        "last_contact_role": entry["last_contact_role"],
        "max_contact_time": max_t,
        "records_at_max_time": len(group),
        "roles_at_max_time": roles,
        "roles_in_tick": sorted({c["r"] for c in contacts}),
        "record_count_in_tick": len(contacts),
    }


def analyse_episode(index: dict, field: dict, source: str) -> dict:
    frames = index["frames"]
    target_index = index["target_index"]
    target_name = None
    if 0 <= target_index < len(index["blocks0"]):
        target_name = index["blocks0"][target_index]["name"]

    result = {
        "source": source,
        "seed": index["reset"]["seed"],
        "target_index": target_index,
        "target_name": target_name,
        "no_score_block": bool(index["episode"].get("no_score_block", False)),
        "policy_ticks": index["episode"].get("info", {}).get("policy_ticks", 0),
        "total_reward": index["episode"].get("total_reward"),
        "locked_target_us_block_scores": index["episode"].get("info", {}).get("us_block_scores", 0),
        "us_drops_counter": index["episode"].get("info", {}).get("us_drops", 0),
        "unowned_block_offs_counter": index["episode"].get("info", {}).get("unowned_block_offs", 0),
        "target_block_offs_counter": index["episode"].get("info", {}).get("target_block_offs", 0),
        "drops": [],
        "block_offs": [],
        "block_scores": [],
        "pairing_ambiguous": 0,
    }

    prev_out = [bool(b.get("out")) for b in index["blocks0"]]
    for position, row in enumerate(frames):
        trace = row["trace"]
        blocks = trace.get("blocks") or []
        out_now = [bool(b.get("out")) for b in blocks]
        flips = [i for i in range(min(len(out_now), len(prev_out))) if out_now[i] and not prev_out[i]]
        events = trace.get("events") or []
        for event in events:
            kind = event.get("kind")
            if kind == "Drop" and event.get("is_us") and not event.get("neutral"):
                result["drops"].append(analyse_drop(index, position, row, field, target_index))
            elif kind in ("BlockOff", "BlockScore"):
                block_index = None
                named = [i for i in flips if blocks[i].get("name") == event.get("block")]
                if len(named) == 1:
                    block_index = named[0]
                elif len(flips) == 1:
                    block_index = flips[0]
                entry = {
                    "tick": trace["tick"],
                    "kind": kind,
                    "event_reason": event.get("reason", ""),
                    "event_role": event.get("role", ""),
                    "event_block_name": event.get("block", ""),
                    "block_index": block_index,
                    "block_is_target": block_index == target_index,
                }
                if block_index is None:
                    result["pairing_ambiguous"] += 1
                else:
                    entry.update(classify_exit(index, block_index, trace["tick"]))
                    exit_block = blocks[block_index]
                    entry["block_kind"] = exit_block.get("kind")
                    us = trace["us"]
                    them = trace["them"]
                    entry["us_distance_to_block_at_exit"] = round(distance(us, exit_block["x"], exit_block["y"]), 4)
                    entry["them_distance_to_block_at_exit"] = round(distance(them, exit_block["x"], exit_block["y"]), 4)
                    entry["us_on_stage_at_exit"] = us.get("on_stage")
                    if kind == "BlockOff":
                        result["block_offs"].append(entry)
                    else:
                        result["block_scores"].append(entry)
        prev_out = out_now

    result["drop_labels"] = Counter(d["label"] for d in result["drops"])
    result["drop_labels"] = dict(result["drop_labels"])
    result["block_off_classifications"] = dict(Counter(o["classification"] for o in result["block_offs"]))
    result["block_score_classifications"] = dict(Counter(s["classification"] for s in result["block_scores"]))
    return result


def analyse_drop(index: dict, position: int, row: dict, field: dict, target_index: int) -> dict:
    trace = row["trace"]
    us = trace["us"]
    normal, signed = outward_normal(us["x"], us["y"], field)
    speed = math.hypot(us["vx"], us["vy"])
    outward_speed = us["vx"] * normal[0] + us["vy"] * normal[1]

    frames = index["frames"]
    window = frames[max(0, position - DROP_WINDOW): position + 1]
    min_edge = min(w["trace"]["us"]["edge_distance"] for w in window)
    opponent_min = min(
        math.hypot(w["trace"]["us"]["x"] - w["trace"]["them"]["x"],
                   w["trace"]["us"]["y"] - w["trace"]["them"]["y"]) for w in window)
    target_contact_us = False
    target_contact_them = False
    if 0 <= target_index:
        for w in window:
            blocks = w["trace"].get("blocks") or []
            if target_index < len(blocks):
                roles = {c["r"] for c in (blocks[target_index].get("contacts") or [])}
                target_contact_us = target_contact_us or ("us" in roles)
                target_contact_them = target_contact_them or ("them" in roles)

    action = row.get("action")
    label = classify_drop(outward_speed, speed, min_edge, target_contact_us, opponent_min)
    return {
        "seed": index["reset"]["seed"],
        "tick": trace["tick"],
        "policy_ticks": row["info"].get("policy_ticks"),
        "action": action,
        "command_v": us.get("v"),
        "command_w": us.get("w"),
        "speed": round(speed, 4),
        "outward_speed": round(outward_speed, 4),
        "signed_edge_distance": round(signed, 4),
        "referee_edge_distance": round(us["edge_distance"], 4),
        "min_edge_distance_in_window": round(min_edge, 4),
        "opponent_min_distance_in_window": round(opponent_min, 4),
        "contacting_target_in_window": target_contact_us,
        "opponent_contacting_target_in_window": target_contact_them,
        "inside_fsm_guard_zone": min_edge < FSM_EDGE_GUARD,
        "inside_fsm_slow_zone": min_edge < FSM_SLOW_EDGE,
        "label": label,
    }


def classify_drop(outward_speed: float, speed: float, min_edge: float,
                  target_contact_us: bool, opponent_min: float) -> str:
    """Coarse, documented label over measured facts; 'unclassified' is a real outcome."""
    if target_contact_us and min_edge < FSM_SLOW_EDGE:
        return "pushing_target_inside_slow_zone"
    if opponent_min < 0.42 and outward_speed > 0.05:
        return "pushed_out_by_opponent_contact"
    if outward_speed > 0.15 and speed > 0.25:
        return "driving_outward_fast"
    if abs(outward_speed) <= 0.15:
        return "slow_drift_over_edge"
    return "unclassified"


def summarize_drop_features(drops: list[dict]) -> dict:
    """Raw measured facts behind the coarse labels (medians are over drop events)."""
    if not drops:
        return {"drops": 0}
    per_seed = Counter(d["seed"] for d in drops)

    def median(key: str) -> float:
        return round(statistics.median(d[key] for d in drops), 4)

    return {
        "drops": len(drops),
        "min_edge_distance_in_window": {
            "median": median("min_edge_distance_in_window"),
            "min": round(min(d["min_edge_distance_in_window"] for d in drops), 4),
            "max": round(max(d["min_edge_distance_in_window"] for d in drops), 4),
        },
        "referee_edge_distance_at_drop": {"median": median("referee_edge_distance")},
        "speed_at_drop": {"median": median("speed")},
        "outward_speed_at_drop": {"median": median("outward_speed")},
        "command_v_at_drop": {
            "median": median("command_v"),
            "min": round(min(d["command_v"] for d in drops), 4),
            "max": round(max(d["command_v"] for d in drops), 4),
        },
        "abs_command_w_at_drop": {
            "median": round(statistics.median(abs(d["command_w"]) for d in drops), 4)
        },
        "entered_fsm_guard_zone_0_27m": sum(d["inside_fsm_guard_zone"] for d in drops),
        "entered_fsm_slow_zone_0_45m": sum(d["inside_fsm_slow_zone"] for d in drops),
        "contacting_target_in_window": sum(d["contacting_target_in_window"] for d in drops),
        "opponent_contacting_target_in_window": sum(
            d["opponent_contacting_target_in_window"] for d in drops),
        "seeds_with_more_than_one_drop": sorted(s for s, c in per_seed.items() if c > 1),
    }


def load_recorded(path: Path) -> dict[tuple[str, int], dict]:
    """Read a recorded ``evaluate.py`` result (both known layouts) as per-episode rows."""
    data = json.loads(path.read_text(encoding="utf-8"))
    if "models" in data and data["models"]:
        policy_rows = data["models"][0].get("per_episode_policy", [])
        fsm_rows = (data.get("fsm_baseline") or {}).get("per_episode", [])
    else:
        per_episode = data.get("per_episode") or {}
        policy_rows = per_episode.get("policy", [])
        fsm_rows = per_episode.get("fsm_baseline", [])
    rows: dict[tuple[str, int], dict] = {}
    for row in policy_rows:
        rows[("policy", int(row["seed"]))] = row
    for row in fsm_rows:
        rows[("fsm", int(row["seed"]))] = row
    return rows


#: Recorded evaluate.py counter -> the episode-analysis field holding the same value.
#: ``int`` compares exactly; ``float`` compares within FLOAT_TOL.
CROSS_CHECK_FIELDS = {
    "locked_target_us_block_scores": ("locked_target_us_block_scores", "int"),
    "us_drops": ("us_drops_counter", "int"),
    "unowned_block_offs": ("unowned_block_offs_counter", "int"),
    "policy_ticks": ("policy_ticks", "int"),
    "total_reward": ("total_reward", "float"),
}
FLOAT_TOL = 1e-6


def cross_check_recorded(episodes: list[dict], path: Path) -> dict:
    recorded = load_recorded(path)
    collected = {(e["source"].split("/")[1], int(e["seed"])): e for e in episodes}
    mismatches = []
    for key, want in sorted(recorded.items()):
        got = collected.get(key)
        if got is None:
            mismatches.append({"key": list(key), "field": "episode", "trace": None, "recorded": "present"})
            continue
        for recorded_field, (trace_field, kind) in CROSS_CHECK_FIELDS.items():
            left = got.get(trace_field)
            right = want.get(recorded_field)
            if kind == "int":
                same = int(left or 0) == int(right or 0)
            else:
                same = left is not None and right is not None and abs(float(left) - float(right)) <= FLOAT_TOL
            if not same:
                mismatches.append({"key": list(key), "field": recorded_field,
                                   "trace": left, "recorded": right})
    return {
        "recorded_file": str(path),
        "recorded_sha256": sha256_file(path),
        "pairs_recorded": len(recorded),
        "pairs_collected": len(collected),
        "fields": sorted(CROSS_CHECK_FIELDS),
        "mismatches": mismatches,
        "reproduces": not mismatches,
    }


#: Representative raw samples quoted in the diagnosis report (tag, mode, seed, tick, note).
EXCERPT_SAMPLES = (
    ("final_holdout_v2", "policy", 6037, 317,
     "单机器人多点接触被误判为 simultaneous：我方距块 0.218 m，对手距块 1.782 m"),
    ("final_holdout_v2", "policy", 6048, 260,
     "同一缺陷：我方距块 0.223 m，对手距块 2.164 m，max 接触时刻 4 条记录全为 us"),
    ("legacy_round1", "fsm", 4005, 349,
     "第一轮 FSM 基线的锁定目标被我方推出界，同样记为 simultaneous → FSM 丢失 +3"),
    ("final_holdout_v2", "policy", 6001, 292,
     "对照组：本 tick 共 10 条接触记录，但 max 接触时刻只有 1 条 → 归属成功并计分"),
)


def excerpts(args: argparse.Namespace) -> dict:
    """Quote the decisive raw trace rows into ``<out>/decisive-excerpt.md``."""
    traces_dir = Path(args.out).resolve() / "traces"
    if not traces_dir.is_dir():
        raise DiagnosisError(f"no traces directory at {traces_dir}; run collect first")
    lines = [
        "# 决定性原始轨迹摘录",
        "",
        "全量逐 tick 轨迹（120 个 `.jsonl.gz`，24.6 MB）不入 Git；下列摘录直接来自这些文件，",
        "可由 `diagnose.py excerpts` 重跑复现。",
        "",
    ]
    found = 0
    for tag, mode, seed, tick, note in EXCERPT_SAMPLES:
        path = traces_dir / f"{tag}-{mode}-{seed}.jsonl.gz"
        lines += [f"## seed {seed} / {mode} / tick {tick}", "", note, "",
                  f"- 轨迹文件: `traces/{path.name}`（不入 Git）", ""]
        if not path.is_file():
            lines += ["（轨迹文件缺失，无法摘录）", ""]
            continue
        row = next((r for r in read_episode(path)
                    if r["kind"] == "tick" and r["trace"]["tick"] == tick), None)
        if row is None:
            lines += ["（该 tick 不存在，无法摘录）", ""]
            continue
        found += 1
        trace = row["trace"]
        payload = {
            "tick": trace["tick"],
            "action": row.get("action"),
            "us": trace["us"],
            "them": trace["them"],
            "blocks": [
                {"name": b["name"], "kind": b["kind"], "x": round(b["x"], 4), "y": round(b["y"], 4),
                 "out": b["out"], "last_contact_role": b["last_contact_role"],
                 "contacts": b["contacts"]}
                for b in trace["blocks"]
            ],
            "events": trace["events"],
        }
        lines += ["```json", json.dumps(payload, indent=1, ensure_ascii=False), "```", ""]
    out_path = Path(args.out).resolve() / "decisive-excerpt.md"
    out_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    result = {"samples_requested": len(EXCERPT_SAMPLES), "samples_written": found,
              "path": str(out_path)}
    print(json.dumps(result, ensure_ascii=False))
    return result


def default_off_check(args: argparse.Namespace) -> dict:
    """Prove the opt-in trace changes neither the default reply shape nor the dynamics.

    Drives the same seed with the same scripted action sequence twice — once with
    the trace off (the training/evaluation contract) and once on — then requires
    every field except ``info.trace`` to be identical, and requires ``info.trace``
    to be absent when off and present when on.
    """
    cli_dll = Path(args.cli_dll)
    if not cli_dll.is_absolute():
        cli_dll = (REPO_ROOT / cli_dll).resolve()
    if not cli_dll.is_file():
        raise DiagnosisError(f"CLI dll not found: {cli_dll}")
    scenario = Path(args.scenario)
    if not scenario.is_absolute():
        scenario = (REPO_ROOT / scenario).resolve()
    dotnet = resolve_dotnet_executable(args.dotnet)

    def scripted(tick: int) -> tuple[float, float]:
        return (0.6 * math.sin(0.05 * tick), 0.3 * math.cos(0.03 * tick))

    runs: dict[str, list[dict]] = {}
    for label, trace in (("off", False), ("on", True)):
        env = ScoreBlockEnv(dotnet, str(cli_dll), str(scenario), duration=args.duration,
                            seed_pool=[args.seed], trace=trace)
        try:
            obs, info = env.reset(seed=args.seed)
            rows = [{"obs": [float(x) for x in obs], "info": info}]
            for tick in range(args.ticks):
                obs, reward, terminated, truncated, info = env.step(scripted(tick))
                rows.append({"obs": [float(x) for x in obs], "reward": float(reward),
                             "terminated": bool(terminated), "truncated": bool(truncated),
                             "info": info})
                if terminated or truncated:
                    break
        finally:
            env.close()
        runs[label] = rows

    off, on = runs["off"], runs["on"]
    problems: list[str] = []
    if len(off) != len(on):
        problems.append(f"row count differs: off={len(off)} on={len(on)}")
    if any("trace" in row["info"] for row in off):
        problems.append("default (trace off) reply exposes info.trace")
    if not all("trace" in row["info"] for row in on):
        problems.append("trace on reply is missing info.trace somewhere")
    for index, (a, b) in enumerate(zip(off, on)):
        if a["obs"] != b["obs"]:
            problems.append(f"row {index}: observation differs")
        if a.get("reward") != b.get("reward"):
            problems.append(f"row {index}: reward differs ({a.get('reward')} vs {b.get('reward')})")
        for key in ("terminated", "truncated"):
            if a.get(key) != b.get(key):
                problems.append(f"row {index}: {key} differs")
        stripped = {k: v for k, v in b["info"].items() if k != "trace"}
        if stripped != a["info"]:
            differing = sorted(set(stripped) ^ set(a["info"])) or [
                k for k in stripped if stripped[k] != a["info"].get(k)]
            problems.append(f"row {index}: info differs on {differing[:5]}")

    result = {
        "stage": "default-off-check",
        "seed": args.seed,
        "rows_compared": min(len(off), len(on)),
        "scripted_action_source": "v=0.6*sin(0.05*tick), w=0.3*cos(0.03*tick)",
        "trace_absent_when_off": not any("trace" in row["info"] for row in off),
        "trace_present_when_on": all("trace" in row["info"] for row in on),
        "problems": problems,
        "identical": not problems,
    }
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "default-off-check.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return result


def analyze(args: argparse.Namespace) -> dict:
    out_dir = Path(args.out).resolve()
    traces_dir = out_dir / "traces"
    if not traces_dir.is_dir():
        raise DiagnosisError(f"no traces directory at {traces_dir}; run collect first")
    scenario = Path(args.scenario)
    if not scenario.is_absolute():
        scenario = (REPO_ROOT / scenario).resolve()
    field = load_field(scenario)

    episodes = []
    for path in sorted(traces_dir.glob("*.jsonl.gz")):
        parts = path.name.split(".")
        stem = parts[0]
        pieces = stem.rsplit("-", 2)
        if len(pieces) != 3:
            continue
        tag, mode, seed_text = pieces
        if mode not in ("policy", "fsm"):
            continue
        if args.tag and tag != args.tag:
            continue
        rows = read_episode(path)
        index = episode_index(rows)
        episodes.append(analyse_episode(index, field, f"{tag}/{mode}/{seed_text}"))

    by_mode: dict[str, list[dict]] = defaultdict(list)
    for episode in episodes:
        by_mode[episode["source"].split("/")[1]].append(episode)

    aggregate = {}
    for mode, items in sorted(by_mode.items()):
        drop_labels: Counter = Counter()
        off_classes: Counter = Counter()
        score_classes: Counter = Counter()
        off_reasons: Counter = Counter()
        for episode in items:
            drop_labels.update(episode["drop_labels"])
            off_classes.update(episode["block_off_classifications"])
            score_classes.update(episode["block_score_classifications"])
            off_reasons.update(o["event_reason"] for o in episode["block_offs"])
        aggregate[mode] = {
            "episodes": len(items),
            "episodes_with_no_score_block": sum(e["no_score_block"] for e in items),
            "total_locked_target_us_block_scores": sum(e["locked_target_us_block_scores"] for e in items),
            "total_us_drops": sum(len(e["drops"]) for e in items),
            "total_block_off_events": sum(len(e["block_offs"]) for e in items),
            "total_block_score_events": sum(len(e["block_scores"]) for e in items),
            "pairing_ambiguous": sum(e["pairing_ambiguous"] for e in items),
            "drop_labels": dict(drop_labels),
            "drop_features": summarize_drop_features([d for e in items for d in e["drops"]]),
            "block_off_referee_reasons": dict(off_reasons),
            "block_off_classifications": dict(off_classes),
            "block_off_by_owner_and_kind": dict(Counter(
                f"{o.get('block_kind')}|{o['classification']}|target={o['block_is_target']}"
                for e in items for o in e["block_offs"])),
            "block_score_classifications": dict(score_classes),
        }

    misattributed = [
        {
            "source": episode["source"], "seed": episode["seed"], "tick": off["tick"],
            "block_index": off["block_index"], "block_is_target": off["block_is_target"],
            "referee_reason": off["event_reason"], "classification": off["classification"],
            "last_contact_tick": off.get("last_contact_tick"),
            "records_at_max_time": off.get("records_at_max_time"),
            "roles_at_max_time": off.get("roles_at_max_time"),
            "record_count_in_tick": off.get("record_count_in_tick"),
            "us_distance_to_block_at_exit": off.get("us_distance_to_block_at_exit"),
            "them_distance_to_block_at_exit": off.get("them_distance_to_block_at_exit"),
        }
        for episode in episodes for off in episode["block_offs"]
        if str(off.get("classification", "")).startswith("multi_point_same_robot_")
    ]
    genuine = [
        {"source": e["source"], "seed": e["seed"], "tick": o["tick"], "classification": o["classification"]}
        for e in episodes for o in e["block_offs"]
        if o.get("classification") == "genuine_two_robot_contest"
    ]

    diagnosis = {
        "stage": "analyze",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "diagnosis_only": True,
        "never_gate_evidence": True,
        "scenario": str(scenario),
        "scenario_sha256": sha256_file(scenario),
        "classification_rules": CLASSIFICATION_RULES,
        "fsm_reference_thresholds": {"edge_guard": FSM_EDGE_GUARD, "edge_slow": FSM_SLOW_EDGE},
        "aggregate": aggregate,
        "misattributed_simultaneous": misattributed,
        "genuine_two_robot_contests": genuine,
        "episodes": episodes,
    }
    if args.compare_recorded:
        recorded_path = Path(args.compare_recorded)
        if not recorded_path.is_absolute():
            recorded_path = (REPO_ROOT / recorded_path).resolve()
        diagnosis["recorded_cross_check"] = cross_check_recorded(episodes, recorded_path)
        if not diagnosis["recorded_cross_check"]["reproduces"]:
            print("warning: traces do not reproduce the recorded evaluation counters",
                  file=sys.stderr)
    (out_dir / "diagnosis.json").write_text(
        json.dumps(diagnosis, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(json.dumps({"aggregate": aggregate,
                      "misattributed_simultaneous_count": len(misattributed),
                      "genuine_two_robot_contest_count": len(genuine),
                      "recorded_cross_check": diagnosis.get("recorded_cross_check", {}).get("reproduces")},
                     indent=2, ensure_ascii=False))
    return diagnosis


# --------------------------------------------------------------------------- #
# cli
# --------------------------------------------------------------------------- #

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    for name in ("collect", "all"):
        node = sub.add_parser(name, help="collect per-tick traces (and analyze, for 'all')")
        node.add_argument("--split", default="final_holdout_v2",
                          help="named split (analysis-only; revealed sets allowed)")
        node.add_argument("--mode", choices=("policy", "fsm", "both"), default="both")
        node.add_argument("--model", default=None)
        node.add_argument("--tag", default=None, help="trace filename tag (default: split name)")
        node.add_argument("--dotnet", default=None)
        node.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
        node.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
        node.add_argument("--duration", type=float, default=120.0)
        node.add_argument("--out", required=True)
        node.add_argument("--force", action="store_true")
        node.add_argument("--max-episodes", type=int, default=0)

    node = sub.add_parser("analyze", help="classify drops and block exits from traces")
    node.add_argument("--out", required=True)
    node.add_argument("--tag", default=None)
    node.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    node.add_argument("--compare-recorded", default=None,
                      help="recorded evaluate.py JSON to reproduce counter-for-counter")

    node = sub.add_parser("excerpts", help="quote the decisive raw trace rows as markdown")
    node.add_argument("--out", required=True)

    node = sub.add_parser("default-off-check",
                          help="prove the opt-in trace leaves the default rl-env path identical")
    node.add_argument("--seed", type=int, default=6001)
    node.add_argument("--ticks", type=int, default=300)
    node.add_argument("--duration", type=float, default=120.0)
    node.add_argument("--dotnet", default=None)
    node.add_argument("--cli-dll", default="src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll")
    node.add_argument("--scenario", default="scenarios/wushu-ring-2026-mujoco.json")
    node.add_argument("--out", required=True)

    args = parser.parse_args()
    try:
        if args.command == "collect":
            collect(args)
        elif args.command == "analyze":
            analyze(args)
        elif args.command == "default-off-check":
            if not default_off_check(args)["identical"]:
                return 1
        elif args.command == "excerpts":
            excerpts(args)
        else:
            collect(args)
            analyze(argparse.Namespace(out=args.out, tag=args.tag, scenario=args.scenario))
    except (DiagnosisError, SplitError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
