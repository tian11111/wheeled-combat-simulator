using System.Text.Json;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// R1 探点位置重标: WheeledCombat11 的 Forward/Lateral/Height 必须来自装配.glb
/// 光电节点的实测车体坐标 (tools/mesh/extend_vehicle_mounts.py →
/// tools/mesh/sensor_mounts.json), 且每一路都落在真车投影 / 尾铲合理区域内,
/// 不再出现旧默认车时代"探点悬在车头外 37mm"的失真。
/// 真车足迹常量与 scenarios/wushu-ring-2026-mujoco-v2.json 同值 (实测 glb)。
/// </summary>
public class SensorMountTests
{
    // 装配.glb 实测足迹 (m): 前伸/尾伸/侧伸, 见 PRD R1。
    private const double FrontExtent = 0.103;
    private const double RearExtent = 0.16949;
    private const double SideExtent = 0.12185;

    /// <summary>v2 车体坐标系: 原点=轮轴平面, 地面在 bodyZ=-0.0325 (轮半径)。
    /// 运行时探点世界 Z 以 ZG(承载面高度) 为锚, 因此 profile 的 Height =
    /// 挂点车体系 z + 轮半径 = 站立离地高度。</summary>
    private const double WheelRadius = 0.0325;

    private static readonly string[] ElevenChannelIds =
    [
        "gray_front", "gray_rear", "gray_left", "gray_right",
        "diag_left_front", "diag_left_rear", "diag_right_front", "diag_right_rear",
        "shovel_under_left", "shovel_under_right", "shovel_front",
    ];

    [Fact]
    public void WheeledCombat11_HasExactlyTheRealElevenChannels()
    {
        var profile = SensorProfiles.WheeledCombat11;
        Assert.Equal(11, profile.Channels.Count);
        Assert.Equal("wheeledCombat11", profile.Id);
        Assert.Equal(ElevenChannelIds, profile.Channels.Select(c => c.Id));
        Assert.Empty(profile.Validate());

        // 决策⑦: 只改数值不改 id/逻辑别名/语义模式。
        Assert.Equal("shovel_front", profile.Logical!["sFL"].Channel);
        Assert.Equal("shovel_front", profile.Logical["sFR"].Channel);
        Assert.True(profile.Logical["r"].IsNull);
        Assert.Equal(["diag_left_front", "diag_right_front"], profile.Logical["f"].Channels);
        Assert.Equal("max", profile.Logical["f"].Reducer);
    }

    [Fact]
    public void WheeledCombat11_ProbePoints_StayInsideRealVehicleFootprint()
    {
        var profile = SensorProfiles.WheeledCombat11;
        foreach (var channel in profile.Channels)
        {
            // 无悬空探点: 每一路都在真车足迹矩形内 (含尾铲)。
            Assert.True(Math.Abs(channel.Forward) <= Math.Max(FrontExtent, RearExtent) + 1e-9,
                $"{channel.Id}: forward {channel.Forward} outside vehicle footprint (±{Math.Max(FrontExtent, RearExtent)})");
            Assert.True(Math.Abs(channel.Lateral) <= SideExtent + 1e-9,
                $"{channel.Id}: lateral {channel.Lateral} outside vehicle footprint (±{SideExtent})");
        }

        foreach (var channel in profile.Channels.Where(c => c.Id.StartsWith("gray_")))
        {
            // 底盘灰度兜底: 投影内贴地 (离地 ≤ 12.5mm), 位置收进旧 ±0.11 口径内侧。
            Assert.True(Math.Abs(channel.Forward) <= 0.09 && Math.Abs(channel.Lateral) <= 0.09,
                $"{channel.Id}: engineering-default gray probe must stay within ±0.09");
            Assert.NotNull(channel.Height);
            Assert.Equal(0.0025, channel.Height!.Value, 4);
        }

        foreach (var channel in profile.Channels.Where(c => c.Id.StartsWith("diag_")))
        {
            // 对角数字 IR: 车头两舷巡台光电节点, 全部在车体前半投影内。
            Assert.True(channel.Forward > 0 && channel.Forward <= FrontExtent + 1e-9,
                $"{channel.Id}: diagonal probe must sit in the front half, forward={channel.Forward}");
            Assert.NotNull(channel.Height);
            Assert.Equal(0.02575 + WheelRadius, channel.Height!.Value, 5);
        }

        foreach (var channel in profile.Channels.Where(c => c.Id.StartsWith("shovel_under_")))
        {
            // 铲下 IR: 侧底光电支座, 贴近两舷且低位 (尾铲下, 离地 ≈ 41mm)。
            Assert.True(Math.Abs(channel.Lateral) > 0.09,
                $"{channel.Id}: under-shovel probe must sit near a side wall, lateral={channel.Lateral}");
            Assert.NotNull(channel.Height);
            Assert.Equal(0.00875 + WheelRadius, channel.Height!.Value, 5);
        }

        var shovelFront = profile.Channels.Single(c => c.Id == "shovel_front");
        // 屁股光电: 车尾约 165mm、高约 61mm(车体系, 高置信节点) —— 在尾铲区域而非车头。
        Assert.Equal(-0.165405, shovelFront.Forward, 6);
        Assert.Equal(0.001845, shovelFront.Lateral, 6);
        Assert.NotNull(shovelFront.Height);
        Assert.Equal(0.061194 + WheelRadius, shovelFront.Height!.Value, 6);
    }

    /// <summary>挂点 json 与 profile 的通道 id 映射 (含灰度兜底 4 路的工程默认 id)。</summary>
    private static readonly Dictionary<string, string> MountToChannel = new()
    {
        ["chassis_gray_front"] = "gray_front",
        ["chassis_gray_back"] = "gray_rear",
        ["chassis_gray_left"] = "gray_left",
        ["chassis_gray_right"] = "gray_right",
        ["diag_fl"] = "diag_left_front",
        ["diag_fr"] = "diag_right_front",
        ["diag_rl"] = "diag_left_rear",
        ["diag_rr"] = "diag_right_rear",
        ["shovel_under_left"] = "shovel_under_left",
        ["shovel_under_right"] = "shovel_under_right",
        ["shovel_front"] = "shovel_front",
    };

    private static string RepoFile(params string[] parts)
    {
        var relative = Path.Combine(parts);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void WheeledCombat11_Numbers_MatchSensorMountsJson()
    {
        var path = RepoFile("tools", "mesh", "sensor_mounts.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var mounts = document.RootElement.GetProperty("mounts");
        var profile = SensorProfiles.WheeledCombat11;

        var seen = new HashSet<string>();
        foreach (var mount in mounts.EnumerateArray())
        {
            var mountId = mount.GetProperty("id").GetString()!;
            Assert.True(MountToChannel.TryGetValue(mountId, out var channelId),
                $"mount '{mountId}' has no channel mapping");
            var channel = profile.Channels.Single(c => c.Id == channelId);
            Assert.Equal(mount.GetProperty("x").GetDouble(), channel.Forward, 9);
            Assert.Equal(mount.GetProperty("y").GetDouble(), channel.Lateral, 9);
            // Height = 挂点车体系 z + 轮半径 (站立离地高度, 见 Profiles.WheeledCombat11 注释)。
            Assert.Equal(mount.GetProperty("z").GetDouble() + WheelRadius, channel.Height!.Value, 9);
            seen.Add(channelId);
        }

        // json 覆盖全部 11 路, 没有通道被遗漏或凭空多出。
        Assert.Equal(profile.Channels.Count, seen.Count);
    }
}
