using Sim.Protocol;
using Sim.GodotShell;

namespace Sim.Tests;

/// <summary>
/// Headless regression for the Godot-free layout draft layer: editing
/// semantics, undo/redo, snapping helpers, validation and atomic
/// save/reload round-trip through the canonical arena-layout-v1 JSON.
/// </summary>
public class LayoutDraftTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    private static Scenario OfficialBase() => new()
    {
        Seed = 42,
        Blocks = OfficialLayout.Blocks,
    };

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simtest-{Guid.NewGuid():N}-{name}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Draft_KeepsOfficialLayout_AndIsApplicable()
    {
        var draft = new LayoutDraft(OfficialBase());
        Assert.Equal(0, draft.State.Pose.X);
        Assert.Equal(0, draft.State.Pose.Y);
        Assert.Equal(0, draft.State.Pose.Th);
        Assert.Equal(new Region { MinX = 0.7, MinY = 0.1, MaxX = 1.2, MaxY = 0.5 },
            draft.State.StartZones[RoleNames.Us]);
        Assert.Equal(3, draft.State.Blocks.Count);
        Assert.True(draft.CanApply);
    }

    [Fact]
    public void MoveField_AndRotateField_UpdatePose()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveField(0.5, -0.25);
        draft.RotateField(LayoutDraft.SnapRotation(0.4));
        Assert.Equal(0.5, draft.State.Pose.X);
        Assert.Equal(-0.25, draft.State.Pose.Y);
        Assert.Equal(RoundTo(0.4), draft.State.Pose.Th);
    }

    private static double RoundTo(double yaw)
        => Math.Round(yaw / LayoutDraft.RotationSnap) * LayoutDraft.RotationSnap;

    [Fact]
    public void SnapHelpers_RoundToGrid()
    {
        Assert.Equal(0.13, LayoutDraft.SnapTranslation(0.127), 9);
        Assert.Equal(0.0, LayoutDraft.SnapTranslation(0.004), 9);
        Assert.Equal(Math.PI / 4, LayoutDraft.SnapRotation(Math.PI / 4), 9);
        Assert.Equal(10 * Math.PI / 180, LayoutDraft.SnapRotation(9.4 * Math.PI / 180), 9);
    }

    [Fact]
    public void MoveStartZone_ShiftsZoneAndStartTogether()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveStartZone(RoleNames.Us, 0.2, -0.1);

        var zone = draft.State.StartZones[RoleNames.Us];
        Assert.Equal(0.9, zone.MinX, 9);
        Assert.Equal(0.0, zone.MinY, 9);
        Assert.Equal(1.15, draft.State.Starts[RoleNames.Us].X, 9);
        Assert.Equal(0.2, draft.State.Starts[RoleNames.Us].Y, 9);
        Assert.Equal(-Math.PI / 2, draft.State.Starts[RoleNames.Us].Th);
    }

    [Fact]
    public void UndoRedo_RestoresStepsAndClearsRedoOnNewEdit()
    {
        var draft = new LayoutDraft(OfficialBase());
        var initial = draft.State.Copy();

        draft.MoveField(1, 0);
        draft.MoveField(0, 2);
        Assert.Equal(1, draft.State.Pose.X);
        Assert.Equal(2, draft.State.Pose.Y);

        draft.Undo();
        Assert.Equal(1, draft.State.Pose.X);
        Assert.Equal(0, draft.State.Pose.Y);

        draft.Undo();
        Assert.Equal(0, draft.State.Pose.X);
        Assert.Equal(0, draft.State.Pose.Y);

        Assert.False(draft.CanUndo);
        Assert.True(draft.CanRedo);
        draft.Redo();
        Assert.Equal(1, draft.State.Pose.X);
        Assert.Equal(0, draft.State.Pose.Y);

        // A fresh edit after undo drops the redo branch.
        draft.MoveField(5, 5);
        Assert.False(draft.CanRedo);
        Assert.Equal(6, draft.State.Pose.X);
        while (draft.CanUndo)
        {
            draft.Undo();
        }
        Assert.Equal(initial.Pose, draft.State.Pose);
    }

    [Fact]
    public void NoOpEdit_DoesNotPolluteHistory()
    {
        var draft = new LayoutDraft(OfficialBase());
        var delta = LayoutDraft.SnapTranslation(0.001); // 0.001 < half a grid cell → 0
        Assert.Equal(0, delta, 12);
        draft.MoveField(delta, 0);
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void DragGroup_CommitsAsSingleUndoStep()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.BeginGroup();
        for (var i = 0; i < 10; i++)
        {
            draft.MoveField(0.05, 0);
        }
        draft.EndGroup();

        Assert.True(draft.CanUndo);
        Assert.Equal(0.5, draft.State.Pose.X, 9);
        draft.Undo();
        Assert.Equal(0, draft.State.Pose.X, 9);
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void DragGroup_WithoutActualChange_LeavesNoHistory()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.BeginGroup();
        draft.MoveField(0, 0);
        draft.EndGroup();
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void MoveStart_ChangesPositionOnly_KeepsHeadingZonesAndOtherRoles()
    {
        var draft = new LayoutDraft(OfficialBase());
        var zoneBefore = draft.State.StartZones[RoleNames.Us];
        var themBefore = draft.State.Starts[RoleNames.Them];

        draft.MoveStart(RoleNames.Us, 1.05, 0.4);

        var start = draft.State.Starts[RoleNames.Us];
        Assert.Equal(1.05, start.X, 9);
        Assert.Equal(0.4, start.Y, 9);
        Assert.Equal(-Math.PI / 2, start.Th); // heading preserved bit-for-bit
        Assert.Equal(zoneBefore, draft.State.StartZones[RoleNames.Us]);
        Assert.Equal(themBefore, draft.State.Starts[RoleNames.Them]);
    }

    [Fact]
    public void MoveStart_DragGroup_CommitsAsOneUndoStep()
    {
        var draft = new LayoutDraft(OfficialBase());
        var before = draft.State.Starts[RoleNames.Us];

        draft.BeginGroup();
        draft.MoveStart(RoleNames.Us, 0.8, 0.2);
        draft.MoveStart(RoleNames.Us, 1.1, 0.45);
        draft.EndGroup();

        Assert.Equal(1.1, draft.State.Starts[RoleNames.Us].X, 9);
        Assert.Equal(0.45, draft.State.Starts[RoleNames.Us].Y, 9);
        Assert.True(draft.CanUndo);
        draft.Undo();
        Assert.Equal(before, draft.State.Starts[RoleNames.Us]);
        Assert.False(draft.CanUndo); // one continuous drag = one undo entry
        draft.Redo();
        Assert.Equal(1.1, draft.State.Starts[RoleNames.Us].X, 9);
        Assert.Equal(0.45, draft.State.Starts[RoleNames.Us].Y, 9);
    }

    [Fact]
    public void MoveStart_NoOpInsideGroup_LeavesNoHistory()
    {
        var draft = new LayoutDraft(OfficialBase());
        var before = draft.State.Starts[RoleNames.Us];
        draft.BeginGroup();
        draft.MoveStart(RoleNames.Us, before.X, before.Y);
        draft.EndGroup();
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void MoveStart_BuildScenarioCarriesStartIntoKernelSpawn()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveStart(RoleNames.Us, 1.05, 0.4);

        var scenario = draft.BuildScenario();
        Assert.Equal(1.05, scenario.Field.Starts[RoleNames.Us].X, 9);
        Assert.Equal(0.4, scenario.Field.Starts[RoleNames.Us].Y, 9);
        Assert.Equal(-Math.PI / 2, scenario.Field.Starts[RoleNames.Us].Th);

        // The kernel spawns the robot exactly at the edited field-local start.
        var engine = new Sim.Core.MatchEngine(scenario);
        var snapshot = engine.CommitSnapshot();
        var (sx, sy) = engine.Field.Transform.LocalToWorldPoint(1.05, 0.4);
        Assert.Equal(sx, snapshot.Robots[RoleNames.Us].X, 12);
        Assert.Equal(sy, snapshot.Robots[RoleNames.Us].Y, 12);
    }

    [Fact]
    public void MoveStart_OutsideField_IsRejectedByValidation()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveStart(RoleNames.Us, -0.5, 0.3);
        Assert.False(draft.CanApply);
        Assert.Contains(draft.Validate(), e => e.Contains("must be inside the inner field"));
        draft.Undo();
        Assert.True(draft.CanApply);
    }

    [Fact]
    public void MoveBlock_FixesCoordinatesAndCanonicalOutput()
    {
        var draft = new LayoutDraft(new Scenario
        {
            Seed = 7,
            Blocks = [new BlockSpec { Kind = BlockKind.Buff }], // seeded (null) placement
        });
        draft.MoveBlock(0, 1.2, 2.3);
        var scenario = draft.BuildScenario();
        Assert.Equal(ProtocolVersion.ArenaLayoutV1, scenario.LayoutVersion);
        Assert.NotNull(scenario.Field.Pose);
        Assert.Equal(1.2, scenario.Blocks[0].X);
        Assert.Equal(2.3, scenario.Blocks[0].Y);
        // Base ruleset/seed/vehicles are preserved by the draft.
        Assert.Equal(7, scenario.Seed);
        Assert.Equal("wushu-ring-2026", scenario.Id);
    }

    [Fact]
    public void Validation_RejectsOutOfBoundsEdits_CanApplyFalse()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveBlock(0, 9.9, 0.5); // outside the 3.8 m field
        Assert.False(draft.CanApply);
        Assert.Contains(draft.Validate(), e => e.Contains("inside the inner field"));

        draft.Undo();
        Assert.True(draft.CanApply);
    }

    [Fact]
    public void RestoreOfficial_ResetsEverything()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveField(1.5, 1.5);
        draft.MoveStartZone(RoleNames.Them, -0.3, -0.3);
        draft.RestoreOfficial();
        var official = LayoutDraft.OfficialState();
        Assert.Equal(0, draft.State.Pose.X);
        Assert.Equal(official.StartZones[RoleNames.Us], draft.State.StartZones[RoleNames.Us]);
        Assert.Equal(official.Blocks.Count, draft.State.Blocks.Count);
        Assert.Equal(official.Blocks[2], draft.State.Blocks[2]);
    }

    [Fact]
    public void LoadFrom_ReplacesStateAndClearsHistory()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveField(2, 2);
        draft.LoadFrom(OfficialBase() with { Seed = 99 });
        Assert.False(draft.CanUndo);
        Assert.False(draft.CanRedo);
        Assert.Equal(0, draft.State.Pose.X);
    }

    [Fact]
    public void SaveTo_WritesCanonicalJson_AndReadScenarioReloadsIdenticalDraft()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveField(LayoutDraft.SnapTranslation(0.3), -0.15);
        draft.RotateField(-LayoutDraft.RotationSnap);
        draft.MoveBlock(1, 2.4, 2.7);
        Assert.True(draft.CanApply);

        var path = TempPath("layout.json");
        var saved = draft.SaveTo(path);
        var json = File.ReadAllText(path);
        Assert.Contains("arena-layout-v1", json);
        Assert.Contains("\"pose\"", json);

        var reloaded = LayoutDraft.ReadScenario(path);
        Assert.Equal(ProtocolJson.Serialize(saved), ProtocolJson.Serialize(reloaded));

        var draft2 = new LayoutDraft(reloaded);
        Assert.Equal(saved.Field.Pose, draft2.State.Pose);
        Assert.Equal(saved.Blocks.Select(b => (b.Kind, b.X, b.Y)),
            draft2.State.Blocks.Select(b => (b.Kind, b.X, b.Y)));

        // The saved file drives the kernel: transformed spawns match the draft.
        var engine = new Sim.Core.MatchEngine(reloaded);
        var snapshot = engine.Tick();
        var t = engine.Field.Transform;
        var (sx, sy) = t.LocalToWorldPoint(saved.Field.Starts[RoleNames.Us].X, saved.Field.Starts[RoleNames.Us].Y);
        Assert.Equal(sx, snapshot.Robots[RoleNames.Us].X, 12);
        Assert.Equal(sy, snapshot.Robots[RoleNames.Us].Y, 12);
    }

    [Fact]
    public void SaveTo_RejectsInvalidLayout_AndLeavesNoStaleFile()
    {
        var draft = new LayoutDraft(OfficialBase());
        draft.MoveStartZone(RoleNames.Us, -2, 0); // out of bounds
        var path = TempPath("bad-layout.json");
        Assert.Throws<InvalidOperationException>(() => draft.SaveTo(path));
        Assert.False(File.Exists(path));
    }

    // ---------- block add / remove / kind toggle (2026-10-04) ----------

    [Fact]
    public void AddBlock_PlacesFreeSpot_AwayFromBlocksAndStarts()
    {
        var draft = new LayoutDraft(OfficialBase());
        var index = draft.AddBlock(BlockKind.Buff);
        Assert.Equal(3, index);

        var added = draft.State.Blocks[index];
        Assert.Equal(BlockKind.Buff, added.Kind);
        Assert.NotNull(added.X);
        Assert.NotNull(added.Y);
        Assert.InRange(added.X!.Value, 0.30, 3.8 - 0.30);
        Assert.InRange(added.Y!.Value, 0.30, 3.8 - 0.30);
        foreach (var b in draft.State.Blocks.Take(3))
        {
            Assert.True(Math.Abs(Math.Sqrt(
                    Math.Pow(added.X.Value - b.X!.Value, 2) + Math.Pow(added.Y.Value - b.Y!.Value, 2))) >= 0.35,
                "new block must keep 0.35 m from every existing block");
        }
        foreach (var s in draft.State.Starts.Values)
        {
            Assert.True(Math.Sqrt(
                    Math.Pow(added.X.Value - s.X, 2) + Math.Pow(added.Y.Value - s.Y, 2)) >= 0.5,
                "new block must keep 0.5 m from both robot starts");
        }
        Assert.True(draft.CanApply);
    }

    [Fact]
    public void AddBlock_StopsAtMaxBlocks_AndLeavesDraftUntouched()
    {
        var draft = new LayoutDraft(OfficialBase());
        var added = 0;
        while (draft.AddBlock(BlockKind.Buff) >= 0)
        {
            added++;
        }

        Assert.True(added > 0, "auto-placement must place at least one block");
        Assert.True(draft.State.Blocks.Count <= Scenario.MaxBlocks);
        var before = draft.State.Blocks.ToList();
        Assert.Equal(-1, draft.AddBlock(BlockKind.Debuff));
        Assert.Equal(before.Count, draft.State.Blocks.Count);
        Assert.Equal(ProtocolJson.Serialize(before), ProtocolJson.Serialize(draft.State.Blocks));
    }

    [Fact]
    public void AddToggleRemove_AreUndoable_AndSaveRoundTrip()
    {
        var draft = new LayoutDraft(OfficialBase());
        var index = draft.AddBlock(BlockKind.Debuff);
        Assert.Equal(4, draft.State.Blocks.Count);
        draft.Undo();
        Assert.Equal(3, draft.State.Blocks.Count);
        draft.Redo();
        Assert.Equal(4, draft.State.Blocks.Count);

        Assert.Equal(BlockKind.Debuff, draft.State.Blocks[index].Kind);
        Assert.Equal(BlockKind.Buff, draft.ToggleBlockKind(index));
        Assert.Equal(BlockKind.Buff, draft.State.Blocks[index].Kind);
        draft.Undo();
        Assert.Equal(BlockKind.Debuff, draft.State.Blocks[index].Kind);

        Assert.True(draft.RemoveBlock(index));
        Assert.Equal(3, draft.State.Blocks.Count);
        draft.Undo();
        Assert.Equal(4, draft.State.Blocks.Count);
        Assert.Equal(BlockKind.Debuff, draft.State.Blocks[index].Kind);

        // saved canonical JSON carries the mixed-kind layout back intact
        draft.RemoveBlock(index);
        draft.AddBlock(BlockKind.Debuff);
        var path = TempPath("blocks-layout.json");
        var saved = draft.SaveTo(path);
        Assert.Equal(4, saved.Blocks.Count);
        Assert.Equal(2, saved.Blocks.Count(b => b.Kind == BlockKind.Buff));
        Assert.Equal(2, saved.Blocks.Count(b => b.Kind == BlockKind.Debuff));
        var reloaded = new LayoutDraft(LayoutDraft.ReadScenario(path));
        Assert.Equal(
            draft.State.Blocks.Select(b => (b.Kind, b.X, b.Y)),
            reloaded.State.Blocks.Select(b => (b.Kind, b.X, b.Y)));
    }
}
