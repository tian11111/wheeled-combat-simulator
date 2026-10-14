"""Minimal self-test for oracle.parse_match_output (no simulator needed).

2026-10-14 口径修正后: 
- us_drops 只计 [fsm] 掉台转移行 (一次掉台恰好一条); [score] 计分行不另计。
- 事件行 t= 为真秒 (Sim.Core.Js.ToFixed 放大缺陷已修, 不再有 0.1s 单位口径)。
"""
import oracle

sample = """scenario: scenarios/x.json (id=wushu-ring-2026, seed=42)
[   7] t=  2.40 Mount            [我方] [fsm] 登台信号: 后向灰度 216>150 → climbed
[   9] t=  3.20 Mount            [我方] [fsm] 已上台 on_stage ✓ → SEARCH
[  19] t= 10.50 Drop             [我方] [score] 我方掉台, 对方 +1 (0:1)
[  20] t= 10.50 Drop             [我方] [fsm] 掉台! (方向 西) → RECOVER
[  24] t= 23.50 BlockScore       [对手] [score] 增益块被推下擂台! 对手 +3 (0:3)
[  25] t= 23.60 BlockScore       [我方] [score] 增益块被推下擂台! 我方 +3 (4:3)
[  31] t= 28.50 Drop             [对手] [score] 对手掉台, 我方 +1 (5:3)
[  32] t= 28.50 Drop             [对手] [fsm] 掉台! (方向 北) → RECOVER
seed=42 ticks=2400 score 我方 8 : 3 对手 done=比赛时间结束 faults(us/them)=0/0 penalties=0/0"""

m = oracle.parse_match_output(sample)

# 我方: BlockScore 1 次; 掉台只计 [fsm] 行 1 次 ([score] 行不另计)
assert m["us_block_scores"] == 1, m
assert m["us_drops"] == 1, m
# mount_t = "已上台" 行的真秒值 (t=3.20)
assert m["mount_t"] == 3.2, m
assert m["done_reason"] == "比赛时间结束", m
assert m["final_score"] == (8.0, 3.0), m

# 分流验证: 只计 [fsm] 行 —— 去掉 fsm 行后 us_drops 应为 0 (score 行不算)
score_only = "\n".join(l for l in sample.splitlines() if "[fsm] 掉台!" not in l)
assert oracle.parse_match_output(score_only)["us_drops"] == 0, "score 行不应计入 us_drops"

# 编码漂移容错 (2026-10-14): UTF-8 与 GBK 字节流都应完整还原 sample。
assert oracle._decode_stdout(sample.encode("utf-8")) == sample
assert oracle._decode_stdout(sample.encode("gb18030")) == sample

print("selftest OK:", m)
