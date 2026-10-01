# 控制器协议（decide(obs) → {v, w}）

外部策略以**独立进程**运行，通过 **JSONL stdio** 与 `Sim.Cli` 或 Godot 桌面端通信。
协议与遗留桥完全兼容；两端共享 `src/Sim.Controller/ExternalControllerBridge` 的校验和故障语义。

## 每帧时序

1. 仿真每个 tick（0.05 s）向策略 **stdin 写一行观测 JSON**（`obs`）。
2. 策略向 **stdout 写一行动作 JSON**：`{"v": <m/s>, "w": <rad/s>, "requestId": <回显>}`。
3. 桥校验并回放到内核；非法/超时按**零动作**处理，绝不按部分动作处理。

**编码**：桥固定按 UTF-8 写 `obs` 行（不随宿主控制台代码页变化），策略进程必须按
UTF-8 读 stdin（如 `py -3.12 -X utf8 ...`）；动作行是 ASCII，任何编码下都等价。

## 观测 `obs`（camelCase）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `requestId` | number | 单调递增；动作须原样回显以对齐帧 |
| `tick` / `t` / `timer` | number | 步数 / 仿真秒 / 剩余比赛秒 |
| `role` | string | `"us"` 或 `"them"` |
| `scores` | object | `{us, them}` 当前比分 |
| `robot` | object | `{x,y,th,v,w,onPlatform,hang,state,action,vehicle}` |
| `opponent` | object | `{x,y,th,onPlatform,state}` |
| `sensors` | object | 遗留逻辑别名：`gF gB gL gR uL uR sFL sFR dLF dRF dLB dRB f r` |
| `rawSensors` | object | 该车 profile 的真实通道集合（id → 数值） |
| `sensorLayout` | object | 传感器布局定义 |
| `perception` | object | `{fieldGray, vision}` 感知实现元数据（保真度证据） |
| `objects` | object | `{buffs:[{x,y,onPlatform,out,lastTouch}], debuff:{...}}` |

`sensors` 灰度值 0–1000（走道 0、黑带约 300、台面白约 1000），红外约 0–1。

## 动作 `{v, w}`

- `v`（m/s）、`w`（rad/s）必须是**有限数值**；否则整行被丢弃并按零动作处理。
- 可选 `requestId`：回显观测的 id。缺失时按当前帧接受；**错配的 id（过晚/过早）一律丢弃**，
  不会应用到后续帧。
- 其他字段（如 `note`）被忽略。
- 内核随后按车辆 profile 的 `maxSpeed`/`maxTurnRate` 对称钳位（默认 1.5 m/s / 4.0 rad/s）。

## 故障策略（桥侧）

| 情况 | 处理 |
| --- | --- |
| 行非合法 JSON / 缺 `v` 或 `w` / 非有限值 | 丢弃，按零动作，计一次 fault |
| 截止时间内无响应 | 零动作，计一次 fault |
| 进程退出 / 写 stdin 失败 | 零动作，计一次 fault |

fault 计数在 `match`/`replay-record` 结果里输出，用于诊断策略稳定性。

## 批量运行（`batch`）中的进程生命周期

`batch` 子命令面向 AI agent 无头批量评测（不启动 Godot）。其中外部控制器
进程的模型是：

- **每场独立进程**：每场比赛（每个输入 seed）为其每个外部角色新建一个桥与
  子进程（stdin/stdout、request-id 队列、fault 计数全部属于该场），比赛结束、
  控制器启动失败、异常等任何路径都会在 `try/finally` 中 `Dispose`——杀死整个
  进程树并回收。**绝不跨场复用进程，也无多路复用协议。**
- **故障隔离**：某一场的控制器超时/坏行/退出只影响该场的 fault 计数与动作
  （零动作回退），不会串线到其他场次；结果按输入顺序输出为
  `sim-batch-result-v1` JSONL，每行的 `faults.{us,them}` 即该场该角色的 fault 数。
  控制器进程无法启动时该场变为 `failed` 行（`failure.kind=controller_start_failed`）。
