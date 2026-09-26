# 报告:MBri 控制器接入契约与可观测性(2026-09-25)

## 结论

AC1-AC5 全部达成。官方移植章节 + 量纲映射表落 `docs/CONTROLLER_PROTOCOL.md`;
最小适配器 `controllers/mbri_adapter.py`(smoke/oracle/calibrated 三模式, 拒绝
猜测系数)与 10 项转换契约自测全过;真实 MBri RobotController 60 s smoke
(oracle 与 estimated 两口径)faults 0/0;Manual 语义实测精确化并修正了此前
"零我方事件"的编码误报;全套 373/373、官方场景/旧回放逐位不变。

## AC1 ✅ 量纲/通道映射表

`docs/CONTROLLER_PROTOCOL.md` "移植真车控制器"节:逐字段标注 灰度(×10 换算,
非光学等效)、六路 GPIO IR(**官方场景无等价通道**, 默认 unavailable;合成值标记
仿真近似)、模拟对角 IR(×10000/1.2)、视觉(objects=**特权真值**, confidence=1.0
+oracle 标记, 不伪称 YOLO)、左右轮→v/w(k/b 必须真机标定)、时间(now=obs.t);
附标定步骤模板(k= d/(c·t), b= 2·c·k·t/θ)与三口径运行命令。

## AC2 ✅ 固定观测测试

`controllers/mbri_adapter_selftest.py` 10 项:灰度量纲/满量程边界、缺通道报错
(零动作+fault)、IR 极性合成、oracle 视觉格式与特权标记、now=obs.t、tick 重复
故障、requestId 回显、calibrated 映射 v/w 数值、车辆限幅、缺 k 启动报错、smoke
不导入车代码。全过。

## AC3 ✅ 真车 60 s smoke

oracle 模式:真实 `RobotController` 经 1200 帧无故障(faults 0/0),比分 0:5、
终局 MANUAL(不要求获胜/保真, 符合 AC3 口径)。另附 estimated 口径运行
(明确标记 estimated, 不用于性能结论)。证据:`evidence/oracle-smoke-60s.txt`、
`evidence/estimated-motion-60s.txt`、`evidence/baseline-simbridge-60s.txt`
(用户原始 sim_bridge 复现)。

## AC4 ✅ Manual 语义与事件归属(含误报修正)

oracle smoke 逐事件分类(GB18030 解码):**Arm×2(双方都发令)**;对手 Fsm/Mount
事件 14 条(内置 FSM 照常);我方 FSM 事件在**首个外部动作后停止**(Manual);
裁判 ScoreClock×5 可见。**修正**:此前"零我方事件"是 grep 以 UTF-8 搜 GBK 文件
的编码误报——实际存在 2 条(Arm)。协议/回放/裁判结果零改动。

## AC5 ✅ 回归

全套 373/373;`seed-42.json`/`godot-parity-seed42.json` 逐位 PASS;
`git diff --check` 干净。C# 零改动(纯文档 + Python 示例)。

## 环境事件(记录)

期间 %TEMP% 的 .NET SDK 被系统清理(dotnet.exe 与部分文件缺失),已按
dotnet-install -Force + 系统 host 文件复制修复(8.0.425),基线与全部验证以
修复后环境重跑。

## 边界

oracle/synthesized 输入不用于性能/保真结论;运动映射未经真机标定(标定模板
已提供, k/b 待真机实测);不把 MBri 仓库纳入本仓库;fidelity.json 未晋升。
