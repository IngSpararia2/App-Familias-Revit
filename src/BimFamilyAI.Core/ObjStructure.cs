using System.Globalization;

namespace BimFamilyAI.Core;

public sealed record ObjPoint(double X, double Y, double Z);
public sealed record ObjBox(ObjPoint Min, ObjPoint Max, ObjPoint Center);
public sealed record ObjFaceDistribution(string Name, int Faces, long Triangles);
public sealed record ObjComponent(int Number, int Vertices, int Faces, long Triangles, ObjBox Box,
    IReadOnlyList<string> Objects, IReadOnlyList<string> Groups, IReadOnlyList<string> Materials);
public sealed record ObjStructureResult(int Vertices, int Faces, long Triangles,
    int ObjectDeclarations, int GroupDeclarations, IReadOnlyList<string> MaterialLibraries,
    IReadOnlyList<ObjFaceDistribution> ByObject, IReadOnlyList<ObjFaceDistribution> ByGroup,
    IReadOnlyList<ObjFaceDistribution> ByMaterial, IReadOnlyList<ObjComponent> Components,
    int DuplicateCoordinateVertices, int? OverlappingComponentBoxPairs, IReadOnlyList<string> Caveats);
internal sealed record ObjTopology(ObjStructureResult Structure, IReadOnlyList<int> FaceComponents);

/// <summary>Read-only OBJ structure. Connectivity means shared position indices in faces, never spatial or semantic identity.</summary>
public static class ObjStructure
{
    private sealed record Face(int[] Indices, int Triangles, string Object, string[] Groups, string Material);
    private sealed class ComponentBuilder
    {
        public readonly HashSet<int> Vertices = [];
        public readonly HashSet<string> Objects = new(StringComparer.Ordinal);
        public readonly HashSet<string> Groups = new(StringComparer.Ordinal);
        public readonly HashSet<string> Materials = new(StringComparer.Ordinal);
        public int Faces;
        public long Triangles;
    }

    public static ObjStructureResult Analyze(string path) => AnalyzeWithFaces(path).Structure;

    internal static ObjTopology AnalyzeWithFaces(string path)
    {
        // Keep the importer's existing validation and size/complexity limits authoritative.
        ObjInspection.Inspect(path);
        var vertices = new List<ObjPoint>();
        var parents = new List<int>();
        var ranks = new List<byte>();
        var used = new HashSet<int>();
        var faces = new List<Face>();
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        var objectName = "(sin declarar)";
        var groupNames = new[] { "(sin declarar)" };
        var materialName = "(sin declarar)";
        int objectDeclarations = 0, groupDeclarations = 0;

        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        void Join(int a, int b)
        {
            a = Root(a); b = Root(b);
            if (a == b) return;
            if (ranks[a] < ranks[b]) (a, b) = (b, a);
            parents[b] = a;
            if (ranks[a] == ranks[b]) ranks[a]++;
        }

        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            switch (fields[0])
            {
                case "v":
                    vertices.Add(new ObjPoint(Parse(fields[1]), Parse(fields[2]), Parse(fields[3])));
                    parents.Add(parents.Count);
                    ranks.Add(0);
                    break;
                case "o":
                    objectDeclarations++;
                    objectName = fields.Length > 1 ? string.Join(' ', fields.Skip(1)) : "(sin declarar)";
                    break;
                case "g":
                    groupDeclarations++;
                    groupNames = fields.Length > 1 ? fields.Skip(1).Distinct(StringComparer.Ordinal).ToArray() : ["(sin declarar)"];
                    break;
                case "usemtl":
                    materialName = fields.Length > 1 ? string.Join(' ', fields.Skip(1)) : "(sin declarar)";
                    break;
                case "mtllib":
                    foreach (var library in fields.Skip(1)) libraries.Add(library);
                    break;
                case "f":
                    var indices = new int[fields.Length - 1];
                    int position = 0;
                    foreach (var field in fields.Skip(1))
                    {
                        // ObjInspection has already validated syntax, bounds and distinct corners.
                        int index = int.Parse(field.Split('/')[0], CultureInfo.InvariantCulture);
                        int resolved = index < 0 ? vertices.Count + index : index - 1;
                        indices[position++] = resolved;
                        used.Add(resolved);
                    }
                    faces.Add(new Face(indices, fields.Length - 3, objectName, groupNames, materialName));
                    break;
            }
        }

        foreach (var face in faces)
            foreach (var index in face.Indices.Skip(1)) Join(face.Indices[0], index);

