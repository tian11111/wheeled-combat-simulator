#!/usr/bin/env python3
"""sensitivity.py 自检（design §5 三项，全过才许进全量扫描）。

①口径对拍：Sim.Cli 原始 stdout（UTF-8 解码）→ oracle.parse_match_output，
   与 sensitivity 基线路径（oracle.write_scenario 空覆盖 + run_match_utf8）
   五指标逐字段相等；oracle.run_match 原样（gb18030）结果作为**已知编码偏差**
   打印（实测 Sim.Cli match 子进程管道输出为 UTF-8 字节——RunMatch 无显式
   编码设置，Program.cs:42-43 显式 UTF-8 仅作用于 rl-env 命令；gb18030
   解码下归属全失效），不作为失败条件（口径一致性以同一 parse 函数 +
   同一场景/种子为判据）。
②变体生效：latFrictionK=16 变体写盘→读回断言 us/them 均为 16 且其余键保留；
   跑 s1 与基线行为不同（正确拼写生效）；错拼 lat_friction_k=16 对照跑 s1
   与基线逐位一致（ProtocolJson 静默忽略未知字段路径实测）。
③断点续跑：小子集（基线+STALL_SPEED）跑完存副本 → 删一半条目 → 重跑 →
   断言：重跑后 runs 键集补齐、未删条目逐字节保持、与全跑副本抽查 3 条逐字段一致。

selftest 全程使用 tmp/sens-selftest/ 独立 out-dir，不污染正式 report/。
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import sensitivity as sens  # noqa: E402
sys.path.insert(0, str(HERE.parent / "param-optimization"))
import oracle  # noqa: E402

TMP = HERE.parent.parent / "tmp" / "sens-selftest"
DOTNET = sens.DOTNET_DEFAULT
DLL = sens.CLI_DLL_DEFAULT
BASE = sens.BASE_SCENARIO_DEFAULT
SEED = 1
FAILS: list[str] = []


def check(cond: bool, name: str, detail: str = "") -> None:
    tag = "PASS" if cond else "FAIL"
    print(f"[{tag}] {name}" + (f" — {detail}" if detail else ""), flush=True)
    if not cond:
        FAILS.append(name)


def fresh_tmp() -> Path:
    if TMP.exists():
        shutil.rmtree(TMP)
    TMP.mkdir(parents=True)
    return TMP


def test1_pariry() -> None:
    print("== ①口径对拍 ==", flush=True)
    tmp = fresh_tmp()
    # a) 原始 stdout UTF-8 解码 → oracle.parse_match_output（独立直跑）
    base_scenario = oracle.write_scenario(BASE, {}, tmp / "t1-baseline.json")
    proc = subprocess.run(
        [DOTNET, DLL, "match", "--seed", str(SEED), "--scenario", str(base_scenario),
         "--duration", "120", "--events"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
    assert proc.returncode == 0, proc.stderr[-300:]
    m_direct = oracle.parse_match_output(proc.stdout)
    # b) sensitivity 基线路径（同一场景文件、同一种子）
    m_tool = sens.run_match_utf8(DOTNET, DLL, base_scenario, SEED, 120.0)
    keys = ["us_block_scores", "us_drops", "mount_t", "done_reason", "final_score"]
    check(all(m_direct[k] == m_tool[k] for k in keys), "①a 直跑 vs 工具路径五指标逐字段相等",
          f"direct={m_direct!r} tool={m_tool!r}")
    # c) oracle.run_match 原样（gb18030）= 已知编码偏差，打印不判失败
    try:
        m_oracle = oracle.run_match(DOTNET, DLL, str(base_scenario), SEED, 120.0)
        if m_oracle is not None and m_oracle.get("final_score") is None:
            print(f"[KNOWN-DEVIATION] oracle.run_match(gb18030) 归属全失效（预期，"
                  f"match 管道输出实测为 UTF-8 字节）：{m_oracle!r}", flush=True)
        else:
            print(f"[NOTE] oracle.run_match 结果：{m_oracle!r}", flush=True)
    except Exception as e:  # oracle.run_match 签名/行为差异也不判失败（自写路径已对拍）
        print(f"[KNOWN-DEVIATION] oracle.run_match 调用异常（不作为失败）：{e}", flush=True)


def test2_variant() -> None:
    print("== ②变体生效（latFrictionK wire 名与静默忽略路径）==", flush=True)
    tmp = fresh_tmp()
    # 正确拼写 latFrictionK=16
    spec = next(s for s in sens.PARAMS if s.name == "latFrictionK")
    good = sens.write_variant(BASE, spec, 16.0, tmp / "good-latFrictionK.json")
    doc = json.loads(good.read_text(encoding="utf-8"))
    check(doc["vehicles"]["us"]["latFrictionK"] == 16.0
          and doc["vehicles"]["them"]["latFrictionK"] == 16.0,
          "②a latFrictionK=16 变体读回 us/them 同值 16",
          f"vehicles={doc['vehicles']!r}")
    # 基线 + 错拼对照
    base_p = oracle.write_scenario(BASE, {}, tmp / "t2-baseline.json")
    bad = tmp / "bad-lat_friction_k.json"
    base_doc = json.loads(Path(BASE).read_text(encoding="utf-8"))
    bad_doc = json.loads(json.dumps(base_doc))
    bad_doc["vehicles"] = {"us": {"lat_friction_k": 16.0}, "them": {"lat_friction_k": 16.0}}
    bad.write_text(json.dumps(bad_doc, ensure_ascii=False), encoding="utf-8")

    m_base = sens.run_match_utf8(DOTNET, DLL, base_p, SEED, 120.0)
    m_good = sens.run_match_utf8(DOTNET, DLL, good, SEED, 120.0)
    m_bad = sens.run_match_utf8(DOTNET, DLL, bad, SEED, 120.0)
    check(m_good != m_base, "②b latFrictionK=16 跑 s1 与基线行为不同（正确拼写生效）",
          f"base={m_base!r} good={m_good!r}")
    check(m_bad == m_base, "②c lat_friction_k=16（错拼）跑 s1 与基线逐位一致（静默忽略路径）",
          f"bad={m_bad!r}")


def test3_resume() -> None:
    print("== ③断点续跑 ==", flush=True)
    tmp = fresh_tmp()
    out_dir = tmp / "report"
    args = type("A", (), {
        "out_dir": str(out_dir), "variants_dir": None,
        "dotnet": DOTNET, "cli_dll": DLL, "base_scenario": BASE,
        "params": "STALL_SPEED", "seeds": "1,2,3,4", "report_only": False,
        "no_xref": True})()
    specs = [s for s in sens.PARAMS if s.name == "STALL_SPEED"]
    seeds = [1, 2, 3, 4]
    t0 = time.time()
    results = sens.run_scan(args, specs, seeds)
    # run_scan 不写报告（报告在 main），这里手动聚合一次验证链路
    agg = sens.aggregate(results, specs, seeds)
    check(len(results["runs"]) == (1 + 4) * 4 and agg["control_ok"] is None,
          "③a 子集扫描完成（基线+STALL_SPEED×4 水平×4 种子，子集模式不做对照门禁）",
          f"runs={len(results['runs'])} wall={time.time()-t0:.1f}s")
    full_copy = json.loads(json.dumps(results))

    # 删一半条目（保留每桶至少一半种子），模拟中断
    keep = {}
    for r in results["runs"]:
        key = (r["param"], r["value"])
        keep.setdefault(key, []).append(r)
    half = []
    for key, rs in keep.items():
        half.extend(rs[: len(rs) // 2])
    results["runs"] = half
    sens.atomic_write_json(out_dir / "sensitivity-results.json", results)
    n_before = len(half)

    # 重跑：断点续跑应只补缺失场次
    results2 = sens.run_scan(args, specs, seeds)
    keys_after = {sens.run_key(r["param"], r["value"], r["seed"]) for r in results2["runs"]}
    keys_full = {sens.run_key(r["param"], r["value"], r["seed"]) for r in full_copy["runs"]}
    check(keys_after == keys_full, "③b 重跑后补齐至完整键集",
          f"before={n_before} after={len(results2['runs'])} expected={len(keys_full)}")

    # 未删条目逐字节保持（重跑不改已有；wall 是每场实测耗时天然波动，不进确定性断言；
    # final_score 内存为 tuple、json 落盘读回为 list，比较前统一 json round-trip 归一化）
    def norm(r: dict) -> dict:
        return json.loads(json.dumps({k: v for k, v in r.items() if k != "wall"}))

    kept_same = all(norm(r) in [norm(x) for x in results2["runs"]] for r in half)
    check(kept_same, "③c 已有条目重跑后保持（param/value/seed/metrics 不变，未覆盖）",
          f"kept={len(half)}")

    # 抽查 3 条与全跑副本逐字段一致（确定性：同种子同场景 metrics 逐位复现）
    by_key_full = {sens.run_key(r["param"], r["value"], r["seed"]): r for r in full_copy["runs"]}
    by_key_after = {sens.run_key(r["param"], r["value"], r["seed"]): r for r in results2["runs"]}
    sample = sorted(keys_full)[:3]
    check(all(norm(by_key_full[k]) == norm(by_key_after[k]) for k in sample),
          "③d 抽查 3 条与全跑副本逐字段一致（metrics 逐位）", f"sample={sample}")


def main() -> int:
    test1_pariry()
    test2_variant()
    test3_resume()
    print("", flush=True)
    if FAILS:
        print(f"[SELFTEST FAILED] {len(FAILS)} 项失败：{FAILS}", flush=True)
        return 1
    print("[SELFTEST PASSED] 三项全过，允许进入全量扫描", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
