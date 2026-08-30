# godot/ — Godot 4 .NET 桌面端（已验证）

本目录是 2026 武术擂台模拟器的 3D 桌面客户端。**仿真权威在 `src/Sim.Core`**；
本客户端只消费 `Snapshot`（经 `SnapshotView` 投影）并发出裁判指令
（arm/pause/resume/restart/加载回放），不复刻规则、不跑自己的物理判分
（`Godot` 物理仅做可视摆放与截图 QA）。

## 状态

- ✅ 已编译验证：Godot 4.7.2 Mono（`Godot.NET.Sdk/4.7.2`）下 `--headless --editor --quit`
  与 `--build-solutions` 均通过，无脚本/场景解析错误。
- ✅ 桌面可运行：实况、回放、布局编辑三种模式在 1280×720 / 1920×1080 窗口冒烟通过
  （证据截图见 `docs/*.png`：标准场地、旋转/平移场地、glTF 模型导入、坏模型回退、回放）。
- ✅ 跨端一致性：`--parity-check` 与 `Sim.Cli replay-check` 语义一致；对
  `replays/godot-parity-seed42.json` 得到最终比分、结束原因、末帧 2400、752 条事件指纹逐位一致；
  对旋转/平移后的场地回放 `replays/rotated-seed42.json`（340 事件）两端同样逐位一致。
- ✅ 场地几何单一来源：`ArenaVisualizer`/`MatchCamera`/`SnapshotView` 全部从 `Scenario` 读取
  尺寸与位姿（无第二份官方常量）；台面灰度纹理由内核同一 `FieldGray` 手绘模型生成。
- ✅ 布局编辑 + glTF 外观导入已交付（见下文“布局编辑模式”“外观模型导入”）。
- ✅ 赛事视觉真实感第一轮已交付：概览取景占视口约 52%×54%（PRD R1 目标 45–65%×45–75%）、
  程序化深色赛事天空、三点灯光、一次更新反射探针、Filmic tonemap + SSAO、MSAA 3D 4×、
  统一 PBR 材质策略与 primitive 机器人分件（见下文“视觉栈”）；台面灰度 Unshaded 契约、
  HUD 层级、镜头三模式与交互语义不变，`--camera-smoke`/`--edit-smoke`/`--parity-check` 全通过。
- ✅ 二轮视觉校正已交付（2026-08-29 实机反馈）：左键拖拽四方向按"反向修正契约"反转
  （右拖减小偏航 −0.3°/px、下拖抬高俯仰 +0.25°/px、俯视自旋同规则, `--camera-smoke`
  对右/左/下/上分别断言）; 台面显示改为官方"四角纯黑→中心纯白"归一化欧氏径向渐变
  （消除 L∞ 方形显示的白色对角亮带; 传感器 0–1000 语义与 Sim.Core 一字不动）;
  机器人接触阴影略增强。SSAO/反射探针 A/B capture 证明台面灰度逐像素不受全局渲染影响。
- ✅ 全屏显示校正已交付：HUD 以 1280×720 为设计基准，Godot `canvas_items` 拉伸模式随窗口
  等比放大，字体启用超采样；能量块按规则 PDF 第 11 页示意图使用黄绿色闪电圆环（增益）
  与紫色警示圆环叠红叉（减益），自定义六面 UV 保证立方体六个面都显示同一张完整图案。
  该改动只影响表现层，不改变仿真快照或能量块坐标。
- ✅ `src/SnapshotView.cs`、`src/MatchSession.cs`、`src/ParityCheck.cs`、`src/LayoutDraft.cs`
  为无 Godot 依赖的纯 C# 层，经 `Sim.Tests` 编译链接纳入回归（含回放重构、跨端比对、
  布局草稿/拖拽分组/保存重载测试）。

## 布局编辑模式（E 键）

仅在实况、比赛未开始时进入（`F5` 重置后进入）；回放模式与比赛进行中禁止编辑。

