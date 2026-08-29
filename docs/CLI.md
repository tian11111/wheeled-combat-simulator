# Sim.Cli 命令参考

无头评测、固定种子批量评估、回放录制/校验。全部在仓库根目录运行。

## 公共选项

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `--seed N` / `--seeds a,b,c` | `42` | 确定性种子（可多个做批量评估） |
| `--scenario <path>` | 官方默认布局 | `scenarios/*.json` 场景文件 |
| `--duration <s>` | 120 | 覆盖比赛时长 |
| `--controller-us <cmd>` | 内置 FSM | 我方外部策略进程命令 |
| `--controller-them <cmd>` | 内置 FSM | 对手外部策略进程命令 |
| `--timeout-ms <ms>` | 100 | 策略单帧响应截止时间 |
| `--events` | 关 | 逐条打印事件日志（`batch` 不支持，其 stdout 固定为 JSONL） |
| `--out <path>` | — | `replay-record` 输出文件；`batch` 的结果文件（原子替换） |

## match — 无头比赛

```bash
dotnet run --project src/Sim.Cli -- match --seed 42
dotnet run --project src/Sim.Cli -- match --seeds 1,2,3,4,5 --events
dotnet run --project src/Sim.Cli -- match --seed 42 \
  --controller-us "python controllers/example_controller.py"
```

输出每种子一行（步数、比分、结束原因、fault、判罚），多种子时附胜负汇总。
未指定 `--controller-*` 的角色使用内置 FSM。

## batch — AI agent 无头并行批量仿真 (JSONL)

面向 AI agent / 脚本的批量入口：**不启动 Godot、不依赖任何桌面组件**，多个独立
种子并行运行，完成后按输入顺序一次性输出机器可读结果。stdout 只有 JSONL，
人类诊断与错误全部走 stderr；不支持 `--events`（需要事件文本用 `match --events`）。

```bash
dotnet run --project src/Sim.Cli -- batch --seeds 1,2,3,4 --parallelism 4 --duration 3

# 接外部控制器 + 结果落盘 (AI agent 一条命令即用):
dotnet run --project src/Sim.Cli -- batch --seeds 1,2,3,4 --parallelism 4 \
  --controller-us "python controllers/example_controller.py" --out artifacts/batch.jsonl
```

选项（与 `match` 相同含义的不再重复）：

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `--seed N` / `--seeds a,b,c` | `42` | 输入种子列表（1..4096 个；**允许重复**，按 `inputIndex` 区分） |
| `--parallelism <k>` | `min(CPU 核数, 8)` | 同时运行的场次数，整数 `1..32` |
| `--out <path>` | — | 结果文件；父目录按需创建，**同目录临时文件 + 原子替换**，缺省写 stdout |

预检在**任何 worker/controller 启动之前**完成（seeds、scenario 读取 + `Validate()`、
duration/timeout/parallelism 数值、`--out` 可写性）；任何非法输入返回 `2`、只写
stderr、不产出 JSONL 也不留临时文件。

### 输出 schema（`sim-batch-result-v1`，每输入种子一行，按输入顺序）

```json
{"schemaVersion":"sim-batch-result-v1","inputIndex":0,"seed":1,"status":"completed","scenarioId":"wushu-ring-2026","ticks":60,"scores":{"us":0,"them":0},"penalties":{"us":0,"them":0},"doneReason":"比赛时间结束","faults":{"us":0,"them":0},"eventCount":20,"eventFingerprint":"<sha256>","resultFingerprint":"<sha256>"}
```

- `inputIndex`：输入列表中的零基位置（并行乱序完成也不影响输出顺序）。
- `status`：`completed` 或 `failed`。失败行保留 `inputIndex`、`seed`、`status`、
  `faults` 并填充 `failure.kind`（如 `controller_start_failed` / `match_error` /
  `batch_scheduler`）与 `failure.message`；ticks/scores/penalties/doneReason/指纹
  全为 null，**不伪造部分成功**。
