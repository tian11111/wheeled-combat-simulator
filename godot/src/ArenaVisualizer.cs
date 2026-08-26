// 脚手架 (未编译验证 — 需要 Godot 4 .NET):
// 纯展示层: 消费 SnapshotView 投影出的 RenderFrame，更新机器人/能量块节点
// 变换与 HUD。Godot 物理仅用于可视摆放，不参与判分 (design: 非权威)。

using Godot;

namespace Sim.GodotShell;

public partial class ArenaVisualizer : Node3D
{
    private Node3D? _usNode;
    private Node3D? _themNode;
    private readonly List<Node3D> _blockNodes = [];
    private Label? _statusLabel;

    public override void _Ready()
    {
        _usNode = GetNodeOrNull<Node3D>("UsRobot");
        _themNode = GetNodeOrNull<Node3D>("ThemRobot");
        _statusLabel = GetNodeOrNull<Label>("/root/Main/Hud/StatusLabel");
        // 块节点若场景未预置，则按首帧数量惰性创建。
    }

    public void ShowFrame(RenderFrame frame)
    {
        Apply(_usNode, frame.Us);
        Apply(_themNode, frame.Them);
        EnsureBlockNodes(frame.Blocks.Count);
        for (var i = 0; i < frame.Blocks.Count && i < _blockNodes.Count; i++)
        {
            var block = frame.Blocks[i];
            _blockNodes[i].Position = new Vector3((float)block.Position.X, (float)block.Position.Up, (float)block.Position.Z);
            _blockNodes[i].Visible = !block.Out;
        }
        if (_statusLabel is not null)
        {
            var hud = frame.Hud;
            _statusLabel.Text =
                $"tick {hud.Tick}  t={hud.T:0.0}s  剩余 {hud.Timer:0.0}s  {hud.Phase}"
                + (hud.Paused ? " (暂停)" : "")
                + $"\n我方 {hud.ScoreUs:0.#} : {hud.ScoreThem:0.#} 对手"
                + (hud.Done ? $"\n结束: {hud.DoneReason}" : "")
                + (hud.RecentEvents.Count > 0 ? $"\n{string.Join("\n", hud.RecentEvents)}" : "");
        }
    }

    private static void Apply(Node3D? node, RobotVisual robot)
    {
        if (node is null)
        {
            return;
        }
        node.Position = new Vector3((float)robot.Position.X, (float)robot.Position.Up, (float)robot.Position.Z);
        // 仿真航向 th 绕竖直轴; Godot 中 -Z 为模型前向，直接旋转 Y 轴即可对齐俯视布局。
        node.Rotation = new Vector3(0, (float)-robot.Yaw, 0);
    }

    private void EnsureBlockNodes(int count)
    {
        while (_blockNodes.Count < count)
        {
            var mesh = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.15f, 0.15f, 0.15f) },
            };
            AddChild(mesh);
            _blockNodes.Add(mesh);
        }
    }
}
