# Godot 3D 视觉三阶验证证据

日期：2026-08-30  
设备：NVIDIA GeForce RTX 5070 Ti Laptop GPU  
Godot：4.7.2-stable Mono  

## 结果摘要

- Forward+ 默认启用 SDFGI、低密度体积雾、阈值 Glow、远景 DoF、材质程序噪声。
- 所有视觉增强都有独立关闭旗标；`--visual-baseline` 可一次关闭五组增强。
- 台面顶层仍走 `Unshaded` 官方灰度纹理；程序噪声只进入走道、围栏、机器人和平台侧面材质。
- 能量块使用自定义倒角 `ArrayMesh`。Godot 4.7 API 没有 `RoundedBoxMesh`，六个主面使用同一张完整贴纸图案，倒角面只补同色几何。
- Sim.Core、协议、HUD 与回放数据流没有改动。

## 真实 renderer capture

命令形态（视觉旗标必须放在 Godot `--` 之后）：

```powershell
Godot_v4.7.2-stable_mono_win64_console.exe --path godot --rendering-method forward_plus --resolution 1280x720 --disable-vsync -- --visual-frame-stats 180 --capture <absolute-path>.png --capture-frames 180
```

| 场景 | 输出 | 结果 |
|---|---|---|
| Forward+ 默认 1280×720 | `ab-1280/default.png` | `sdfgi=True fog=True glow=True dof=True materialNoise=True`; capture 成功 |
| Forward+ 基线 1280×720 | `ab-1280/baseline.png` | 五组增强均为 false; capture 成功 |
| `--visual-no-sdfgi` | `ab-1280/no-sdfgi.png` | 仅 SDFGI 为 false; capture 成功 |
| `--visual-no-fog` | `ab-1280/no-fog.png` | 仅体积雾为 false; capture 成功 |
| `--visual-no-glow` | `ab-1280/no-glow.png` | 仅 Glow 为 false; capture 成功 |
| `--visual-no-dof` | `ab-1280/no-dof.png` | 仅 DoF 为 false; capture 成功 |
| `--visual-no-material-noise` | `ab-1280/no-material-noise.png` | 仅材质噪声为 false; capture 成功 |
| Forward+ 默认 1920×1080 | `default-1920.png` | capture 成功 |
| Forward+ 基线 1920×1080 | `baseline-1920.png` | capture 成功 |
| gl_compatibility 1280×720 | `compatibility-1280.png` | capture 成功；引擎按预期忽略 SDFGI、体积雾、DoF |

## 帧间隔 A/B

`--visual-frame-stats 180` 统计 Godot 主循环 `delta`，是同机相对比较用的进程代理指标，不等同 GPU frame time。

| 分辨率 | 默认全套 | `--visual-baseline` | 回归 |
|---|---:|---:|---:|
| 1280×720 | 4.867 ms | 4.276 ms | 13.8% |
| 1920×1080 | 5.585 ms | 4.866 ms | 14.8% |

以上为倒角几何修正后的热缓存 180 帧测量；1920×1080 的组合回归仍低于 30% 门槛，因此默认保留；低端设备可按旗标逐项回退。
1280×720 单项旗标的平均值为：SDFGI 关闭 2.909 ms、雾关闭 3.071 ms、Glow 关闭 3.310 ms、
DoF 关闭 2.999 ms、材质噪声关闭 3.208 ms。

## 灰度契约抽样

对 1920×1080 默认/基线的同机位截图，在台面空白区域抽样 RGB 绝对差：

| 区域 | 平均绝对差（/255） | 最大差 |
|---|---:|---:|
| 台面左上空白 | 1.407 | 7 |
| 台面下中空白 | 0.412 | 1 |
| 台外背景 | 3.758 | 12 |

差异集中在雾/景深/抗锯齿与截图时序；台面材质本身没有挂程序噪声，中央白心未出现可见泛光污染。
现有 capture 分桶仍命中 `us/them/buff/debuff/platform/floor` 全部类别。

## 自动回归

- `dotnet build godot/GodotSim.csproj --no-restore -m:1 /p:UseSharedCompilation=false`：0 警告、0 错误。
- `dotnet test --no-restore`：330 通过，0 失败，0 跳过。
- `--camera-smoke`：全部镜头缩放、四方向拖拽、俯视、平移、编辑器指针所有权断言通过。
- `--edit-smoke`：能量块/车辆拾取拖动、撤销重做、应用后布局与 parity 断言通过。
- `dotnet run --project src/Sim.Cli --no-build -- replay-check replays/seed-42.json`：比分 4:49、752/752 事件，通过。
- Godot `--parity-check ../replays/godot-parity-seed42.json`：比分 4:49、2400/2400 ticks、752/752 事件，通过。

兼容渲染器会输出 Godot 自身关于不支持 SDFGI/体积雾/DoF 的 warning；这是预期降级，不影响进程退出码或截图结果。