- **协议不变**：批量路径与单场 `match` 走同一逐帧时序与校验规则（本文件所述
  全部语义逐位一致）；并行只改变同时运行的场次数，不改变单场帧序。

## 示例

最小策略骨架：

```python
import sys, json
for line in sys.stdin:
    obs = json.loads(line)
    v, w = 0.5, 0.0          # 你的决策
    print(json.dumps({"v": v, "w": w, "requestId": obs.get("requestId")}), flush=True)
```

可直接运行的完整示例见 [`../controllers/example_controller.py`](../controllers/example_controller.py)
（冲向最近增益块，无块时回中心），接入方式：

```bash
dotnet run --project src/Sim.Cli -- match --seed 42 \
  --controller-us "python controllers/example_controller.py"
```

## 移植真车控制器（MBri RobotController 示例）

把真车决策代码接入仿真时，控制器进程负责**三重转换**；官方示例
[`../controllers/mbri_adapter.py`](../controllers/mbri_adapter.py)（自测
`mbri_adapter_selftest.py`）是参照实现。**没有官方换算系数的字段必须显式
标定，缺配置时报错而非猜测。**

### 量纲/通道映射表（逐项标注可信边界）

| 仿真字段 | MBri 侧 | 换算 | 可信边界 |
| --- | --- | --- | --- |
| `sensors.gF/gB/gL/gR` | `gray_raw[front/rear/left/right]` | ×10（0–1000 → ADC 0–10000） | 接口量纲换算，**非已标定光学等效** |
| `rawSensors.uL/uR/r/f` | 数字红外 6 路（低有效 GPIO） | 阈值合成（默认 0.3），仅 oracle/calibrated 模式 | **仿真近似**，非真机传感器 |
| `rawSensors.dLB/dRB` | 模拟对角红外 ADC | ×(10000/1.2) | 接口量纲换算 |
| `objects.buffs/debuff` | `vision.detections`（type good/bad） | 包装 + `confidence=1.0` + `oracle:true` | **特权真值**，非 YOLO 置信度 |
| `vehicle.maxSpeed/maxTurnRate` | 动作限幅 | 直接 | 物理 v/w 上限 |
| 动作 `{v,w}` | `result[left/right]` | `v=(L+R)k/2`、`w=(R−L)k/b` | **k/b 必须真机标定**，未标定时动作恒零 |

### 轮速比例/轮距标定步骤模板（安全条件下）

1. 固定左右轮命令 `L=R=c`，测直线位移 `d` 与用时 `t` → `k = d/(c·t)`（m/s 每单位）。
2. 固定差速命令 `L=−c, R=c`，测原地旋转角 `θ` 与用时 `t` → `b = 2·c·k·t/θ`（m，复核用）。
3. 记录：命令、位移/角度/时间、拟合 `k/b`、车体编号、地面、日期；结果按车体/日期存档，
   **不写入 `fidelity.json`**。未标定的运行只能标 `estimated`，不得用于性能/保真结论。

### 时间注入与帧语义

车端状态机禁用墙钟：`now = obs.t`（仿真秒）。首帧、连续帧按 0.05 s 递增校验；
重复/跳帧/坏行计为协议故障并回零动作（与既有 fault 契约一致）。stdout 只写动作
JSONL；诊断与运行元数据（模式、k/b、标记）写 stderr 首行。

### Manual 语义（实测，oracle smoke seed 42）

- 开赛时引擎对**双方**发 `Arm`（外部角色也在内，事件可见）。
- 外部角色**首个动作到达后进入 Manual**：其内置 FSM 不再决策，**该角色后续
  FSM/登台事件不再产生**；裁判事件（ScoreClock/Drop/BlockScore）与对手 FSM 事件
  照常。实测：oracle smoke 1200 ticks → Arm×2（双方）、对手 Fsm/Mount 事件 14 条、
  我方此后 0 条 FSM 事件、裁判 ScoreClock×5 可见。