| 操作 | 方式 |
| --- | --- |
| 进入 / 退出编辑 | E（未应用的修改退出后不生效） |
| 选择对象 | 鼠标点击场地整体 / 黄蓝出发区 / 能量块（顶部检视栏显示选中文本与数值） |
| 拖动 | 按住左键在地面拖动（出发区/能量块为场局部坐标，场地为世界平移） |
| 微调 / 旋转 | ← → ↑ ↓ 微调 0.05m · `[` `]` 旋转场地 ±5° |
| 网格吸附 | S 开关（默认开: 平移 0.01m · 旋转 5°） |
| 撤销 / 重做 | Ctrl+Z / Ctrl+Y（一次连续拖动算一步） |
| 恢复官方布局 | 顶栏按钮 |
| 打开 / 另存为 | 顶栏按钮（场景 JSON, `arena-layout-v1`；保存原子写入且先过校验） |
| 应用布局 | Enter 或顶栏按钮——重建 `MatchSession`（新引擎/新场地，旧比赛作废） |

编辑只写 `LayoutDraft`（纯 C#，无 Godot 依赖，`Sim.Tests` 可回归），预览帧经临时
`MatchEngine` 快照走与比赛同一渲染管线；非法布局（越界等）会拒绝保存/应用并显示原因。

## 外观模型导入（`.glb` / `.gltf`，仅渲染层）

机器人外观可加载 glTF 模型替换 primitive 圆柱；权威碰撞/质量/铲子参数仍来自 `VehicleProfile`，
模型永不进入 `Scenario`/`Snapshot`/回放。配置在本地偏好 JSON（`--robot-models <file>`，
或项目根 `res://robot-models.json`）：

```json
{
  "us":   { "path": "test-data/robot-cube.gltf", "scale": 0.22, "yawOffset": 0.0, "heightOffset": 0.0 },
  "them": { "path": "" }
}
```

- `res://`/`user://` 路径走资源导入快速路径；文件系统路径走运行时 `GltfDocument`。
- 校验扩展名/存在性/文件大小(≤32MB)/节点数(≤5000)；缺失/损坏模型打印错误并回退 primitive，
  仿真结果不变。登台指示环始终保留为诊断层。
- `test-data/robot-cube.gltf` 是仓库自带的品红测试立方体（视觉 QA 用）。

## 布局

```
godot/
├─ project.godot           # 输入映射 (比赛/回放/编辑三组 actions, 编辑见上节)
├─ GodotSim.csproj         # Godot.NET.Sdk (4.7.2), 依赖 Sim.Core + Sim.Protocol
├─ scenes/Main.tscn        # 相机/灯光/环境/可视化器/HUD 场景树
├─ docs/*.png              # 桌面冒烟截图证据 (标准/旋转/模型/坏模型回退/回放)
└─ src/
   ├─ SnapshotView.cs      # 纯快照→RenderFrame 投影与插值（可单测）
   ├─ MatchSession.cs      # 纯会话门面: 固定步长实况 + 回放重构/缓存/导航（可单测）
   ├─ ParityCheck.cs       # 纯跨端一致性校验（可单测, 与 CLI replay-check 同语义）
   ├─ LayoutDraft.cs       # 纯布局编辑模型: 快照式撤销重做 + 拖拽分组 + 原子保存（可单测）
   ├─ FieldGrayTextureMap.cs # 纯灰度纹理像素↔场局部映射（无 Godot 依赖, 可单测）
   ├─ Main.cs              # 壳入口: 指令路由 + 无头 parity-check / 截图 QA / 冒烟 / 场景与模型偏好参数
   ├─ LayoutEditor.cs      # 编辑交互层: 拾取/拖动/旋转/吸附/对话框/预览
   ├─ ArenaVisualizer.cs   # Scenario 驱动的程序化场地(灰度纹理/出发区/围栏/武)+机器人/能量块
   ├─ RobotModelLoader.cs  # glTF 外观导入 (上限/校验/primitive 回退, 仅渲染层)
   ├─ HudPanel.cs          # 锚点 HUD: 状态卡/事件/操作帮助/回放时间轴/编辑器顶栏
   └─ MatchCamera.cs       # 概览/跟随/俯视 三模式相机 (按场地位姿/尺寸取景)
```

