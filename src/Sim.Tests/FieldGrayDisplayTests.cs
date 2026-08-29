using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Field-gray display evidence (task 08-28-godot-camera-gray-restart):
/// representative <see cref="FieldModel.FieldGrayLocal"/> values (0–1000
/// semantics: walkway 0, edge band 300, ramp, red zone 650), axis symmetry
/// (no manufactured diagonal), and the pure texture mapping used by the Godot
/// shell — image row 0 = field south, column 0 = field west, official display
/// palette (ring edge 300 → black band, center 1000 → white, walkway/red zone
/// as official palette colors). The measured-grid runtime path is out of scope
/// here; coordinate-less sensor CSV must never reach these models.
/// </summary>
public sealed class FieldGrayDisplayTests
{
    private static FieldParams OfficialField() => new();

    private static (byte R, byte G, byte B) TestRed() => (217, 38, 33);

    private static (byte R, byte G, byte B) TestWalkway() => (72, 72, 72);

    // ---------- core gray model: representative values ----------

    [Fact]
    public void FieldGray_WalkwayIsZero_EdgeBandIs300()
    {
        var model = new FieldModel(OfficialField());
        Assert.Equal(0, model.FieldGrayLocal(0.5, 1.9));  // 平台外走道
        Assert.Equal(0, model.FieldGrayLocal(3.5, 0.2));
        // 浮点积分在边缘留 1e-16 残差, 按 9 位小数判定黑带值。
        Assert.Equal(300, model.FieldGrayLocal(0.7, 1.9), precision: 9); // 台面西边缘黑带
        Assert.Equal(300, model.FieldGrayLocal(1.9, 3.1), precision: 9); // 台面北边缘黑带
    }

    [Fact]
    public void FieldGray_RampAndRedZone_RepresentativeValues()
    {
        var model = new FieldModel(OfficialField());
        // 中央红区 (0.6×0.6): 650,与传感器模型同值。
        Assert.Equal(650, model.FieldGrayLocal(1.9, 1.9));
        // 红区边界: 0.29 仍在红区内, 0.31 已回到灰度斜坡。
        Assert.Equal(650, model.FieldGrayLocal(2.19, 1.9));
        Assert.Equal(300.0 + 700.0 * (1 - 0.31 / 1.2), model.FieldGrayLocal(2.21, 1.9), precision: 9);
        // 斜坡中间点 (t = dx/half)。
        Assert.Equal(300.0 + 700.0 * (1 - 0.84 / 1.2), model.FieldGrayLocal(1.06, 1.9), precision: 9);
    }

    [Fact]
    public void FieldGray_AxisSymmetric_NoDiagonalAsymmetry()
    {
        var model = new FieldModel(OfficialField());
        // 手绘模型只依赖 max(|dx|,|dy|): 同半径四点同值, 不产生斜向条带。
        var east = model.FieldGrayLocal(2.6, 1.9);
        var west = model.FieldGrayLocal(1.2, 1.9);
        var north = model.FieldGrayLocal(1.9, 2.6);
        var south = model.FieldGrayLocal(1.9, 1.2);
        Assert.Equal(east, west, precision: 9);
        Assert.Equal(east, north, precision: 9);
        Assert.Equal(east, south, precision: 9);
        // 对角对称点同值。
        Assert.Equal(model.FieldGrayLocal(1.0, 1.0), model.FieldGrayLocal(2.8, 2.8), precision: 9);
        Assert.Equal(model.FieldGrayLocal(1.0, 2.8), model.FieldGrayLocal(2.8, 1.0), precision: 9);
    }

    [Fact]
    public void FieldGray_WorldEntryMatchesFieldLocalThroughTransform()
    {
        var pose = new Pose2 { X = 10, Y = -4, Th = Math.PI / 3 };
        var model = new FieldModel(new FieldParams { Pose = pose });
        var t = model.Transform;
        var (lx, ly) = (1.2, 2.4);
        var (wx, wy) = t.LocalToWorldPoint(lx, ly);
        Assert.Equal(model.FieldGrayLocal(lx, ly), model.FieldGray(wx, wy), precision: 12);
    }

    // ---------- texture mapping: image-axis orientation ----------

    [Fact]
    public void PixelToFieldLocal_Row0IsSouth_Column0IsWest()
    {
        // 2×2 采样 [0,10]²: 中心点位于四分位。
        var (x00, y00) = FieldGrayTextureMap.PixelToFieldLocal(0, 0, 2, 0, 0, 10, 10);
        var (x10, y10) = FieldGrayTextureMap.PixelToFieldLocal(1, 0, 2, 0, 0, 10, 10);
        var (x01, y01) = FieldGrayTextureMap.PixelToFieldLocal(0, 1, 2, 0, 0, 10, 10);
        Assert.Equal((2.5, 2.5), (x00, y00));
        Assert.Equal(7.5, x10); // 列随 x 增 (西→东)
        Assert.Equal(7.5, y01); // 行随 y 增 (南→北, 图像行 0 = 南)
        Assert.Equal(2.5, y10);
    }

