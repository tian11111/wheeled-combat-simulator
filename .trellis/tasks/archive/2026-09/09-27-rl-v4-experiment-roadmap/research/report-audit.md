# 研究报告核查记录（2026-09-27）

源报告：`C:/Users/Neco/Downloads/deep-research-report.md`，标题《wheeled-combat-simulator 下一步强化学习工程实施与实验计划》，报告日期 2026-09-26。报告中的 `turn…view`、`turn…search` 和 `filecite` 标记无法由 Markdown 独立解析；本记录只用下列仓库证据与带版本公开链接支撑采纳项。

## 已由当前仓库或归档证实

| 结论 | 可复核证据 | 规划处理 |
| --- | --- | --- |
| v3 仅一次训练 seed `20260925`；500k 请求步、501760 实际步、1631.238 秒、307.595 steps/s、CPU、`n_envs=1` | `.trellis/tasks/archive/2026-09/09-26-score-block-v3-edge-speed-retrain/report.md` §1，行 20–42 | 单次历史记录；先测同机重复基线，不据此承诺加速倍数 |
| v3 最终 50 场锁定目标得分 PPO 2、FSM 6；掉台 25、29；盲验一次且失败 | 同上 §0，行 8–16 和结果明细 §4 | v3 已揭示；新实验必须注册 v4 最终集 |
| 当前 `final_holdout_v3` 仍在 `BLIND_SPLITS`，不在 `REVEALED_HOLDOUT_SPLITS` | `controllers/score_block_rl/splits.py` 行 18–20、31–55 | 首任务修复防复用，v3 只允许 `--analysis-only` 且不得生成门槛证据 |
| Python 侧单环境 PPO、11 维特权观测，持久 JSONL 与 .NET 交互 | `controllers/score_block_rl/train.py`、`gym_env.py`；v3 报告 §1 | 保持原版配置做多训练 seed 基线，记录 privileged 限制 |
| 当前 benchmark 无并行规模、资源与 optimizer/IPC 分解 | `controllers/score_block_rl/benchmark.py` | 单独立项重复 profiling，先判断瓶颈 |

## 采纳但需本机验证

- 多训练 seed、开发集按配对裁判事件选模、唯一候选冻结和新盲集一次验收。这与 [SB3 v2.9.0 RL Tips](https://github.com/DLR-RM/stable-baselines3/blob/v2.9.0/docs/guide/rl_tips.md) 的独立评估与多随机种子建议相符；本项目仍以真实 `BlockScore`、`Drop`、归因和 fault 判定。
- PPO 向量化时每次更新的 rollout 样本量是 `n_steps × n_envs`，checkpoint callback 的调用次数也随 `n_envs` 变化。实现时核对 [SB3 v2.9.0 PPO 文档](https://github.com/DLR-RM/stable-baselines3/blob/v2.9.0/docs/modules/ppo.md) 和 [Callback 文档](https://github.com/DLR-RM/stable-baselines3/blob/v2.9.0/docs/guide/callbacks.md)。
- `SubprocVecEnv` 仅在独立单环境并行未达标后试；原生 .NET batch 仅在本机重复测量显示 IPC/序列化≥25%、CPU 未饱和时试。报告给出的并行倍率和 batch 收益不是已验证事实。
- 边缘进展奖励可能包含非我方接触造成的位移。因果影响未被证明；先用轨迹确立归因规则，再作为**单变量实验**比较，不能先称为修复。

## 不采纳为本轮门槛

- 报告的八周工期、20–30M transitions 预算、2.5×/3.5× 加速、200-seed 盲集均为假设或建议，当前无本机重复证据；本轮采用用户锁定的 5×500k≤60 分钟与 50-seed 新盲集。
- GPU/MJX、SAC、观测扩维、课程学习、真机部署和域随机化会同时改变多个变量；本轮不做。真机动力学区间缺乏遥测标定，不把报告中的随机化建议当作已验证参数。
- 五个训练 seed 可以揭示明显不稳定和逐 seed 差异，但不能支撑强总体统计保证；报告逐项结果与范围，不用单个最佳模型或 bootstrap 区间代替五项原始数据。

公开链接访问/版本口径：2026-09-27，Stable-Baselines3 仓库标签 `v2.9.0`。仓库证据路径以本工作树为基准；实现前须复核是否发生上游代码变化。