## 操作

| 键 | 实况模式 | 回放模式 |
| --- | --- | --- |
| Enter | 发令 (arm) | — |
| P | 暂停 / 继续 | — |
| R / T | 真实重启我方 / 对手（回出发点、清理瞬态, 对手 +3；仅 RUNNING/PAUSED。2026 规则: 裁判同意的重启 +3, +4 是未经同意的违规判罚, 仅存于 legacy restart 命令） | — |
| F5 | 重置为同 seed 新比赛 | 回到实况 |
| C | 切换镜头 (概览→跟随→俯视) | 同左 |
| L | 打开回放文件对话框 | 同左 |
| E | 进入/退出布局编辑（仅比赛未开始的实况模式） | 提示先回实况 |
| 空格 / ← / → / Home / End | — | 播放暂停 / 单步 / 到首尾帧 |
| 时间轴滑块 | — | 跳转到任意 tick |

窗口底部回放条仅在回放模式显示；时间轴滑块把回放缓存中每个 tick 快照
（由内核按 CLI 录制的动作/指令流重构）直接呈现，**不重写任何规则**。

### 镜头操作（非编辑模式）

| 操作 | 行为 |
| --- | --- |
| 左键拖动 | 转动视角（反向修正契约: 右拖减小偏航 −0.3°/px, 下拖抬高俯仰 +0.25°/px, 限幅 10°–85°; 即"抓住场景随光标转动"的手感）, 概览/跟随绕焦点环绕; 俯视绕视线轴自旋（右拖 −0.3°/px, 俯仰固定 -90°）; F5/场地重建时回默认角 |
| 右键拖动 | 画面平移（抓取语义: 被抓住的地面点跟随光标, 射线投到 y=0 地面取世界位移; 跟随模式焦点由渲染帧驱动, 不支持平移） |
| 滚轮 | 缩放: 概览距离 ×0.3–3 / 俯视高度 ×0.5–3 按基准取景限幅, 跟随模式缩放跟拍距离 ×0.5–2.5 |
| C | 概览 → 跟随 → 俯视循环；俯视绕 X 轴 -90° 正视向下, 完整场地可见 |

布局编辑器激活时相机让出全部指针处理（`MatchCamera.PointerInputEnabled` 由 `Main` 镜像
`LayoutEditor.Active`），编辑器的选择/拖拽不受影响；被相机消费的事件一律标记已处理。
旋转/自旋/平移/焦点的确定性证据由 `--camera-smoke` 无人值守校验。

### 台面灰度显示（官方外观渐变, 与传感器语义分离）

台面显示是 **visual-only 官方外观**（规则 PDF 第 10 页: "擂台表面底色从外侧四角到
中心分别为纯黑到纯白渐变"）: 归一化欧氏径向渐变 `FieldGrayTextureMap.OfficialSurfaceLuminance`
——中心纯白、四角纯黑、边中点中间灰（1−1/√2）、同一欧氏半径任意方向同亮度
（旧 L∞ 方形范数显示会画出白色对角亮带, 已废弃）。中央红区按几何优先覆盖渐变
（红底白"武"为独立 Label3D 层）; 走道在外场地面材质上, 不在台面纹理内。
**传感器 0–1000 语义**（`FieldModel.FieldGrayLocal`: 走道 0、边缘黑带 300、斜坡、
红区 650）保持一字不动, 也永不进入显示纹理——两种灰度语义严格分离, 显示层不
回写仿真。像素↔场局部坐标的轴向契约集中在纯 C# `FieldGrayTextureMap`（图像行 0 =
场地南侧、列 0 = 西侧, `Sim.Tests` 可无头断言），材质为 `Unshaded` + 线性过滤
（无 mipmap）——有向光/SSAO/反射探针不得在台面制造灰度梯度（已用关 SSAO/探针
的 A/B capture 验证分桶逐像素一致）。未标定的坐标无关灰度 CSV 永不进入运行时。

