"""Minimal self-test for oracle.parse_match_output (no simulator needed)."""
import oracle

sample = """scenario: scenarios/x.json (id=wushu-ring-2026, seed=42)
[   7] t=  24.00 Mount            [我方] [fsm] 登台信号: 后向灰度 216>150 → climbed
[   9] t=  32.00 Mount            [我方] [fsm] 已上台 on_stage ? → SEARCH
[  19] t= 105.00 Drop             [我方] [score] 我方掉台, 对方 +1 (0:1)
[  24] t= 235.00 BlockScore       [对手] [score] 增益块被推下擂台! 对手 +3 (0:3)
[  25] t= 236.00 BlockScore       [我方] [score] 增益块被推下擂台! 我方 +3 (4:3)
[  31] t= 285.00 Drop             [对手] [score] 对手掉台, 我方 +1 (5:3)
seed=42 ticks=2400 score 我方 8 : 3 对手 done=比赛时间结束 faults(us/them)=0/0 penalties=0/0"""
m = oracle.parse_match_output(sample)
assert m["us_block_scores"] == 1, m
assert m["us_drops"] == 1, m
assert m["mount_t"] == 32.0, m
assert m["done_reason"] == "比赛时间结束", m
assert m["final_score"] == (8.0, 3.0), m
print("selftest OK:", m)
