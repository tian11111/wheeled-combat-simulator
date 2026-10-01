namespace Sim.Core;

/// <summary>六路数字红外注入（真车 DigiIR.read_states() 的 C# 投影：6 路布尔 + valid）。</summary>
public readonly record struct MbriDigiIr(
    bool Front, bool Rear, bool LeftFront, bool LeftRear, bool RightFront, bool RightRear, bool Valid)
{
    /// <summary>任一路有值（reentry.py:190 DIGI_IR_PINS 迭代语义）。</summary>
    public bool Any => Front || Rear || LeftFront || LeftRear || RightFront || RightRear;
}

/// <summary>前头两路模拟红外注入（真车 IrSensor.read_raw() 的 C# 投影：left/right ADC + valid）。</summary>
public readonly record struct MbriAnalogIr(double Left, double Right, bool Valid);

/// <summary>前墙对齐模型单次输出（ir.py IrAlignmentModel.update dict 的 C# 投影）。</summary>
public sealed record MbriAlignmentObservation
{
    public bool Valid { get; init; }

    public bool Ready { get; init; }

    public double Left { get; init; }

    public double Right { get; init; }

    /// <summary>滤波后 left − right。</summary>
    public double Diff { get; init; }

    /// <summary>max(滤波左, 滤波右)。</summary>
    public double Signal { get; init; }

    /// <summary>ready 且 signal ≥ signal_min(296)。</summary>
    public bool Strong { get; init; }

    /// <summary>invalid / warming / left_bias / right_bias / center。</summary>
    public string Position { get; init; } = "invalid";

    /// <summary>stop / right / left。</summary>
    public string Correction { get; init; } = "stop";
}

/// <summary>
/// 前墙对齐分类模型逐行移植（ir.py:69-136，纯状态机）：9 帧中值滤波后按
/// diff 区间分类——left_bias（diff&lt;−75.1→向右矫正）/ right_bias（diff&gt;63.5→向左
/// 矫正）/ center（区间内保持）；strong = ready 且 signal≥296。参数 config.py:80-84
/// （新车 2026-08-15 四组固定姿态重采重算）。
/// </summary>
public sealed class MbriAlignmentModel
{
    /// <summary>config.py IR_ALIGNMENT_FILTER_WINDOW（正奇数校验同 ir.py:77-78）。</summary>
    public const int FilterWindow = 9;
    public const double DiffLow = -75.1;      // IR_ALIGNMENT_DIFF_LOW
    public const double DiffHigh = 63.5;      // IR_ALIGNMENT_DIFF_HIGH
    public const int Confirm = 3;             // IR_ALIGNMENT_CONFIRM（reentry 用）
    public const double SignalMin = 296.0;    // IR_ALIGNMENT_SIGNAL_MIN
    public const double AdcMax = 10000.0;     // IR_ADC_MAX

    private readonly Queue<double> _left = new(FilterWindow);
    private readonly Queue<double> _right = new(FilterWindow);

    /// <summary>清空滤波窗口（ir.py:86-88 reset；reentry 每次 _start_correct/_finish_approach 调用）。</summary>
    public void Reset()
    {
        _left.Clear();
        _right.Clear();
    }

