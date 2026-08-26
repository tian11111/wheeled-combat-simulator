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
