# 设计：独立实车记录查看

## 边界

- 实车记录和参考采样只作为独立查看数据，不创建模拟 `Snapshot`、不驱动物理/规则/控制器、不改变 `sim-replay-v1`、场地灰度模型或 fidelity。允许单独的 3D 渲染实例消费记录的位姿以展示，不推导赛况。
- 不修改树莓派及用户输入文件；原始坐标、通道方向、实际车体轨迹、缺失的事件均不可推断。相机画面不在范围内。
- Godot 负责独立模态面板和文件选择；导入、校验、导航数据和参考点保存使用可链接到 `Sim.Tests` 的无 Godot 依赖 C# 代码。

## 数据与跨层接口

- `godot/src/RealMatchReviewData.cs` 的命名空间为 `Sim.GodotShell`。数据层提供记录、时间序列、统计与人工参考点类型，以及加载、校验、参考点保存/重开；不得依赖 `Godot`。
- 跨层公开 C# 契约：`ReviewEvent(double Time, string Type, IReadOnlyDictionary<string,string?> Values)`；`ReviewRecording(string Format, IReadOnlyList<ReviewEvent> Events)`；`ReviewSample(double Time, IReadOnlyDictionary<string,double?> Values)`；`ReviewSeries(string Source, IReadOnlyList<string> Channels, IReadOnlyList<ReviewSample> Rows)`；`ReviewStats(int Count, double Min, double Max, double Mean)`；`ReviewReference(string Name, string Source, int StartIndex, int EndIndex, double X, double Y, double Heading, IReadOnlyDictionary<string,ReviewStats> Stats)`。`RealMatchReviewData` 暴露 `LoadRecording(string path)`、`LoadSeries(string path)`、`MakeReference(ReviewSeries series, string name, int startIndex, int endIndex, double x, double y, double heading)`、`LoadReferences(string path)`、`SaveReferences(string path, IReadOnlyList<ReviewReference> references)`；索引起止包含，输入不合法抛带原因的异常，UI 捕获并显示。
- 对真实巡台 CSV 精确匹配 `t,front,rear,left,right,alert,on_stage,action,ir1,ir2`，空字段保留为缺失，非递减时间允许相同值并保留原始顺序；空文件和不支持的格式应报告错误。CSV 灰度列不能擅自称为原始 ADC。
- `real-match-v1` 使用独立 JSON 文档：`format` 为 `real-match-v1`，`events` 为依原时间顺序的数组；每条 `{ "time": 秒, "type": 类型, "values": {字段名: 原始值} }`，时间为同一记录的相对秒，允许相同时间并保序；decision、sensor、safety、state、vision、motor 为示例事件类型，缺失值保持缺失。对来源无法保证统一时钟的数据不冒充同步。
- 采样 CSV 支持精确表头 `t,front,rear,left,right` 和 `t,left,right,valid`；显示原值、时间和各数值列的数量/最小/最大/均值。原列名不自动交换，显示映射待核实。
- 参考点只由用户显式确认静止区间后创建，索引保留源文件和起止行、用户输入的场局部 X/Y（米）、朝向（弧度）、区间统计，保存独立 JSON，重新打开后可在场地图点选；不更改采样源文件。
- `.log` 无可靠时间戳，仅显示独立文本，不关联/补全 CSV 事件。新格式通过合成示例和文档提供消费契约，不能宣称有完整真车录制。
- `real-match-v1` 新增可选 `type: "pose"` 事件：`values` 必须包含 `role`（`us` / `them`）、场局部 `x`、`y`（米）、`heading`（弧度）和非空 `source`（原样展示，例如 `measured` 或 `synthetic`）；记录事件同一时间基准、非递减，重复时间按原始事件顺序。数据层公开 `ReviewPose(int EventIndex, double Time, string Role, double X, double Y, double Heading, string Source)` 和 `RealMatchReviewData.ExtractPoses(ReviewRecording recording)` / `RealMatchReviewData.PoseAtOrBefore(IReadOnlyList<ReviewPose> poses, string role, int eventIndex)`；缺失/格式无效的位姿事件明确报错，不用默认坐标。
- 3D 视图以最近一条**已记录**位姿渲染并标注来源和记录时刻，不插值或外推；未出现位姿前或旧 CSV 不显示该车辆、状态提示“未记录位姿”。另一个角色没有位姿时隐藏，不伪造能量块、得分和登台状态。二维位姿未含高度，显示高度由当前场地图形投影并明确标记为展示假设，不当作实测。

## 桌面行为

- 在现有 HUD 增加独立“实车复盘”入口，打开独立面板；复盘导航不借用 `MatchSession.LoadReplay`。旧 CSV 单步对应记录行；新格式单步查看实际提供的决策/事件，单条记录内及决策之间的安全/电机事件不得隐藏或伪造。
- 包含读取记录、上一条/下一条、播放/暂停和时间轴、未记录提示、独立日志查看、采样曲线及统计、区间静止确认、坐标/朝向输入、参考点保存/加载与场地示意图。
- 打开面板后应屏蔽比赛快捷键和相机输入，关闭后恢复；保持用户设置、比赛流程和原有模拟回放。
- 增加隔离 3D 擂台视图，用独立渲染场景，按所有事件的原始时间和顺序推进播放头；前后单步仍以真实决策为主（旧 CSV 逐行），拖动时间线/跳步时仅显示目标时刻及之前已记录的位姿，安全/电机事件仍可查看。不可用旧 CSV 动作猜坐标。
