// Godot-free layout editing model (no Godot namespace): an immutable-snapshot
// draft of the scenario's *layout* (field pose, start zones/poses, block
// positions) with undo/redo, validation, canonical arena-layout-v1
// serialization and atomic save. The desktop editor drives this layer; the
// kernel authority stays untouched — Apply builds a fresh Scenario/Engine.
//
// Semantics mirror the kernel: all editable positions are field-local (m);
// the field pose maps local → simulation-world exactly like
// Sim.Core.FieldTransform.

using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>One editable state of a layout draft (immutable; snapshots back undo history).</summary>
public sealed record LayoutState
{
    public required Pose2 Pose { get; init; }

    /// <summary>Start zones per role in field-local metres.</summary>
    public required Dictionary<string, Region> StartZones { get; init; }

    /// <summary>Robot start poses per role in field-local metres.</summary>
    public required Dictionary<string, Pose2> Starts { get; init; }

    /// <summary>Block layout in field-local metres.</summary>
    public required List<BlockSpec> Blocks { get; init; }

    public LayoutState Copy() => this with
    {
        StartZones = StartZones.ToDictionary(kv => kv.Key, kv => kv.Value),
        Starts = Starts.ToDictionary(kv => kv.Key, kv => kv.Value),
        Blocks = Blocks.Select(b => b).ToList(),
    };
}

/// <summary>
/// Mutable working copy of the editable arena layout with a bounded undo /
/// redo history. Every public mutator records the previous state, so Undo is
/// a pointer move, not an inverse-op reimplementation.
/// </summary>
public sealed class LayoutDraft
{
    /// <summary>Default translation snap in metres (design: 0.01 m).</summary>
    public const double TranslationSnap = 0.01;

    /// <summary>Default rotation snap in radians (design: 5 degrees).</summary>
    public const double RotationSnap = 5 * Math.PI / 180;

    private readonly Scenario _base;
    private readonly Stack<LayoutState> _undo = new();
    private readonly Stack<LayoutState> _redo = new();
    private LayoutState? _groupSnapshot;
    private int _groupDepth;

    public LayoutDraft(Scenario baseScenario)
    {
        _base = baseScenario ?? throw new ArgumentNullException(nameof(baseScenario));
        State = FromScenario(baseScenario);
    }

    public LayoutState State { get; private set; }

    /// <summary>Official reference layout used by RestoreOfficial.</summary>
    public static LayoutState OfficialState() => FromScenario(new Scenario { Blocks = OfficialLayout.Blocks });

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Snaps a translation delta to the 0.01 m grid.</summary>
    public static double SnapTranslation(double value)
        => Math.Round(value / TranslationSnap) * TranslationSnap;

    /// <summary>Snaps a yaw (rad) to the 5° grid.</summary>
    public static double SnapRotation(double yaw)
        => Math.Round(yaw / RotationSnap) * RotationSnap;

    // ---------- field pose ----------

    /// <summary>Translates the whole field in simulation-world metres.</summary>
    public void MoveField(double dx, double dy)
    {
        var pose = State.Pose;
        Apply(State with { Pose = new Pose2 { X = pose.X + dx, Y = pose.Y + dy, Th = pose.Th } });
    }

    /// <summary>Rotates the whole field by <paramref name="dyaw"/> radians about the field origin.</summary>
    public void RotateField(double dyaw)
    {
        var pose = State.Pose;
        Apply(State with { Pose = new Pose2 { X = pose.X, Y = pose.Y, Th = pose.Th + dyaw } });
    }

    public void SetFieldPose(Pose2 pose)
        => Apply(State with { Pose = pose });

    // ---------- start zones ----------

    /// <summary>
    /// Drags a start zone (and its robot start pose) by a field-local delta.
    /// The zone rectangle keeps its official dimensions in the MVP.
    /// </summary>
    public void MoveStartZone(string role, double dx, double dy)
    {
        if (State.StartZones.TryGetValue(role, out var zone) && zone is not null)
        {
            PlaceStartZone(role, zone.MinX + dx, zone.MinY + dy);
        }
    }

    /// <summary>Places a start zone at an absolute field-local corner; the start pose keeps its zone-relative offset.</summary>
    public void PlaceStartZone(string role, double minX, double minY)
    {
        if (!State.StartZones.TryGetValue(role, out var zone) || zone is null)
        {
            return;
        }
        var width = zone.MaxX - zone.MinX;
        var height = zone.MaxY - zone.MinY;
        var zones = new Dictionary<string, Region>(State.StartZones)
        {
            [role] = new Region { MinX = minX, MinY = minY, MaxX = minX + width, MaxY = minY + height },
        };
        var starts = new Dictionary<string, Pose2>(State.Starts);
        if (starts.TryGetValue(role, out var start) && start is not null)
        {
            var cx = (zone.MinX + zone.MaxX) / 2;
            var cy = (zone.MinY + zone.MaxY) / 2;
            starts[role] = new Pose2
            {
                X = minX + width / 2 + (start.X - cx),
                Y = minY + height / 2 + (start.Y - cy),
                Th = start.Th,
            };
        }
        Apply(State with { StartZones = zones, Starts = starts });
    }

