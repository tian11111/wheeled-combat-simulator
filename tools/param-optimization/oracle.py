"""Sim.Cli match oracle for FSM parameter optimization.

Wraps `dotnet exec Sim.Cli.dll match --events` as a deterministic evaluation
oracle: writes a per-trial scenario JSON (base scenario + parameters overlay),
runs one match per seed, and parses the event stream into per-side metrics.

Event line shapes relied upon (see docs/CLI.md --events):
  [  25] t= 236.00 BlockScore       [我方] [score] 增益块被推下擂台! 我方 +3 (4:3)
  [  19] t= 105.00 Drop             [我方] [score] 我方掉台, 对方 +1 (0:1)
  [   9] t=  32.00 Mount            [我方] [fsm] 已上台 on_stage ? → SEARCH
  seed=42 ticks=2400 score 我方 8 : 3 对手 done=比赛时间结束 faults(us/them)=0/0 ...
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

# [  25] t= 236.00 BlockScore       [我方] ...
_event_prefixes = ("BlockScore", "Drop", "Mount", "ScoreClock")


def write_scenario(base_scenario_path: str | Path, parameters: dict[str, float], out_path: str | Path) -> Path:
    """Copy the base scenario and overlay a trial's `parameters` dict."""
    base = json.loads(Path(base_scenario_path).read_text(encoding="utf-8"))
    base["parameters"] = dict(parameters)
    out = Path(out_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(base, ensure_ascii=False), encoding="utf-8")
    return out


def run_match(dotnet_exe: str, cli_dll: str, scenario_path: str | Path, seed: int, duration: float) -> dict:
    """Run one match with --events; return raw stdout plus exit code."""
    # 子进程经管道输出时使用系统 ANSI 代码页(中文 Windows 为 GBK/GB18030),
    # UTF-8 解码会把 [我方]/[对手] 变成替换符, 归属匹配全部失效。
    proc = subprocess.run(
        [dotnet_exe, cli_dll, "match", "--seed", str(seed), "--scenario", str(scenario_path),
         "--duration", str(duration), "--events"],
        capture_output=True, text=True, encoding="gb18030", errors="replace", timeout=600,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"match seed={seed} exited {proc.returncode}: {proc.stderr[-400:]}")
    return parse_match_output(proc.stdout)


def parse_match_output(text: str) -> dict:
    """Extract per-side metrics from one `match --events` run."""
    us_block_scores = 0
    us_drops = 0
    mount_t = None
    done_reason = ""
    final_score = None
    for line in text.splitlines():
        # 事件行: "[  25] t= 236.00 BlockScore       [我方] [score] ..." — 类型名
        # 出现在 t= 之后; 用子串匹配(类型名后跟多空格 + [角色])。
        if "BlockScore" in line and "[我方]" in line:
            us_block_scores += 1
        elif " Drop " in line and "[我方]" in line:
            us_drops += 1
        elif "Mount" in line and "[我方]" in line and "已上台" in line:
            t = _event_t(line)
            if t is not None and (mount_t is None or t < mount_t):
                mount_t = t
        elif line.strip().startswith("seed=") and "done=" in line:
            done_reason = _between(line, "done=", " faults") or ""
            final_score = _final_score(line)
    return {
        "us_block_scores": us_block_scores,
        "us_drops": us_drops,
        "mount_t": mount_t,
        "done_reason": done_reason,
        "final_score": final_score,
    }


def _event_t(line: str) -> float | None:
    # "[  25] t= 236.00 Kind ..." → t value in 0.1 s units (SimT display).
    marker = "t="
    idx = line.find(marker)
    if idx < 0:
        return None
    rest = line[idx + len(marker):].strip()
    token = rest.split()[0] if rest.split() else ""
    try:
        return float(token)
    except ValueError:
        return None


def _between(line: str, start: str, end: str) -> str | None:
    i = line.find(start)
    if i < 0:
        return None
    j = line.find(end, i + len(start))
    return line[i + len(start): j if j >= 0 else len(line)].strip()


def _final_score(line: str) -> tuple[float, float] | None:
    # seed=42 ticks=2400 score 我方 8 : 3 对手 ...
    marker = "score 我方"
    idx = line.find(marker)
    if idx < 0:
        return None
    rest = line[idx + len(marker):].split()
    try:
        return float(rest[0]), float(rest[2])
    except (IndexError, ValueError):
        return None
