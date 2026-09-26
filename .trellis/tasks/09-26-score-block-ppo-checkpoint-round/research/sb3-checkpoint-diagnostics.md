# SB3 2.9.0 训练诊断与 checkpoint 接口核实（2026-09-26）

本轮不复制任何第三方实现，只核实**本机锁定依赖 `stable-baselines3==2.9.0`** 的真实行为，避免把通用文档记忆当成已核验事实。证据来自本机虚拟环境中已安装的源码副本：

`%TEMP%\score-block-rl-venv-11d\Lib\site-packages\stable_baselines3\`（Python 3.12.10，SB3 2.9.0，Gymnasium 1.3.0，Torch 2.13.0+cpu，NumPy 2.5.0）。

## 1. `CheckpointCallback` 的 `save_freq` 单位

`common/callbacks.py:245-303`：

```python
class CheckpointCallback(BaseCallback):
    """
    Callback for saving a model every ``save_freq`` calls to ``env.step()``.
    ...
      When using multiple environments, each call to ``env.step()``
      will effectively correspond to ``n_envs`` steps.
      To account for that, you can use ``save_freq = max(save_freq // n_envs, 1)``
    """
    def _checkpoint_path(self, checkpoint_type: str = "", extension: str = "") -> str:
        return os.path.join(self.save_path, f"{self.name_prefix}_{checkpoint_type}{self.num_timesteps}_steps.{extension}")

    def _on_step(self) -> bool:
        if self.n_calls % self.save_freq == 0:
            model_path = self._checkpoint_path(extension="zip")
            self.model.save(model_path)
```

结论（直接决定实现细节）：

- `save_freq` 的计数单位是 **`env.step()` 调用次数**，不是全局 timestep。本任务固定 `n_envs=1`，所以 `save_freq=51200` 恰好等于每 51,200 个**单环境 step** 存一次；**不能**再写 `save_freq // n_envs`（那会在多环境场景才对，本任务单环境会变成 51,200/1 = 51,200，其实等价，但语义靠的是 `n_envs=1` 这个前提）。
- 因此实现里显式断言 `model.n_envs == 1`，把这个隐含前提变成可检查条件；`n_envs != 1` 时直接报错而不是静默改变 checkpoint 间隔。
- 文件名内嵌 `self.num_timesteps`，单环境下等于训练步数 → `rl_model_51200_steps.zip` 的步数可从文件名解析，并与 `run-config.json` 交叉核对。
- `_init_callback` 会 `os.makedirs(self.save_path, exist_ok=True)`，因此 checkpoint 目录不必预先创建，但实现里仍创建以固定相对路径（`<out>/checkpoints`）。
- `save_replay_buffer` / `save_vecnormalize` 默认 `False`；本任务不用向量环境也没有 replay buffer，保持默认。

## 2. CSV logger 与 PPO 诊断字段

`common/logger.py:325-389` 的 `CSVOutputFormat` 在首次 `write()` 时写表头（**没有** `#` 注释行，与 Monitor CSV 不同），之后每行一次 `logger.dump()`；值是纯 `str(value)`，字符串加引号。`Logger.dump()`（`logger.py:534`）按 `_writer` 分派到 CSV/TensorBoard/Human 输出，并把 `key_values` 清空。

`OnPolicyAlgorithm._dump_logs()` 每个 `log_interval`（PPO 默认 1）迭代记录 `time/*` 与 `rollout/ep_rew_mean`、`rollout/ep_len_mean`；`PPO.train()` 记录 `train/*`。因此 `progress.csv` 可用于回答“PPO 是否在更新、是否过度裁剪、价值函数是否在学”，这是 Monitor 逐集 CSV 无法提供的：

- `train/approx_kl`、`train/clip_fraction`：策略更新幅度与裁剪比例，解释“训练是否还在动 / 是否被 clip 卡住”；
- `train/explained_variance`：价值函数是否拟合回报；
- `train/value_loss`、`train/entropy_loss`、`train/learning_rate`、`train/loss`；
- `time/total_timesteps`、`time/fps`：吞吐与步数对齐；
- `rollout/ep_rew_mean`：**只能**用于解释训练趋势，本项目选模不看它（见第 3 点）。

实现方式（源码核实后的修正）：**SB3 2.9.0 默认并不会写出 `progress.csv`。** `common/utils.py:265-299` 的 `configure_logger()` 在 `tensorboard_log is None` 且 `verbose=0` 时把 `format_strings` 设成 `[""]`，而 `common/logger.py:662` 的 `configure()` 做 `format_strings = list(filter(None, format_strings))`，于是 output_formats 为空——既没有 stdout，也没有 CSV/JSON/log writer。这也解释了上一轮为什么只有 Monitor 逐集 CSV。

因此实现不走 `tensorboard_log` 的隐式路径，而是显式构造：

```python
csv_logger = Logger(folder=str(out), output_formats=[CSVOutputFormat(str(out / "progress.csv"))])
model.set_logger(csv_logger)  # base_class.py:255-267 置 _custom_logger=True
```

`base_class.py:429-431` 只在 `not self._custom_logger` 时才调用 `configure_logger`，所以自定义 logger 不会被 `learn()` 覆盖。收尾用 `Logger.close()`（`logger.py:621-626`）关闭 writer 并 flush。这样 `progress.csv`（优化诊断）与 `episodes.monitor.csv`（`Monitor(info_keywords=...)` 记录的每集裁判信息，本任务的 `INFO_LOG_FIELDS`）分离。

## 3. 为什么不用 `EvalCallback` 选模

`common/callbacks.py:447-522` 的 `EvalCallback._on_step()` 在评估后用 `mean_reward` 比较并（可选）写 `best_model.zip`；其默认口径是**平均 episode 回报**。本项目上一轮的 AC4 判据是裁判事件：锁定目标我方**真实** `BlockScore` 次数与我校 `Drop` 次数，回报里还混着边缘 shaping 与步长成本。用平均回报选模会把“离边缘更近但从未得分”的模型选出来，与项目指标冲突。

因此本轮只借用 `CheckpointCallback` 做**无偏定期快照**，所有选模在独立的 `evaluate.py` 里按裁判事件完成（见 design.md）。

## 4. 与本项目上一轮的差异

| 项目 | 上一轮 | 本轮 |
|---|---|---|
| 训练快照 | 仅最终模型 | 最终模型 + 每 51,200 单环境 step 的 checkpoint |
| PPO 诊断 | 无（只有 Monitor 逐集回报） | `progress.csv`（CSV logger） |
| 选模 | 开发集 3001–3010 上人工比较两档步数 | 5001–5020 上逐 checkpoint 门槛 + 排序键，唯一候选 |
| 盲验集 | 4001–4010（已揭示） | 6001–6050（一次性，需冻结记录） |
| 旧 split | 默认即开发集 | 保留 `--final-holdout` = 4001–4010，新增 v2 split 标签 |

上一轮的 AC4 失败结论不因本轮任何结果而改变。
