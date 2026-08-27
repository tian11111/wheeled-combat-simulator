using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Headless proof of the editor apply loop: spawn-resolved drafting, edits,
/// atomic save, and reload produce one canonical file that both the CLI-side
/// engine and the shell session agree on (platform, starts, blocks).
/// </summary>
public class ArenaLayoutFlowTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simflow-{Guid.NewGuid():N}-{name}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void ResolvedBlocksScenario_FreezesSeededPlacementsAtEnginePositions()
    {
        var scenario = new Scenario { Seed = 42 }; // null block coords → seeded placement
        var session = new MatchSession(scenario);
        var resolved = session.ScenarioWithResolvedBlocks();

        Assert.All(resolved.Blocks, b => Assert.NotNull(b.X));
        Assert.All(resolved.Blocks, b => Assert.NotNull(b.Y));
        for (var i = 0; i < session.Engine.Blocks.Count; i++)
        {
            var (lx, ly) = session.Engine.Field.Transform
                .WorldToLocalPoint(session.Engine.Blocks[i].X, session.Engine.Blocks[i].Y);
            Assert.Equal(lx, resolved.Blocks[i].X!.Value, 12);
            Assert.Equal(ly, resolved.Blocks[i].Y!.Value, 12);
        }
    }

    [Fact]
    public void DraftApply_SaveReload_DrivesEngineWithEditedGeometry()
    {
        var baseScenario = new Scenario
        {
            Seed = 42,
            Blocks = OfficialLayout.Blocks,
        };
        var session = new MatchSession(baseScenario);
        var draft = new LayoutDraft(session.ScenarioWithResolvedBlocks());

        // Edit the layout: move the field, rotate it, drag a block and a zone.
        draft.MoveField(0.6, -0.4);
        draft.RotateField(LayoutDraft.RotationSnap);
        draft.MoveBlock(0, 1.4, 1.5);
        draft.MoveStartZone(RoleNames.Them, -0.1, -0.1);
        Assert.True(draft.CanApply);

        var path = TempPath("edited-layout.json");
        var saved = draft.SaveTo(path);

        // CLI side: engine loaded straight from the file.
        var fileScenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        Assert.Equal(ProtocolJson.Serialize(saved), ProtocolJson.Serialize(fileScenario));

        // Shell side: reload the file as a fresh session (what Apply does).
        var applied = new MatchSession(fileScenario);
        var engine = new MatchEngine(fileScenario);

        // Same geometry on both ends: spawns, blocks, platform membership.
        var snapshotApplied = applied.Engine.CommitSnapshot();
        var snapshotCli = engine.CommitSnapshot();
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            var (ax, ay) = (snapshotApplied.Robots[role].X, snapshotApplied.Robots[role].Y);
            var (bx, by) = (snapshotCli.Robots[role].X, snapshotCli.Robots[role].Y);
            Assert.Equal(ax, bx, 12);
            Assert.Equal(ay, by, 12);
        }
        Assert.Equal(
            snapshotCli.Objects!.Buffs.Select(b => (b.X, b.Y)).ToList(),
            snapshotApplied.Objects!.Buffs.Select(b => (b.X, b.Y)).ToList());

        // Blocks sit where the draft put them (field-local → world).
        var t = engine.Field.Transform;
        var (b0x, b0y) = t.LocalToWorldPoint(1.4, 1.5);
        Assert.Equal(b0x, snapshotCli.Objects.Buffs[0].X, 12);
        Assert.Equal(b0y, snapshotCli.Objects.Buffs[0].Y, 12);

        // Platform membership follows the transformed geometry in world space.
        Assert.True(engine.Field.OnPlatform(b0x, b0y));
        Assert.True(engine.Field.OnPlatform(
            t.LocalToWorldPoint(1.9, 1.9).X, t.LocalToWorldPoint(1.9, 1.9).Y));
        Assert.False(engine.Field.OnPlatform(0.0, 0.0)); // world origin is outside the moved field

        // Same seed + no actions ⇒ identical event streams on both ends.
        for (var i = 0; i < 60; i++)
        {
            engine.Tick();
            applied.Engine.Tick();
        }
        Assert.Equal(engine.Scores.Us, applied.Engine.Scores.Us);
        Assert.Equal(engine.Scores.Them, applied.Engine.Scores.Them);
        Assert.Equal(
            engine.Events.Events.Select(e => $"{e.Tick}|{e.Kind}|{e.Msg}").ToList(),
            applied.Engine.Events.Events.Select(e => $"{e.Tick}|{e.Kind}|{e.Msg}").ToList());
    }

    [Fact]
    public void EditedLayout_RecordedByCli_ReproducesOnShellSideReplayLoad()
    {
        // Apply → record on the "CLI" side, then reconstruct through MatchSession
        // (shell side), mirroring the desktop replay-load path.
        var draft = new LayoutDraft(new Scenario { Seed = 5, Blocks = OfficialLayout.Blocks });
        draft.RotateField(2 * LayoutDraft.RotationSnap);
        draft.MoveField(-0.2, 0.3);
        var scenario = draft.BuildScenario();
        Assert.True(draft.CanApply);

        var engine = new MatchEngine(scenario);
        engine.Arm();
        var events = new List<string>();
        Snapshot last = null!;
        while (!engine.Done)
        {
            last = engine.Tick();
            events.AddRange(last.Events?.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }

        var file = new ReplayFile
        {
            Scenario = scenario,
            Header = engine.BuildReplayHeader(),
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = last.DoneReason,
            EventFingerprints = events,
        };
        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence ?? "(unknown)");

        // The shell's replay reconstruction caches every tick of the edited layout.
        var session = new MatchSession(scenario);
        session.LoadReplay(file);
        Assert.Equal(engine.TickIndex, session.ReplayCache.Count);
    }
}