- `eventFingerprint`：按事件顺序拼接 `seq|tick|type|cls|message` 行（每行含尾部
  换行）后的 SHA-256；`resultFingerprint` 再纳入 seed/ticks/比分/判罚/结束原因。
  均为 UTF-8、InvariantCulture、小写 hex；**不含时间戳、路径、线程或调度信息**，
  同输入重复运行逐位一致。
- 序列化对非 ASCII 转义（`\uXXXX`），解析后即中文原文。

### 控制器生命周期与隔离

外部控制器**每场独立启动/回收**（`PythonBridge` 进程、stdout reader、fault 计数
均属于该场，绝不跨场复用）；超时、坏 JSONL、requestId 错配、死进程沿用既有
zero-action 回退语义，fault 计入该场该角色的结果。单场异常转为该场的 `failed`
行，不影响其他场次，也不会静默丢 seed。

### 退出码

- `0` 全部场次 completed
- `1` 至少一条 `failed` 行或输出写失败（仍输出完整 N 行 JSONL）
- `2` 参数/场景预检失败（零 JSONL、零部分文件）

## replay-record — 录制回放

```bash
dotnet run --project src/Sim.Cli -- replay-record --seed 42 --out replays/seed-42.json
dotnet run --project src/Sim.Cli -- replay-record --seed 42 \
  --controller-us "python controllers/example_controller.py" --out replays/seed-42-pyus.json
```

生成 `sim-replay-v1` 文件：场景 + 每步被接受的真实动作流 + 最终比分与事件指纹。
只记录**实际被接受**的动作（request-id 匹配与钳位之后）；超时/坏行不落盘。

## replay-check — 校验回放

```bash
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
```

用文件里的场景重建内核，逐步重放记录的动作流，逐位比对**比分与事件序列**。
`PASS`（exit 0）= 完全复现；`FAIL`（exit 1）打印首个分叉事件。这是确定性的持续证据。

## calibrate — 离线真机标定（遥测 → 参数报告 → 场景/保真度）

```bash
dotnet run --project src/Sim.Cli -- calibrate --input telemetry/data/robot-01.json \
  --out calibration/robot-01-report.json \
  --emit-scenario scenarios/calibrated-robot-01.json   # 可选：直接生成可加载场景
# 复核报告后才登记保真度晋升（只晋升留出达标且 source=real 的子系统）：
dotnet run --project src/Sim.Cli -- calibrate --input telemetry/data/robot-01.json \
  --out calibration/robot-01-report.json --force --update-fidelity
```

消费 `telemetry-v1` 契约（见 `telemetry/README.md`）：入口一次性校验 SI 单位、时间戳、
kind 必填字段；**任何校验失败都不会产出报告或 patch**。拟合算法数值等价迁移自遗留
`sim_calibrate.js`（指数衰减/块摩擦一维搜索/恢复系数最小二乘/堵转阈值分类）；
每个参数分列**拟合集与留出集**指标，晋升要求留出达标且数据为真机
（合成自测数据永不晋升）。登台门控只验证不拟合：误判率/覆盖不足时如实报告模型不足。
报告带输入 SHA-256 与内容指纹（排除生成时间，重跑逐字节一致）；结果应用为新场景
文件 + `--update-fidelity` 显式登记，官方场景与旧回放逐位不变。

## sensor-calibration import — MBri 传感器标定证据导入（离线，另一条标定线）

与 `calibrate`（物理遥测 telemetry-v1）不同，本命令导入 **MBri 真车传感器判定模型**
（灰度四路、前头双路 ADC、铲下双路），产出 `sensor-calibration-v1` 证据报告。
采集规范与选择规则见 `telemetry/README.md` 的传感器章节；仓库不复制原始数据。

```bash
dotnet run --project src/Sim.Cli -- sensor-calibration import   --data-dir "D:/project/robocup/MBri/data" --manifest selection.json   --out calibration/sensor-report.json [--config config.py] [--force]
```

要点：
- 只消费选择清单点名的文件（表头精确匹配），其余文件**全部列入 ignored**，
  被拒文件带原因；绝不按列名猜测导入 187 个文件。
- 对导入模型回放对应原始 CSV：灰度 zone/white 迟滞、前差带 left/forward/right、
  铲子悬空/收回；输出就绪/无效样本数、决策分布、误判与失败文件、运行时候选标志。
