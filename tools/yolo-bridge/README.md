# yolo-bridge — 真车 YOLO 检测流 → 仿真活源桥（外部进程源）

把"真车（或任何服务）的 YOLO 检测流"作为**外部进程**喂给仿真的活源桥：

```
mbri_yolo_bridge.py (stdout JSONL) → ExternalProcessStreamSource (Sim.Core)
    → LiveVisionBridge → FSM classify → 整场比赛 + sidecar 证据包
```

图像**永不**进入契约（没有像素/帧缓冲字段）；契约只有每帧的检测元数据。

## 契约（子进程 stdout，一行一帧）

字段与 `Sim.Core.VisionStreamFrame` 一一对应（JSON 名对齐真车 MBri CSV 列）：

| 字段 | 说明 |
| --- | --- |
| `sequence` | 帧号（严格递增、会话内唯一） |
| `vision_timestamp_ms` | 采集主机 epoch 毫秒（SimT 0 = 会话首帧） |
| `vision_status` | `target` / `no_target` / `error` / `no_data_or_stale` |
| `vision_error` | 错误文本（可空） |
| `selected_target` | 选中检测的下标（`target` 帧可为空 = no_selection） |
| `detection_count` | 必须等于 `detections` 行数 |
| `frame_width` / `frame_height` | 帧尺寸（sidecar 证据包要求为正） |
| `fps` / `inference_ms` / `received_age_ms` | 服务自报的审计指标（桥的帧龄由 SimT 自算，不采信自报） |
| `arrival_sim_t` | （首帧 − 本帧）/1000 s，审计用 |
| `detections[]` | 每检测：`class_id`(0=good/1=bad)、`target_type`(good/bad)、`label`(buff/debuff)、`confidence`∈[0,1]、`bbox_x1..y2`、`center_x/y`、`offset_x/y`∈[-1,1] |
| `label` / `confidence` | 选中检测的副本（扁平消费者用） |

硬要求：

1. **stdout 只有 JSONL**，诊断全部走 stderr；
2. **逐帧 flush**（`python -u` 或每帧 `flush=True`）。整块缓冲会让多帧在同一时刻成簇
   到达，桥看到的就不是"实时流"而是"回放"，帧龄与 stale 率随之失真；
3. 帧按**时间戳升序**输出（sidecar 证据包要求单调非降；桥的 SimT 0 锚点取首个交付帧）；
4. 进程退出/流断裂不是异常：已缓冲的完整行继续交付，之后的 classify 由桥按
   `stale`/`no_frame` 记账（不炸引擎）。子进程不自己退出时由 CLI 回收整个进程树；
5. 每条坏行都会记入流故障（`vision live` 报告的 `process` 分区），绝不静默成帧。

## 模式

### `--stub <csv>`（仓库内可跑，不依赖权重/相机）

读真车 MBri hunt 方言 CSV，按**墙钟真实节奏**推帧（帧到达间隔 = 时间戳差）。默认
提前 `--lead-ms 200` 发出：补偿本进程启动/JIT/管道延迟，保证"时间戳 ≤ 锚点+SimT×1000
的帧在对应 classify 之前已经到达"——这是进程源等价确认成立的前提。CSV 归一化口径与
`vision import` 同语义：预热行丢弃、重收帧保留首次接收、每检测一行按 `detection_index`
聚合、`selected_target=1` 的那一行是选中检测；`class_id`/`target_type`/置信度/offset
违约在源头即失败（零 stdout 输出）。

```bash
python -u tools/yolo-bridge/mbri_yolo_bridge.py \
  --stub src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv

# 接到 CLI（外部流按墙钟到达 ⇒ 必须 --realtime 1x，否则引擎快跑会全 stale）
dotnet run --project src/Sim.Cli -- vision live \
  --process "python -u tools/yolo-bridge/mbri_yolo_bridge.py --stub src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv" \
  --realtime 1x --scenario scenarios/wushu-ring-2026.json \
  --out calibration/vision-live-process.json --force
```

自测：`py -3.12 tools/yolo-bridge/selftest.py`（归一化口径、JSONL 字段契约、
逐帧 flush 与节奏、坏输入零输出、真权重占位退出码）。

### `--model-dir <dir>`（真权重模式；仓库内**不实现**，退出码 3）

权重与原生扩展**不进仓库**，因此仓库内只保留接口与文档。用户环境接入点（素材在手）：

- 素材目录：`D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps/`
  （`README.txt`：YOLO26n 320 NCNN full INT8、JSON Lines 输出；`model/model.ncnn.param|bin`
  非本仓库资产）；
- 接入做法：加载 ncnn 模型 + 相机/视频帧 → 按本仓库契约字段输出 JSONL（逐帧 flush），
  把模型自身的 `status` / `timestamp_ms` / `target{type,confidence,center_x,center_y,bbox…}`
  映射为上面的 `vision_status` / `vision_timestamp_ms` / `detections[]`（good→buff、
  bad→debuff；`sequence` 会话内自增；`frame_width/height` 照输入分辨率）；
- 等价确认要求同一条：按墙钟推帧、`--realtime 1x` 跑。真推理进程不会自己退出，
  由 `vision live` 在场次结束时回收。

## 与 CSV 源（`--source`）的关系

| | `--source <csv>` | `--process "<命令>"` |
| --- | --- | --- |
| 释放时间轴 | SimT 缩放：到达 =（时间戳 − 首帧）/1000 s | 墙钟：到达 = stdout 读到该行的时刻 |
| 需要 `--realtime` | 否（快跑复现同一结果） | **是（1x）**：快跑会让窗内无新帧 ⇒ 全 stale |
| sidecar | 本场交付帧流 | 本场交付帧流（`session=yolo-bridge-process`） |
| 可复现性 | 同输入逐位一致 | 帧到达依赖时序 ⇒ 用 sidecar 做确定性复现 |

两条路都产出 `vision-replay-v1` 证据包（`frames.jsonl` + `import-report.json`），
可用既有 `vision evaluate --evidence <sidecar 目录>` 复跑。
