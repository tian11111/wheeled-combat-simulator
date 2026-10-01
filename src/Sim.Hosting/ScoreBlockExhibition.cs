using Sim.Core;
using Sim.Protocol;

namespace Sim.Hosting;

/// <summary>
/// SCORE_BLOCK 展演共享缝（纯函数）: 与训练入口 <c>rl-env</c> 同语义的
/// 双方内置 FSM 预推进、目标锁定与 11 维 RL 观测投影。
///
/// 唯一实现: <c>Sim.Cli.RlEnvCommand</c>（训练/评测）、无头 runner 与桌面驱动
/// 都委托这里; 任何第二份投影（尤其 Python 侧重算 bx/by、relForward/relLeft、
/// 归一化 x/y）都是缺陷。
///
/// 纪律: 无 IO、无时钟、无 RNG; 只读引擎状态, 唯一的写入是调用方显式请求的
/// <see cref="ArmAndPreroll"/> 预推进。进程/时钟编排留在 Sim.Cli/桌面/Sim.Controller 桥。
/// </summary>
public static class ScoreBlockExhibition
{
    /// <summary>
    /// 前 9 项: 目标块相对车体坐标/平台 X 边长 (relForward/relLeft)、块坐标/边长、
    /// v/w 归一、我方在台标志、目标在台标志、剩余时间比。顺序是 rl-env 契约的一部分。
    /// </summary>
    public const int BaseObservationSize = 9;

    /// <summary>前 9 项 + 我方位置相对平台中心的归一化 x/y（相对半边长）。</summary>
    public const int ObservationSize = 11;

    /// <summary>预推进上限: 4800 tick（与 <c>RlEnvCommand</c> 训练入口同值）。</summary>
    public const int PrerollMaxTicks = 4800;

    /// <summary>预推进上限内双方内置 FSM 未让我方进入 SCORE_BLOCK。</summary>
    public const string NoScoreBlockReason = "no_score_block";

    /// <summary>已进入 SCORE_BLOCK 但没有锁定的台上增益块。</summary>
    public const string NoValidTargetReason = "score_block_without_valid_buff_target";

    /// <summary>
    /// 预推进结果。<see cref="Reason"/> 为 null 即完成交接: <see cref="EntryTick"/> /
    /// <see cref="TargetIndex"/> / <see cref="EntrySnapshot"/> 描述交接帧; 否则是
    /// <see cref="NoScoreBlockReason"/> 或 <see cref="NoValidTargetReason"/>。
    /// </summary>
    public readonly record struct PrerollResult(
        bool NoScoreBlock,
        string? Reason,
        int EntryTick,
        int TargetIndex,
        Snapshot EntrySnapshot,
        int PrerollTicks)
    {
        /// <summary>True 表示预推进在我方 SCORE_BLOCK 状态停下且目标已锁定（可交接）。</summary>
        public bool HasTarget => !NoScoreBlock && TargetIndex >= 0;
    }

