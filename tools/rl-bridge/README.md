# rl-bridge — SCORE_BLOCK RL 策略 → 仿真外部控制器（JSONL stdio）

把训练好的 SCORE_BLOCK PPO 策略接成仿真的**外部控制器进程**：桥每 tick 往进程
stdin 写一行观测 JSON，进程回一行 `{v, w, requestId}`。协议、超时与故障语义由
`src/Sim.Controller/ExternalControllerBridge.cs:40-103` 实现（CLI 与 Godot 共用），
本目录只提供 Python 端：

| 文件 | 作用 |
| --- | --- |
| `rl_desktop_runner.py` | 控制器进程：观测 JSONL → SB3 `predict(deterministic=True)` → 动作 JSONL |
| `selftest.py` | 自测：协议契约 / 逐帧 flush / 维度门 / 确定性 / stub / 跨语言 11 维钉桩 |
| `README.md` | 本文件（接入点、契约、展演语义） |

## 接入点

```bash
# CLI 无头（批 1 的 --start-at score_block 展演交接到位后才会真正注入 rlObservation）
dotnet exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --events \
  --timeout-ms 5000 \
  --controller-us "py -3.12 -X utf8 tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>"

# 桌面：F10 → 控制器 → us = external（them = builtin），Arm 前预检。
#   注意子进程 CWD 是 godot/：相对脚本路径要写成 ../tools/rl-bridge/...
godot --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json

# 协议链路自测（不需要 torch / 权重）
py -3.12 tools/rl-bridge/rl_desktop_runner.py --stub
```

进程生命周期沿用既有契约：**每角色一进程、一场一 Dispose**（`MatchRunner.cs:50-57,100-104`、
`DesktopLiveDriver.cs:176-177,220-230`）。本进程**不自己退出**：stdin EOF / Ctrl-C /
stdout 管道断裂才收尾，且退出码 0。启动失败（模型缺失/维度不符）则**启动即非零退出**，
桥记 startupFault / 预检失败——这是有意的"早失败"，不是崩溃。

> 冷启动实测（2026-10-01，本机）：真实 checkpoint 完整加载约 1–2 s（含 torch import）。
> 桥的 `--timeout-ms` 是**单帧**截止时间，首帧会撞上加载窗口，建议 ≥ 5000 ms 或
> 先让桥在预推进期间启动（design 决策②）。预检只证明"进程起得来 + 残帧能回"，
> **不覆盖**首帧模型加载时间。

## 契约（逐帧一比一）

**stdin（观测）** —— 优先 `rlObservation`，其次 rl-env 响应形态：

```json
{"requestId": 12, "tick": 281, "robot": {...}, "objects": {...}, "rlObservation": [11 个数]}
{"type": "reset", "obs": [11 个数], "info": {...}}          // rl-env 响应兼容（联调用）
```

* `rlObservation` 是 C# 展演 runner/driver 注入的加性字段（design 决策③），
  **只在 SCORE_BLOCK 交接后填充**；普通 match / rl-env 不带。
* 两者都没有（预推进入口前的任何帧、`ControllerPreflight` 的残帧
  `Observation{RequestId=1}`，`godot/src/ControllerPreflight.cs:26-47`）
  → **立即零动作、不计 fault**，绝不试图从 `robot`/`objects` 重算。
* 载体存在但维度/有限性不符 → 零动作 + fault；进程继续服务（坏行不炸流）。

**stdout（动作）** —— 每个输入行**恰一行**，先写后 flush：

```json
{"v": 1.0, "w": -0.98, "requestId": 12}
```

`requestId` 原样回显（数字/字符串）；缺失或非标量时不回显（桥按当前帧接受）。
stdout **只有**动作 JSONL；诊断、运行身份、fault 计数一律 stderr。
响应数与输入行数恒等是硬契约：`ExternalControllerBridge.Decide`
（`ExternalControllerBridge.cs:62-103`）逐帧同步读取，缺行会让后续帧读到过期动作、
多行会占缓冲。

## 与训练侧契约的关系（唯一实现，不重算）

```
RlEnvCommand.cs:241-253 预推进 → :406-429 目标锁定 → :481-512 + :514-529 11 维投影
        （批 1 起由 Sim.Hosting.ScoreBlockExhibition 唯一承载，rl-env 委托）
                    │  展演 runner/driver 注入 obs.rlObservation
                    ▼
rl_desktop_runner.py ── import ──→ controllers/score_block_rl/gym_env.py
   解析/11 维有限校验（:123-130）· 动作映射 v=clip(a0,-1,1)、w=clip(a1,-1,1)*2.0（:162-168）
                    │  PPO.load + predict(deterministic=True)   （evaluate.py:141 同口径）
                    ▼
            {"v","w","requestId"}
```

* **11 值只由 C# 构造**（`RlEnvCommand.cs:25-26` `BaseObservationSize=9`/`ObservationSize=11`）。
  Python 侧重算 `bx/by`、`relForward/relLeft`、归一化 x/y 都是被禁止的第二实现
  （`.trellis/spec/sim/index.md:50-51` 同契约）。
* Python 侧解析与动作映射**直接调用** `controllers/score_block_rl/gym_env.py`
  的既有实现（`ScoreBlockEnv._observation` / `ScoreBlockEnv.step`），不复制公式；
  checkpoint 发现与哈希复用 `train_artifacts.py`。
* **维度门**：`observation_space.shape != (11,)` ⇒ 退出码 3、**零 stdout**、stderr 给出
  "Models trained with the previous 9-value observation must be retrained"
  （`evaluate.py:70,387-393` 同话术）。绝不静默加载旧 9 维模型。
