using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// telemetry-v1 contract tests: strict SI, kind-specific required payloads,
/// timestamp ordering and the canonical wire spellings (snake_case kinds).
/// </summary>
public class TelemetryContractTests
{
    private static TelemetryFrame Frame(double t, double? x = null, double? y = null, double? th = null)
        => new()
        {
            T = t,
            Robot = x is null ? null : new TelemetryPose { X = x, Y = y, Th = th },
        };

    private static TelemetryFile ValidFile() => new()
    {
        Schema = ProtocolVersion.TelemetryFormat,
        Vehicle = new TelemetryVehicle { Id = "bot-1" },
        Capture = new TelemetryCapture { Source = "synthetic", Date = "2026-08-27" },
        Trials =
        [
            new TelemetryTrial
            {
                Id = "lateral-1",
                Kind = TelemetryTrialKind.LateralCoast,
                Frames = [Frame(0.0, 0, 0, 0), Frame(0.05, 0, 0.1, 0), Frame(0.1, 0, 0.18, 0)],
            },
            new TelemetryTrial
            {
                Id = "collision-1",
                Kind = TelemetryTrialKind.Collision,
                Normal = new TelemetryPoint { X = 1, Y = 0 },
                Impact = new TelemetryImpact
                {
                    Pre = new TelemetryImpactVelocities { Robot = new TelemetryVelocity { Vx = 1, Vy = 0 } },
                    Post = new TelemetryImpactVelocities { Robot = new TelemetryVelocity { Vx = -0.33, Vy = 0 } },
                },
            },
            new TelemetryTrial
            {
                Id = "mount-1",
                Kind = TelemetryTrialKind.Mount,
                Set = "holdout",
                Approach = new TelemetryMountApproach { Vn = 0.5, Vt = 0.01 },
                Outcome = true,
            },
        ],
    };

    [Fact]
    public void ValidFile_Passes_WithEmptyErrors()
        => Assert.Empty(ValidFile().Validate());

    [Fact]
    public void TelemetryFile_RoundTrips_WithSnakeCaseKinds()
    {
        var json = ProtocolJson.Serialize(ValidFile());
        Assert.Contains("telemetry-v1", json);
        Assert.Contains("lateral_coast", json);
        var parsed = ProtocolJson.Deserialize<TelemetryFile>(json);
        Assert.Equal(json, ProtocolJson.Serialize(parsed));
        Assert.Equal(TelemetryTrialKind.LateralCoast, parsed.Trials[0].Kind);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void Deserialize_RejectsUnknownTrialKind()
        => Assert.Throws<System.Text.Json.JsonException>(() => ProtocolJson.Deserialize<TelemetryFile>(
            """{"schema":"telemetry-v1","vehicle":{"id":"b"},"capture":{"source":"real","date":"2026-08-27"},"trials":[{"id":"x","kind":"moon_walk"}]}"""));

    [Theory]
    [InlineData("cm")]
    [InlineData("degrees")]
    public void NonSiUnits_AreRejected(string lengthUnit)
    {
        var file = ValidFile() with { Units = new TelemetryUnits { Length = lengthUnit } };
        Assert.Contains(file.Validate(), e => e.Contains("units.length"));
    }

    [Fact]
    public void NonIncreasingTimestamps_AreRejected()
    {
        var trial = ValidFile().Trials[0] with
        {
            Frames = [Frame(0.1, 0, 0, 0), Frame(0.05, 0, 0.1, 0)],
        };
        var file = ValidFile() with { Trials = [trial] };
        Assert.Contains(file.Validate(), e => e.Contains("strictly increasing"));
    }

    [Fact]
    public void NaNHeading_IsRejected_ForCoastTrial()
    {
        var trial = ValidFile().Trials[0] with
        {
            Frames = [Frame(0.0, 0, 0, double.NaN), Frame(0.05, 0, 0.1, 0)],
        };
        Assert.Contains((ValidFile() with { Trials = [trial] }).Validate(),
            e => e.Contains("th must be a finite heading"));
    }

    [Fact]
    public void MissingOutcome_IsRejected_ForMountTrial()
    {
        var trial = ValidFile().Trials[2] with { Outcome = null };
        Assert.Contains((ValidFile() with { Trials = [trial] }).Validate(),
            e => e.Contains("outcome label"));
    }

    [Fact]
    public void UnknownTrialSet_IsRejected()
    {
        var trial = ValidFile().Trials[2] with { Set = "test" };
        Assert.Contains((ValidFile() with { Trials = [trial] }).Validate(),
            e => e.Contains("set must be"));
    }

    [Fact]
    public void BadCaptureMetadata_IsRejected()
    {
        var file = ValidFile() with
        {
            Capture = new TelemetryCapture { Source = "guess", Date = "08/27/2026" },
        };
        var errors = file.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("capture.source"));
        Assert.Contains(errors, e => e.Contains("capture.date"));
    }

    [Fact]
    public void DuplicateTrialIds_AreRejected()
    {
        var dup = ValidFile().Trials[0] with { Kind = TelemetryTrialKind.BlockPush };
        var file = ValidFile() with { Trials = [ValidFile().Trials[0], dup] };
        Assert.Contains(file.Validate(), e => e.Contains("duplicate trial id"));
    }

    [Fact]
    public void Collision_WithoutNormalSourceOrImpact_IsRejected()
    {
        var trial = ValidFile().Trials[1] with { Impact = null };
        Assert.Contains((ValidFile() with { Trials = [trial] }).Validate(),
            e => e.Contains("impact{pre,post}"));
    }

    [Fact]
    public void EmptyTrials_IsRejected()
    {
        var file = ValidFile() with { Trials = [] };
        Assert.Contains(file.Validate(), e => e.Contains("non-empty array"));
    }
}