- 接车代码即"你的代码就是整个状态机"：发令/登台/搜索/得分全部由你的决策负责，
  仿真只提供物理、裁判计分与对手。

### 运行命令（三种口径）

```bash
# 协议 smoke: 不导入车代码, 恒零动作(纯协议/帧序检查)
dotnet exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --duration 60 \
  --controller-us "python controllers/mbri_adapter.py --mode smoke"

# oracle: 车端决策 + 仿真近似 IR + 真值视觉(显式标记), 动作恒零(无标定系数)
dotnet exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --duration 60 \
  --controller-us "python controllers/mbri_adapter.py --mode oracle --mbri-path D:/project/robocup/2026/MBri"

# calibrated: 显式 k/b 的运动映射(k/b 来自上方标定, 未标定不得用于性能结论)
dotnet exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --duration 60 \
  --controller-us "python controllers/mbri_adapter.py --mode calibrated --mbri-path D:/project/robocup/2026/MBri --k 0.00055 --track-width 0.18"
```

注意: 适配器经管道读取子进程输出时按系统 ANSI 代码页（中文 Windows 为 GB18030）
解码，UTF-8 解码会使 `[我方]/[对手]` 归属失效。

## 桌面 SCORE_BLOCK 展演（我方 RL 策略，非门禁）

桌面 F10 →「小车控制器」把**我方**设为“外部命令”，命令指向 RL 桥
（`TimeoutMs` 建议 ≥ 5000），对手保持内置 FSM，即可在桌面复现 CLI
`match --start-at score_block` 的交接语义。**桌面控制器子进程的工作目录是 `godot/`**
（实测：`--path godot` 把进程 CWD 带进项目目录），相对脚本路径写
`../tools/rl-bridge/rl_desktop_runner.py`；checkpoint 等数据路径由 runner 先按 CWD、
再按仓库根解析。相对路径写错时应用设置时的预检会当场失败并回退内置 FSM：

```text
py -3.12 -X utf8 ../tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>
```

- 发令后先由双方内置 FSM 预推进到我方 `SCORE_BLOCK` 并锁定目标块（共享缝
  `Sim.Hosting.ScoreBlockExhibition`，上限 4800 tick）；交接前我方不接收任何外部动作。
- 交接后每 tick 的 `obs` 追加 11 维 `rlObservation`（前 9 项 + 我方位置归一化 x/y，
  唯一投影在 C#；Python 只消费，禁止重算）；桥/超时/坏行语义与普通外部控制器完全一致。
- 预检残帧（`Observation{RequestId=1}`，无 `rlObservation`）必须零动作快速应答——
  桌面“预检”按钮与应用设置时的自动预检都走这条路径，预检不覆盖首帧模型加载时间。

装配与拒绝矩阵（`godot/src/ControllerWiring.cs`，纯逻辑，`Sim.Tests` 无头回归）：

| 情况 | 行为 |
| --- | --- |
| legacy 场景（`physics.backend` 未写）选我方 external | **拒绝**并回退内置 FSM（RL 训练线是 mujoco），设置仍保存 |
| 预检启动失败/坏应答（launch/protocol） | 拒绝并回退内置 FSM，控制台 + HUD 红色报错 |
| 预检应答超时（timeout） | 保留外部控制器，HUD 告警（可能只是首帧加载慢） |
| 预推进未进入 `SCORE_BLOCK` | 不外发动作、释放子进程，HUD 显示 `展演未交接` 原因 |
| 交接前进程退出 | 我方完整回退内置 FSM（尚未进入 Manual） |
| 交接后进程退出 | HUD 红色告警；内核 Manual 语义安全停车（`Fsm.cs` Manual 分支） |

纪律：展演恒为**非门禁证据**（不写回放、不晋升 `fidelity.json`、不触碰 v4 盲集），
seed 取场景文件 `scenario.Seed`，桌面实时驱动按墙钟推进、**不可位对位复现**。
