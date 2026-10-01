# 对照实验复现件（主会话从 tmp/ 归档，2026-10-01）

原评审发现：comparison.md 引用的场景副本与脚本在 gitignore 的 tmp/ 下，干净 checkout 不可复现。
此目录为入库副本：4 份场景副本（mirror/head × builtin/mbri）+ run-comparison.py/analyze.py。
复跑示例（仓库根）：

    dotnet run --project src/Sim.Cli --no-build -- match --seeds 1,2,3,4,5,6,7,8,9,10,42 \
      --scenario .trellis/tasks/10-01-mbri-fsm-port/evidence/repro/mirror-mbri.json --duration 120 --stats

注意：对照数字（fall≠0 时的 44/44 一致、中位 43→1）建立在一个**已知缺陷**上
（mbri 开场被 reentry 误接管后 SAFE_STOP 冻结）——见 report.md 与对比台账；
修复该缺陷后数字会变，届时此目录的脚本按同法复跑即可。