    /// <summary>
    /// <c>Arm()</c> 之后的双方内置 FSM 预推进: 每 tick 双方都不注入外部动作, 直到
    /// 我方首次进入 <see cref="FsmState.ScoreBlock"/>、比赛结束或达到
    /// <paramref name="maxTicks"/> 上限。停在 SCORE_BLOCK 后再按 FSM 规则锁定目标块。
    /// 调用方必须先 <c>engine.Arm()</c>（本方法不做发令, 也不消耗 RNG 之外的任何外部状态）。
    /// </summary>
    public static PrerollResult ArmAndPreroll(MatchEngine engine, int maxTicks = PrerollMaxTicks)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTicks);

        var guard = 0;
        var snap = engine.CommitSnapshot();
        while (!engine.Done && guard < maxTicks)
        {
            if (engine.Us.Fsm.State == FsmState.ScoreBlock)
            {
                break;
            }
            snap = engine.Tick();
            guard++;
        }

        var entryTick = (int)engine.TickIndex;
        if (engine.Done || engine.Us.Fsm.State != FsmState.ScoreBlock)
        {
            return new PrerollResult(true, NoScoreBlockReason, entryTick, -1, snap, guard);
        }

        var targetIndex = LockTargetIndex(engine);
        return targetIndex < 0
            ? new PrerollResult(true, NoValidTargetReason, entryTick, -1, snap, guard)
            : new PrerollResult(false, null, entryTick, targetIndex, snap, guard);
    }

    /// <summary>
    /// 目标锁定: 优先取我方 FSM 已选中的 <c>ScoreTarget</c>; 为空时按 FSM 规则取
    /// 第一个"台上未出界的增益块"; 都没有则 -1。
    /// </summary>
    public static int LockTargetIndex(MatchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var locked = engine.Us.Fsm.ScoreTarget;
        if (locked is not null)
        {
            for (var i = 0; i < engine.Blocks.Count; i++)
            {
                if (ReferenceEquals(engine.Blocks[i], locked))
                {
                    return i;
                }
            }
        }
        // ScoreTarget 为空: 按现有 FSM 规则选第一个有效增益块。
        for (var i = 0; i < engine.Blocks.Count; i++)
        {
            var b = engine.Blocks[i];
            if (b.Kind == BlockKind.Buff && !b.Out && engine.Field.OnPlatform(b.X, b.Y))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 11 维 RL 观测投影（rl-env 契约唯一实现）。显式参数化 episode 语义:
    /// <paramref name="platform"/>/<paramref name="matchDuration"/> 来自 episode 场景,
    /// <paramref name="usOnPlatform"/>/<paramref name="timer"/> 来自交接/提交帧快照
    /// （与 rl-env 的 <c>snap.Robots[us].OnPlatform</c> / <c>snap.Timer</c> 同源）。
    /// 所有项 clamp 到 [-1,1]。
    /// </summary>
    public static double[] BuildObservation(MatchEngine engine, int targetIndex, Region platform,
        double matchDuration, bool usOnPlatform, double timer)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(platform);

        var side = platform.MaxX - platform.MinX;
        var (bx, by) = targetIndex >= 0 && targetIndex < engine.Blocks.Count
            ? (engine.Blocks[targetIndex].X, engine.Blocks[targetIndex].Y)
            : (double.NaN, double.NaN);
        var us = engine.Us;
        var dx = bx - us.X;
        var dy = by - us.Y;
        var cos = Math.Cos(us.Th);
        var sin = Math.Sin(us.Th);
        var relForward = double.IsNaN(dx) ? 0.0 : (cos * dx + sin * dy) / side;
        var relLeft = double.IsNaN(dx) ? 0.0 : (-sin * dx + cos * dy) / side;
        var remaining = matchDuration > 0 ? timer / matchDuration : 0.0;
        var clip = (double v) => Clamp(v, -1.0, 1.0);
        var baseObs = new[]
        {
            clip(relForward),
            clip(relLeft),
            clip(bx / side),
            clip(by / side),
            Clamp(us.V / (us.Vehicle.MaxSpeed != 0 ? us.Vehicle.MaxSpeed : 1.5), -1.0, 1.0),
            Clamp(us.Omega / (us.Vehicle.MaxTurnRate != 0 ? us.Vehicle.MaxTurnRate : 4.0), -1.0, 1.0),
            usOnPlatform ? 1.0 : 0.0,
            targetIndex >= 0 && engine.Field.OnPlatform(engine.Blocks[targetIndex].X, engine.Blocks[targetIndex].Y) ? 1.0 : 0.0,
            Clamp(remaining, 0.0, 1.0),
        };
        return AppendOwnPositionObservation(baseObs, us.X, us.Y, platform);
    }

    /// <summary>
    /// 末尾追加我方位置: 相对平台中心、按平台半边长归一化的 x/y（clamp 到 [-1,1]）。
    /// </summary>
    public static double[] AppendOwnPositionObservation(double[] observation, double ownX, double ownY, Region platform)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(platform);
        if (observation.Length != BaseObservationSize)
        {
            throw new ArgumentException($"expected {BaseObservationSize} base observation values", nameof(observation));
        }

        var halfSide = (platform.MaxX - platform.MinX) / 2.0;
        var centerX = (platform.MinX + platform.MaxX) / 2.0;
        var centerY = (platform.MinY + platform.MaxY) / 2.0;
        var expanded = new double[ObservationSize];
        Array.Copy(observation, expanded, BaseObservationSize);
        expanded[BaseObservationSize] = Clamp((ownX - centerX) / halfSide, -1.0, 1.0);
        expanded[BaseObservationSize + 1] = Clamp((ownY - centerY) / halfSide, -1.0, 1.0);
        return expanded;
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));
}