    // ---------- blocks ----------

    /// <summary>Moves a block to a fixed field-local position.</summary>
    public void MoveBlock(int index, double localX, double localY)
    {
        if (index < 0 || index >= State.Blocks.Count)
        {
            return;
        }
        var blocks = State.Blocks.ToList();
        var spec = blocks[index];
        blocks[index] = spec with { X = localX, Y = localY };
        Apply(State with { Blocks = blocks });
    }

    // ---------- history / lifecycle ----------

    /// <summary>
    /// Begins a grouped edit (e.g. one continuous drag): mutations until
    /// <see cref="EndGroup"/> commit as a single undo step. Groups nest; only
    /// the outermost start records the pre-group snapshot.
    /// </summary>
    public void BeginGroup()
    {
        if (_groupDepth == 0)
        {
            _groupSnapshot = State.Copy();
        }
        _groupDepth++;
    }

    /// <summary>Commits the grouped edits as one undo entry (or discards a no-op group).</summary>
    public void EndGroup()
    {
        if (_groupDepth == 0)
        {
            return;
        }
        _groupDepth--;
        if (_groupDepth == 0)
        {
            if (_groupSnapshot is { } group && !Same(group, State))
            {
                _undo.Push(group);
                _redo.Clear();
            }
            _groupSnapshot = null;
        }
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }
        _redo.Push(State.Copy());
        State = _undo.Pop();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }
        _undo.Push(State.Copy());
        State = _redo.Pop();
    }

    /// <summary>Resets the draft to the 2026 official layout (identity pose).</summary>
    public void RestoreOfficial()
        => Apply(OfficialState());

    /// <summary>Replaces the draft content with a loaded scenario (resets history).</summary>
    public void LoadFrom(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _undo.Clear();
        _redo.Clear();
        State = FromScenario(scenario);
    }

    // ---------- validation / serialization ----------

    /// <summary>Builds the canonical arena-layout-v1 scenario for this draft, keeping the base ruleset/seed/vehicles.</summary>
    public Scenario BuildScenario()
    {
        return _base with
        {
            LayoutVersion = ProtocolVersion.ArenaLayoutV1,
            Field = _base.Field with
            {
                Pose = State.Pose,
                StartZones = State.StartZones.ToDictionary(kv => kv.Key, kv => kv.Value),
                Starts = State.Starts.ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            Blocks = State.Blocks.Select(b => b).ToList(),
        };
    }

    /// <summary>Validates the draft as a full scenario; empty list = savable.</summary>
    public List<string> Validate() => BuildScenario().Validate().ToList();

    public bool CanApply => Validate().Count == 0;

    /// <summary>
    /// Atomically writes the canonical scenario JSON to <paramref name="path"/>
    /// (temp file + move) and returns the scenario that was saved.
    /// </summary>
    public Scenario SaveTo(string path)
    {
        var scenario = BuildScenario();
        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid layout: {string.Join(" ", errors)}");
        }
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = full + ".tmp";
        File.WriteAllText(temp, ProtocolJson.Serialize(scenario));
        File.Move(temp, full, overwrite: true);
        return scenario;
    }

    /// <summary>Reads and validates a scenario file (for the editor Open action).</summary>
    public static Scenario ReadScenario(string path)
    {
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid scenario: {string.Join(" ", errors)}");
        }
        return scenario;
    }

    // ---------- internals ----------

    private void Apply(LayoutState next)
    {
        if (Same(State, next))
        {
            return; // no-op edits (e.g. snapping to the same grid cell) don't pollute history
        }
        var previous = State;
        State = next;
        if (_groupDepth == 0)
        {
            // Inside a group the pre-drag snapshot is committed by EndGroup.
            _undo.Push(previous);
            _redo.Clear();
        }
    }

    private static bool Same(LayoutState a, LayoutState b)
        => a.Pose == b.Pose
            && SameDict(a.StartZones, b.StartZones)
            && SameDict(a.Starts, b.Starts)
            && a.Blocks.Count == b.Blocks.Count
            && !a.Blocks.Where((x, i) => x != b.Blocks[i]).Any();

    private static bool SameDict<T>(Dictionary<string, T> a, Dictionary<string, T> b)
        => a.Count == b.Count && !a.Where(kv => !b.TryGetValue(kv.Key, out var v) || !Equals(v, kv.Value)).Any();

    private static LayoutState FromScenario(Scenario scenario)
    {
        var field = scenario.Field;
        return new LayoutState
        {
            Pose = field.Pose ?? new Pose2(),
            StartZones = field.StartZones.ToDictionary(kv => kv.Key, kv => kv.Value),
            Starts = field.Starts.ToDictionary(kv => kv.Key, kv => kv.Value),
            Blocks = scenario.Blocks.Select(b => b).ToList(),
        };
    }
}
