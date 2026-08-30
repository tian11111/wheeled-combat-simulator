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
    private const float SurfaceVisualLift = 0.001f;
    private const float BlockContactShadowLift = 0.0005f;
    private const float BlockContactShadowScale = 1.25f;
    // 主光 (DirectionalLight3D) 从世界上方偏 (+X,+Z) 照来; 阴影盘顺光平移到
    // 地面可见处 (块底边之外), 低角度近距观察时提供"落地"线索。
    private const float BlockShadowOffsetX = -0.12f;
    private const float BlockShadowOffsetZ = -0.22f;

    private static readonly Color FloorColor = new(0.16f, 0.18f, 0.22f);
    // 官方效果图外观: 底座侧面白、台面走道深灰、台面四角纯黑→中心纯白径向渐变、
    // 中央红区(白"武")。
    private static readonly Color PlatformSideColor = new(0.93f, 0.93f, 0.95f);
    private static readonly Color RedZoneColor = new(0.85f, 0.15f, 0.13f);
    private static readonly Color UsColor = new(0.28f, 0.48f, 0.95f);
    private static readonly Color ThemColor = new(0.92f, 0.30f, 0.28f);
    // 能量块外观取自规则 PDF 第 11 页示意图: 白色贴纸上是黄绿色闪电
    // 圆环 (增益) 或紫色警示圆环叠红色叉号 (减益), 不是语义化的 +/− 字符。
    private const string BuffTexturePath = "res://assets/energy/energy-buff-official.png";
    private const string DebuffTexturePath = "res://assets/energy/energy-debuff-official.png";
    private static readonly Color RingOn = new(0.35f, 0.92f, 0.45f);
    private static readonly Color RingOff = new(0.42f, 0.44f, 0.50f);
    private static readonly Color StartZoneUs = new(0.95f, 0.85f, 0.15f);   // 纯黄出发区
    private static readonly Color StartZoneThem = new(0.15f, 0.35f, 0.95f); // 纯蓝出发区

    private Node3D? _arenaRoot;
    private readonly List<MeshInstance3D> _blockNodes = [];
    private Node3D? _usRoot;
    private Node3D? _themRoot;
    private MeshInstance3D? _usRing;
    private MeshInstance3D? _themRing;
    private float _blockSize = 0.15f;
    private StandardMaterial3D? _buffBlockMaterial;
    private StandardMaterial3D? _debuffBlockMaterial;
    private StandardMaterial3D? _outBlockMaterial;
    private StandardMaterial3D? _blockContactShadowMaterial;
    private Texture2D? _buffBlockTexture;
    private Texture2D? _debuffBlockTexture;
    private ArrayMesh? _energyBlockMesh;
    private float _energyBlockMeshSize = -1f;
    private float _platformTop;

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
        // Keep the visual block base on the same rendered support plane as the
        // grayscale platform surface.  SnapshotView's Up is the simulation
        // height (0.06 m), while the visual surface is intentionally lifted by
        // 1 mm to avoid z-fighting with the platform body.
        _platformTop = (float)field.PlatformHeight + SurfaceVisualLift;

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
        surface.Position = new Vector3(center, top + SurfaceVisualLift, center);
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
            // SnapshotView.Up is a simulation height.  Anchor the rendered base
            // to the actual visual support plane so the sticker cube cannot read
            // as suspended when the top plane is lifted for z-fighting safety.
            var baseHeight = block.OnPlatform ? _platformTop : 0f;
            // Blocks are world-space, axis-aligned cubes.  They are not billboards
            // and must never inherit a view/editor rotation or scale.
            node.Rotation = Vector3.Zero;
            node.Scale = Vector3.One;
            node.Position = new Vector3(
                (float)block.Position.X,
                baseHeight + _blockSize / 2,
                (float)block.Position.Z);
            PositionBlockContactShadow(node, _blockSize);
            node.Visible = true;
            // 能量块: 每个面使用同一张赛事风格标识贴图，避免只能靠纯色猜类别；
            // 材质在 EnsureBlockNodes 中缓存，不在 50 Hz 快照
            // 更新时重复创建 GPU 资源。
            node.MaterialOverride = block.Out
                ? _outBlockMaterial
                : block.Kind == "buff" ? _buffBlockMaterial : _debuffBlockMaterial;
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
        EnsureBlockMaterials();
        EnsureBlockMesh();
        _blockContactShadowMaterial ??= MakeBlockContactShadowMaterial();
        while (_blockNodes.Count < count)
        {
            var mesh = new MeshInstance3D
            {
                // 自定义六面网格为每个面复制同一套 UV, 避免 BoxMesh 的面向
                // 展开把图案旋转/镜像成不同方向。
                Mesh = _energyBlockMesh,
                MaterialOverride = _outBlockMaterial,
            };
            var edges = new MeshInstance3D
            {
                Name = "Edges",
                Mesh = MakeBlockEdgeMesh(_blockSize),
                MaterialOverride = MakeBlockEdgeMaterial(),
            };
            var shadow = new MeshInstance3D
            {
                Name = "ContactShadow",
                Mesh = MakeBlockContactShadowMesh(_blockSize),
                MaterialOverride = _blockContactShadowMaterial,
            };
            mesh.AddChild(edges);
            mesh.AddChild(shadow);
            AddChild(mesh);
            _blockNodes.Add(mesh);
        }
    }

    /// <summary>
    /// 12 条深色棱线: 白底贴纸立方体在白亮的擂台中心会失去轮廓, 角对角视角
    /// 被读成"交叉的斜面板"; 棱线让立方体轮廓在所有角度可辨 (纯渲染装饰)。
    /// </summary>
    private static ArrayMesh MakeBlockEdgeMesh(float size)
    {
        var half = size / 2f;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        // 沿三条世界轴各布 4 条边; 厚度约为块边长的 6% (似骰子倒角, 远景可辨)。
        var t = size * 0.06f;
        var axisX = new Vector3(1, 0, 0);
        var axisY = new Vector3(0, 1, 0);
        var axisZ = new Vector3(0, 0, 1);
        foreach (var s in new[] { -1f, 1f })
        {
            foreach (var w in new[] { -half, half })
            {
                // X 轴边: y/z 固定在角上。
                AddEdgeBox(tool, axisX, new Vector3(0, s * half, w), half, t);
                AddEdgeBox(tool, axisY, new Vector3(w, 0, s * half), half, t);
                AddEdgeBox(tool, axisZ, new Vector3(s * half, w, 0), half, t);
            }
        }
        return tool.Commit() ?? throw new InvalidOperationException("Could not build block edge mesh");
    }

    private static void AddEdgeBox(SurfaceTool tool, Vector3 axis, Vector3 center, float length, float thickness)
    {
        var right = axis;
        var up = Math.Abs(axis.Y) > 0.5f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
        var forward = right.Cross(up);
        var hl = length / 2f;
        var ht = thickness / 2f;
        for (var face = 0; face < 4; face++)
        {
            var normal = face switch
            {
                0 => up,
                1 => up * -1,
                2 => forward,
                _ => forward * -1,
            };
            var side = face < 2 ? forward : up;
            var a = center + normal * ht - side * ht - right * hl;
            var b = center + normal * ht - side * ht + right * hl;
            var c = center + normal * ht + side * ht + right * hl;
            var d = center + normal * ht + side * ht - right * hl;
            // Clockwise from outside (Godot front-face convention).
            tool.SetNormal(normal);
            tool.AddVertex(a);
            tool.SetNormal(normal);
            tool.AddVertex(d);
            tool.SetNormal(normal);
            tool.AddVertex(c);
            tool.SetNormal(normal);
            tool.AddVertex(a);
            tool.SetNormal(normal);
            tool.AddVertex(c);
            tool.SetNormal(normal);
            tool.AddVertex(b);
        }
    }

    private static StandardMaterial3D MakeBlockEdgeMaterial()
    {
        return new StandardMaterial3D
        {
            AlbedoColor = new Color(0.16f, 0.18f, 0.24f),
            Roughness = 0.9f,
            Metallic = 0f,
        };
    }

    private void EnsureBlockMaterials()
    {
        _buffBlockTexture ??= LoadEnergyTexture(BuffTexturePath);
        _debuffBlockTexture ??= LoadEnergyTexture(DebuffTexturePath);
        _buffBlockMaterial ??= MakeEnergyBlockMaterial(_buffBlockTexture, Colors.White);
        _debuffBlockMaterial ??= MakeEnergyBlockMaterial(_debuffBlockTexture, Colors.White);
        // 出界块仍保留官方识别图案, 仅整体压暗表示它已离开本局有效区域;
        // 不再绘制会被误认成规则图案的自制斜线/棋盘标记。
        _outBlockMaterial ??= MakeEnergyBlockMaterial(_buffBlockTexture,
            new Color(0.48f, 0.49f, 0.53f));
    }

    private static Texture2D LoadEnergyTexture(string resourcePath)
    {
        // 直接从 res:// 读取 PNG, 不依赖编辑器先生成 .import 文件; 这同时覆盖
        // 首次启动、headless 快速仿真和导出到 PCK 的运行方式。
        var bytes = Godot.FileAccess.GetFileAsBytes(resourcePath);
        if (bytes.Length > 0)
        {
            var image = new Image();
            if (image.LoadPngFromBuffer(bytes) == Error.Ok
                && image.GetWidth() > 0 && image.GetHeight() > 0)
            {
                return ImageTexture.CreateFromImage(image);
            }
        }

        GD.PushError($"Energy block texture could not be loaded: {resourcePath}");
        return ImageTexture.CreateFromImage(MakeMissingEnergyTexture());
    }

    private static StandardMaterial3D MakeEnergyBlockMaterial(Texture2D texture, Color tint)
    {
        return new StandardMaterial3D
        {
            AlbedoTexture = texture,
            AlbedoColor = tint,
            Roughness = 0.72f,
            Metallic = 0f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
    }

    private static StandardMaterial3D MakeBlockContactShadowMaterial()
    {
        return new StandardMaterial3D
        {
            AlbedoColor = new Color(0.03f, 0.04f, 0.06f, 0.40f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
    }

    private static PlaneMesh MakeBlockContactShadowMesh(float size)
    {
        // A flat plane is deliberately used instead of a thin cylinder.  A
        // cylinder protrudes below an oblique camera and reads as a pointed
        // underside rather than a cube resting on the field.
        return new PlaneMesh
        {
            Size = new Vector2(size * BlockContactShadowScale, size * BlockContactShadowScale),
        };
    }

    private static void PositionBlockContactShadow(MeshInstance3D block, float blockSize)
    {
        if (block.GetNodeOrNull<MeshInstance3D>("ContactShadow") is not { } shadow)
        {
            return;
        }

        shadow.Position = new Vector3(
            BlockShadowOffsetX * blockSize,
            -blockSize / 2f + BlockContactShadowLift,
            BlockShadowOffsetZ * blockSize);
        shadow.Visible = true;
    }

    /// <summary>Builds a cube whose six faces all use the same upright UV square.</summary>
    private static ArrayMesh MakeOfficialEnergyBlockMesh(float size)
    {
        var half = size / 2f;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);

        // right × up = outward normal.  The up vector is world-up for vertical
        // faces; on the top/bottom it keeps the printed symbol aligned to +Z/−Z.
        AddTexturedFace(tool, half, new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        AddTexturedFace(tool, half, new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 1, 0));
        AddTexturedFace(tool, half, new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        AddTexturedFace(tool, half, new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0));
        AddTexturedFace(tool, half, new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, -1));
        AddTexturedFace(tool, half, new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1));

        return tool.Commit() ?? throw new InvalidOperationException("Could not build energy block mesh");
    }

    private static void AddTexturedFace(SurfaceTool tool, float half, Vector3 normal,
        Vector3 right, Vector3 up)
    {
        var center = normal * half;
        var bottomLeft = center - right * half - up * half;
        var bottomRight = center + right * half - up * half;
        var topRight = center + right * half + up * half;
        var topLeft = center - right * half + up * half;

        // Godot textures use v=0 at the top.  Duplicate vertices keep each face's
        // normal and UV orientation independent from its neighbours.  Godot uses
        // CLOCKWISE winding for front faces — from outside, each face's vertices
        // must go bottom-left → top-left → top-right (…), otherwise the face is
        // culled from outside and the cube renders hollow (see-through far
        // faces), which reads as floating tilted sticker panels.
        AddTexturedVertex(tool, bottomLeft, normal, new Vector2(0, 1));
        AddTexturedVertex(tool, topLeft, normal, new Vector2(0, 0));
        AddTexturedVertex(tool, topRight, normal, new Vector2(1, 0));
        AddTexturedVertex(tool, bottomLeft, normal, new Vector2(0, 1));
        AddTexturedVertex(tool, topRight, normal, new Vector2(1, 0));
        AddTexturedVertex(tool, bottomRight, normal, new Vector2(1, 1));
    }

    private static void AddTexturedVertex(SurfaceTool tool, Vector3 position, Vector3 normal, Vector2 uv)
    {
        tool.SetNormal(normal);
        tool.SetUV(uv);
        tool.AddVertex(position);
    }

    private static Image MakeMissingEnergyTexture()
    {
        var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(new Color(0.75f, 0.75f, 0.78f));
        return image;
    }

    private void EnsureBlockMesh()
    {
        if (_energyBlockMesh is not null && Mathf.Abs(_energyBlockMeshSize - _blockSize) <= 1e-5f)
        {
            return;
        }

        _energyBlockMesh = MakeOfficialEnergyBlockMesh(_blockSize);
        _energyBlockMeshSize = _blockSize;
        foreach (var node in _blockNodes)
        {
            node.Mesh = _energyBlockMesh;
            if (node.GetNodeOrNull<MeshInstance3D>("ContactShadow") is { } shadow)
            {
                shadow.Mesh = MakeBlockContactShadowMesh(_blockSize);
                shadow.Position = new Vector3(0f,
                    -_blockSize / 2f + BlockContactShadowLift,
                    0f);
            }
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
