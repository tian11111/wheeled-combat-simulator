# 实施清单:MuJoCo 内置 FSM 倒车登台机动

1. [x] task.py 建任务,PRD 沿用外部种子 Goal;根因分析(design.md)。
2. [x] 阶段 A 物理修复:力上限 3.0、底盘离地 +2 cm、kv 0.25、AccelK 斜坡、轮子加宽软化、台沿 20° 倒角。
3. [x] 强化 `NativeMode_MountsTheSixCentimetreStageContinuously`(FullOn 断言)。
4. [x] 阶段 B:新增 `NativeMode_FsmMountsFromOfficialSpawn` —— 纯物理修复即达标,FSM 零改动。
5. [x] 传感器边界测试期望值改用上一提交帧位姿(采样时序契约)。
6. [x] 回归:全套 362/362;旧回放 5/6(rotated 为分支陈旧基线,先前已定位);p1=p4 一致;
       fresh 回放 + 真实 Godot parity PASS;32×5s p32 32/32。
7. [x] 文档:ARCHITECTURE.md 登台修复节;report.md(根因链+证据+SEARCH 新发现)。
