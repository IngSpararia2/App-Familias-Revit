using System.Globalization;

namespace BimFamilyAI.Core;

public enum ObjUnit { Millimeters, Centimeters, Meters, Inches, Feet }
public enum ObjVerticalAxis { Z, Y, X }
public sealed record ObjImportRequest(string Path, ObjUnit Unit, ObjVerticalAxis VerticalAxis,
    double? TargetHeightMm, string FamilyName, string Description, bool SourceUnitsUnspecified = false);
public sealed record ObjBounds(double X, double Y, double Z);

/// <summary>Bounded structural inspection only. Autodesk performs the actual OBJ import.</summary>
public static class ObjInspection
{
    public const long MaxBytes = 50 * 1024 * 1024;
    public const int MaxVertices = 250_000, MaxFaces = 500_000;

    public static ObjBounds Inspect(string path)
    {
        if (!File.Exists(path)) throw new IOException("El archivo OBJ no existe.");
        if (!string.Equals(System.IO.Path.GetExtension(path), ".obj", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Seleccione un archivo con extensión .obj.");
        var length = new FileInfo(path).Length;
        if (length == 0 || length > MaxBytes) throw new IOException("El OBJ está vacío o supera el límite de 50 MiB.");
        var vertices = new List<(double X, double Y, double Z)>();
        var used = new HashSet<int>();
        int faces = 0, lineNumber = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length > 65536) throw new IOException($"Línea {lineNumber} demasiado larga (máximo 65536 caracteres).");
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields[0] == "v")
            {
                if (fields.Length < 4 || vertices.Count >= MaxVertices) throw new IOException("Vértice incompleto o más de 250000 vértices.");
                double Number(int i)
                {
                    if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                        !double.IsFinite(value) || Math.Abs(value) > 1e8)
                        throw new IOException($"Coordenada inválida o extrema en línea {lineNumber}.");
                    return value;
                }
                vertices.Add((Number(1), Number(2), Number(3)));
            }
            else if (fields[0] == "f")
            {
                if (++faces > MaxFaces || fields.Length < 4 || fields.Length > 1025)
                    throw new IOException($"Cara inválida o demasiado compleja en línea {lineNumber}.");
                var face = new HashSet<int>();
                foreach (var field in fields.Skip(1))
                {
                    if (!int.TryParse(field.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
                        throw new IOException($"Índice de cara inválido en línea {lineNumber}.");
                    long resolved = index < 0 ? (long)vertices.Count + index : (long)index - 1;
                    if (resolved < 0 || resolved >= MaxVertices) throw new IOException("Índice OBJ fuera de límites.");
                    face.Add((int)resolved); used.Add((int)resolved);
                }
                if (face.Count < 3) throw new IOException("Una cara requiere tres vértices distintos.");
            }
        }
        if (vertices.Count == 0 || faces == 0) throw new IOException("El OBJ debe contener vértices y caras.");
        if (used.Any(i => i >= vertices.Count)) throw new IOException("Una cara referencia vértices inexistentes.");
        var points = used.Select(i => vertices[i]).ToArray();
        var bounds = new ObjBounds(points.Max(p => p.X)-points.Min(p => p.X), points.Max(p => p.Y)-points.Min(p => p.Y), points.Max(p => p.Z)-points.Min(p => p.Z));
        if (new[] { bounds.X, bounds.Y, bounds.Z }.Count(v => v > 1e-9) < 2)
            throw new IOException("La geometría tiene dimensiones nulas o es solamente lineal.");
        return bounds;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name != name.Trim() ||
            name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new IOException("Nombre de familia inválido. Use de 1 a 100 caracteres válidos para un archivo Windows.");
    }
}
