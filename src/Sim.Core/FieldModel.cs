using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// Field geometry and the field-gray model, ported from the legacy CORE.
/// The hand-drawn model is the default; a measured grid map can be loaded
/// (values 0..1000, rows south→north, columns west→east).
/// </summary>
public sealed class FieldModel
{
    private readonly double _el;
    private readonly double _er;
    private readonly double _center;
    private readonly double _half;
    private readonly double _fieldSize;

    public FieldModel(FieldParams field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
        _el = field.Platform.MinX;
        _er = field.Platform.MaxX;
        // The legacy core assumes a square platform; the platform Y range
        // mirrors X (official 2026 field: [0.7, 3.1]^2).
        _center = (_el + _er) / 2;
        _half = (_er - _el) / 2;
        _fieldSize = field.FieldSize;
    }

    public FieldParams Field { get; }

    public double El => _el;

    public double Er => _er;

    public double Center => _center;

    public double Half => _half;

    /// <summary>Loaded gray grid map, or null when the hand-drawn model is active.</summary>
    public GrayGridMap? GrayMap { get; private set; }

    /// <summary>True when the point is on the platform footprint.</summary>
    public bool OnPlatform(double x, double y)
        => x >= _el && x <= _er && y >= _el && y <= _er;

    /// <summary>Legacy hand-drawn gray value (0 walkway, ~300 black band, ~1000 center, 650 red zone).</summary>
    public double FieldGray(double x, double y)
    {
        if (x < _el || x > _er || y < _el || y > _er)
        {
            return 0; // 走道≈0
        }
        if (GrayMap is not null)
        {
            return GrayMap.Sample(x, y);
        }
        var dx = Math.Abs(x - _center) / _half;
        var dy = Math.Abs(y - _center) / _half;
        var t = Math.Max(dx, dy);                       // 0=中心 1=整圈黑边
        var g = 300 + 700 * (1 - t);                    // 中心≈1000, 黑带≈300
        if (Math.Abs(x - _center) < 0.30 && Math.Abs(y - _center) < 0.30)
        {
            g = 650;                                    // 中央红区(白"武")
        }
        return g;
    }

    /// <summary>Minimum distance from a point to the platform edge lines.</summary>
    public double DistToNearestEdge(double x, double y)
        => Math.Min(Math.Min(Math.Abs(x - _el), Math.Abs(x - _er)),
                    Math.Min(Math.Abs(y - _el), Math.Abs(y - _er)));

    /// <summary>Clamps a point to the platform footprint (nearest platform point).</summary>
    public (double X, double Y) NearestPlatPoint(double x, double y)
        => (Js.Clamp(x, _el, _er), Js.Clamp(y, _el, _er));

    /// <summary>Platform step height at a point (display/diagnostic).</summary>
    public double StageHeightAt(double x, double y) => OnPlatform(x, y) ? Field.PlatformHeight : 0;

    /// <summary>Loads a measured gray grid map; null restores the hand-drawn model.</summary>
    public void SetGrayMap(GrayGridMap? map) => GrayMap = map;

    /// <summary>Field-gray perception metadata for observations/snapshots.</summary>
    public FieldGrayInfo GetFieldGrayInfo()
    {
        if (GrayMap is null)
        {
            return new FieldGrayInfo
            {
                Mode = "hand_drawn",
                Id = "fieldGray-default",
            };
        }
        return new FieldGrayInfo
        {
            Mode = "grid",
            Id = GrayMap.Id,
            Interpolation = GrayMap.Interpolation,
        };
    }
}

/// <summary>
/// A measured field-gray grid: values 0..1000, rows south→north (yMin→yMax),
/// columns west→east (xMin→xMax), with bilinear (default) or nearest sampling.
/// </summary>
public sealed class GrayGridMap
{
    public GrayGridMap(string id, int width, int height, double[] values,
        double xMin, double xMax, double yMin, double yMax, string interpolation)
    {
        if (width < 2 || height < 2 || values.Length != width * height)
        {
            throw new ArgumentException("Gray grid must be at least 2x2 and match the values length.");
        }
        if (width > 256 || height > 256)
        {
            throw new ArgumentException("Gray grid is limited to 256x256.");
        }
        if (!(xMax > xMin) || !(yMax > yMin))
        {
            throw new ArgumentException("Gray grid bounds must satisfy xMax>xMin and yMax>yMin.");
        }
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException("Gray grid values must be finite.");
            }
        }
        Id = id;
        Width = width;
        Height = height;
        Values = (double[])values.Clone();
        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;
        Interpolation = interpolation == "nearest" ? "nearest" : "bilinear";
    }

    public string Id { get; }

    public int Width { get; }

    public int Height { get; }

    public double[] Values { get; }

    public double XMin { get; }

    public double XMax { get; }

    public double YMin { get; }

    public double YMax { get; }

    public string Interpolation { get; }

    /// <summary>Samples the grid at field coordinates (clamped to bounds).</summary>
    public double Sample(double x, double y)
    {
        var u = Js.Clamp((x - XMin) / (XMax - XMin), 0, 1) * (Width - 1);
        var v = Js.Clamp((y - YMin) / (YMax - YMin), 0, 1) * (Height - 1);
        if (Interpolation == "nearest")
        {
            return Values[(int)Math.Round(v) * Width + (int)Math.Round(u)];
        }
        var x0 = (int)Math.Floor(u);
        var y0 = (int)Math.Floor(v);
        var x1 = Math.Min(Width - 1, x0 + 1);
        var y1 = Math.Min(Height - 1, y0 + 1);
        var fx = u - x0;
        var fy = v - y0;
        var a = Values[y0 * Width + x0] * (1 - fx) + Values[y0 * Width + x1] * fx;
        var b = Values[y1 * Width + x0] * (1 - fx) + Values[y1 * Width + x1] * fx;
        return a * (1 - fy) + b * fy;
    }
}