## 无头参数（`godot --path godot -- ...`）

多开：直接再次启动即可（无单实例锁，各窗口独立比赛/回放互不影响）；窗口标题带
seed/模式/场景/pid 便于分辨。注意自定义用户参数（`--scenario-path`/`--replay-path` 等）
必须放在 `--` 分隔符之后，否则会被当作引擎参数丢弃。

| 参数 | 作用 |
| --- | --- |
| `--parity-check <replay.json>` | 跨端一致性校验，按 CLI `replay-check` 语义比对比分/结束原因/末帧/事件指纹；成功退出码 0 |
| `--scenario-path <scenario.json>` | 用指定场景文件开局（布局编辑器保存的文件即用此参数在两端验证） |
| `--robot-models <prefs.json>` | 机器人外观模型偏好（渲染层, 见上节；缺省读 `res://robot-models.json`） |
| `--replay-path <replay.json>` | 启动即加载回放（也读导出属性 `ReplayPath`） |
| `--replay-tick <n>` | 加载回放后跳到第 n tick |
| `--auto-arm` | 启动即发令（演示/截图用） |
| `--edit-smoke` | 无人值守编辑器冒烟: 注入真实键盘动作 + 拾取/拖动/撤销重做/恢复官方/应用全流程断言, 并顺带跑一次"应用后布局"的逐位 parity 校验; 全部通过退出码 0 |
| `--camera-smoke` | 无人值守镜头冒烟: 经真实输入管线注入滚轮/左键拖动/动作键, 断言概览取景、缩放限幅、俯视 -90° 姿态与全场地覆盖、抓取语义平移、跟随缩放、编辑器指针所有权, 并校验概览取景占 16:9 视口约 45–65%×45–75% (PRD R1); 全部通过退出码 0 |
| `--capture <out.png>` | 渲染 30 帧后保存视口 PNG 并输出分桶像素统计（视觉 QA 证据），随后退出；与 `--edit-smoke`/`--camera-smoke` 同用时改为冒烟结束后截图, 退出码=冒烟结果。无头 dummy 渲染器无真实视口纹理, 截图跳过但冒烟退出码不变 |
| `--capture-frames <n>` | 覆盖 `--capture` 的默认 30 帧等待（例: 相机阻尼收敛/比赛推进后再截） |
| `--camera-cycle <0-2>` | 启动即切换镜头模式 (0=概览 1=跟随 2=俯视, 仅表现层), 供三种机位的 capture 证据留存 |
| `--camera-orbit <yaw>,<pitch>` | 启动即设置概览环绕角 (度, 同一限幅), 复现"左键拖动后"机位供 capture 证据 (仅表现层, 不注入输入事件) |

## 视觉栈（第一轮，官方赛事贴图）

Forward+ 默认画面全部由内置能力构成, 每项都经真实 renderer 双分辨率 (1280×720 / 1920×1080)
capture 验证; 不引入第三方 GLB/HDRI/自定义全屏 shader, 也不默认启用
SDFGI/VoxelGI/Lightmap 烘焙。能量块仅使用仓库内两张由官方规则第 11 页示意图整理的 PNG；
规则原文注明示意图仅供参考，实际标准打印图仍以赛项交流群发布版本为准。

