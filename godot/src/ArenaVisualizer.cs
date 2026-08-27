// 纯展示层: 把 SnapshotView 投影出的 RenderFrame 摆到场景里。所有网格由基本
// 图元程序化生成; Godot 物理只做静态摆放, 不参与判分 (design: 非权威)。
// 场地几何只用于视觉: 平台 [0.7,3.1]^2 高 0.06, 外围走道 3.8×3.8, 与
// FieldParams 官方默认一致 (视觉常量与内核参数同源, 已在注释中标注)。

using Godot;

namespace Sim.GodotShell;

public partial class ArenaVisualizer : Node3D
{
    private const float ArenaSize = 3.8f;          // FieldParams.FieldSize
    private const float PlatformMin = 0.7f;        // FieldParams.Platform.MinX/MinY
    private const float PlatformMax = 3.1f;        // FieldParams.Platform.MaxX/MaxY
    private const float PlatformTop = 0.06f;       // FieldParams.PlatformHeight
    private const float BlockSize = 0.15f;         // 能量块 15 cm
    private const float RobotRadius = 0.09f;       // VehicleProfile.CollisionRadius 默认
    private const float RobotHeight = 0.05f;

    private static readonly Color FloorColor = new(0.16f, 0.18f, 0.22f);
    private static readonly Color WalkwayColor = new(0.34f, 0.36f, 0.41f);
    private static readonly Color PlatformColor = new(0.88f, 0.90f, 0.93f);
    private static readonly Color BandColor = new(0.16f, 0.17f, 0.19f);
    private static readonly Color RedZoneColor = new(0.62f, 0.22f, 0.20f);
    private static readonly Color UsColor = new(0.28f, 0.48f, 0.95f);
    private static readonly Color ThemColor = new(0.92f, 0.30f, 0.28f);
    private static readonly Color BuffColor = new(0.24f, 0.82f, 0.72f);
    private static readonly Color DebuffColor = new(0.91f, 0.55f, 0.22f);
    private static readonly Color OutColor = new(0.45f, 0.45f, 0.48f);
    private static readonly Color RingOn = new(0.35f, 0.92f, 0.45f);
    private static readonly Color RingOff = new(0.42f, 0.44f, 0.50f);

    private MeshInstance3D? _platform;
    private MeshInstance3D? _redZone;
    private readonly List<MeshInstance3D> _walls = [];
    private readonly List<MeshInstance3D> _blockNodes = [];
    private Node3D? _usRoot;
    private Node3D? _themRoot;
    private MeshInstance3D? _usRing;
    private MeshInstance3D? _themRing;
    private readonly List<MeshInstance3D> _blockMaterials = [];

    private void BuildArena()
    {
        // 走道地面 (arena 全幅 3.8×3.8)。
        var floor = MakeBox(FloorColor, new Vector3(ArenaSize, 0.02f, ArenaSize), new Vector3(1.9f, -0.012f, 1.9f));
        AddChild(floor);

        // 擂台 (6 cm 高, 2.4×2.4)。
        _platform = MakeBox(PlatformColor, new Vector3(PlatformMax - PlatformMin, PlatformTop, PlatformMax - PlatformMin),
            new Vector3(ArenaSize / 2, PlatformTop / 2, ArenaSize / 2));
        AddChild(_platform);

        // 擂台顶面黑带: 沿平台边缘的四条细框, 沿用 FieldGray 的"黑边≈300"视觉。
        var top = PlatformTop + 0.001f;
        const float bandThickness = 0.06f;
        foreach (var (cx, cz, sx, sz) in new[]
        {
            (1.9f, PlatformMin + bandThickness / 2, PlatformMax - PlatformMin, bandThickness),  // 南
            (1.9f, PlatformMax - bandThickness / 2, PlatformMax - PlatformMin, bandThickness),  // 北
            (PlatformMin + bandThickness / 2, 1.9f, bandThickness, PlatformMax - PlatformMin),  // 西
            (PlatformMax - bandThickness / 2, 1.9f, bandThickness, PlatformMax - PlatformMin),  // 东
        })
        {
            AddChild(MakeBox(BandColor, new Vector3(sx, 0.004f, sz), new Vector3(cx, top, cz)));
        }

        // 中央红区 (FieldGray 中心 0.6×0.6 "武" 区域)。
        _redZone = MakeBox(RedZoneColor, new Vector3(0.6f, 0.004f, 0.6f), new Vector3(1.9f, top, 1.9f));
        AddChild(_redZone);

        // 场地四边护栏 (视觉接地, 0.12 m 高)。
        foreach (var (cx, cz, sx, sz) in new[]
        {
            (1.9f, -0.02f, ArenaSize, 0.04f),
            (1.9f, ArenaSize + 0.02f, ArenaSize, 0.04f),
            (-0.02f, 1.9f, 0.04f, ArenaSize),
            (ArenaSize + 0.02f, 1.9f, 0.04f, ArenaSize),
        })
        {
            var wall = MakeBox(BandColor, new Vector3(sx, 0.12f, sz), new Vector3(cx, 0.06f, cz));
            AddChild(wall);
            _walls.Add(wall);
        }
    }

