using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Field-gray evidence for two strictly separate semantics:
/// - <b>sensor</b>: <see cref="FieldModel.FieldGrayLocal"/> keeps its 0–1000
///   L∞ model (walkway 0, edge band 300, ramp, red zone 650) — the display
///   work never touches it;
/// - <b>display</b>: the official rulebook surface look (规则 PDF 第 10 页
///   "擂台表面底色从外侧四角到中心分别为纯黑到纯白渐变") via
///   <see cref="FieldGrayTextureMap.OfficialSurfaceLuminance"/> — normalized
///   Euclidean radial gradient: center pure white, four corners pure black,
///   edge midpoints medium gray, and equal luminance at equal Euclidean
///   radius regardless of direction. The axial-vs-diagonal equal-radius
///   samples are the discriminating check against the old L∞ square display
///   that rendered bright diagonal streaks. Also covered: red-zone priority,
///   image-axis orientation (row 0 = field south, column 0 = field west),
///   and clamping. The measured-grid runtime path is out of scope here;
///   coordinate-less sensor CSV must never reach these models.
/// </summary>
public sealed class FieldGrayDisplayTests
{
    private static FieldParams OfficialField() => new();

    private static (byte R, byte G, byte B) TestRed() => (217, 38, 33);

    // ---------- core gray model: representative values (sensor semantics) ----------

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
        // 传感器模型只依赖 max(|dx|,|dy|): 同 L∞ 半径四点同值。注意这只说明
        // 传感器对称; 旧显示层直接把它当亮度才产生对角亮带 — 显示层的
        // 等欧氏半径约束由 OfficialSurfaceLuminance 的专项测试保证。
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

    // ---------- official display gradient (visual-only) ----------