    /// <summary>ir.py:90-136 update 逐行移植。坏值→0+valid=false 并重置窗口（fail-safe）。</summary>
    public MbriAlignmentObservation Update(MbriAnalogIr raw)
    {
        var valid = raw.Valid;
        var left = raw.Left;
        var right = raw.Right;
        if (!double.IsFinite(left) || !double.IsFinite(right)
            || left < 0.0 || left > AdcMax || right < 0.0 || right > AdcMax)
        {
            left = 0.0;
            right = 0.0;
            valid = false;
        }
        if (!valid)
        {
            Reset();
            return new MbriAlignmentObservation
            {
                Valid = false,
                Ready = false,
                Left = left,
                Right = right,
                Diff = 0.0,
                Signal = 0.0,
                Strong = false,
                Position = "invalid",
                Correction = "stop",
            };
        }

        _left.Enqueue(left);
        if (_left.Count > FilterWindow)
        {
            _left.Dequeue();
        }
        _right.Enqueue(right);
        if (_right.Count > FilterWindow)
        {
            _right.Dequeue();
        }
        var filteredLeft = Median(_left.ToArray());
        var filteredRight = Median(_right.ToArray());
        var diff = filteredLeft - filteredRight;
        var signal = Math.Max(filteredLeft, filteredRight);
        var ready = _left.Count == FilterWindow;
        string position, correction;
        if (!ready)
        {
            position = "warming";
            correction = "stop";
        }
        else if (diff < DiffLow)
        {
            position = "left_bias";
            correction = "right";
        }
        else if (diff > DiffHigh)
        {
            position = "right_bias";
            correction = "left";
        }
        else
        {
            position = "center";
            correction = "stop";
        }
        return new MbriAlignmentObservation
        {
            Valid = true,
            Ready = ready,
            Left = filteredLeft,
            Right = filteredRight,
            Diff = diff,
            Signal = signal,
            Strong = ready && signal >= SignalMin,
            Position = position,
            Correction = correction,
        };
    }

    /// <summary>9 元素中位数（奇数窗取中位；Clone 后排序，不修改输入——批1 教训）。</summary>
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}

/// <summary>掉台回归单次决策输出（reentry.py _result dict 的 C# 投影）。</summary>
public sealed record MbriReentryResult
{
    public int Left { get; init; }

    public int Right { get; init; }

    public string State { get; init; } = "";

    public string Reason { get; init; } = "";

    /// <summary>掉台电平（持续：四路 zone 全&lt;0）。</summary>
    public bool Fall { get; init; }

    /// <summary>掉台触发边沿（连续帧数刚达 FALL_CONFIRM 那帧为 true）。</summary>
    public bool FallEdge { get; init; }

    public MbriGrayObservation Observation { get; init; } = new();
}

/// <summary>
/// 真车 ReentryController 全状态机逐行移植（reentry.py:61-276，纯状态机，无 IO/无时钟/
/// 无随机——真车 time.monotonic 由调用方按 tick 注入）。
/// 流程（reentry.py:10-22）：
///   WAIT（掉台触发：四路 zone 全&lt;0 连续 FALL_CONFIRM=3 帧）
///     → 前头有值：ADC_CORRECT；正后：TURN_180；右侧任一：TURN_RIGHT_90；
///       左侧任一：TURN_LEFT_90；全无值：IR_WAIT（有值后重新分派）
///     → 转向按标定时长完整执行 → ADC_APPROACH 一次性大力前冲撞墙
///       （前头模拟红外 signal≥APPROACH_TOUCH_SIGNAL 判定贴墙 → 停车进 ADC_CORRECT；
///       超时 APPROACH_TIMEOUT → SAFE_STOP）
///     → ADC_CORRECT：9 帧中值滤波按校准区间原地矫正，连续确认正对
///       （超时 CORRECT_TIMEOUT → SAFE_STOP）
///     → REVERSE：倒车直到前头红外无值（超时 REVERSE_TIMEOUT → SAFE_STOP）
///     → SAFE_STOP：灰度恢复（人工/后续上台）后回 WAIT。
/// 参数 config.py:150-158 + 80-84；转向查表复用 MbriPatrol.MotorTurnCalibration
/// （真车 reentry.py:46 与 ring_patrol 共用 MOTOR_TURN_CALIBRATION）。
/// </summary>
public sealed class MbriReentry
{
    public const int FallConfirm = 3;               // REENTRY_FALL_CONFIRM
    public const int CorrectTurnSpeed = 400;        // REENTRY_CORRECT_TURN_SPEED = PATROL_MIN_ACTIVE_SPEED
    public const double CorrectTimeout = 3.0;       // REENTRY_CORRECT_TIMEOUT
    public const int ApproachSpeed = 700;           // REENTRY_APPROACH_SPEED
    public const double ApproachTimeout = 2.0;      // REENTRY_APPROACH_TIMEOUT
    public const double ApproachTouchSignal = 1060.0; // REENTRY_APPROACH_TOUCH_SIGNAL
    public const int ReverseSpeed = 900;            // REENTRY_REVERSE_SPEED
    public const double ReverseTimeout = 3.0;       // REENTRY_REVERSE_TIMEOUT