    private Node3D BuildRobot(Color bodyColor)
    {
        var root = new Node3D();
        var body = MakeMesh(new CylinderMesh
        {
            TopRadius = RobotRadius,
            BottomRadius = RobotRadius,
            Height = RobotHeight,
        }, bodyColor);
        body.Position = new Vector3(0, RobotHeight / 2, 0);
        root.AddChild(body);

        // 车头指示: 与机身同色的短箭头, 沿 +Z (Godot 前向)。
        var nose = MakeBox(bodyColor.Lightened(0.25f), new Vector3(0.05f, 0.018f, 0.12f), Vector3.Zero);
        nose.Position = new Vector3(0, RobotHeight - 0.008f, RobotRadius + 0.02f);
        root.AddChild(nose);

        // 推铲示意: 机身前缘加宽低框。
        var shovel = MakeBox(bodyColor.Darkened(0.15f), new Vector3(0.16f, 0.02f, 0.03f), Vector3.Zero);
        shovel.Position = new Vector3(0, RobotHeight - 0.02f, RobotRadius + 0.025f);
        root.AddChild(shovel);

        // 登台指示环 (绿=在台上, 灰=不在)。
        var ring = MakeMesh(new CylinderMesh
        {
            TopRadius = RobotRadius + 0.045f,
            BottomRadius = RobotRadius + 0.045f,
            Height = 0.004f,
        }, RingOff);
        ring.Position = new Vector3(0, 0.002f, 0);
        root.AddChild(ring);
        return root;
    }

    public override void _Ready()
    {
        BuildArena();

        _usRoot = BuildRobot(UsColor);
        _usRoot.Name = "UsRobot";
        AddChild(_usRoot);
        _usRing = (MeshInstance3D)_usRoot.GetChild(2);

        _themRoot = BuildRobot(ThemColor);
        _themRoot.Name = "ThemRobot";
        AddChild(_themRoot);
        _themRing = (MeshInstance3D)_themRoot.GetChild(2);
    }

    public void ShowFrame(RenderFrame frame)
    {
        ApplyRobot(_usRoot, frame.Us, _usRing);
        ApplyRobot(_themRoot, frame.Them, _themRing);
        EnsureBlockNodes(frame.Blocks.Count);
        for (var i = 0; i < frame.Blocks.Count && i < _blockNodes.Count; i++)
        {
            var block = frame.Blocks[i];
            var node = _blockNodes[i];
            // 快照给出的 Up 是块底 (台上 0.06 / 台下 0), 方块中心抬高半个边长。
            node.Position = new Vector3(
                (float)block.Position.X,
                (float)block.Position.Up + BlockSize / 2,
                (float)block.Position.Z);
            node.Visible = true;
            _blockMaterials[i].MaterialOverride = MakeMaterial(block.Out ? OutColor
                : block.Kind == "buff" ? BuffColor : DebuffColor);
        }
    }

    private static void ApplyRobot(Node3D? root, RobotVisual robot, MeshInstance3D? ring)
    {
        if (root is null)
        {
            return;
        }
        // 仿真 (x, y) → 世界 (x, 高度, y=z); 车头沿 +Z, 旋转 y = π/2 - th。
        root.Position = new Vector3((float)robot.Position.X, (float)robot.Position.Up, (float)robot.Position.Z);
        root.Rotation = new Vector3(0, (float)(Math.PI / 2 - robot.Yaw), 0);
        if (ring is not null)
        {
            ring.MaterialOverride = MakeMaterial(robot.OnPlatform ? RingOn : RingOff);
        }
    }

    private void EnsureBlockNodes(int count)
    {
        while (_blockNodes.Count < count)
        {
            var mesh = MakeMesh(new BoxMesh { Size = new Vector3(BlockSize, BlockSize, BlockSize) }, OutColor);
            AddChild(mesh);
            _blockNodes.Add(mesh);
            _blockMaterials.Add(mesh);
        }
    }

    // ---------- helpers ----------

    private static MeshInstance3D MakeBox(Color color, Vector3 size, Vector3 position)
    {
        var mesh = MakeMesh(new BoxMesh { Size = size }, color);
        mesh.Position = position;
        return mesh;
    }

    private static MeshInstance3D MakeMesh(PrimitiveMesh primitive, Color color)
    {
        var mesh = new MeshInstance3D { Mesh = primitive };
        mesh.MaterialOverride = MakeMaterial(color);
        return mesh;
    }

    private static StandardMaterial3D MakeMaterial(Color color)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.85f,
            Metallic = 0.0f,
        };
        return material;
    }
}