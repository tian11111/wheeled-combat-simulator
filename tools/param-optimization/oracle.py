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
    """Run one match with --events; return parsed metrics.

    管道输出编码随 runtime 环境漂移(实证, match 路径从未显式设置输出编码,
    git -S OutputEncoding 全历史唯一提交 6aadde8 只作用于 rl-env):
    2026-09-25 旧 Temp SDK 下为 GBK(150-trial 历史数据 final_scores/mount_t
    有效可证), 2026-10-14 便携 SDK 8.0.31 实测为 UTF-8。故按字节捕获,
    先试 UTF-8 再按中文 marker 有效性签名回退 gb18030, 两种环境都正确
    (取代原单押 gb18030 的误判, 2026-10-14 修复)。
    """
    proc = subprocess.run(
        [dotnet_exe, cli_dll, "match", "--seed", str(seed), "--scenario", str(scenario_path),
         "--duration", str(duration), "--events"],
        capture_output=True, timeout=600,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"match seed={seed} exited {proc.returncode}: {proc.stderr[-400:]!r}")
    return parse_match_output(_decode_stdout(proc.stdout))


def _decode_stdout(raw: bytes) -> str:
    # 有效性签名: 汇总行 "seed=" 存在时 "score 我方" 中文 marker 应完好;
    # UTF-8 解 GBK 字节(或反之)都会使 marker 变替换符 → 换另一种编码重解。
    text = raw.decode("utf-8", errors="replace")
    if "seed=" in text and "score 我方" not in text:
        text = raw.decode("gb18030", errors="replace")
    return text


def parse_match_output(text: str) -> dict:
    """Extract per-side metrics from one `match --events` run.

    us_drops 口径 (2026-10-14 修正): 一次掉台在事件流里产生**两行** ——
    计分行 `[score] 我方掉台, 对方 +1` (带 points) 与 FSM 转移行
    `[fsm] 掉台! (方向 西) → RECOVER`。原实现 `" Drop " in line` 把两行各计一次
    (seed1 旧流实测 212 行 ≈ 105 次真实掉台的 2 倍)。现在**只计 [fsm] 行**
    (= 真实掉台宣告, 与 EventKind.Drop 的边沿触发语义一致: 每次掉台恰好一条)。
    """
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
        elif " Drop " in line and "[我方]" in line and "[fsm]" in line:
            # 只认 FSM 转移行 (真实掉台); "[score]" 计分行不另计。
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
    """事件行 t= 值 (单位: 秒)。

    2026-10-14 修正: 原先这里返回的是**显示值**, 注释称 "t value in 0.1 s units"
    —— 那是 Sim.Core.Js.ToFixed 缺陷造成的 10× 放大 (5.0s 显示成 "50.0")。该缺陷
    已在 10-14-oracle-metrics-fix 修复 (Js.cs), 事件行 t= 现在就是真秒, 无需再换算。
    """
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
