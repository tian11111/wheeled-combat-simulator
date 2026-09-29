# tools/mesh — 真车几何资产提取

把 SolidWorks 导出的整车 `装配.glb` 提取成 MuJoCo 物理车体使用的二进制 STL。

## 资产溯源

| 项 | 值 |
| --- | --- |
| 源文件 | `装配.glb`(SolidWorks 导出, 41 件) |
| 源文件大小 | 2 881 660 B |
| 源文件 sha256 | `9538408e7a7e1cc039f30c49b9a56519382133dc9028eb6693bd0cd2a58b88a0` |
| 提取脚本 | `tools/mesh/extract_vehicle_mesh.py`(脚本内 `EXPECTED_GLB_SHA256` 与上表一致, 不符即拒绝运行) |
| 入库位置 | `src/Sim.Mujoco/assets/`(以 `EmbeddedResource` 进 `Sim.Mujoco.dll`) |

生成命令(2026-09-28, 仓库根目录):

```
py -3.12 tools/mesh/extract_vehicle_mesh.py --out src/Sim.Mujoco/assets
```

入库资产(sha256):

| 文件 | 三角面 | 大小 | sha256 |
| --- | --- | --- | --- |
| `robot_chassis.stl` | 6 692 | 334 684 B | `93375d787b8535032c928f64571c236500542ba72d63fe8caaeb76ff2609a987` |
| `robot_rear_shovel.stl` | 4 166 | 208 384 B | `ce557cbe23368537fc46eefd3b4920d213c62bcc36b1827e3df9917efbf91d61` |
| `robot_front_arm.stl` | 3 766 | 188 384 B | `b01e93f400a8035143444ecc2c6af3451fea893fd507e92926e570189f42673c` |

`robot_front_arm.stl` 物理 v2 未使用(只有 chassis + rear_shovel 两个 mesh geom), 入库供渲染/后续版本比对;
**它不参与 `ModelSha256`** —— 未被物理引用的文件不应改变物理身份。

## 坐标映射(已验证 det=+1, 非镜像)

```
bodyX = -modelZ    # 车头方向: 模型里车头朝 -Z
bodyY = -modelX
bodyZ =  modelY    # 上
```

原点取模型原点 = **轮轴平面**(轮心), 所以 bodyZ=0 与轮心共面: 物理车体的 spawn 高度 = 轮半径,
`ZG`(快照 Up) 在平地恰好为 0、台面恰好为 0.06(与 legacy 物理语义一致)。

## 分组规则

| 输出 | 源零件(glb 节点名) | 理由 |
| --- | --- | --- |
| `robot_chassis` | `底板.stp-1` + `上板.stp-1` | 两板近似平行且间隙仅 0.03 m, 分开做 geom 只增加接触对而无行为收益 |
| `robot_rear_shovel` | `后铲.stp-1` | 必须与车体分开: 后铲与底板之间的凹角是唯一能勾台沿的几何, 单一整车凸包会把它填平 |
| `robot_front_arm` | `铲臂（2）.00.stp-1` + `铲臂（2）.00_MIR.stp-1` | 物理未使用(见上) |

## 顶点去重 / 凸包

设计草稿建议导出前做顶点去重 + 凸包。这里**保持原始三角面**, 由 MuJoCo 在编译期自己算凸包
(`<mesh ... maxhullvert="256"/>`), 原因:

- 入库字节必须与阶段 1 实验(20° 倒角登台 / 力矩标定 / mj_geomDistance 交叉校验)使用的
  `tmp/assets/*.stl` **逐字节一致**, 否则"实验验证过的几何"与"生产几何"不是同一份东西;
  本脚本产出的三个文件与 `tmp/assets/` 的 sha256 完全相同(2026-09-28 逐字节比对通过)。
- 文件体积(334/208 KB)与编译时间(2 个 mesh 编译 103 ms, 见阶段 1 报告)都不构成约束。

## 轮胎实测(v2 物理常量的来源)

`轮胎.stp-*` 不导出几何(物理用 `type="cylinder"`), 但尺寸/位置是 v2 常量, 由本脚本每次都打印:

```
轮胎.stp-1/胎皮.stp-1  center=(+0.0730, +0.11635, -0.00025)  diameter=0.064899  width=0.029000   -> 前左
轮胎.stp-2/胎皮.stp-1  center=(-0.0770, +0.11635, -0.00025)  diameter=0.064988  width=0.029000   -> 后左
轮胎.stp-3/胎皮.stp-1  center=(+0.0730, -0.11265, -0.00025)  diameter=0.064970  width=0.029000   -> 前右
轮胎.stp-4/胎皮.stp-1  center=(-0.0770, -0.11265, -0.00025)  diameter=0.064990  width=0.029000   -> 后右
```

(车体局部系, X=车头, Y=左, Z=上; model 空间到 body 空间的换算见上。)

- **轮半径 r = 0.0325 m**(四个胎皮外径 0.064899–0.064990, 即 r=0.032450–0.032495; 取名义 0.0325)。
  `prd.md` 记录的 r=0.046 来自 `tmp/inspect_glb.py` 的"局部 bbox 八角点旋转法", 那是随轮胎自转相位
  变化的**假膨胀**, 不是轮径(独立复核见任务 `deviations`)。
- 半宽 0.0145 m(= 0.029/2, 精确值)。
- 轮心: 前 x=+0.0730、后 x=−0.0770(轴距 0.150), 左 y=+0.11635、右 y=−0.11265(轮距 0.229)。
  相对模型原点是**不对称**的(原点在轮轴平面但不在轴距中点), 物理按实测位置放置。

## 复现 / 校验

```
py -3.12 tools/mesh/extract_vehicle_mesh.py --glb "C:/path/装配.glb" --out src/Sim.Mujoco/assets
py -3.12 tools/mesh/extract_vehicle_mesh.py --check --out src/Sim.Mujoco/assets   # 只比对不写入
```

换 glb 或改分组会改变入库字节 → `ModelSha256` 变化 → 既有 v2 replay/checkpoint 身份失效, 属预期;
v1(`wushu-mjcf-v1`)生成路径不使用资产, 身份不受影响。
