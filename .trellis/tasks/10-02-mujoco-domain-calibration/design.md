# Design：MuJoCo 域校准

## 1. 边界

- **只改 `Sim.Mujoco`**（模型生成 `MujocoModel` / 驱动 `MujocoPhysicsBackend`）。必要时可改 `Sim.Protocol` 车辆档案的**既有字段值**（如 `MaxSpeed`），但不新增字段。
- **不得**为了"看起来能跑"改 `Sim.Core`（裁判/计分/确定性契约）或向场景 JSON 加字段。
- 注入点沿用既有先例：`MotorDriveOptions` 是"内部构造器注入、不进协议"，本轮的 `WheelContactOptions` 同例。

## 2. 现状与数据流

```
Scenario(physics.backend=mujoco, modelVersion=v1|v2)
  → MujocoPhysicsBackend ctor (inPlaceTurnCompensation, MotorDriveOptions, WheelContactOptions)
    → MujocoModel.Generate(context, assets, wheelContact)
      → Robot()/RobotV2() → WheelBodies(..., wheelContact)   // 轮 geom: condim?/friction/solref
  → per-tick: SetControls(duty = clamp((v ± w·halfTrack)/MaxSpeed), ctrl = duty·ω_noload)
  → MuJoCo 25 substeps × 0.002 s
```

## 3. 已确立的机理（本轮实测）

1. **原地转向的本质**：差速原地转 = 四轮反向滚动；接触点必须**横向擦滑**（scrub）。检查点：若车体按命令 2.0 rad/s 转，v2 轮铰链应为 ±(2.0×0.1145)/0.0325 ≈ **±3.5 rad/s**；实测 **±9.6 rad/s** ⇒ 轮在空转打滑。
2. **限制器不是地面摩擦**：质量 ×10 ⇒ 偏航率 ÷10（摩擦受限应对质量不敏感）。⇒ 限制形如「**与质量无关的力矩 ÷ 惯量**」，即**驱动侧（占空比/力矩分配）**。
3. **v1 是占空比受限**：`inPlaceTurnCompensation` 4→10 使 0.236→0.989 rad/s（4.2×），×10 起 duty 饱和于 ±ω_noload。
4. **v2 已饱和但只有 0.274 rad/s**：它的 `MaxSpeed=0.408` 让 duty 在 ×4 就打到 ±1。⇒ v2 的 0.274 是**另一个纯物理限制**，补偿动不了它，必须靠接触力读数定位（当前封装只有接触对、无法向/切向力读数）。
5. 已排除：滑动摩擦降档（更差且破坏登台）、condim 3/4/6、滚动摩擦 0.002→1.0。

## 4. 关键权衡

| 权衡 | 内容 | 处置 |
|---|---|---|
| 摩擦：登台抓地 vs 原地擦滑 | 摩擦同时是登台（爬 6 cm 台阶）的必需与原地转的阻力；单变量降摩擦实测使登台失败 | 不把摩擦当主旋钮；若最终仍需要，必须**同时**给出登台仍达标的证据 |
| 模型身份 | 改 MJCF ⇒ `ModelSha256` 变 ⇒ 旧 Mujoco 回放被 `replay-check` 拒绝、旧 RL checkpoint 分布失配 | 用户已拍板：**直接改现有 v1/v2**、RL **重训**；报告显式披露 |
| 默认路径安全 | 任何注入点默认必须零影响 | `WheelContactOptions` 的 `Condim=0` 语义 = 未设置 ⇒ 取现状常量；`GeneratedActuators_*`/`MujocoTimebase` 哈希断言护住 |
| 真值纪律 | 模型常量此前是"真值→仿真量"推导（τ_stall/ω_noload 来自 2342 datasheet） | 新增/改动量必须标注来源；无实测锚点的写成工程初值并披露，不得伪装真值 |

## 5. 校准目标锚点

真车原地转向（`MOTOR_TURN_CALIBRATION`，2500 行档表）：90°/0.65 s ⇒ **2.42 rad/s**；135°/1.0 s ⇒ 2.36；180°/1.2 s ⇒ **2.62**。取 **≈2.4 rad/s** 作为"满指挥原地转向应达到的量级"，验收下限暂定 **≥2.0 rad/s**（留 20% 余量）。

## 6. 回滚形态

- 所有改动保持"默认中性 + 显式注入"：回滚 = `git checkout -- src/Sim.Mujoco`（探针用例独立文件，单独删）。
- 每步先测后改、改完即测；任一步 R5（legacy 逐位 + 默认 MJCF 逐字节）变红即回滚该步。
