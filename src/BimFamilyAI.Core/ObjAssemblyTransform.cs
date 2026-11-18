namespace BimFamilyAI.Core;

/// <summary>One global OBJ-to-family pose. Coordinates remain in the source OBJ system until mapped.</summary>
public sealed class ObjAssemblyTransform
{
    public ObjVerticalAxis VerticalAxis { get; }
    public double MillimetersPerSourceUnit { get; }
    public ObjPoint TranslationMm { get; }
    public ObjBox GlobalBoxMm { get; }

    private ObjAssemblyTransform(ObjVerticalAxis axis, double scale, ObjPoint translation, ObjBox global)
    { VerticalAxis = axis; MillimetersPerSourceUnit = scale; TranslationMm = translation; GlobalBoxMm = global; }

    public static ObjAssemblyTransform Create(ObjBox sourceBox, ObjVerticalAxis axis,
        double sourceUnitMillimeters, double? targetHeightMillimeters)
    {
        if (!Enum.IsDefined(axis) || !double.IsFinite(sourceUnitMillimeters) || sourceUnitMillimeters <= 0)
            throw new ArgumentException("Eje o unidad de origen inválidos.");
        if (targetHeightMillimeters is { } height && (!double.IsFinite(height) || height <= 0))
            throw new ArgumentException("Altura objetivo inválida.");
        var oriented = MapBox(sourceBox, axis, 1, new ObjPoint(0, 0, 0));
        double rawHeight = oriented.Max.Z - oriented.Min.Z;
        if (rawHeight <= 1e-9 && targetHeightMillimeters.HasValue)
            throw new ArgumentException("La altura original debe ser positiva.");
        double scale = targetHeightMillimeters is { } requested ? requested / rawHeight : sourceUnitMillimeters;
        var shift = new ObjPoint(-(oriented.Min.X + oriented.Max.X) * scale / 2,
            -(oriented.Min.Y + oriented.Max.Y) * scale / 2, -oriented.Min.Z * scale);
        var global = MapBox(sourceBox, axis, scale, shift);
        double x = global.Max.X - global.Min.X, y = global.Max.Y - global.Min.Y, z = global.Max.Z - global.Min.Z;
        double diagonal = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(diagonal) || diagonal < 0.1 || diagonal > 100_000)
            throw new ArgumentException("Dimensiones finales fuera del rango admitido: diagonal entre 0.1 mm y 100 m.");
        return new ObjAssemblyTransform(axis, scale, shift, global);
    }

    public ObjPoint Transform(ObjPoint point)
    {
        var oriented = Orient(point, VerticalAxis);
        return new ObjPoint(oriented.X * MillimetersPerSourceUnit + TranslationMm.X,
            oriented.Y * MillimetersPerSourceUnit + TranslationMm.Y,
            oriented.Z * MillimetersPerSourceUnit + TranslationMm.Z);
    }

    public ObjBox Transform(ObjBox box) => MapBox(box, VerticalAxis, MillimetersPerSourceUnit, TranslationMm);

    private static ObjPoint Orient(ObjPoint point, ObjVerticalAxis axis) => axis switch
    {
        ObjVerticalAxis.Y => new ObjPoint(point.X, -point.Z, point.Y),
        ObjVerticalAxis.X => new ObjPoint(-point.Z, point.Y, point.X),
        _ => point
    };

    private static ObjBox MapBox(ObjBox source, ObjVerticalAxis axis, double scale, ObjPoint shift)
    {
        var points = from x in new[] { source.Min.X, source.Max.X }
                     from y in new[] { source.Min.Y, source.Max.Y }
                     from z in new[] { source.Min.Z, source.Max.Z }
                     let p = Orient(new ObjPoint(x, y, z), axis)
                     select new ObjPoint(p.X * scale + shift.X, p.Y * scale + shift.Y, p.Z * scale + shift.Z);
        var array = points.ToArray();
        var min = new ObjPoint(array.Min(p => p.X), array.Min(p => p.Y), array.Min(p => p.Z));
        var max = new ObjPoint(array.Max(p => p.X), array.Max(p => p.Y), array.Max(p => p.Z));
        return new ObjBox(min, max, new ObjPoint((min.X + max.X) / 2,
            (min.Y + max.Y) / 2, (min.Z + max.Z) / 2));
    }
}