    [Fact]
    public void IsRedZone_CenterSquare30Cm()
    {
        Assert.True(FieldGrayTextureMap.IsRedZone(1.9, 1.9, 1.9));
        Assert.True(FieldGrayTextureMap.IsRedZone(2.19, 1.9, 1.9));
        Assert.True(FieldGrayTextureMap.IsRedZone(1.61, 2.19, 1.9));
        Assert.False(FieldGrayTextureMap.IsRedZone(2.21, 1.9, 1.9));
        Assert.False(FieldGrayTextureMap.IsRedZone(1.9, 1.59, 1.9));
    }

    [Fact]
    public void DisplayLuminance_OfficialPalette_MapsEdgeBandToBlackCenterToWhite()
    {
        // 官方调色板: 擂台边带 300 → 黑, 中心 1000 → 白; 传感器 0–1000 语义不变。
        Assert.Equal(0.0, FieldGrayTextureMap.DisplayLuminance(300), precision: 9);
        Assert.Equal(0.5, FieldGrayTextureMap.DisplayLuminance(650), precision: 9);
        Assert.Equal(1.0, FieldGrayTextureMap.DisplayLuminance(1000), precision: 9);
        Assert.Equal(0.0, FieldGrayTextureMap.DisplayLuminance(-5), precision: 9);
        Assert.Equal(1.0, FieldGrayTextureMap.DisplayLuminance(2000), precision: 9);
    }

    [Fact]
    public void IsWalkwayGray_DetectsWalkwayOnlyNearZero()
    {
        Assert.True(FieldGrayTextureMap.IsWalkwayGray(0));
        Assert.True(FieldGrayTextureMap.IsWalkwayGray(0.4));
        Assert.False(FieldGrayTextureMap.IsWalkwayGray(300));
    }

    [Fact]
    public void BuildRgb8_WestEastGradient_PlacesEastBright()
    {
        var buffer = FieldGrayTextureMap.BuildRgb8(4, 0, 0, 1000, 1000, center: -100,
            (x, y) => x, redZone: (255, 0, 0), walkway: TestWalkway());
        // 同一行的西列暗、东列亮; 同一列的行间一致 (梯度只沿 x)。
        int V(int px, int py) => buffer[(py * 4 + px) * 3];
        Assert.True(V(0, 1) < V(3, 1));
        Assert.Equal(V(0, 1), V(0, 3));
        Assert.Equal(V(3, 1), V(3, 2));
    }

    [Fact]
    public void BuildRgb8_SouthNorthGradient_Row0IsSouth()
    {
        var buffer = FieldGrayTextureMap.BuildRgb8(4, 0, 0, 1000, 1000, center: -100,
            (x, y) => y, redZone: (255, 0, 0), walkway: TestWalkway());
        int V(int px, int py) => buffer[(py * 4 + px) * 3];
        // 图像行 0 = 场地南侧: 顶行暗、底行亮。
        Assert.True(V(1, 0) < V(1, 3));
        Assert.Equal(V(0, 0), V(3, 0));
        Assert.Equal(V(0, 3), V(3, 3));
    }

    [Fact]
    public void BuildRgb8_WalkwaySamples_RenderOfficialDarkGray()
    {
        // 走道区域 (sensor 值 ≈ 0) 显示官方走道深灰, 而不是纯黑。
        var buffer = FieldGrayTextureMap.BuildRgb8(4, 0, 0, 1000, 1000, center: -100,
            (x, y) => 0, redZone: (255, 0, 0), walkway: TestWalkway());
        int R(int px, int py) => buffer[(py * 4 + px) * 3];
        Assert.Equal(TestWalkway().R, R(0, 0));
        Assert.Equal(TestWalkway().R, R(3, 3));
    }

    [Fact]
    public void BuildRgb8_OfficialPlatform_RepresentativePixels()
    {
        var field = OfficialField();
        var model = new FieldModel(field);
        var buffer = FieldGrayTextureMap.BuildRgb8(
            FieldGrayTextureMap.DefaultResolution,
            field.Platform.MinX, field.Platform.MinY,
            field.Platform.MaxX, field.Platform.MaxY,
            model.Center,
            model.FieldGrayLocal,
            TestRed(),
            TestWalkway());

        const int res = FieldGrayTextureMap.DefaultResolution;
        int V(int px, int py) => buffer[(py * res + px) * 3];
        int G(int px, int py) => buffer[(py * res + px) * 3 + 1];
        int B(int px, int py) => buffer[(py * res + px) * 3 + 2];

        // 角像素 (西南角) 的绝对代表值: 距边 0.009375m → g=305.46875 →
        // 官方调色板 (305.47-300)/700 → 亮度 0.008 → 2 (擂台外圈黑边)。
        Assert.Equal(2, V(0, 0));
        // 图像中心在红区内: 精确的红区字节 (白"武"由 Label3D 叠加)。
        Assert.Equal(TestRed().R, V(res / 2, res / 2));
        Assert.Equal(TestRed().G, G(res / 2, res / 2));
        Assert.Equal(TestRed().B, B(res / 2, res / 2));
        // 东北角像素与西南角同值 (轴对称), 但必须落在"最后一行/列"而不是首行。
        Assert.Equal(V(0, 0), V(res - 1, res - 1));
        Assert.NotEqual(TestWalkway().R, V(0, res - 1)); // 采样点永远在平台内, 不会是走道
    }
}