* **确定性**：`predict(deterministic=True)`、CPU 单线程、进程内无 `random`/`np.random`；
  同输入两次运行 stdout 逐字节一致（selftest 钉住）。

## checkpoint（`--checkpoint`）

SB3 产物是 **zip 归档**，`policy.pth` / `policy.optimizer.pth` /
`pytorch_variables.pth` 是 zip 内成员；仓库内**没有**裸 `policy.pth`。传裸 `.pth`
会显式报错（退出码 2），不会含糊成"文件缺失"。

`--checkpoint` 接受：

1. zip 文件（任意 SB3 归档）；
2. 运行目录 → 依次尝试 `<dir>/ppo_score_block.zip` → `<dir>/checkpoints/rl_model_*_steps.zip`
   中**训练步数最大**者（选择结果打印在 stderr，不静默）→ 目录内唯一 zip。

相对路径先按进程 cwd 解析，找不到再按**仓库根**解析一次（命中时 stderr 显式写明来源），
避免桌面以 `godot/` 为 cwd 启动时"shell 里能跑、Godot 里找不到模型"。

现成产物（`research/facts.md` §3 实测在位）：

```
# 五 seed 最终模型（501,760 transitions，run-config status=completed）
.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-<20260927..20261001>/ppo_score_block.zip
# 同 seed 的 CheckpointCallback 快照（51,200 步一档，末点 460800）
.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-<seed>/checkpoints/rl_model_<steps>_steps.zip
# 展演推荐候选（开发集 50 候选中目标得分第一：9 分 / 0 掉台）
.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip
# 同构副本：suite-02/03 与 score-block-v4-independent-throughput-20260927-rerun/suite-01|02
```

运行身份（`run-config.json` 的 `train_seed`/`actual_global_transitions`/`model_zip_sha256`）
与 checkpoint 的 SHA-256 会打印在 stderr 首行；固定复现按 `policy.pth` 三件套内容哈希
判断，不用整包 ZIP 哈希（`.trellis/spec/sim/index.md:70-72`）。

## 展演语义声明（**非门禁证据**）

桌面对抗是本轮任务的**表演赛**：验证"策略能接进桌面外部控制器并在 SCORE_BLOCK 后
驱动我方"，**不是**策略质量或门禁证据。

* 展演产物一律标 `exhibition=true` / `gate_evidence_eligible=false`；不得计入
  `controllers/score_block_rl/evaluate.py` 的任何 gate（其门槛只看真实裁判事件）。
* 不写 replay、不晋升 `fidelity.json`；桌面实时驱动按墙钟，声明为**不可位对位复现**。
* 不改 `controllers/score_block_rl/splits.py`、不创建/消费
  `.sim_runs/score-block-final-holdout-v4-run.json`（盲集纪律，
  `.trellis/spec/sim/rl-split-contract.md:1-6`）；**不要**为展演跑 `evaluate.py`。
* seed 取场景文件的 `seed`（`scenarios/wushu-ring-2026-mujoco.json` = 42），
  runner 不引入任何 RNG。
* 性能/得分结论只能来自训练侧评测管线；展演里"看起来在推块"不算证据。

## 退出码与故障矩阵

| 退出码 | 含义 |
| --- | --- |
| `0` | stdin EOF / Ctrl-C / stdout 管道断裂（正常收尾） |
| `2` | 命令行或 checkpoint 路径错误（`--checkpoint` 缺失/不存在/非 zip；零 stdout） |
| `3` | torch/SB3 不可用、加载失败、观测/动作维度不符（零 stdout，原因在 stderr） |

| 情况 | 行为 |
| --- | --- |
| 模型缺失/加载失败/维度不符 | 启动即退出 3，零 stdout，stderr 指名路径与重训指引 |
| 交接前/预检残帧（无 `rlObservation`） | 零动作立即应答，不计 fault、不崩溃、不超时 |
| `rlObservation` 维度/有限性不符 | 零动作 + fault，进程继续服务 |
| 坏 JSON 行 / 空行 | 零动作 + fault，进程继续服务 |
| 策略输出非法（形状/非有限） | 零动作 + fault（`gym_env` 的既有校验口径） |
| stdout 管道断裂（桥被杀/消费者退出） | 收尾并退出 0。注意 Windows 上是 `OSError [Errno 22]`（EINVAL）而非 `BrokenPipeError`，只捕后者会空转刷故障帧——自测有真实断管道回归腿 |
| stdin 持续空闲 | 进程阻塞等待，绝不自行超时退出 |
| stderr 混入日志 | 不会被送到 stdout（动作行只由 `_write_line` 产出） |

已知边界：Ctrl-C 收尾走 `KeyboardInterrupt` 处理器（有实现、无逐步验证）；在没有交互控制台的
自动化环境里，控制台控制事件会被 OS 直接终止进程（实测退出码 `0xC000013A`，与 runner 无关——
同一环境下阻塞在 stdin 的裸 Python 进程行为一致）。桥的常规收尾路径是 `Dispose` 杀进程树，
不依赖 Ctrl-C。

## 自测

```bash
py -3.12 tools/rl-bridge/selftest.py     # 退出码 0 = 全绿
```

覆盖：CLI 契约、解析/映射单元（与 `gym_env` 同一实现）、stub 协议流（残帧、UTF-8、
坏行、维度不符、逐行一比一、**stdin 未关也能读到动作 ⇒ 逐帧 flush**）、stdout 断管道
收尾（exit 0）、确定性（同输入逐字节同输出）、真模式（现场生成微型 11 维 PPO 验证
加载/动作一致/确定性、9 维模型拒载零 stdout、路径错误），以及可选腿：用真实 `rl-env`
的 11 维观测钉跨语言契约（`src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll` 不存在时 SKIP）。
不依赖真权重。