    /// <summary>
    /// 仿真无人对局的人工复位等价物（批2 评审 finding 1 修复）：真车 SAFE_STOP/IR_WAIT
    /// 等待"灰度恢复（人工/后续上台）"后回 WAIT（reentry.py:22/198-199），headless 对局
    /// 无人工 → 吸收态（评审复现：开场误接管后冻结至终场）。仿真侧在仍掉台且无进展
    /// 满 2s 后重新武装（清掉台计数 → 仍暗的走道重新长出 fall 边沿 → 重走分派），
    /// 避免吸收态；真车语义（等人工）保留在"灰度恢复 → WAIT"出口上。
    /// </summary>
    public const double RearmWaitSeconds = 2.0;

    private readonly MbriRiskModel _model;
    private readonly MbriAlignmentModel _alignment = new();

    public string State { get; private set; } = "WAIT";
    public string Reason { get; private set; } = "等待掉台触发";
    public (int Left, int Right) Command { get; private set; } = (0, 0);

    /// <summary>掉台电平（持续）。</summary>
    public bool Fall { get; private set; }

    /// <summary>最近一次 update 的掉台触发边沿。</summary>
    public bool FallEdge { get; private set; }

    private long _stateStartedTick;
    private int _fallCount;
    private double _turnAngle;
    private double _turnDuration;
    private int _correctCount;
    private readonly double _tickSeconds;

    public MbriReentry(double tickSeconds = MbriUnits.DefaultTickSeconds, MbriRiskModel? model = null)
    {
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        _tickSeconds = tickSeconds;
        _model = model ?? new MbriRiskModel(
            MbriRiskModel.FilterWindow,
            nearEdgeEnter: MbriRiskModel.NearEdgeEnter,
            nearEdgeClear: MbriRiskModel.NearEdgeClear,
            adcMax: MbriRiskModel.AdcMax);
    }

    // ---------- reentry.py:102-108 _enter（注意：不清转向字段，与巡台 _enter 不同） ----------
    private void Enter(string state, long tick, (int Left, int Right) command, string reason)
    {
        State = state;
        _stateStartedTick = tick;
        Command = command;
        Reason = reason;
    }

    // ---------- reentry.py:109-120 _start_from_trigger ----------
    private void StartFromTrigger(MbriDigiIr ir, long tick)
    {
        if (ir.Front)
        {
            StartCorrect(tick, "掉台触发且前头红外亮，直接矫正");
        }
        else if (ir.Rear)
        {
            StartTurn(tick, 180.0, "正后红外亮，转 180 度");
        }
        else if (ir.RightFront || ir.RightRear)
        {
            StartTurn(tick, 90.0, "右侧红外亮，右转 90 度");
        }
        else if (ir.LeftFront || ir.LeftRear)
        {
            StartTurn(tick, -90.0, "左侧红外亮，左转 90 度");
        }
        else
        {
            Enter("IR_WAIT", tick, (0, 0), "掉台但六路红外暂时无值，停车等待");
        }
    }

    // ---------- reentry.py:122-133 _start_turn（angle 正=右转、负=左转） ----------
    private void StartTurn(long tick, double angle, string reason)
    {
        var sign = angle > 0 ? 1.0 : -1.0;
        var table = MbriPatrol.MotorTurnCalibration[angle > 0 ? "right" : "left"];
        if (!table.TryGetValue(Math.Abs(angle), out var entry))
        {
            throw new InvalidOperationException($"转向标定表缺少角度 {Math.Abs(angle)}");
        }
        var (speed, duration) = entry;
        string state;
        if (angle > 0)
        {
            state = Math.Abs(angle) == 90.0 ? "TURN_RIGHT_90" : "TURN_180";
        }
        else
        {
            state = "TURN_LEFT_90";
        }
        _turnAngle = Math.Abs(angle);
        _turnDuration = duration;
        Enter(state, tick, Mix(0, (int)Math.Round(sign * speed)), reason);
    }

