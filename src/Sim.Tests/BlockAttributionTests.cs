using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Regression coverage for block-off attribution.
///
/// The referee decides a block's last-contact role from the <em>distinct roles</em> at the
/// largest contact time, not from the number of contact records. The MuJoCo backend reports
/// one record per contact point without deduplicating geom pairs, so a single robot touching
/// a block with several geoms used to be labelled <c>"simultaneous"</c> and scored nothing —
/// which is exactly how the SCORE_BLOCK pilot lost real pushes in both blind rounds.
/// </summary>
public class BlockAttributionTests
{
    private static BlockRuntime MakeBlock(params (string Role, double T)[] contacts)
    {
        var block = new BlockRuntime { Name = "增益块", Kind = BlockKind.Buff };
        block.ContactThisStep.AddRange(contacts);
        return block;
    }

    private static string? RoleOf(params (string Role, double T)[] contacts)
    {
        var block = MakeBlock(contacts);
        PhysicsWorld.FinalizeBlockContacts(new List<BlockRuntime> { block });
        return block.LastContactRole;
    }

    [Fact]
    public void SingleContactRecord_KeepsItsRole()
    {
        Assert.Equal(RoleNames.Us, RoleOf((RoleNames.Us, 0.05)));
        Assert.Equal(RoleNames.Them, RoleOf((RoleNames.Them, 0.05)));
    }

    [Fact]
    public void SeveralRecordsFromOneRobot_AreAttributedToThatRobot()
    {
        // The defect: two or more records for a single robot at the same instant.
        Assert.Equal(RoleNames.Us, RoleOf(
            (RoleNames.Us, 0.03), (RoleNames.Us, 0.045), (RoleNames.Us, 0.05), (RoleNames.Us, 0.05)));
        Assert.Equal(RoleNames.Them, RoleOf(
            (RoleNames.Them, 0.05), (RoleNames.Them, 0.05), (RoleNames.Them, 0.05)));
        Assert.Equal(RoleNames.Us, RoleOf(
            (RoleNames.Us, 0.05), (RoleNames.Us, 0.05), (RoleNames.Us, 0.05), (RoleNames.Us, 0.05)));
    }

    [Fact]
    public void BothRobotsAtTheSameInstant_RemainSimultaneous()
    {
        Assert.Equal("simultaneous", RoleOf((RoleNames.Us, 0.05), (RoleNames.Them, 0.05)));
        Assert.Equal("simultaneous", RoleOf(
            (RoleNames.Us, 0.05), (RoleNames.Us, 0.05), (RoleNames.Them, 0.05)));
        Assert.Equal("simultaneous", RoleOf(
            (RoleNames.Them, 0.05), (RoleNames.Them, 0.05), (RoleNames.Us, 0.05), (RoleNames.Us, 0.05)));
    }

    [Fact]
    public void OnlyTheLargestContactTimeDecides_AnEarlierTieIsIgnored()
    {
        Assert.Equal(RoleNames.Us, RoleOf(
            (RoleNames.Us, 0.02), (RoleNames.Them, 0.02), (RoleNames.Us, 0.05)));
    }

    [Fact]
    public void EmptyContactSet_LeavesThePreviousRoleUntouched()
    {
        var block = MakeBlock();
        block.LastContactRole = RoleNames.Us;
        PhysicsWorld.FinalizeBlockContacts(new List<BlockRuntime> { block });
        Assert.Equal(RoleNames.Us, block.LastContactRole);
    }

    [Fact]
    public void MujocoSinglePusher_PushingTheBlockOffStage_ScoresForUs()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Same geometry as NativeMode_PushesBlockOffStage: us drives straight at the buff
        // block, the opponent is far away at the opposite corner.
        var scenario = new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
            Blocks =
            [
                OfficialLayout.Blocks[0] with { X = 2.85, Y = 1.9 },
                OfficialLayout.Blocks[1] with { X = 1.1, Y = 2.8 },
                OfficialLayout.Blocks[2] with { X = 1.9, Y = 2.8 },
            ],
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 2.2, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 1.9, Y = 1.1, Th = 0 },
                },
            },
        };

        using var engine = MatchEngineHost.Create(scenario);
        var block = engine.Blocks[0];
        var leftStage = false;
        var usScored = false;
        var blockOffs = new List<string>();
        for (var i = 0; i < 160; i++)
        {
            var wasOn = block.WasOn;
            engine.Tick(new RobotAction { V = 0.6 }, RobotAction.Zero);
            if (wasOn && !block.WasOn)
            {
                leftStage = true;
            }
            // Events are cumulative; only this tick's are new.
            var tick = engine.TickIndex;
            foreach (var e in engine.Events.Events)
            {
                if (e.Tick != tick)
                {
                    continue;
                }
                if (e.Kind == EventKind.BlockScore && e.Robot.IsUs && e.Msg.Contains("增益块"))
                {
                    usScored = true;
                }
                if (e.Kind == EventKind.BlockOff && e.Msg.Contains("增益块"))
                {
                    blockOffs.Add(e.Msg);
                }
            }
        }

        Assert.True(leftStage || block.Out, $"the buff block never left the stage (x={block.X:F3})");
        Assert.True(usScored,
            $"a lone pusher was not credited; block offs seen: [{string.Join(" | ", blockOffs)}]");
        Assert.DoesNotContain(blockOffs, msg => msg.Contains("同时接触"));
        Assert.True(engine.Scores.Us >= 3, $"us score {engine.Scores.Us} did not include +3 for the buff");
    }
}
