# 技术设计 — tools/sensitivity 敏感性分析工具

## 1. 组件与边界

```
tools/sensitivity/
  sensitivity.py     # 主工具：OAT 扫描 + 聚合 + 报告
  selftest.py         # 口径对拍自测（见 §5）
  report/             # 产物（git 状态见 §7）
    sensitivity-report.md
    sensitivity-results.json
```

- **复用**：`sys.path` 插入 `tools/param-optimization` 后 `import oracle`，直接用 `oracle.run_match` / `oracle.parse_match_output`（GB18030 解码坑在其内部已处理）；`oracle.write_scenario` 只覆盖 `parameters` 字典，vehicles 覆盖自写（见 §3）。
- **不碰**：src/ 全部、tools/param-optimization/ 现有文件（只读 import）。

## 2. 数据流

```
BASE(scenarios/wushu-ring-2026.json)
  → variant = deep_copy(BASE)
      [parameters 级] variant["parameters"][KEY] = value
      [vehicle  级] variant["vehicles"]["us"][FIELD] = value
                    variant["vehicles"]["them"][FIELD] = value
  → oracle.run_match(dotnet, cli_dll, variant_path, seed, 120)
  → oracle.parse_match_output(stdout) → {us_block_scores, us_drops, mount_t, done_reason, final_score}
  → 派生 score_margin / mount_fail
  → 逐 (param, level) 桶存 8 种子原始指标
```

聚合（纯离线，无再次仿真）：

```
S(param, level, metric) = |mean_m(level) − mean_m(baseline)| / ( |Δp/p| × (std_m(baseline)+ε) )
S(param, metric) = max over levels
裁决: S<1 不敏感 · 1≤S<3 弱/存疑 · S≥3 敏感 · 相变单独标注
```

- ε 平滑项防 0 除；基线 std 为 0 时（指标 8 种子全同）用 |mean 差|直接判（差非 0 即相变级敏感）。
- wire 名注意：vehicles 字段的 JSON 拼写以 `VehicleProfile` 序列化输出为准（camelCase，如 `latFrictionK`）；实施第一步实测序列化样例钉死拼写（防拼写错→Validate 抛错虽会失败暴露，但要在自测里覆盖）。

## 3. variant 生成

- 深拷贝基础场景 dict；parameters 级按 R1 键名写入；vehicle 级同时写 `vehicles.us.<field>` 与 `vehicles.them.<field>`。
- **COLLISION_RESTITUTION 特例**：null 基线 → 扰动水平直接设数值（0.5×/0.75×/1.25×/1.5× 参照速度相关公式的等效域，clamp [0,0.9] 拟合域一致）；报告分"null 基线"与"数值基线"两段，见 PRD R1。
- 有界参数 clamp：`MOUNT_V_MIN∈(0,2]`、`MOUNT_ANGLE_MAX∈(0,1.2)`、`latFrictionK∈[0.5,60]`、`angDamping∈[0,40]`（校验域来自 Profiles.cs/SimParameters.cs，research/baseline-facts.md §1）。
- variant 文件落 `report/variants/`（调试与复现证据），命名 `<param>-<level>-s<seed>.json` 可复用同 (param,level) 单文件跑 8 种子。

## 4. 调用与预算

- dotnet 定位沿用 `optimize.py default_dotnet()` 候选表（便携 SDK 路径）；Sim.Cli.dll 用 Release 构建产物，启动时打印实际路径+git HEAD。
- 串行执行（每场 120s 模拟，单进程 wall-clock 估 10-20s/场）；全量 264 场 → 全程 1-1.5h 量级；支持 `--params`/`--seeds` 子集参数（断点续跑：results.json 已有 (param,level,seed) 条目跳过）。
- **断电/中断安全**：每场跑完立即增量写 results.json（写临时文件+rename 原子替换，参照仓库既有原子写盘惯例）。

## 5. selftest（口径对拍）

1. 基线场景 s1 一场：`oracle.run_match` 直跑 vs sensitivity.py 基线路径跑 → 五指标逐字段相等。
2. 构造一个 vehicles 覆盖变体（latFrictionK=16）→ 校验生效证明：场景 JSON 落盘读回值正确 + `match` 正常退出（Validate 通过即拼写正确）。
3. 断点续跑：删除 results.json 半数条目重跑 → 跳过已有、补齐缺失、终表与全量一致（抽查 3 条）。

## 6. 报告结构

```markdown
# Sim 物理参数敏感性报告（配置头：git HEAD/种子/时长/扰动表/dotnet 路径）
## 阳性对照（CLASSIFY_RATE）—— 敏感 ⇒ 方法学有效
## 逐参数表：参数|子系统|通道|基线|±10/±25/±50 各指标 S|裁决
## 相变记录（done_reason/mount 状态翻转明细）
## 方法边界（OAT 局部性/无交互分解/合成数据不入标定链）
## 对 08-28 的采集优先级建议（证据行，不拍板）
```

## 7. 取舍记录

- **OAT vs Sobol**：成本差数量级，目标只是采集优先级排序（见 research/sensitivity-method.md §1）。
- **120s vs 60s**：60s 是 param-optimization 的 FSM 决策筛选口径；本任务对齐 08-28 判读口径用 120s。
- **产物 git 策略**：report/ 下 results.json + sensitivity-report.md 提交（可审计证据，报告含结论）；variants/ 264 个 JSON **不提交**（gitignore 追加一行），理由：可由脚本确定性再生成，进库只添重。此点在 implement.md 的提交分组里体现。
- **敏感度单位**：以"基线种子噪声的倍数"为单位而非绝对值——跨参数可比且自带显著性判据。