| 层 | 默认配置 | 说明 |
| --- | --- | --- |
| 背景 | `ProceduralSkyMaterial` 深色赛事空间渐变 | 消除大面积空黑, 天空/地面两半球都保持可辨的蓝灰, 不抢主体 |
| 灯光 | 主光 (暖白 1.2, 带阴影) + 补光 (冷蓝 0.3) + 轮廓光 (冷蓝 0.6) | 三点关系: 方向/接触阴影、暗部抬升、机器人与围栏边缘分离 |
| 反射 | `ReflectionProbe` 一次更新 (`UPDATE_ONCE`), 包围盒 7.5×3.5×7.5 限于擂台+围栏 | 机器人外壳/平台侧面获得稳定环境反射; 非每帧重渲染, 不依赖动态仿真 |
| 后处理 | tonemap = Filmic, SSAO 开 (仅 Forward+; gl_compatibility 自动忽略) | 轻量层次增强; 台面灰度纹理为 `Unshaded`, 不受 SSAO/灯光/反射影响 |
| 抗锯齿 | MSAA 3D 4× (`project.godot`) | Forward+/gl_compatibility 都支持, 无运动拖影 |
| 材质 | `ArenaVisualizer` 统一材质策略: 哑光橡胶 (地面/围栏/轮暗部)、喷涂金属 (车体/推铲)、白色板材 (平台侧面)、哑光赛事贴纸 (能量块)、自发光 (灯带/登台环) | roughness/metallic/emission 集中在可复用工厂方法, 避免逐节点漂移 |
| 机器人 | primitive fallback 含车体分件: 上盖/侧带/四轮暗件/车头/金属推铲/团队灯带/接触阴影盘 | 纯渲染层, 尺寸由碰撞半径推导, 打 `primitivePart` meta, glTF 导入成功时整体让位 (登台指示环保留) |

### 关闭/否决的高成本实验（可复现配置）

- **Glow/Bloom**: 默认关闭。台面白心接近 1.0 亮度, 屏幕空间泛光会在白边外产生光晕,
  可能被误读为灰度阶梯, 威胁官方调色板像素判读; 若要实验, 在 `Main.tscn` 的
  `Environment` 中启用 `glow_enabled` 并把 `glow_hdr_threshold` 抬到 >1.0。
- **TAA**: 默认关闭。仅 Forward+ 可用, 且会在移动机器人后留下拖影 (PRD 明确静态截图
  不能作为选择依据); MSAA 4× 已满足静态与动态清晰度。实验开关:
  `project.godot` → `rendering/anti_aliasing/quality/use_taa`。
- **SSIL/SDFGI/VoxelGI/Lightmap**: 不进入第一轮 (成本/烘焙资产限制, 收益对本场景不显著)。
- **ReflectionProbe 每帧更新**: 否决, 一次更新已足够 (静态擂台)。

### 性能回退策略

以同机/同场景/同窗口尺寸/同帧数的改动前测量为基线: 任一后处理造成帧时间退化超过
约 30% 时必须默认关闭并记录为可选实验。实测 (RTX 5070 Ti Laptop, `--disable-vsync`,
两点法 900/2700 帧边际帧时间, 720p): 改动前 ≈5.33–5.44 ms/帧, 改动后 ≈4.89–5.39 ms/帧
——本场景在该 GPU 上为 CPU-bound, SSAO+MSAA 4×+天空+三点光+一次更新探针的总开销
在测量噪声以内, 未触发热处理门限。低性能 GPU 上如出现退化, 按上节逐项关闭
(先 SSAO, 再 MSAA 降为 2×)。

## 与无头端的一致性承诺

同一 `seed` + 同一动作序列下，客户端渲染的比赛与
`dotnet run --project src/Sim.Cli -- match --seed N` 完全一致——因为两边共用
同一个 `MatchEngine`，客户端不做任何本地仿真。渲染掉帧只影响画面流畅度，
不改变仿真时钟（固定步长累加器）。

## 无头校验脚本

```powershell
# 生成基线并验证跨端一致
dotnet run --project src/Sim.Cli --no-build -- replay-record --seed 42 --out replays/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json

# 布局编辑后: 保存的场景文件驱动两端一致 (编辑器"另存为"产物)
dotnet run --project src/Sim.Cli --no-build -- replay-record --seed 42 --scenario scenarios/edited.json --out replays/edited-seed42.json
godot --headless --path godot -- --parity-check ../replays/edited-seed42.json
```
