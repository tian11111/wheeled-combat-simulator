// 纯展示层: 把 SnapshotView 投影出的 RenderFrame 摆到场景里。所有网格由基本
// 图元程序化生成; Godot 物理只做静态摆放, 不参与判分 (design: 非权威)。
//
// 场地几何完全来自 Scenario (ArenaVisualizer.Configure): 外场/擂台/走道/围栏/
// 出发区/能量块尺寸均读取 FieldParams, 不存在第二份官方常量。场地的整体平移与
// 旋转 (field.pose) 通过 ArenaRoot 节点变换呈现; 机器人和能量块使用仿真世界
// 坐标, 挂在 ArenaVisualizer 根下, 不受 ArenaRoot 变换影响。
// 台面灰度纹理由 FieldModel.FieldGray 提供区域几何, 按"官方效果图"调色板显示
// (传感器 0–1000 语义不变); 中央红区+白"武"为纯视觉元素, 不改变任何判定。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class ArenaVisualizer : Node3D
{
    private const float WallThickness = 0.04f;

    private static readonly Color FloorColor = new(0.16f, 0.18f, 0.22f);
    // 官方效果图外观: 底座侧面白、台面走道深灰、擂台黑边渐入白心、中央红区(白"武")。
    private static readonly Color PlatformSideColor = new(0.93f, 0.93f, 0.95f);
    private static readonly Color RedZoneColor = new(0.85f, 0.15f, 0.13f);
    private static readonly (byte R, byte G, byte B) WalkwayRgb = (72, 72, 72); // 官方走道深灰
    private static readonly Color UsColor = new(0.28f, 0.48f, 0.95f);
    private static readonly Color ThemColor = new(0.92f, 0.30f, 0.28f);
    private static readonly Color BuffColor = new(0.24f, 0.82f, 0.72f);
    private static readonly Color DebuffColor = new(0.91f, 0.55f, 0.22f);
    private static readonly Color OutColor = new(0.45f, 0.45f, 0.48f);
    private static readonly Color RingOn = new(0.35f, 0.92f, 0.45f);
    private static readonly Color RingOff = new(0.42f, 0.44f, 0.50f);
    private static readonly Color StartZoneUs = new(0.95f, 0.85f, 0.15f);   // 纯黄出发区
    private static readonly Color StartZoneThem = new(0.15f, 0.35f, 0.95f); // 纯蓝出发区

    private Node3D? _arenaRoot;
    private readonly List<MeshInstance3D> _blockNodes = [];
    private readonly List<MeshInstance3D> _blockMaterials = [];
    private Node3D? _usRoot;
    private Node3D? _themRoot;
    private MeshInstance3D? _usRing;
    private MeshInstance3D? _themRing;
    private float _blockSize = 0.15f;

    private Scenario? _scenario;

    /// <summary>The scenario the arena was last configured from (null before the first Configure).</summary>
    public Scenario? CurrentScenario => _scenario;

    /// <summary>Robot visual root node for a role (null before Configure).</summary>
    public Node3D? RobotRoot(string role) => role == RoleNames.Us ? _usRoot : _themRoot;

    public override void _Ready()
    {
        // 机器人在 Configure 时按场景碰撞半径构建; 未配置时保持空场景。
        _arenaRoot = new Node3D { Name = "ArenaRoot" };
        AddChild(_arenaRoot);
    }

    /// <summary>Rebuilds the static arena geometry (and robot visuals on first call) from a scenario.</summary>
    public void Configure(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _scenario = scenario;
        RebuildArena(scenario);

        if (_usRoot is null)
        {
            var usRadius = RobotRadiusFor(scenario, RoleNames.Us);
            var themRadius = RobotRadiusFor(scenario, RoleNames.Them);
            _usRoot = BuildRobot(UsColor, usRadius);
            _usRoot.Name = "UsRobot";
            AddChild(_usRoot);
            _usRing = (MeshInstance3D)_usRoot.GetChild(3);

            _themRoot = BuildRobot(ThemColor, themRadius);
            _themRoot.Name = "ThemRobot";
            AddChild(_themRoot);
            _themRing = (MeshInstance3D)_themRoot.GetChild(3);
        }
    }

    private static float RobotRadiusFor(Scenario scenario, string role)
        => scenario.Vehicles.TryGetValue(role, out var v) && v is not null && v.CollisionRadius > 0
            ? (float)v.CollisionRadius
            : 0.09f;

    // ---------- arena construction ----------

    private void RebuildArena(Scenario scenario)
    {
        var root = _arenaRoot!;
        foreach (var child in root.GetChildren())
        {
            child.QueueFree();
        }

        var field = scenario.Field;
        _blockSize = (float)field.BlockSize;

        // 场地位姿: field-local → 仿真世界 (与 FieldModel 同一变换)。
        var pose = field.Pose;
        root.Position = new Vector3((float)(pose?.X ?? 0), 0, (float)(pose?.Y ?? 0));
        root.Rotation = new Vector3(0, -(float)(pose?.Th ?? 0), 0);

        BuildFloor(root, field);
        BuildPlatform(root, field);
        BuildStartZones(root, field);
        BuildFence(root, field);
    }

    private void BuildFloor(Node3D root, FieldParams field)
    {
        var size = (float)field.FieldSize;
        // 黑色哑光走道地面 (整幅外场, 擂台盖在上面)。
        root.AddChild(MakeBox(FloorColor,
            new Vector3(size, 0.02f, size), new Vector3(size / 2, -0.012f, size / 2)));
    }

    private void BuildPlatform(Node3D root, FieldParams field)
    {
        var model = new FieldModel(field);
        var el = (float)field.Platform.MinX;
        var span = (float)(field.Platform.MaxX - field.Platform.MinX);
        var center = el + span / 2;
        var top = (float)field.PlatformHeight;

        // 擂台主体 (6 cm 高, 官方 2.4×2.4)。
        root.AddChild(MakeBox(PlatformSideColor,
            new Vector3(span, top, span), new Vector3(center, top / 2, center)));

        // 顶面灰度纹理: 采样内核同一 FieldGrayLocal 的区域几何, 按"官方效果图"
        // 调色板显示 (走道深灰 / 擂台黑边渐入白心 / 中央红区), 传感器 0–1000
        // 语义不变。像素↔场局部坐标的轴向契约见 FieldGrayTextureMap (行 0 = 南)。
        var surface = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(span, span) },
            // Unshaded: 调色板逐像素固定, 有向光/镜面高光不得在平面上制造
            // 随视角变化的灰度梯度 (旧版斜向灰带即源于此)。
            MaterialOverride = MakeTexturedMaterial(MakeFieldGrayTexture(model, field)),
        };
        surface.Position = new Vector3(center, top + 0.001f, center);
        // PlaneMesh 默认 FACE_Y (朝 +Y), UV.u 随局部 +X、UV.v 随局部 +Z 增长,
        // 与 FieldGrayTextureMap 的图像轴向契约一致; 无需再翻转。
        root.AddChild(surface);

        // 中央红区上的白"武" (纯视觉, 与灰度传感器无关); 字号 ≈ 红区的 3/4,
        // 官方样式为红底白字, 不加描边。
        var wu = new Label3D
        {
            Text = "武",
            FontSize = 88,
            Modulate = new Color(0.98f, 0.98f, 0.98f),
            OutlineSize = 0,
            Position = new Vector3(center, top + 0.004f, center),
            Rotation = new Vector3(-Mathf.Pi / 2, 0, 0),
        };
        root.AddChild(wu);
    }

    private void BuildStartZones(Node3D root, FieldParams field)
    {
        foreach (var (role, color) in new[]
        {
            (RoleNames.Us, StartZoneUs), (RoleNames.Them, StartZoneThem),
        })
        {
            if (!field.StartZones.TryGetValue(role, out var zone) || zone is null)
            {
                continue;
            }
            var sx = (float)(zone.MaxX - zone.MinX);
            var sz = (float)(zone.MaxY - zone.MinY);
            root.AddChild(MakeBox(color, new Vector3(sx, 0.005f, sz),
                new Vector3((float)((zone.MinX + zone.MaxX) / 2), 0.003f, (float)((zone.MinY + zone.MaxY) / 2))));
        }
    }

    private void BuildFence(Node3D root, FieldParams field)
    {
        var size = (float)field.FieldSize;
        var height = (float)field.FenceHeight;
        var t = WallThickness;
        foreach (var (cx, cz, sx, sz) in new[]
        {
            (size / 2, -t / 2, size + t * 2, t),
            (size / 2, size + t / 2, size + t * 2, t),
            (-t / 2, size / 2, t, size + t * 2),
            (size + t / 2, size / 2, t, size + t * 2),
        })
        {
            root.AddChild(MakeBox(FloorColor.Darkened(0.05f),
                new Vector3(sx, height, sz), new Vector3(cx, height / 2, cz)));
        }
    }

    /// <summary>
    /// Generates the platform top-surface texture: region geometry sampled from
    /// the same hand-drawn gray model the sensors use (via the pure
    /// <see cref="FieldGrayTextureMap"/> mapping: row 0 = field south,
    /// column 0 = field west), painted with the official display palette
    /// (walkway dark gray, ring black-edge→white ramp, red center square).
    /// </summary>
    private static ImageTexture MakeFieldGrayTexture(FieldModel model, FieldParams field)
    {
        const int resolution = FieldGrayTextureMap.DefaultResolution;
        var redZone = ToByteRgb(RedZoneColor);
        var buffer = FieldGrayTextureMap.BuildRgb8(
            resolution,
            field.Platform.MinX, field.Platform.MinY,
            field.Platform.MaxX, field.Platform.MaxY,
            model.Center,
            model.FieldGrayLocal,
            redZone,
            WalkwayRgb);
        var image = Image.CreateEmpty(resolution, resolution, false, Image.Format.Rgb8);
        for (var py = 0; py < resolution; py++)
        {
            for (var px = 0; px < resolution; px++)
            {
                var offset = (py * resolution + px) * 3;
                image.SetPixel(px, py, Godot.Color.Color8(buffer[offset], buffer[offset + 1], buffer[offset + 2]));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static (byte R, byte G, byte B) ToByteRgb(Color color)
        => ((byte)MathF.Round(Mathf.Clamp(color.R, 0f, 1f) * 255f),
            (byte)MathF.Round(Mathf.Clamp(color.G, 0f, 1f) * 255f),
            (byte)MathF.Round(Mathf.Clamp(color.B, 0f, 1f) * 255f));

    // ---------- dynamic entities ----------

    private Node3D BuildRobot(Color bodyColor, float radius)
    {
        const float robotHeight = 0.05f;
        var root = new Node3D();
        var body = MakeMesh(new CylinderMesh
        {
            TopRadius = radius,
            BottomRadius = radius,
            Height = robotHeight,
        }, bodyColor);
        body.Name = "Body"; // RobotModelLoader 导入成功后隐藏它 (登台环保留)
        body.Position = new Vector3(0, robotHeight / 2, 0);
        root.AddChild(body);

        // 车头指示: 与机身同色的短箭头, 沿 +Z (Godot 前向)。
        var nose = MakeBox(bodyColor.Lightened(0.25f),
            new Vector3(0.05f, 0.018f, 0.12f), Vector3.Zero);
        nose.Name = "Nose";
        nose.Position = new Vector3(0, robotHeight - 0.008f, radius + 0.02f);
        root.AddChild(nose);

        // 推铲示意: 机身前缘加宽低框。
        var shovel = MakeBox(bodyColor.Darkened(0.15f),
            new Vector3(radius * 1.78f, 0.02f, 0.03f), Vector3.Zero);
        shovel.Name = "Shovel";
        shovel.Position = new Vector3(0, robotHeight - 0.02f, radius + 0.025f);
        root.AddChild(shovel);

        // 登台指示环 (绿=在台上, 灰=不在)。
        var ring = MakeMesh(new CylinderMesh
        {
            TopRadius = radius + 0.045f,
            BottomRadius = radius + 0.045f,
            Height = 0.004f,
        }, RingOff);
        ring.Position = new Vector3(0, 0.002f, 0);
        root.AddChild(ring);
        return root;
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
            // 快照给出的 Up 是块底 (台上 = PlatformHeight / 台下 0), 中心抬高半个边长。
            node.Position = new Vector3(
                (float)block.Position.X,
                (float)block.Position.Up + _blockSize / 2,
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
            var mesh = MakeMesh(new BoxMesh { Size = new Vector3(_blockSize, _blockSize, _blockSize) }, OutColor);
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

    private static StandardMaterial3D MakeTexturedMaterial(Texture2D texture)
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            // 受控着色路径: 台面灰度纯自发光显示, 方向光/高光无法制造梯度
            // (视觉 QA 的像素分桶也依赖颜色与纹理一一对应)。
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Roughness = 1f,
            Metallic = 0.0f,
            VertexColorUseAsAlbedo = false,
        };
        return material;
    }
}
