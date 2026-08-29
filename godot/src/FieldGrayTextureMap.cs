// Pure pixel↔field-local mapping + pixel colors for the platform-top gray
// texture. Intentionally free of any Godot namespace so Sim.Tests can assert
// image-axis orientation and representative values headlessly; ArenaVisualizer
// only bridges the result into a Godot Image/Texture. The gray VALUES always
// come from Sim.Core.FieldModel.FieldGrayLocal — this helper never re-implements
// the field model. It owns two conventions:
//
// 1. Image-axis contract: Godot 4 PlaneMesh (FACE_Y, the default) generates
//    UVs with U growing along local +X and V growing along local +Z, and UV
//    (0,0) samples the image's top-left pixel. Therefore for the texture to be
//    world-truthful (every rendered pixel shows the point it actually covers):
//      image column px = 0 (left)   → field west  (platform MinX)
//      image row    py = 0 (top)    → field south (platform MinY)
//    i.e. the image is stored "south-up".
//
// 2. Official display palette (视觉约定, 非传感器亮度): the sensor keeps its
//    0–1000 semantics (walkway 0, ring edge 300, center 1000, red zone 650)
//    unchanged; the DISPLAY maps them to the official arena look (官方效果图):
//      walkway (g≈0)        → official dark gray (ArenaVisualizer 提供)
//      ring edge (g=300)    → black band, ramping to white at g=1000
//      red zone (几何判定)   → official red (ArenaVisualizer 提供)
//    i.e. luminance = (g − 300) / 700 inside the ring. Region GEOMETRY always
//    comes from FieldGrayLocal; only the paint is conventional.

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

    /// <summary>True when the sample is the walkway region (sensor value ≈ 0, outside the ring).</summary>
    public static bool IsWalkwayGray(double gray) => gray <= 0.5;

    /// <summary>
    /// Official display luminance inside the ring: g=300 (edge band) → 0
    /// (black), g=1000 (center) → 1 (white). Sensor values are NOT the display
    /// luminance — see the file header (official palette convention).
    /// </summary>
    public static double DisplayLuminance(double gray)
        => Math.Clamp((gray - 300.0) / 700.0, 0.0, 1.0);

    /// <summary>
    /// Builds the whole texture as an RGB8 byte buffer (row-major, row 0 =
    /// image top = field south). <paramref name="grayAt"/> is the single gray
    /// sample function (FieldModel.FieldGrayLocal); <paramref name="redZone"/>
    /// and <paramref name="walkway"/> are the visual-only official palette
    /// colors chosen by the shell.
    /// </summary>
    public static byte[] BuildRgb8(
        int resolution,
        double minX, double minY, double maxX, double maxY,
        double center,
        Func<double, double, double> grayAt,
        (byte R, byte G, byte B) redZone,
        (byte R, byte G, byte B) walkway)
    {
        ArgumentNullException.ThrowIfNull(grayAt);
        var buffer = new byte[resolution * resolution * 3];
        for (var py = 0; py < resolution; py++)
        {
            for (var px = 0; px < resolution; px++)
            {
                var (x, y) = PixelToFieldLocal(px, py, resolution, minX, minY, maxX, maxY);
                var offset = (py * resolution + px) * 3;
                var g = grayAt(x, y);
                (byte R, byte G, byte B) color;
                if (IsRedZone(x, y, center))
                {
                    color = redZone;
                }
                else if (IsWalkwayGray(g))
                {
                    color = walkway;
                }
                else
                {
                    var v = (byte)Math.Round(DisplayLuminance(g) * 255.0);
                    color = (v, v, v);
                }
                buffer[offset] = color.R;
                buffer[offset + 1] = color.G;
                buffer[offset + 2] = color.B;
            }
        }
        return buffer;
    }
}
