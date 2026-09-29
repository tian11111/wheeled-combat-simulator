# 技术设计：真车几何进 MuJoCo 物理

## 1. 几何管线（glb → 凸体 asset）

```
装配.glb (SolidWorks, 41 件)
  └─ tmp/mesh_extract.py 按件名筛选 + 坐标变换 + 导出 STL
       ├─ robot_rear_shovel.stl   ← 后铲.stp-1
       ├─ robot_chassis.stl       ← 底板.stp-1 + 上板.stp-1（合并为单一凸体）
       └─ (可选) robot_front_arm.stl ← 铲臂（2）×2
```

- **坐标变换**（已验证 det=+1 的旋转，不是镜像）：`bodyX = −modelZ`、`bodyY = −modelX`、`bodyZ = modelY`；原点取**轮轴平面**（模型 Y=0），使 body 局部原点与轮心共面，spawn 高度 = `WheelRadius`。
- **轮径更正（2026-09-28）**：本项目早期文档里的 `r=0.046` 是错误的（`inspect_glb.py` 的八角点法假膨胀）。实测：glb 无非单位缩放/四元数，四轮世界尺寸 = mesh 局部尺寸 = 直径 0.0650（r=0.0325）、宽 0.029，轮心左右 ±0.113/±0.116、前后 ±0.073/±0.077（轮距 0.229、轴距 0.150）。落地的 v2 生产实现按此值。
- 每件导出前做**顶点去重 + 凸包**（MuJoCo 自己也会算凸包，但预算是为了控制 `maxhullvert` 与文件体积）；后铲/底板各为独立 geom，保留两者之间的凹角。
- **合并底板+上板**为一个凸体：两板近似平行且间隙仅 0.03 m，分开做两个 geom 只会增加接触对而无行为收益。

## 2. MJCF 结构变化（v2）

```xml
<asset>
  <mesh name="chassis" file="robot_chassis.stl"/>
  <mesh name="rear_shovel" file="robot_rear_shovel.stl"/>
</asset>
...
<body name="robot_us" ...>
  <freejoint/>
  <geom name="robot_chassis_us"    type="mesh" mesh="chassis"     mass="..."/>
  <geom name="robot_shovel_us"     type="mesh" mesh="rear_shovel" mass="0.02"/>
  4× wheel body（type="cylinder" r=0.0325 half-length=0.0145，位置 x=+0.073/−0.077、y=±0.113/±0.116）
</body>
```

- 质量分配：body 总质量保持 `VehicleProfile.Mass`（其余分配同 v1 思路：轮 0.03 ×4、后铲 0.02、其余归车体）。
- 轮摩擦沿用 v1 的 `friction="1.5 0.02 0.002"`、`solref="0.02 1"`；车体 mesh geom 继承默认 `friction 0.85 / solref 0.008`。
- `maxhullvert` 显式设置（默认 200 足够，四轮车底盘不需要更多）。

## 3. 资产加载与身份

- **VFS**：新增 P/Invoke `mj_defaultVFS` / `mju_addBufferToVFS` / `mj_deleteVFS`；`MujocoNative.CreateModel` 改为"建 VFS → 挂入资产字节 → parse → compile → 释放 VFS"。XML 里只出现逻辑名（`robot_chassis.stl`），不含任何机器路径。
- **哈希**：`MujocoModel.Generate` 的返回值从 `(xml, sha256)` 扩展为 `(xml, assets, sha256)`；`sha256 = SHA256(xml ‖ 各资产字节按名字排序)`。这样"换网格不改哈希"的漏洞被关闭。
- **资产随构建产出**：`.stl` 以 `<None ... CopyToOutputDirectory="PreserveNewest">` 或 `<EmbeddedResource>` 进 `Sim.Mujoco.csproj`（与现有 `mujoco.dll` 的拷贝方式一致）；Godot 与 CLI 两侧都要能找到。

## 4. 场景与版本

- `PhysicsSpec` 增 `MujocoModelV2 = "wushu-mjcf-v2"`；`Validate` 接受 v1/v2。
- 新场景 `scenarios/wushu-ring-2026-mujoco-v2.json`：`modelVersion=v2` + `vehicles` 段写真车参数（Length 0.36 / Width 0.24 / Height 0.06? / WheelBase 0.15 / TrackWidth 0.23 / ... 以实体测量为准，且必须与 glb 中的几何一致）。
- v1 场景与 v1 生成路径**保持逐字节不变**（回归基线：v1 replay 仍 PASS）。

## 5. 关键风险与判定

| 风险 | 判定方式 | 兜底 |
| --- | --- | --- |
| 真轮 r=0.0325 爬不上 6 cm 台沿 | 实验：后铲/前臂接触台沿的 tick、爬升高度曲线 | 实测（r=0.0325 + 20° 倒角）：能爬上去但末态半悬、FSM on_stage 仅 1 帧；r≥0.039 才出现"后铲零接触" |
| 台沿 20° 倒角是为 6.5 cm 轮定的 | 同上 | 倒角角度/延伸长度是模型参数，改它要显式记录 |
| 接触对增多 → 训练吞吐下降 | v1/v2 单 tick 与 reset 耗时对比 | 限制 geom 数量（只 3 个车体件 + 4 轮），必要时提高 `solref` 阻尼 |
| 凸包化后轮廓与渲染模型不一致 | 视觉 QA 截图（Godot `--capture`） | 渲染层仍用 glb，物理用凸包，属于可接受近似，需在报告说明 |
| mesh 与场地的穿透（薄板类） | 接触诊断（penetration 深度） | 板件加最小厚度、必要时禁用板件的接触（`contype/conaffinity`） |

## 6. 与既有产物的关系

- `physicsModelSha256` 必变 → v1 replay 身份校验失败属预期（v1 场景保留即可校验 v1 replay）；RL checkpoint（11 维 obs 不变，但环境物理已变）**不可比**：报告里列出作废范围，后续重训重评。
- `SearchTurnCompensationTests` 的补偿系数 4.0 是为 v1 几何标定的；v2 下需要重新做候选对照实验（2/4/6 或新候选），否则 SEARCH 对准可能超时。这属于阶段 2 的必做项。
- `TrainingResetPerformanceTests` 的冷/热门在新几何下的 ratio 需实测。
