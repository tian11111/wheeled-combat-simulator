# 控制器协议（decide(obs) → {v, w}）

外部策略以**独立进程**运行，通过 **JSONL stdio** 与 `Sim.Cli` 通信。协议与遗留桥完全兼容。

## 每帧时序

1. 仿真每个 tick（0.05 s）向策略 **stdin 写一行观测 JSON**（`obs`）。
2. 策略向 **stdout 写一行动作 JSON**：`{"v": <m/s>, "w": <rad/s>, "requestId": <回显>}`。
3. 桥校验并回放到内核；非法/超时按**零动作**处理，绝不按部分动作处理。

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