        var builders = new Dictionary<int, ComponentBuilder>();
        foreach (int index in used)
        {
            int root = Root(index);
            if (!builders.TryGetValue(root, out var builder)) builders[root] = builder = new();
            builder.Vertices.Add(index);
        }
        foreach (var face in faces)
        {
            var builder = builders[Root(face.Indices[0])];
            builder.Faces++; builder.Triangles += face.Triangles;
            builder.Objects.Add(face.Object);
            foreach (var group in face.Groups) builder.Groups.Add(group);
            builder.Materials.Add(face.Material);
        }
        var ordered = builders.OrderBy(item => item.Value.Vertices.Min()).ToArray();
        var rootToNumber = ordered.Select((item, index) => (item.Key, Number: index + 1)).ToDictionary(x => x.Key, x => x.Number);
        var components = ordered.Select((item, number) =>
        {
            var b = item.Value;
            var points = b.Vertices.Select(i => vertices[i]).ToArray();
            var min = new ObjPoint(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
            var max = new ObjPoint(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            return new ObjComponent(number + 1, b.Vertices.Count, b.Faces, b.Triangles,
                new ObjBox(min, max, new ObjPoint((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2)),
                b.Objects.Order(StringComparer.Ordinal).ToArray(), b.Groups.Order(StringComparer.Ordinal).ToArray(),
                b.Materials.Order(StringComparer.Ordinal).ToArray());
        }).ToArray();
        var caveats = new List<string>();
        if (objectDeclarations == 0) caveats.Add("Sin declaraciones o: los objetos no están identificados.");
        if (groupDeclarations == 0) caveats.Add("Sin declaraciones g: los grupos no están identificados.");
        if (components.Any(c => c.Groups.Count > 1 || c.Objects.Count > 1))
            caveats.Add("Hay componentes que comparten índices entre objetos o grupos declarados.");
        if (components.Length > 1 && components.SelectMany(c => c.Materials).GroupBy(x => x).Any(g => g.Count() > 1))
            caveats.Add("Al menos un material se reutiliza entre componentes desconectados.");
        int duplicates = used.Select(i => vertices[i]).GroupBy(p => p).Sum(g => g.Count() - 1);
        if (duplicates > 0) caveats.Add("Hay coordenadas duplicadas con índices distintos; la conectividad espacial puede diferir.");
        int? boxOverlaps = null;
        // Pairwise boxes are only a diagnostic; do not turn many tiny shells into quadratic work.
        if (components.Length <= 1000)
        {
            boxOverlaps = 0;
            for (int i = 0; i < components.Length; i++)
                for (int j = i + 1; j < components.Length; j++)
                {
                    var a = components[i].Box; var b = components[j].Box;
                    if (a.Min.X <= b.Max.X && b.Min.X <= a.Max.X &&
                        a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y &&
                        a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z) boxOverlaps++;
                }
        }
        else caveats.Add("Más de 1000 componentes: se omite la comparación cuadrática de cajas.");
        if (boxOverlaps > 0) caveats.Add("Hay cajas de componentes superpuestas; eso no demuestra intersección de superficies.");
        caveats.Add("mtllib y usemtl son referencias declaradas; no se verifica la existencia del MTL ni los índices vt/vn.");
        caveats.Add("La conectividad usa índices de posición; no prueba contacto espacial, ausencia de intersecciones ni identidad semántica.");
        var result = new ObjStructureResult(vertices.Count, faces.Count, faces.Sum(f => (long)f.Triangles),
            objectDeclarations, groupDeclarations, libraries.Order(StringComparer.Ordinal).ToArray(),
            Distribution(faces.Select(f => (f.Object, f.Triangles))),
            Distribution(faces.SelectMany(f => f.Groups.Select(g => (g, f.Triangles)))),
            Distribution(faces.Select(f => (f.Material, f.Triangles))), components, duplicates, boxOverlaps, caveats);
        return new ObjTopology(result, faces.Select(f => rootToNumber[Root(f.Indices[0])]).ToArray());
    }

    private static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static ObjFaceDistribution[] Distribution(IEnumerable<(string Name, int Triangles)> faces) =>
        faces.GroupBy(f => f.Name, StringComparer.Ordinal)
            .Select(g => new ObjFaceDistribution(g.Key, g.Count(), g.Sum(f => (long)f.Triangles)))
            .OrderBy(g => g.Name, StringComparer.Ordinal).ToArray();
}
