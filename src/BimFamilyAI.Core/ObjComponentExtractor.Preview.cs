using System.Numerics;
using System.Text.Json;
using System.Globalization;

namespace BimFamilyAI.Core;

public static partial class ObjComponentExtractor
{
    public static ObjRegionPreview ReadRegionPreview(string source, string directory, ObjVerticalAxis axis)
    {
        var manifest = Verify(source, directory);
        var box = manifest.GlobalBox;
        double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        if (span <= 0) throw new IOException("El OBJ no tiene geometría seleccionable.");
        var pose = ObjAssemblyTransform.Create(box, axis, 1 / span, null);
        var colors = ReadPreviewColors(directory, manifest);
        var triangles = new List<ObjPreviewTriangle>();
        var faces = new List<(int, string, int[], Vector3)>();
        foreach (var piece in manifest.Components)
        {
            string path = Path.Combine(directory, piece.FileName);
            using var stream = File.OpenRead(path);
            if (Hash(path) != piece.Sha256) throw new IOException("La pieza cambió durante la lectura.");
            var document = Parse(path);
            var vertices = document.Positions.Select(line =>
            {
                var fields = line.Split(' ');
                var p = pose.Transform(new ObjPoint(double.Parse(fields[1], CultureInfo.InvariantCulture),
                    double.Parse(fields[2], CultureInfo.InvariantCulture), double.Parse(fields[3], CultureInfo.InvariantCulture)));
                return new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            }).ToArray();
            foreach (var face in document.Faces)
            {
                int original = piece.OriginalFaceNumbers[face.Number - 1];
                int[] indices = face.Corners.Select(c => c.Position).ToArray();
                var points = indices.Select(i => vertices[i]).ToArray();
                var (corners, normal) = ObjRegionPreview.Triangulate(points, original);
                for (int i = 0; i < corners.Count; i += 3)
                    triangles.Add(new(original, piece.Id, points[corners[i]], points[corners[i+1]], points[corners[i+2]],
                        face.Material is not null, face.Material is null ? null : colors.GetValueOrDefault(face.Material[7..])));
                faces.Add((original, piece.Id, indices, normal));
            }
        }
        return new(triangles, faces);
    }

    private static Dictionary<string, ObjRegionColor> ReadPreviewColors(string directory, ObjExtractionManifest manifest)
    {
        var colors = new Dictionary<string, ObjRegionColor>(StringComparer.Ordinal);
        foreach (var asset in manifest.AuxiliaryFiles)
        {
            string? name = null;
            foreach (var line in File.ReadLines(Path.Combine(directory, asset.FileName)))
            {
                var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) continue;
                if (fields[0] == "newmtl") name = string.Join(' ', fields.Skip(1));
                if (name is null || fields[0] != "Kd" || fields.Length != 4) continue;
                var values = fields.Skip(1).Select(s => double.TryParse(s,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                    && double.IsFinite(v) ? v : double.NaN).ToArray();
                if (values.All(double.IsFinite))
                    colors[name] = new(Channel(values[0]), Channel(values[1]), Channel(values[2]));
            }
        }
        return colors;
        static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    public static ObjRegionPlan ReadRegionPlan(string file, string source, string extraction)
    {
        if (new FileInfo(file).Length > ObjInspection.MaxBytes) throw new IOException("Plan demasiado grande.");
        return ValidateRegionPlan(source, extraction, JsonSerializer.Deserialize<ObjRegionPlan>(File.ReadAllText(file)));
    }

    public static ObjRegionPlan ValidateRegionPlan(string source, string extraction, ObjRegionPlan? plan) =>
        NormalizeRegionPlan(plan, Verify(source, extraction), extraction);

    public static void SaveNewRegionPlan(string file, ObjRegionPlan plan)
    {
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(plan, RegionJson));
            File.Move(temp, file, overwrite: false);
        }
        finally { File.Delete(temp); }
    }
}
