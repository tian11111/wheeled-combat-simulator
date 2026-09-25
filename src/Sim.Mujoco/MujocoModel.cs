using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Mujoco;

/// <summary>Versioned, deterministic contact model. All coordinates are SI, Z up.</summary>
internal static class MujocoModel
{
    internal const string Version = PhysicsSpec.MujocoModelV1;
    internal const double SubstepSeconds = 0.005;
    internal const int SubstepsPerTick = 10;
    internal const double WheelRadius = 0.065;
    internal const double WheelForceLimit = 0.3;
    internal const double WheelAngularSpeedLimit = 80.0;

    internal static (string Xml, string Sha256) Generate(PhysicsBackendContext context)
    {
        var field = context.Scenario.Field;
        var t = context.Field.Transform;
        var sb = new StringBuilder(8192);
        sb.Append("<mujoco model=\"wushu-mjcf-v1\"><compiler angle=\"radian\"/><option timestep=\"0.005\" gravity=\"0 0 -9.81\" integrator=\"implicitfast\"/><size njmax=\"2000\" nconmax=\"500\"/><default><geom friction=\"0.85 0.01 0.002\" solref=\"0.008 1\" solimp=\"0.95 0.99 0.001\"/></default><worldbody>");
        // The field is placed once by a rigid parent; entity initial poses are already world poses.
        sb.Append("<body name=\"arena\" pos=\"").Append(N(t.X)).Append(' ').Append(N(t.Y))
            .Append(" 0\" euler=\"0 0 ").Append(N(t.Th)).Append("\">");
        Box(sb, "ground", field.FieldSize / 2, field.FieldSize / 2, -0.025,
            field.FieldSize / 2, field.FieldSize / 2, 0.025, friction: 1.0);
        var p = field.Platform;
        Box(sb, "platform", (p.MinX + p.MaxX) / 2, (p.MinY + p.MaxY) / 2,
            field.PlatformHeight / 2, (p.MaxX - p.MinX) / 2, (p.MaxY - p.MinY) / 2,
            field.PlatformHeight / 2, friction: 1.0);
        const double fenceThickness = 0.025;
        var fs = field.FieldSize;
        Box(sb, "fence_s", fs / 2, -fenceThickness / 2, field.FenceHeight / 2,
            fs / 2 + fenceThickness, fenceThickness / 2, field.FenceHeight / 2);
        Box(sb, "fence_n", fs / 2, fs + fenceThickness / 2, field.FenceHeight / 2,
            fs / 2 + fenceThickness, fenceThickness / 2, field.FenceHeight / 2);
        Box(sb, "fence_w", -fenceThickness / 2, fs / 2, field.FenceHeight / 2,
            fenceThickness / 2, fs / 2, field.FenceHeight / 2);
        Box(sb, "fence_e", fs + fenceThickness / 2, fs / 2, field.FenceHeight / 2,
            fenceThickness / 2, fs / 2, field.FenceHeight / 2);
        sb.Append("</body>");
        Robot(sb, context.Us, context.Field);
        Robot(sb, context.Them, context.Field);
        for (var i = 0; i < context.Blocks.Count; i++)
        {
            var b = context.Blocks[i];
            var z = context.Field.StageHeightAt(b.X, b.Y) + field.BlockSize / 2;
            sb.Append("<body name=\"block_").Append(i).Append("\" pos=\"")
                .Append(N(b.X)).Append(' ').Append(N(b.Y)).Append(' ').Append(N(z))
                .Append("\"><freejoint name=\"block_joint_").Append(i).Append("\"/>");
            sb.Append("<geom name=\"block_geom_").Append(i).Append("\" type=\"box\" size=\"")
                .Append(N(field.BlockSize / 2)).Append(' ').Append(N(field.BlockSize / 2)).Append(' ')
                .Append(N(field.BlockSize / 2)).Append("\" mass=\"0.3\" friction=\"0.6 0.01 0.002\"/>");
            sb.Append("</body>");
        }
        sb.Append("</worldbody><actuator>");
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            foreach (var axle in new[] { "front", "rear" })
            foreach (var side in new[] { "left", "right" })
            {
                sb.Append("<velocity name=\"motor_").Append(role).Append('_').Append(axle).Append('_').Append(side)
                    .Append("\" joint=\"wheel_").Append(role).Append('_').Append(axle).Append('_').Append(side)
                    .Append("\" kv=\"1.0\" ctrllimited=\"true\" ctrlrange=\"-").Append(N(WheelAngularSpeedLimit))
                    .Append(' ').Append(N(WheelAngularSpeedLimit)).Append("\" forcelimited=\"true\" forcerange=\"-")
                    .Append(N(WheelForceLimit)).Append(' ').Append(N(WheelForceLimit)).Append("\"/>");
            }
        }
        sb.Append("</actuator></mujoco>");
        var xml = sb.ToString();
        return (xml, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant());
    }

    private static void Robot(StringBuilder sb, RobotRuntime r, FieldModel field)
    {
        var v = r.Vehicle;
        var ground = field.StageHeightAt(r.X, r.Y);
        var z = ground + WheelRadius + 0.04;
        var halfL = Math.Min(v.Length / 2, v.FrontExtent);
        var halfW = Math.Min(v.Width / 2, v.SideExtent);
        sb.Append("<body name=\"robot_").Append(r.Role).Append("\" pos=\"")
            .Append(N(r.X)).Append(' ').Append(N(r.Y)).Append(' ').Append(N(z))
            .Append("\" euler=\"0 0 ").Append(N(r.Th)).Append("\"><freejoint name=\"robot_joint_")
            .Append(r.Role).Append("\"/>");
        sb.Append("<geom name=\"robot_body_").Append(r.Role).Append("\" type=\"box\" size=\"")
            .Append(N(halfL)).Append(' ').Append(N(halfW)).Append(' ').Append(N(v.Height / 2))
            .Append("\" mass=\"").Append(N(v.Mass)).Append("\"/>");
        sb.Append("<geom name=\"robot_shovel_").Append(r.Role).Append("\" type=\"box\" pos=\"")
            .Append(N(v.FrontExtent - v.ShovelLength / 2)).Append(" 0 ")
            .Append(N(-z + ground + v.ShovelHeight + 0.006))
            .Append("\" size=\"").Append(N(v.ShovelLength / 2)).Append(' ')
            .Append(N(v.ShovelWidth / 2)).Append(" 0.006\" mass=\"0.02\"/>");
        foreach (var axle in new[] { "front", "rear" })
        foreach (var side in new[] { "left", "right" })
        {
            var x = (axle == "front" ? 1 : -1) * v.WheelBase / 2;
            var y = (side == "left" ? 1 : -1) * v.TrackWidth / 2;
            var name = $"{r.Role}_{axle}_{side}";
            sb.Append("<body name=\"wheel_body_").Append(name).Append("\" pos=\"")
                .Append(N(x)).Append(' ').Append(N(y)).Append(" -0.04\">");
            sb.Append("<joint name=\"wheel_").Append(name)
                .Append("\" type=\"hinge\" axis=\"0 1 0\" damping=\"0.02\"/>");
            sb.Append("<geom name=\"wheel_geom_").Append(name)
                .Append("\" type=\"cylinder\" euler=\"1.5707963267948966 0 0\" size=\"")
                .Append(N(WheelRadius)).Append(" 0.012\" mass=\"0.03\" friction=\"1.3 0.01 0.002\"/>");
            sb.Append("</body>");
        }
        sb.Append("</body>");
    }

    private static void Box(StringBuilder sb, string name, double x, double y, double z,
        double hx, double hy, double hz, double friction = 0.8)
    {
        sb.Append("<geom name=\"").Append(name).Append("\" type=\"box\" pos=\"")
            .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(z)).Append("\" size=\"")
            .Append(N(hx)).Append(' ').Append(N(hy)).Append(' ').Append(N(hz))
            .Append("\" friction=\"").Append(N(friction)).Append(" 0.01 0.002\"/>");
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
