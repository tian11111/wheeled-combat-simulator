// 纯展示层: 把 SnapshotView 投影出的 RenderFrame 摆到场景里。所有网格由基本
// 图元程序化生成; Godot 物理只做静态摆放, 不参与判分 (design: 非权威)。
//
// 场地几何完全来自 Scenario (ArenaVisualizer.Configure): 外场/擂台/走道/围栏/
// 出发区/能量块尺寸均读取 FieldParams, 不存在第二份官方常量。场地的整体平移与
// 旋转 (field.pose) 通过 ArenaRoot 节点变换呈现; 机器人和能量块使用仿真世界
// 坐标, 挂在 ArenaVisualizer 根下, 不受 ArenaRoot 变换影响。
// 台面显示是 visual-only 官方外观 (规则第 10 页: 四角纯黑→中心纯白径向渐变,
// FieldGrayTextureMap.OfficialSurfaceLuminance) + 几何红区 (白"武"独立层);
// 传感器 0–1000 语义 (FieldModel.FieldGrayLocal) 不进入显示纹理, 也不被显示层
// 改写 — 两种灰度语义严格分离。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class ArenaVisualizer : Node3D
{
    private const float WallThickness = 0.04f;

    private static readonly Color FloorColor = new(0.16f, 0.18f, 0.22f);
    // 官方效果图外观: 底座侧面白、台面走道深灰、台面四角纯黑→中心纯白径向渐变、
    // 中央红区(白"武")。
    private static readonly Color PlatformSideColor = new(0.93f, 0.93f, 0.95f);
    private static readonly Color RedZoneColor = new(0.85f, 0.15f, 0.13f);
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
            _usRing = _usRoot.GetNodeOrNull<MeshInstance3D>("Ring");

            _themRoot = BuildRobot(ThemColor, themRadius);
            _themRoot.Name = "ThemRobot";
            AddChild(_themRoot);
            _themRing = _themRoot.GetNodeOrNull<MeshInstance3D>("Ring");
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
        root.AddChild(MakeBoxMatte(FloorColor,
            new Vector3(size, 0.02f, size), new Vector3(size / 2, -0.012f, size / 2)));
    }

    private void BuildPlatform(Node3D root, FieldParams field)
    {
        var model = new FieldModel(field);
        var el = (float)field.Platform.MinX;
        var span = (float)(field.Platform.MaxX - field.Platform.MinX);
        var center = el + span / 2;
        var top = (float)field.PlatformHeight;

        // 擂台主体 (6 cm 高, 官方 2.4×2.4): 侧面白色板材 (可被反射探针映出环境)。
        root.AddChild(MakeBoxWhiteBoard(PlatformSideColor,
            new Vector3(span, top, span), new Vector3(center, top / 2, center)));

        // 底座裙边: 比主体略宽的深色收边条, 给平台一个"落地"的倒角深度线索。
        root.AddChild(MakeBoxMatte(FloorColor.Darkened(0.25f),
            new Vector3(span + 0.03f, 0.014f, span + 0.03f), new Vector3(center, 0.007f, center)));

        // 顶面灰度纹理: 官方效果图径向渐变 (四角纯黑→中心纯白, visual-only,
        // 见 FieldGrayTextureMap.OfficialSurfaceLuminance; 传感器 0–1000 语义
        // 不进入显示层)。像素↔场局部坐标的轴向契约见 FieldGrayTextureMap
        // (行 0 = 南)。中央红区在纹理内优先覆盖, 白"武"为 Label3D 独立层。
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
            root.AddChild(MakeBoxMatte(color, new Vector3(sx, 0.005f, sz),
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
            root.AddChild(MakeBoxMatte(FloorColor.Darkened(0.05f),
                new Vector3(sx, height, sz), new Vector3(cx, height / 2, cz)));
        }

        // 围栏立柱: 四角略高于栏板的橡胶色方柱, 纯装饰深度线索 (尺寸由场边长推导)。
        var postSide = MathF.Max(0.05f, size * 0.014f);
        var postHeight = height + postSide;
        foreach (var (px, pz) in new[] { (0f, 0f), (0f, size), (size, 0f), (size, size) })
        {
            root.AddChild(MakeBoxMatte(FloorColor.Darkened(0.35f),
                new Vector3(postSide, postHeight, postSide),
                new Vector3(px, postHeight / 2, pz)));
        }
    }

    /// <summary>
    /// Generates the platform top-surface texture with the official display
    /// gradient (visual-only: center pure white → four corners pure black,
    /// via the pure <see cref="FieldGrayTextureMap"/> mapping: row 0 = field
    /// south, column 0 = field west) and the red center square. The sensor
    /// gray model (<c>FieldModel.FieldGrayLocal</c>) is neither read nor
    /// modified here — the two gray semantics stay separate.
    /// </summary>
    private static ImageTexture MakeFieldGrayTexture(FieldModel model, FieldParams field)
    {
        const int resolution = FieldGrayTextureMap.DefaultResolution;
        var redZone = ToByteRgb(RedZoneColor);
        var buffer = FieldGrayTextureMap.BuildOfficialRgb8(
            resolution,
            field.Platform.MinX, field.Platform.MinY,
            field.Platform.MaxX, field.Platform.MaxY,
            model.Center,
            redZone);
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

    /// <summary>
    /// Primitive 机器人 fallback (render-only 分件): 车体/上盖/侧带/车轮/车头/推铲/
    /// 团队灯带/接触阴影。所有尺寸从碰撞半径推导, 不新建物理体、不改变
    /// 碰撞半径/位置/朝向/状态; glTF 模型导入成功时这些分件整体隐藏
    /// (RobotModelLoader 按 "primitivePart" meta 切换), 登台指示环始终保留。
    /// 子节点按名字查找: Configure 取 GetNodeOrNull("Ring"), 不依赖顺序。
    /// </summary>
    private Node3D BuildRobot(Color bodyColor, float radius)
    {
        const float robotHeight = 0.05f;
        var root = new Node3D();

        var body = MakeMeshPaintedMetal(new CylinderMesh
        {
            TopRadius = radius,
            BottomRadius = radius,
            Height = robotHeight,
        }, bodyColor, 0.15f, 0.5f);
        body.Name = "Body";
        body.Position = new Vector3(0, robotHeight / 2, 0);
        TagPrimitive(body);
        root.AddChild(body);

        // 车体上盖: 略小的浅色顶板, 表现舱盖分件。
        var topCap = MakeMeshPaintedMetal(new CylinderMesh
        {
            TopRadius = radius * 0.62f,
            BottomRadius = radius * 0.62f,
            Height = robotHeight * 0.28f,
        }, bodyColor.Lightened(0.18f), 0.2f, 0.4f);
        topCap.Name = "TopCap";
        topCap.Position = new Vector3(0, robotHeight + robotHeight * 0.14f, 0);
        TagPrimitive(topCap);
        root.AddChild(topCap);

        // 侧带 (轮/履带暗部): 车身下缘一圈橡胶质感暗环。
        var sideBand = MakeMeshMatte(new CylinderMesh
        {
            TopRadius = radius * 1.015f,
            BottomRadius = radius * 1.015f,
            Height = robotHeight * 0.4f,
        }, new Color(0.08f, 0.08f, 0.09f));
        sideBand.Name = "SideBand";
        sideBand.Position = new Vector3(0, robotHeight * 0.24f, 0);
        TagPrimitive(sideBand);
        root.AddChild(sideBand);

        // 四只车轮暗件: 车身两侧前后各一, 只露窄边, 不参与任何物理。
        var wheelRadius = radius * 0.3f;
        foreach (var (wx, wz, name) in new[]
        {
            (-radius * 0.93f, radius * 0.5f, "WheelFL"), (radius * 0.93f, radius * 0.5f, "WheelFR"),
            (-radius * 0.93f, -radius * 0.5f, "WheelRL"), (radius * 0.93f, -radius * 0.5f, "WheelRR"),
        })
        {
            var wheel = MakeMeshMatte(new CylinderMesh
            {
                TopRadius = wheelRadius,
                BottomRadius = wheelRadius,
                Height = 0.014f,
            }, new Color(0.05f, 0.05f, 0.06f));
            wheel.Name = name;
            wheel.Position = new Vector3(wx, wheelRadius, wz);
            wheel.Rotation = new Vector3(0, 0, Mathf.Pi / 2);
            TagPrimitive(wheel);
            root.AddChild(wheel);
        }

        // 车头指示: 与机身同色的短箭头, 沿 +Z (Godot 前向)。
        var nose = MakeMeshPaintedMetal(new BoxMesh
        {
            Size = new Vector3(0.05f, 0.018f, 0.12f),
        }, bodyColor.Lightened(0.25f), 0.2f, 0.45f);
        nose.Name = "Nose";
        nose.Position = new Vector3(0, robotHeight - 0.008f, radius + 0.02f);
        TagPrimitive(nose);
        root.AddChild(nose);

        // 推铲示意: 机身前缘加宽低框, 金属铲刀质感。
        var shovel = MakeMeshPaintedMetal(new BoxMesh
        {
            Size = new Vector3(radius * 1.78f, 0.02f, 0.03f),
        }, bodyColor.Darkened(0.15f), 0.6f, 0.35f);
        shovel.Name = "Shovel";
        shovel.Position = new Vector3(0, robotHeight - 0.02f, radius + 0.025f);
        TagPrimitive(shovel);
        root.AddChild(shovel);

        // 团队识别灯带: 车身上缘一圈同色自发光细环 (渲染层标识, 不承载状态)。
        var strip = MakeMeshEmissive(new CylinderMesh
        {
            TopRadius = radius * 1.02f,
            BottomRadius = radius * 1.02f,
            Height = 0.006f,
        }, bodyColor, 1f);
        strip.Name = "TeamStrip";
        strip.Position = new Vector3(0, robotHeight * 0.82f, 0);
        TagPrimitive(strip);
        root.AddChild(strip);

        // 登台指示环 (绿=在台上, 灰=不在)。按名字约定供 Configure 取用;
        // 不打 primitivePart meta (glTF 模型导入后仍作为诊断层保留)。
        var ring = MakeMesh(new CylinderMesh
        {
            TopRadius = radius + 0.045f,
            BottomRadius = radius + 0.045f,
            Height = 0.004f,
        }, RingOff);
        ring.Name = "Ring";
        ring.Position = new Vector3(0, 0.002f, 0);
        root.AddChild(ring);

        // 接触阴影承载: 脚下半透明暗盘, 补足方向光阴影的近地贴合感
        // (二轮微调: 台面中心现在更亮, 略提高不透明度让机器人在白心处"落地")。
        var shadow = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = radius * 1.22f,
                BottomRadius = radius * 1.22f,
                Height = 0.0015f,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0f, 0f, 0f, 0.32f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        shadow.Name = "ContactShadow";
        // 抬到台面纹理平面 (PlatformHeight + 0.001) 之上、登台环 (0.002) 之下:
        // 否则台上机器人的接触阴影盘会被台面顶面盖住, 恰好在最需要的位置失效。
        shadow.Position = new Vector3(0, 0.0016f, 0);
        TagPrimitive(shadow);
        root.AddChild(shadow);

        return root;
    }

    /// <summary>Marks a render-only primitive part so RobotModelLoader can hide
    /// the whole set when an imported glTF model takes over.</summary>
    private static void TagPrimitive(MeshInstance3D part) => part.SetMeta("primitivePart", true);

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
            // 能量块: buff/debuff 保留官方色相, 叠加微弱同色自发光提升可读性;
            // 出界块为灰色哑光。albedo 不变, 像素分桶 QA 不受影响。
            _blockMaterials[i].MaterialOverride = block.Out ? MakeMatte(OutColor)
                : block.Kind == "buff" ? MakeEmissive(BuffColor, 0.35f)
                : MakeEmissive(DebuffColor, 0.35f);
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
            // 登台指示: 在台上 = 绿色自发光状态灯; 不在台上 = 灰色哑光。
            ring.MaterialOverride = robot.OnPlatform ? MakeEmissive(RingOn, 0.8f) : MakeMatte(RingOff);
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

    private static MeshInstance3D MakeMesh(PrimitiveMesh primitive, Color color)
    {
        var mesh = new MeshInstance3D { Mesh = primitive };
        mesh.MaterialOverride = MakeMaterial(color);
        return mesh;
    }

    // ---------- reusable render material strategies ----------
    // 静态装饰/机器人分件的材质参数统一在这里表达 (喷涂金属/橡胶暗部/白色板材/
    // 发光标识), 避免每个节点各写一套互相漂移的参数。台面灰度纹理不在此列:
    // 它必须保持 Unshaded (见 MakeTexturedMaterial), 任何光照/高光都碰不到它。

    /// <summary>橡胶/深色结构件: 高粗糙度、无金属感 (走道地面、围栏、轮/履带暗部)。</summary>
    private static StandardMaterial3D MakeMatte(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.92f,
            Metallic = 0f,
        };
    }

    /// <summary>喷涂金属: 中低粗糙度 + 少量金属感/高光 (机器人车体、推铲、平台侧面)。</summary>
    private static StandardMaterial3D MakePaintedMetal(Color color, float metallic, float roughness)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = roughness,
            Metallic = metallic,
            MetallicSpecular = 0.5f,
        };
    }

    /// <summary>发光标识: albedo 保持原色 (像素分桶 QA 依赖), emission 叠加同色能量。</summary>
    private static StandardMaterial3D MakeEmissive(Color color, float energy)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.6f,
            Metallic = 0f,
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = energy,
        };
    }

    /// <summary>白色板材 (平台侧面): 低金属感、中等粗糙度, 轻微高光。</summary>
    private static StandardMaterial3D MakeWhiteBoard(Color color)
        => MakePaintedMetal(color, 0.05f, 0.5f);

    private static MeshInstance3D MakeBoxMatte(Color color, Vector3 size, Vector3 position)
    {
        var mesh = MakeMeshMatte(new BoxMesh { Size = size }, color);
        mesh.Position = position;
        return mesh;
    }

    private static MeshInstance3D MakeBoxWhiteBoard(Color color, Vector3 size, Vector3 position)
    {
        var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = size } };
        mesh.MaterialOverride = MakeWhiteBoard(color);
        mesh.Position = position;
        return mesh;
    }

    private static MeshInstance3D MakeMeshMatte(PrimitiveMesh primitive, Color color)
    {
        var mesh = new MeshInstance3D { Mesh = primitive };
        mesh.MaterialOverride = MakeMatte(color);
        return mesh;
    }

    private static MeshInstance3D MakeMeshPaintedMetal(PrimitiveMesh primitive, Color color, float metallic, float roughness)
    {
        var mesh = new MeshInstance3D { Mesh = primitive };
        mesh.MaterialOverride = MakePaintedMetal(color, metallic, roughness);
        return mesh;
    }

    private static MeshInstance3D MakeMeshEmissive(PrimitiveMesh primitive, Color color, float energy)
    {
        var mesh = new MeshInstance3D { Mesh = primitive };
        mesh.MaterialOverride = MakeEmissive(color, energy);
        return mesh;
    }

    // 动态换色材质 (能量块/指示环): 每帧按快照状态重设, 保持基础哑光参数。
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
            // 稳定线性过滤: 128×128 无 mipmap 图像的双线性采样, 不产生硬阶梯,
            // 也不让 mipmap 层在掠射角制造假灰度 (设计 3.2)。
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
        return material;
    }
}
