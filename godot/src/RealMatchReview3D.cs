using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>Isolated presentation of recorded poses; no simulation state is created.</summary>
public partial class RealMatchReview3D : SubViewportContainer
{
    private SubViewport _viewport = null!;
    private ArenaVisualizer _arena = null!;
    private Camera3D _camera = null!;
    private Scenario? _scenario;
    public string AppearanceStatus { get; private set; } = "";

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        _viewport = new SubViewport { OwnWorld3D = true, Size = new Vector2I(640, 400), RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible };
        AddChild(_viewport);
        _arena = new ArenaVisualizer();
        _viewport.AddChild(_arena);
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -35, 0), LightEnergy = 1.3f });
        _viewport.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.08f, .11f, .16f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .6f } });
        _camera = new Camera3D { Current = true, Fov = 50 };
        _viewport.AddChild(_camera);
    }

    public void Configure(Scenario scenario, IReadOnlyDictionary<string, RobotModelConfig>? models)
    {
        _scenario = scenario;
        _arena.Configure(scenario);
        var fallbacks = new List<string>();
        foreach (var role in new[] { "us", "them" })
        {
            var root = _arena.RobotRoot(role)!;
            var config = models is not null && models.TryGetValue(role, out var found) ? found : null;
            var error = RobotModelLoader.Apply(root, config);
            if (config is null || config.IsEmpty || error is not null)
                fallbacks.Add(role == "us" ? "我方" : "对手");
            if (error is not null) GD.PrintErr($"[real-review] {role} 外观载入失败: {error}");
            if (root.GetNodeOrNull<Node3D>("Ring") is { } ring) ring.Visible = false;
            root.Visible = false;
        }
        AppearanceStatus = fallbacks.Count == 0 ? "" : string.Join("、", fallbacks) + "使用默认小车外观。";
        var model = new FieldModel(scenario.Field);
        var (x, y) = model.CenterWorld;
        var center = new Vector3((float)x, 0, (float)y);
        var size = (float)scenario.Field.FieldSize;
        _camera.Position = center + new Vector3(0, size * .95f, size * .75f);
        _camera.LookAt(center, Vector3.Up);
    }

    public void ShowPoses(ReviewPose? us, ReviewPose? them)
    {
        if (_scenario is null) return;
        ShowPose("us", us);
        ShowPose("them", them);
    }

    private void ShowPose(string role, ReviewPose? pose)
    {
        var root = _arena.RobotRoot(role)!;
        root.Visible = pose is not null;
        if (pose is null) return;
        var field = _scenario!.Field;
        var transform = new FieldTransform(field.Pose?.X ?? 0, field.Pose?.Y ?? 0, field.Pose?.Th ?? 0);
        var (x, y) = transform.LocalToWorldPoint(pose.X, pose.Y);
        var platform = field.Platform;
        var height = pose.X >= platform.MinX && pose.X <= platform.MaxX && pose.Y >= platform.MinY && pose.Y <= platform.MaxY
            ? field.PlatformHeight + .001 : 0;
        root.Position = new Vector3((float)x, (float)height, (float)y);
        root.Rotation = new Vector3(0, (float)(Math.PI / 2 - transform.LocalToWorldHeading(pose.Heading)), 0);
    }
}
