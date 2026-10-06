using Sim.Core;
using Sim.GodotShell;

namespace Sim.Tests;

/// <summary>
/// Locks the layout-editor entry matrix (godot/src/EditorGate.cs).
///
/// Regression under test (2026-10-06): the gate used to require
/// <see cref="MatchControlPhase.Prep"/>. The kernel leaves Prep on its own once
/// the 60 s prep countdown expires and flips to Ready, while the snapshot keeps
/// reporting both as <c>MatchPhase.Prep</c> — so the HUD still said 「发令准备」
/// and pressing E silently did nothing. Ready must therefore be allowed.
/// </summary>
public sealed class EditorGateTests
{
    [Theory]
    [InlineData(MatchControlPhase.Prep)]
    [InlineData(MatchControlPhase.Ready)]
    public void LiveWithoutDriver_NotYetStarted_AllowsEditor(MatchControlPhase phase)
    {
        Assert.True(EditorGate.CanEnter(SessionMode.Live, liveDriverActive: false, phase));
        Assert.Null(EditorGate.RejectReason(SessionMode.Live, liveDriverActive: false, phase));
    }

    [Theory]
    [InlineData(MatchControlPhase.Running)]
    [InlineData(MatchControlPhase.Paused)]
    public void LiveWithoutDriver_StartedMatch_RejectsWithRunningReason(MatchControlPhase phase)
    {
        Assert.False(EditorGate.CanEnter(SessionMode.Live, liveDriverActive: false, phase));
        Assert.Equal("比赛已进行中 · 按 F5 重置后再按 E",
            EditorGate.RejectReason(SessionMode.Live, liveDriverActive: false, phase));
    }

    [Fact]
    public void LiveWithoutDriver_Finished_RejectsWithFinishedReason()
    {
        // Finished must not be reported as "in progress": the user needs the F5 hint
        // that matches what actually happened.
        Assert.Equal("比赛已结束 · 按 F5 重置后再按 E",
            EditorGate.RejectReason(SessionMode.Live, liveDriverActive: false, MatchControlPhase.Finished));
    }

    [Theory]
    [InlineData(MatchControlPhase.Prep)]
    [InlineData(MatchControlPhase.Ready)]
    [InlineData(MatchControlPhase.Running)]
    public void LiveDriverActive_AlwaysRejects(MatchControlPhase phase)
    {
        Assert.Equal("外部控制器运行中 · 按 F5 重置后再编辑",
            EditorGate.RejectReason(SessionMode.Live, liveDriverActive: true, phase));
    }

    [Theory]
    [InlineData(MatchControlPhase.Prep)]
    [InlineData(MatchControlPhase.Ready)]
    [InlineData(MatchControlPhase.Running)]
    public void Replay_AlwaysRejects(MatchControlPhase phase)
    {
        Assert.Equal("回放模式不能编辑 · 按 F5 回到实况",
            EditorGate.RejectReason(SessionMode.Replay, liveDriverActive: false, phase));
    }

    [Fact]
    public void EveryRejectionHasANonEmptyDistinctReason()
    {
        // The reason is shown verbatim on the HUD; an empty string would look like
        // "nothing happened" again.
        var reasons = new[]
        {
            EditorGate.RejectReason(SessionMode.Replay, false, MatchControlPhase.Prep),
            EditorGate.RejectReason(SessionMode.Live, true, MatchControlPhase.Prep),
            EditorGate.RejectReason(SessionMode.Live, false, MatchControlPhase.Running),
            EditorGate.RejectReason(SessionMode.Live, false, MatchControlPhase.Finished),
        };
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }
}