    [Fact]
    public void OfficialSurfaceLuminance_CenterWhite_FourCornersBlack()
    {
        var p = OfficialField().Platform;
        Assert.Equal(1.0,
            FieldGrayTextureMap.OfficialSurfaceLuminance(1.9, 1.9, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        foreach (var (x, y) in new[]
        {
            (p.MinX, p.MinY), (p.MaxX, p.MinY), (p.MinX, p.MaxY), (p.MaxX, p.MaxY),
        })
        {
            Assert.Equal(0.0,
                FieldGrayTextureMap.OfficialSurfaceLuminance(x, y, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        }
    }

    [Fact]
    public void OfficialSurfaceLuminance_EdgeMidpointsAreEqualMediumGray()
    {
        var p = OfficialField().Platform;
        var expected = 1 - 1 / Math.Sqrt(2); // 归一化半径 1/√2
        foreach (var (x, y) in new[]
        {
            (p.MinX, 1.9), (p.MaxX, 1.9), (1.9, p.MinY), (1.9, p.MaxY),
        })
        {
            Assert.Equal(expected,
                FieldGrayTextureMap.OfficialSurfaceLuminance(x, y, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        }
    }

    [Fact]
    public void OfficialSurfaceLuminance_EqualEuclideanRadius_AxialAndDiagonalAgree()
    {
        // 官方渐变的核心判别测试: 同一欧氏半径上轴向与对角向亮度必须一致。
        // 旧 L∞ 显示在对角向更亮 (t=0.354 vs 0.5), 正是白色对角亮带的来源。
        var p = OfficialField().Platform;
        const double r = 0.6;            // 归一化半径 0.5
        var axial = FieldGrayTextureMap.OfficialSurfaceLuminance(
            1.9 + r, 1.9, p.MinX, p.MinY, p.MaxX, p.MaxY);
        var d = r / Math.Sqrt(2);
        foreach (var (x, y) in new[]
        {
            (1.9 + r, 1.9), (1.9 - r, 1.9), (1.9, 1.9 + r), (1.9, 1.9 - r),       // 四轴向
            (1.9 + d, 1.9 + d), (1.9 + d, 1.9 - d), (1.9 - d, 1.9 + d), (1.9 - d, 1.9 - d), // 四对角
        })
        {
            Assert.Equal(axial,
                FieldGrayTextureMap.OfficialSurfaceLuminance(x, y, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        }
        // 明确否定旧 L∞ 行为: 对角点不得比同半径轴向点更亮。
        var diagonal = FieldGrayTextureMap.OfficialSurfaceLuminance(
            1.9 + d, 1.9 + d, p.MinX, p.MinY, p.MaxX, p.MaxY);
        Assert.Equal(axial, diagonal, precision: 12);
    }

    [Fact]
    public void OfficialSurfaceLuminance_MonotonicTowardCenter_AxialAndDiagonal()
    {
        var p = OfficialField().Platform;
        double L(double x, double y) => FieldGrayTextureMap.OfficialSurfaceLuminance(x, y, p.MinX, p.MinY, p.MaxX, p.MaxY);
        // 轴向: 越靠近中心越亮。
        Assert.True(L(2.8, 1.9) < L(2.5, 1.9));
        Assert.True(L(2.5, 1.9) < L(2.2, 1.9));
        Assert.True(L(1.9, 1.0) < L(1.9, 1.5));
        // 对角向同样单调 (无方向性平台)。
        Assert.True(L(2.8, 2.8) < L(2.5, 2.5));
        Assert.True(L(2.5, 2.5) < L(2.2, 2.2));
        Assert.True(L(2.2, 2.2) < L(1.9, 1.9));
    }

    [Fact]
    public void OfficialSurfaceLuminance_ClampsOutsideBounds_RejectsDegenerateBounds()
    {
        var p = OfficialField().Platform;
        // 平台外: 归一化半径 > 1 → 0 (clamp), 且永不越出 [0,1]。
        Assert.Equal(0.0, FieldGrayTextureMap.OfficialSurfaceLuminance(5.0, 5.0, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        Assert.Equal(0.0, FieldGrayTextureMap.OfficialSurfaceLuminance(-1.0, 1.9, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        var inRange = FieldGrayTextureMap.OfficialSurfaceLuminance(1.9, 1.9000001, p.MinX, p.MinY, p.MaxX, p.MaxY);
        Assert.InRange(inRange, 0.0, 1.0);
        // 退化边界 (零宽度平台) 是编程错误。
        Assert.Throws<ArgumentException>(() =>
            FieldGrayTextureMap.OfficialSurfaceLuminance(1.9, 1.9, 1.9, p.MinY, 1.9, p.MaxY));
    }

    [Fact]
    public void DisplayGradient_IsIndependentOfSensorModel_TwoSemanticsStaySeparate()
    {
        var field = OfficialField();
        var model = new FieldModel(field);
        var p = field.Platform;
        // 传感器语义一字不动: 边中点仍是 L∞ 黑带 300, 斜坡仍按手绘公式。
        Assert.Equal(300, model.FieldGrayLocal(p.MinX, 1.9), precision: 9);
        Assert.Equal(300.0 + 700.0 * (1 - 0.7 / 1.2), model.FieldGrayLocal(1.2, 1.9), precision: 9);
        // 显示语义独立: 同一点显示为官方径向渐变 (边中点 = 1 − 1/√2 中间灰,
        // 既不是传感器黑带 300 的黑, 也不是 (g−300)/700 的 0)。
        Assert.Equal(1 - 1 / Math.Sqrt(2),
            FieldGrayTextureMap.OfficialSurfaceLuminance(p.MinX, 1.9, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        // 反方向同样不等: 显示中心纯白 ≠ 传感器中心值 (红区 650 / 白心 1000)。
        Assert.Equal(1.0, FieldGrayTextureMap.OfficialSurfaceLuminance(1.9, 1.9, p.MinX, p.MinY, p.MaxX, p.MaxY), precision: 12);
        Assert.Equal(650, model.FieldGrayLocal(1.9, 1.9));
    }

    // ---------- official RGB8 texture: representative pixels ----------

    [Fact]
    public void BuildOfficialRgb8_OfficialPlatform_CornersBlack_EdgeMidpointsGray_CenterRed()
    {
        var field = OfficialField();
        var model = new FieldModel(field);
        var p = field.Platform;
        var buffer = FieldGrayTextureMap.BuildOfficialRgb8(
            FieldGrayTextureMap.DefaultResolution,
            p.MinX, p.MinY, p.MaxX, p.MaxY,
            model.Center,
            TestRed());

        const int res = FieldGrayTextureMap.DefaultResolution;
        int V(int px, int py) => buffer[(py * res + px) * 3];
        int G(int px, int py) => buffer[(py * res + px) * 3 + 1];
        int B(int px, int py) => buffer[(py * res + px) * 3 + 2];

        // 四角像素近纯黑 (像素中心内缩半像素 → 亮度 0.0078 → 2), 四角同值。
        Assert.Equal(2, V(0, 0));
        Assert.Equal(V(0, 0), V(res - 1, 0));
        Assert.Equal(V(0, 0), V(0, res - 1));
        Assert.Equal(V(0, 0), V(res - 1, res - 1));
        // 四个边中点同值中间灰 (≈76 = round((1−1/√2)·255 附近), 不得被压成黑色。
        Assert.Equal(76, V(0, res / 2));
        Assert.Equal(V(0, res / 2), V(res - 1, res / 2));
        Assert.Equal(V(0, res / 2), V(res / 2, 0));
        Assert.Equal(V(0, res / 2), V(res / 2, res - 1));
        // 图像中心在红区内: 精确的红区字节 (白"武"由 Label3D 叠加)。
        Assert.Equal(TestRed().R, V(res / 2, res / 2));
        Assert.Equal(TestRed().G, G(res / 2, res / 2));
        Assert.Equal(TestRed().B, B(res / 2, res / 2));
        // 从边中点到对角的亮度单调递减 (径向渐变, 无方形平台)。
        Assert.True(V(res / 2, 0) > V(res / 4, 0));
        Assert.True(V(res / 4, 0) > V(0, 0));
    }

    [Fact]
    public void BuildOfficialRgb8_RedZoneWinsOverGradient_ExactGeometry()
    {
        // 红区边界内 (1.9±0.29) 是红区字节, 边界外回到渐变灰度。
        var p = OfficialField().Platform;
        var buffer = FieldGrayTextureMap.BuildOfficialRgb8(
            FieldGrayTextureMap.DefaultResolution,
            p.MinX, p.MinY, p.MaxX, p.MaxY, 1.9, TestRed());
        const int res = FieldGrayTextureMap.DefaultResolution;
        int V(int px, int py) => buffer[(py * res + px) * 3];

        var (rx, ry) = FieldGrayTextureMap.PixelToFieldLocal(
            res / 2 + 7, res / 2, res, p.MinX, p.MinY, p.MaxX, p.MaxY);
        // px=71 → x≈2.0406 (距中心 0.141m), 仍在红区内。
        Assert.True(Math.Abs(rx - 1.9) < 0.30 && Math.Abs(ry - 1.9) < 0.30);
        Assert.Equal(TestRed().R, V(res / 2 + 7, res / 2));
        // 平台西缘 (px=0) 在红区外: 渐变灰度, 不是红区。
        Assert.NotEqual(TestRed().R, V(0, res / 2));
    }

    // ---------- generic RGB8 builder: buffer/axis layout with asymmetric fields ----------

    [Fact]
    public void BuildRgb8_WestEastGradient_PlacesEastBright()
    {
        var buffer = FieldGrayTextureMap.BuildRgb8(4, 0, 0, 1000, 1000, center: -100,
            (x, y) => x / 1000.0, redZone: (255, 0, 0));
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
            (x, y) => y / 1000.0, redZone: (255, 0, 0));
        int V(int px, int py) => buffer[(py * 4 + px) * 3];
        // 图像行 0 = 场地南侧: 顶行暗、底行亮。
        Assert.True(V(1, 0) < V(1, 3));
        Assert.Equal(V(0, 0), V(3, 0));
        Assert.Equal(V(0, 3), V(3, 3));
    }
}