    // ---------- reentry.py:135-138 / 146-149 ----------
    private void StartCorrect(long tick, string reason)
    {
        _alignment.Reset();
        _correctCount = 0;
        Enter("ADC_CORRECT", tick, (0, 0), reason);
    }

    private void FinishApproach(long tick, string reason)
    {
        _alignment.Reset();
        _correctCount = 0;
        Enter("ADC_CORRECT", tick, (0, 0), reason);
    }

    // ---------- reentry.py:155-164 _mix（银行家舍入，同巡台） ----------
    private static (int Left, int Right) Mix(int linear, int turn)
    {
        var left = (double)(linear + turn);
        var right = (double)(linear - turn);
        var peak = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 1.0);
        const int limit = MbriPatrol.CommandLimit;
        if (peak > limit)
        {
            var scale = limit / peak;
            left *= scale;
            right *= scale;
        }
        return ((int)Math.Round(left), (int)Math.Round(right));
    }

    private long ElapsedTicks(long tick) => tick - _stateStartedTick;

    private long DurationTicks(double seconds) => MbriUnits.SecondsToTicks(seconds, _tickSeconds);

    /// <summary>
    /// 仅预热灰度滤波窗口，不推进状态机/不计数（批2 评审 finding 1 修复）：
    /// main.py:135-137 在 START_REVERSE 期间喂 reentry 的显式意图是"预热灰度滤波，
    /// 避免动作结束后冷启动误判 SENSOR_STOP"（main.py:136 注释）。逐字副作用
    /// （让状态机同步推进）会让仿真开场——机器人在走道上（合法赛前位姿，随后由
    /// START_REVERSE 倒车上台）——在预热窗内误判掉台并分派，窗后接管并冻结至终场。
    /// 故只喂滤波：窗结束时状态机仍是 WAIT、计数为 0，而滤波已热。
    /// </summary>
    public void Preheat(MbriGraySample raw) => _model.Update(raw);

    // ---------- reentry.py:167-265 update ----------
    /// <summary>单 tick 决策（gray_raw 为 ADC 域四路采样；ir/analog 由调用方注入）。</summary>
    public MbriReentryResult Update(MbriGraySample raw, MbriDigiIr ir, MbriAnalogIr analog, long tick, bool healthy = true)
    {
        var obs = _model.Update(raw);
        var irValid = ir.Valid;
        if (!healthy || !obs.Valid || !obs.Ready || !irValid)
        {
            _fallCount = 0;
            Fall = false;
            FallEdge = false;
            Enter("SENSOR_STOP", tick, (0, 0), "传感器无效或数据过期");
            return Result(obs);
        }

        var minZone = Math.Min(Math.Min(obs.Zone.Front, obs.Zone.Rear),
            Math.Min(obs.Zone.Left, obs.Zone.Right));
        var allDark = minZone < 0.0;   // all(z < 0.0 for z in obs["zone"].values())
        _fallCount = allDark ? _fallCount + 1 : 0;
        Fall = allDark;
        FallEdge = _fallCount == FallConfirm;

        var elapsedTicks = ElapsedTicks(tick);

        if (State == "IR_WAIT")
        {
            if (!Fall)
            {
                Enter("WAIT", tick, (0, 0), "等待掉台触发");
            }
            else if (ir.Any)
            {
                StartFromTrigger(ir, tick);
            }
            else if (elapsedTicks >= DurationTicks(RearmWaitSeconds))
            {
                // 仿真无人复位：仍掉台且六路无值超时 → 重新武装（重分派重试）。
                _fallCount = 0;
                Enter("WAIT", tick, (0, 0), "等待掉台触发（仿真无人复位，重新武装回归）");
            }
            return Result(obs);
        }

        if (State is "WAIT" or "SAFE_STOP" or "SENSOR_STOP")
        {
            if (State != "SAFE_STOP" && FallEdge)
            {
                // WAIT 首次触发 / SENSOR_STOP 恢复后重新达到连续帧数：重走流程
                StartFromTrigger(ir, tick);
            }
            else if (!Fall)
            {
                Enter("WAIT", tick, (0, 0), "等待掉台触发");
            }
            else if (State == "SAFE_STOP" && elapsedTicks >= DurationTicks(RearmWaitSeconds))
            {
                // 仿真无人复位：仍掉台且 SAFE_STOP 超时 → 重新武装（清计数 → 仍暗的
                // 走道重新长出 fall 边沿 → 重走分派），避免吸收态（评审 finding 1）。
                _fallCount = 0;
                Enter("WAIT", tick, (0, 0), "等待掉台触发（仿真无人复位，重新武装回归）");
            }
            // SAFE_STOP 且仍掉台且未到复位时限：保持停车（不重复触发）
            return Result(obs);
        }

        if (State is "TURN_180" or "TURN_LEFT_90" or "TURN_RIGHT_90")
        {
            if (elapsedTicks >= DurationTicks(_turnDuration))
            {
                Enter("ADC_APPROACH", tick, (ApproachSpeed, ApproachSpeed), "定时转向完成，大力前冲撞墙");
            }
            return Result(obs);
        }

        if (State == "ADC_CORRECT")
        {
            var alignment = _alignment.Update(analog);
            if (!alignment.Valid)
            {
                Enter("SAFE_STOP", tick, (0, 0), "模拟红外无效，无法矫正");
                return Result(obs);
            }
            if (elapsedTicks >= DurationTicks(CorrectTimeout))
            {
                Enter("SAFE_STOP", tick, (0, 0), "矫正超时未正对");
                return Result(obs);
            }
            if (!alignment.Ready)
            {
                Command = (0, 0);
                Reason = "ADC 中值滤波准备中";
                return Result(obs);
            }

            if (!alignment.Strong)
            {
                // 信号弱（前头数字红外亮但模拟信号不足）：一次性大力冲撞，不循环
                Enter("ADC_APPROACH", tick, (ApproachSpeed, ApproachSpeed),
                    $"ADC 信号弱 {alignment.Signal:F0}，大力前冲撞墙");
                return Result(obs);
            }

            var d = alignment.Diff;
            if (alignment.Position == "center")
            {
                _correctCount++;
                Command = (0, 0);
                Reason = $"ADC 正对确认 {_correctCount}/{MbriAlignmentModel.Confirm}，diff={d:F0}";
                if (_correctCount >= MbriAlignmentModel.Confirm)
                {
                    Enter("REVERSE", tick, (-ReverseSpeed, -ReverseSpeed), "ADC 矫正完成，倒车");
                }
            }
            else
            {
                _correctCount = 0;
                var sign = alignment.Correction == "right" ? 1.0 : -1.0;
                Command = Mix(0, (int)Math.Round(sign * CorrectTurnSpeed));
                Reason = $"ADC {(alignment.Position == "left_bias" ? "左偏" : "右偏")}，向{(alignment.Correction == "right" ? "右" : "左")}矫正，diff={d:F0}";
            }
            return Result(obs);
        }

        if (State == "ADC_APPROACH")
        {
            if (analog.Valid && Math.Max(analog.Left, analog.Right) >= ApproachTouchSignal)
            {
                // 前头红外 signal 已达贴墙阈值：立即停车防堵转，回矫正
                FinishApproach(tick, "大力冲撞贴墙，停车矫正");
            }
            else if (elapsedTicks >= DurationTicks(ApproachTimeout))
            {
                Enter("SAFE_STOP", tick, (0, 0), "大力冲撞超时未贴墙，停车");
            }
            return Result(obs);
        }

        if (State == "REVERSE")
        {
            if (!ir.Front)
            {
                Enter("SAFE_STOP", tick, (0, 0), "倒车完成（前头红外无值）");
            }
            else if (elapsedTicks >= DurationTicks(ReverseTimeout))
            {
                Enter("SAFE_STOP", tick, (0, 0), "倒车超时");
            }
            return Result(obs);
        }

        // 理论不可达；防御兜底（reentry.py:263-264）
        Enter("SAFE_STOP", tick, (0, 0), "未知状态");
        return Result(obs);
    }

    private MbriReentryResult Result(MbriGrayObservation obs) => new()
    {
        Left = Command.Left,
        Right = Command.Right,
        State = State,
        Reason = Reason,
        Fall = Fall,
        FallEdge = FallEdge,
        Observation = obs,
    };
}
