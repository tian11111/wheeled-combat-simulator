# oracle.py 编码 bug 污染判定（10-14-oracle-encoding-fix）

## 结论：历史优化数据**不受污染**

## 证据链（全部实测，2026-10-14）

1. **optimize.py:87 评分确实经 oracle.run_match（gb18030 路径）**——依赖关系成立，污染前提存在。
2. **历史数据有效签名**：`study/trials.csv`（150 trials）与 `study/optimize-log.txt`（2026-09-25 跑）中 `final_scores`（如 `[[10.0,9.0],[4.0,5.0],…]`）与 `mount_t`（`[29.0×4]`）全部有效非零——而这两个指标分别依赖中文 marker `"score 我方"`（oracle.py:103）与 `"已上台"`（oracle.py:62）匹配，**gb18030 解码 UTF-8 字节会使其全变 None**（10-14-sensitivity selftest KNOWN-DEVIATION 实证）。⇒ 09-25 历史优化跑时管道输出是 **GBK**，当时 gb18030 解码正确，解析无失效。
3. **当前实测**：同一 oracle 路径在当前便携 SDK 8.0.31（runtime 8.0.31）下管道输出为 **UTF-8**（`[我方]` 首两字字节 `E6 88 91 E6 96 B9`），原 gb18030 解码使 us_drops 210→0。
4. **match 路径代码未变**：`git log -S "OutputEncoding" -- src/Sim.Cli/Program.cs` 全历史唯一提交 6aadde8（rl-env 专属设置，Program.cs:38-43 RunRlEnv 内），match 路径从未设置输出编码。

## 判定

- 150-trial TPE 优化结论、baseline/holdout 对比（baseline-60s/holdout-120s）、best params（MOUNT_SPEED 710 系）**全部有效，无需重跑**。
- 漂移时点：2026-09-25（GBK，历史有效）→ 2026-10-14（UTF-8，实测）。**机制未完全钉死**：旧 Temp SDK（`%LOCALAPPDATA%\Temp\robot-simulator-dotnet-sdk`）已被删除无法复验其 runtime 版本；候选解释为 SDK/runtime servicing 版本差异改变管道默认编码。如实标注，不做超出证据的断言。

## 修复

- oracle.py：按字节捕获 + `_decode_stdout` 有效性签名容错（汇总行 `"seed="` 在而 `"score 我方"` 破损 → 回退 gb18030）——两种历史环境均正确，取代单押任一编码的镜像错误。
- selftest.py：补双编码字节流还原断言。
- 偏离 PRD "一行修复"边界的原因：编码漂移实证成立后，单押 UTF-8 在 GBK 环境会复现本 bug 的镜像——容错解码有客观签名判定，非猜测。

## 遗留（不属本任务）

- `study/study.db`（311KB）被 git 跟踪与自家文档矛盾——仓库卫生遗留（10-14 审计已记录）。