- stored 模型 / 全量重算 / config.py 快照三者差异全部进 comparison 表，
  **只报告不合并**；任何不一致把状态压回 `evidence_only`/`rejected`。
- 灰度数据无坐标 → 报告恒含 `coordinateData=false` 与不能构造 GrayGridMap 的限制。
- 不触碰 FieldModel/SensorSampler/FSM/官方场景/fidelity.json/回放；纯离线产物。
- 退出码：0 报告已写（允许 evidence_only）；1 校验/IO 错误（零输出）；2 用法。

## vision import / vision evaluate — 真实视觉回放证据（vision-replay-v1，evidence_only）

把 MBri 真车 YOLO 视觉 CSV 规范化为**哈希锁定的回放证据包**，并注入
`MatchEngine(Scenario, IVisionAdapter)` 做两层离线评估：链路质量（无真值即可算）
+ 策略消费（证明 视觉→FSM 数据流）。与 sensor-calibration / telemetry **分线**：
新 schema、新命令、互不扩用。

```bash
# 1) 导入: 选择清单点名 + SHA-256 校验; 方言按表头列集精确匹配 (完整 hunt 方言可导入)
dotnet run --project src/Sim.Cli -- vision import \
  --manifest src/Sim.Tests/fixtures/mbri-vision-mini/selection.manifest.json \
  --data-dir "D:/project/robocup/MBri/data" \
  --evidence-out vision/evidence-mini \
  --out calibration/vision-import.json [--force]

# 2) 评估: 链路质量 + 注入引擎整场策略消费回放 (--json 输出全量报告)
dotnet run --project src/Sim.Cli -- vision evaluate \
  --evidence vision/evidence-mini \
  --scenario scenarios/wushu-ring-2026.json \
  --out calibration/vision-eval.json [--max-age-ms 500] [--session <file>] [--json] [--force]
```

要点：
- 清单必须**显式**给出 `good→buff / bad→debuff` 类别映射与帧尺寸；禁止从
  `good_*`/`bad_*` 文件名推断真值；`label` 列是实验名，报告恒 `groundTruth=false`。
- 校验矩阵：sequence 严格递增且唯一（重收帧聚合进首次接收）、时间戳单调非降、
  数值有限、帧尺寸与清单一致、状态枚举、类别映射一致性、置信度 [0,1]、bbox 在帧内、
  offset ∈ [-1,1]、同接收组 selected_target 至多一个。任一违规 → 非零退出、**零产出**。
- `main_*` 简化方言（无逐检测明细）与未知表头 → 列入 `rejectedFiles` 并给出缺列清单，
  不静默降级；数据根（`--data-dir`，缺省为清单所在目录）下未被点名的 CSV 一律列入
  `ignoredFiles`。
- `evaluate` 输出：有效/过期/错误率、sequence 缺口直方图、FPS/推理延迟分布、目标保持、
  选中抖动、首次有效检测延迟；逐帧被消费/跳过原因、FSM 标准化检测、状态转移与
  `policyFingerprint`（同证据同场景重放逐位一致）。
- `--session` 未指定时回放**清单第一个文件**（按文件名序），多 session 证据请显式点名；
  链路质量层始终统计整个证据包的全部 session。时间映射固定 SimT 0 = 该 session 首帧，
  证据时长短于比赛时长时，之后的 classify 调用按过期（`stale`）计入 unknown 并如实报告。
- 回放路径经 `VisionReplayAdapter` 提供：不读模拟器世界真值、不消费共享随机流；
  回放头写入 `visionMode="visionReplay"` 与加性 `visionEvidenceId/Sha256`。
- **保真度诚实性**：回放的是模型自身输出，只证明链路与策略消费，不证明识别准确率；
  结论恒 `vision=random_stub (evidence_only)`，Phase A 不触碰 `fidelity.json`，
  报告内含 Phase B 补采/补标清单。
- 退出码：0 报告已写（evidence_only）；1 校验/IO 错误（零输出）；2 用法。

## 退出码

- `0` 成功 / 回放一致
- `1` 运行错误或回放分叉
- `2` 参数错误
