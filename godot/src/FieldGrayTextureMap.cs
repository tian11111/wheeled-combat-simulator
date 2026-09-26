// Pure pixel↔field-local mapping + pixel colors for the platform-top gray
// texture. Intentionally free of any Godot namespace so Sim.Tests can assert
// image-axis orientation and representative values headlessly; ArenaVisualizer
// only bridges the result into a Godot Image/Texture. It owns two conventions
// and keeps two gray SEMANTICS strictly apart:
//
// 1. Image-axis contract: Godot 4 PlaneMesh (FACE_Y, the default) generates
//    UVs with U growing along local +X and V growing along local +Z, and UV
//    (0,0) samples the image's top-left pixel. Therefore for the texture to be
//    world-truthful (every rendered pixel shows the point it actually covers):
//      image column px = 0 (left)   → field west  (platform MinX)
//      image row    py = 0 (top)    → field south (platform MinY)
//    i.e. the image is stored "south-up".
//
// 2. Gray semantics separation (2026-08-29 official-surface pass):
//      sensor semantics  — Sim.Core.FieldModel.FieldGrayLocal keeps its 0–1000
//        L∞ model (walkway 0, edge band 300, center 1000, red zone 650). It is
//        what robots perceive; it is NEVER painted into the display texture.
//      display semantics — OfficialSurfaceLuminance below is the visual-only
//        official look (规则 PDF 第 10 页: "擂台表面底色从外侧四角到中心分别
//        为纯黑到纯白渐变"): a normalized Euclidean radial gradient, center
//        pure white, four corners pure black, edge midpoints medium gray. The
//        old L∞ display produced bright diagonal streaks (square iso-lines);
//        this function must never reintroduce a directional max/abs sum.
//    Region GEOMETRY (red zone square) stays geometric; no rule reads this.

namespace Sim.GodotShell;

public static class FieldGrayTextureMap
{
    /// <summary>Default texture resolution (square, pixels per platform side).</summary>
    public const int DefaultResolution = 128;

    /// <summary>Half extent of the central red zone (m); mirrors FieldModel's hand-drawn model.</summary>
    public const double RedZoneHalfExtent = 0.30;

    /// <summary>
    /// Center of pixel (px, py) in field-local coordinates. Row 0 is field
    /// south (MinY), column 0 is field west (MinX) — see the file header.
    /// </summary>
    public static (double X, double Y) PixelToFieldLocal(
        int px, int py, int resolution,
        double minX, double minY, double maxX, double maxY)
    {
        return (
            minX + (px + 0.5) / resolution * (maxX - minX),
            minY + (py + 0.5) / resolution * (maxY - minY));
    }

    /// <summary>True when the field-local point is inside the central red zone (0.6 × 0.6 m).</summary>
    public static bool IsRedZone(double x, double y, double center)
        => Math.Abs(x - center) < RedZoneHalfExtent && Math.Abs(y - center) < RedZoneHalfExtent;

    /// <summary>
    /// Official surface display luminance (visual-only, 规则第 10 页外观依据):
    /// the platform-local point is normalized to the platform bounds and its
    /// Euclidean distance to the center is normalized by the corner distance,
    /// so the center is pure white (1), the four corners pure black (0), the
    /// edge midpoints 1 − 1/√2 ≈ 0.29, and every point at the same Euclidean
    /// radius gets the same luminance regardless of direction (no diagonal
    /// streaks). This never feeds sensors and never reads
    /// <c>FieldModel.FieldGrayLocal</c> — see the file header.
    /// </summary>
    public static double OfficialSurfaceLuminance(
        double x, double y,
        double minX, double minY, double maxX, double maxY)
    {
        var halfX = (maxX - minX) / 2;
        var halfY = (maxY - minY) / 2;
        if (halfX <= 0 || halfY <= 0)
        {
            throw new ArgumentException("Platform bounds must satisfy maxX>minX and maxY>minY.");
        }
        var nx = (x - (minX + maxX) / 2) / halfX;
        var ny = (y - (minY + maxY) / 2) / halfY;
        var radius = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
        return Math.Clamp(1.0 - radius, 0.0, 1.0);
    }

    /// <summary>
    /// Generic RGB8 builder (row-major, row 0 = image top = field south).
    /// <paramref name="lumaAt"/> is the DISPLAY luminance field in [0,1]
    /// (explicitly not the sensor gray); red-zone samples win first. This
    /// entry exists to verify buffer/axis layout with asymmetric fields and
    /// for alternate visual palettes; the shipped platform display is
    /// <see cref="BuildOfficialRgb8"/>.
    /// </summary>
    public static byte[] BuildRgb8(
        int resolution,
        double minX, double minY, double maxX, double maxY,
        double center,
        Func<double, double, double> lumaAt,
        (byte R, byte G, byte B) redZone)
    {
        ArgumentNullException.ThrowIfNull(lumaAt);
        var buffer = new byte[resolution * resolution * 3];
        for (var py = 0; py < resolution; py++)
        {
            for (var px = 0; px < resolution; px++)
            {
                var (x, y) = PixelToFieldLocal(px, py, resolution, minX, minY, maxX, maxY);
                var offset = (py * resolution + px) * 3;
                byte r, g, b;
                if (IsRedZone(x, y, center))
                {
                    (r, g, b) = redZone;
                }
                else
                {
                    var v = (byte)Math.Round(Math.Clamp(lumaAt(x, y), 0.0, 1.0) * 255.0);
                    r = g = b = v;
                }
                buffer[offset] = r;
                buffer[offset + 1] = g;
                buffer[offset + 2] = b;
            }
        }
        return buffer;
    }

    /// <summary>
    /// Shipped platform-top display texture: the official radial gradient
    /// (<see cref="OfficialSurfaceLuminance"/>) with the central red zone
    /// painted on top (white "武" is a separate Label3D layer). Coordinates
    /// cover exactly the platform bounds; the walkway outside is the floor
    /// mesh's own material, never this texture. Delegates to the single
    /// <see cref="BuildRgb8"/> loop so both builders cannot drift.
    /// </summary>
    public static byte[] BuildOfficialRgb8(
        int resolution,
        double minX, double minY, double maxX, double maxY,
        double center,
        (byte R, byte G, byte B) redZone)
        => BuildRgb8(
            resolution,
            minX, minY, maxX, maxY,
            center,
            (x, y) => OfficialSurfaceLuminance(x, y, minX, minY, maxX, maxY),
            redZone);
}
