# Design — 物理反僵局 (铲刃微调)

## 机制 (全部在 `src/Sim.Core/Physics.cs` 既有楔入判定内)

`ResolveRobotPair` 楔入段现状:

```csharp
var facing = Math.Abs(Js.Norm(a.Th - b.Th));
if (facing > Math.PI * 0.6)
{
    var aBlade = a.ZG + a.Vehicle.ShovelHeight;
    var bBlade = b.ZG + b.Vehicle.ShovelHeight;
    if (Math.Abs(aBlade - bBlade) > 0.004) { ... WedgedFront ... }
}
```

改为: 在 facing 判定内, 双方有效铲刃高度各叠加微调项

```
bladeUs' = bladeUs + A_us · sin(2π · SimT / P_us + φ_us)
bladeThem' = bladeThem + A_them · sin(2π · SimT / P_them + φ_them)
```

- `SimT` = 被判定机器人 FSM 的比赛时间 (两侧取 `a.Fsm.SimT`, 与读秒/FSM 同源,
  逐位确定; FsmRuntime.SimT 对双方一致推进)。
- `A_us = A_them = AntiStallBladeAmp` (SimParameters 新加性键
  `antiStallBladeAmp`, 默认 0.006; 0 = 关闭)。
- `P_us = AntiStallBladePeriodUs` (默认 2.1 s), `P_them = AntiStallBladePeriodThem`
  (默认 2.7 s) — 周期互质错开形成拍频, 双方交替获得楔入优势。
- `φ = (hash(seed, role) / uint.MaxValue) * 2π` — 由 MatchEngine 用既有
  `DeterministicRandom.HashString32`/`Mix32` 派生, 构造 PhysicsWorld 时传入
  (构造函数加两个 double 字段, 默认 0)。
- 叠加后仍用原阈值 `0.004` 与原 `WedgedFront` 路径 — 楔入的一切下游
  (FrontLoad → thrust 0.2、motion、恢复) 不改。

## 参数 (SimParameters 加性, camelCase 键, 缺省值)

| 键 | 默认 | 含义 |
|---|---|---|
| `antiStallBladeAmp` | 0.006 | 铲刃微调振幅 (m); 0 = 关闭 |
| `antiStallBladePeriodUs` | 2.1 | 我方微调周期 (s) |
| `antiStallBladePeriodThem` | 2.7 | 对手微调周期 (s) |

`SimParameters` 为 record 时追加三个可空属性 + FromDictionary 解析 (沿用
既有可空参数模式, 缺省 null → Physics 内取默认)。

## 边界与不变量

- 微调仅存在于 `facing > 0.6π` 的楔入判定分支内; `ZG`/`Pitch`/`Roll` 显示
  姿态、传感器、掉台几何、块体碰撞全部不受影响。
- 非同型车 (铲刃静差已 > 0.004) 也会被微调轻微推移楔入时机 — 有意行为,
  更贴近真实; PORTING_NOTES 记录。
- 确定性: 全部输入 (SimT、seed 派生相、参数) 逐位确定; 无 rng 流消费
  (不触碰 Mulberry32 匹配流)。

## 基线再生成范围 (有意偏差, 全部走正规流程)

| 基线 | 再生成方式 |
|---|---|
| `src/Sim.Tests/fixtures/legacy-fsm-seed21.json` / `legacy-pushoff-seed42.json` | `node tools/legacy-baseline.js` |
| `src/Sim.Tests/fixtures/restart-replay-seed42.json` | 测试内重生成 (删除后首跑) |
| `src/Sim.Tests/fixtures/godot-parity-seed42.json` | CLI `replay-record` 重录 |
| `replays/seed-42.json` | CLI `replay-record` 重录 |

`docs/PORTING_NOTES.md` 追加: 反僵局铲刃微调为有意偏差, 与遗留原型不再逐位
一致; 理由 = 同型对推死锁不符合真实比赛观察。

## 测试设计 (src/Sim.Tests)

- 新增 `AntiStallmateTests` (engine-free, 直接驱动 MatchEngine):
  - `HeadOnPush_WithOscillation_BreaksStalemate`: 官方场景, 双车置于中线
    相向 2 m, 全速 ATTACK 注入动作 (脚本化 `RobotAction`), 跑 20 s —
    断言 10 s 内出现 WedgedFront 且双方相对位置净变化 > 0.05 m。
  - `HeadOnPush_OscillationOff_StaysLocked`: `antiStallBladeAmp=0` 场景,
    60 s 内 WedgedFront 永不发生 (对照, 且锁死旧行为的可选性)。
  - `WedgeDirection_FollowsInstantaneousBladeHeight`: 构造倾斜台面/不同
    `SimT` 相位, 断言楔入方 = 瞬时更高铲刃。
  - `Determinism_SameSeed_BitIdentical`: 同 seed 跑两遍, 快照指纹逐位一致。
- 既有 `PhysicsTests`/回归: 涉及顶牛的旧断言若因再生成基线而变, 随基线
  一起更新, 不手改数值。

## 回滚

- 参数置 0 即回到旧行为 (无代码回滚)。
- 代码回滚: 楔入分支 revert 单提交。
