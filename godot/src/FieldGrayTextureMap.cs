// Pure pixel↔field-local mapping + pixel colors for the platform-top gray
// texture. Intentionally free of any Godot namespace so Sim.Tests can assert
// image-axis orientation and representative values headlessly; ArenaVisualizer
// only bridges the result into a Godot Image/Texture. The gray VALUES always
// come from Sim.Core.FieldModel.FieldGrayLocal — this helper never re-implements
// the field model, it only owns the image-axis convention:
//
// Godot 4 PlaneMesh (FACE_Y, the default) generates UVs with U growing along
// local +X and V growing along local +Z, and UV (0,0) samples the image's
// top-left pixel. Therefore for the texture to be world-truthful (every
// rendered pixel shows the gray of the field-local point it actually covers):
//   image column px = 0 (left)   → field west  (platform MinX)
//   image row    py = 0 (top)    → field south (platform MinY)
// i.e. the image is stored "south-up". Cameras put world -Z toward screen-up,
// so the texture on screen lines up with the arena geometry either way.

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

    /// <summary>Maps a 0..1000 gray value into a 0..1 luminance.</summary>
    public static double NormalizedGray(double gray) => Math.Clamp(gray / 1000.0, 0.0, 1.0);

    /// <summary>
    /// Builds the whole texture as an RGB8 byte buffer (row-major, row 0 =
    /// image top = field south). <paramref name="grayAt"/> is the single gray
    /// sample function (FieldModel.FieldGrayLocal); <paramref name="redZone"/>
    /// is the visual-only red-zone color chosen by the shell.
    /// </summary>
    public static byte[] BuildRgb8(
        int resolution,
        double minX, double minY, double maxX, double maxY,
        double center,
        Func<double, double, double> grayAt,
        (byte R, byte G, byte B) redZone)
    {
        ArgumentNullException.ThrowIfNull(grayAt);
        var buffer = new byte[resolution * resolution * 3];
        for (var py = 0; py < resolution; py++)
        {
            for (var px = 0; px < resolution; px++)
            {
                var (x, y) = PixelToFieldLocal(px, py, resolution, minX, minY, maxX, maxY);
                var offset = (py * resolution + px) * 3;
                if (IsRedZone(x, y, center))
                {
                    buffer[offset] = redZone.R;
                    buffer[offset + 1] = redZone.G;
                    buffer[offset + 2] = redZone.B;
                }
                else
                {
                    var v = (byte)Math.Round(NormalizedGray(grayAt(x, y)) * 255.0);
                    buffer[offset] = v;
                    buffer[offset + 1] = v;
                    buffer[offset + 2] = v;
                }
            }
        }
        return buffer;
    }
}
