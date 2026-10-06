# AI / 外部程序接入指南（Agent Guide）

本文件写给要把**用户程序接进模拟器**的 AI agent（或人）：按任务选路径、复制可跑命令、
避开已知坑。协议逐字段语义见 [CONTROLLER_PROTOCOL.md](CONTROLLER_PROTOCOL.md)，本文件只讲
"怎么做"。

## 0. 先选路径

| 你想让程序做什么 | 走哪条路 |
| --- | --- |
| 当**比赛控制器**：每 tick 拿观测、输出速度指令（最常用） | §1 外部控制器 |
| **批量评测/对比调参**：多种子无头并行，不启动 Godot | §2 batch |
| **训练 RL 策略**（PPO） | §3 rl-env（仅 MuJoCo 场景） |
| 接**真车决策代码**（MBri RobotController） | [CONTROLLER_PROTOCOL.md](CONTROLLER_PROTOCOL.md) 的"移植真车控制器"章节（三重转换 + 标定模板） |
| 在**桌面**上人工验收 | §5 桌面接线 |

## 1. 外部控制器（最短路径）

**完整契约是 5 行**：每 tick（0.05 s 仿真时）从 stdin 读一行观测 JSON，向 stdout 写一行
动作 JSON 并 **flush**：

```python
import sys, json
for line in sys.stdin:
    obs = json.loads(line)
    v, w = 0.5, 0.0                      # 你的决策: m/s 与 rad/s
    print(json.dumps({"v": v, "w": w, "requestId": obs.get("requestId")}), flush=True)
```

直接跑（CLI 任意场景可用，legacy 默认场景即可）：

```bash
dotnet run --project src/Sim.Cli -- match --seed 42 \
  --controller-us "python controllers/example_controller.py"
```

`controllers/example_controller.py` 是完整可跑的参照实现（冲向最近增益块）。

**观测里有什么**（camelCase，逐字段表见协议文档）：
`robot{x,y,th,v,w,onPlatform,state,vehicle}` / `opponent{...}` /
`objects.buffs[{x,y,out,...}]`、`objects.debuff`（= 第一个减益块）与 `objects.debuffs[]`
（全部减益块，**真值坐标**）/
`sensors{gF,gB,gL,gR,dLF,dRF,f,...}`（灰度 0–1000、红外 0–1）/
`scores` / `tick` / `t` / `timer`。

**动作规则**：`v`/`w` 必须是有限数值（非法整行丢弃、按零动作计 fault）；`requestId`
原样回显（错配帧一律丢弃）；内核按车辆 profile 钳位（默认 1.5 m/s / 4.0 rad/s）。

**Manual 语义（重要）**：开赛时引擎对双方发 `Arm`；你的首个动作到达后该角色进入
Manual——**它的内置 FSM 不再决策**，发令/登台/搜索/得分全由你的代码负责，仿真只提供
物理、裁判计分与对手。

## 2. 已知坑（每个都真实发生过）

| 坑 | 后果与对策 |
| --- | --- |
| stdin 不是 UTF-8（Windows 控制台代码页） | 观测行解析失败。策略进程用 `py -3.12 -X utf8 ...` 启动 |
| 写动作后没 `flush=True` | 桥等到超时 → 零动作 + fault 刷屏 |
| 不回显 `requestId` 或回显错帧 | 帧被丢弃，不应用到后续帧 |
| 冷启动超过 `--timeout-ms`（默认 100） | 冷启动全 fault。重模型（如 RL checkpoint）给 **`--timeout-ms 5000`** |
| 决策函数抛异常 | 进程崩 = 之后每 tick 都 fault。**捕获一切异常回零动作**（参照 example_controller.py 的 try/except） |
| Windows 下 grep CLI 输出无结果 | CLI 输出是 GBK：先 `iconv -f GBK -t UTF-8 -c` 再 grep |
| 以为对手/登台还归 FSM 管 | 该角色进 Manual 后全部归你的代码（见上 Manual 语义） |

## 3. 批量评测（多种子无头）

```bash
dotnet run --project src/Sim.Cli -- batch --seeds 1,2,3,4 --parallelism 4 --duration 3
```

每场为每个外部角色**新建独立进程**（绝不跨场复用），fault 按场隔离；stdout 按输入顺序
每场一行 `sim-batch-result-v1` JSON（含 `faults.{us,them}`）。完整旗标见
[CLI.md](CLI.md) 的 batch 章节。固定证据：`replay-record` 录制 → `replay-check` 逐位校验。

## 4. RL 训练路径（仅 MuJoCo 场景）

```bash
py -3.12 -X utf8 controllers/score_block_rl/train.py --steps 500000 --n-envs 4 \
  --scenario scenarios/wushu-ring-2026-mujoco.json \
  --dotnet <dotnet.exe 全路径> --out <输出目录(须不存在或为空)>
```

- 环境侧是 `rl-env` JSONL 持久进程；单环境 ~300-470 步/s，500k 步一轮 **约 20-30 分钟**
  （每步跨进程通信是瓶颈，`--n-envs 4` 是吞吐 workaround；随机块场景必须开并行）。
- **诚实预期**：当前 RL 整体不敌内置 FSM（见 README"已知局限"）；checkpoint 与物理参数
  强耦合，物理一变全部作废需重训。
- 评测/选模/盲验契约见 [controllers/score_block_rl/README.md](../controllers/score_block_rl/README.md)。

## 5. 桌面接线

F10 设置页"小车控制器"区为双方绑定控制器（内置 FSM / 内置 MBri / 外部命令），下一场或
F5 重置后生效。注意：**桌面展演的外部控制器只在 MuJoCo 场景启用**（legacy 场景明确拒绝
并回退内置 FSM）；外部展演是墙钟实时驱动，属不可位对位复现的展演，不作为训练/门禁证据。

## 6. 深读

- [控制器协议逐字段语义与故障策略](CONTROLLER_PROTOCOL.md)
- [Sim.Cli 命令参考（match/batch/replay/rl-env）](CLI.md)
- [架构与确定性契约](ARCHITECTURE.md)
- 示例：[example_controller.py](../controllers/example_controller.py)（通用）、
  [mbri_adapter.py](../controllers/mbri_adapter.py)（真车代码移植）
