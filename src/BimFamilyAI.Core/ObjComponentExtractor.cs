using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BimFamilyAI.Core;

public sealed record ObjExtractionAsset(string FileName, string Sha256, long Bytes);
public sealed record ObjExtractionPiece(string Id, string FileName, string Sha256, int Vertices,
    int TextureVertices, int Normals, int Faces, long Triangles, ObjBox Box,
    IReadOnlyList<int> OriginalFaceNumbers, IReadOnlyList<int> OriginalVertexNumbers);
public sealed record ObjExtractionManifest(int FormatVersion, string SourceSha256, int ComponentCount,
    string CoordinateSystem, ObjBox GlobalBox, IReadOnlyList<ObjExtractionPiece> Components,
    IReadOnlyList<ObjExtractionAsset> AuxiliaryFiles, IReadOnlyList<string> Warnings);
public sealed record ObjExtractionResult(string OutputDirectory, ObjExtractionManifest Manifest);

/// <summary>Copies topological components into independent OBJ files without transforming source coordinates.</summary>
public static class ObjComponentExtractor
{
    private sealed record Corner(int Position, int? Texture, int? Normal, int SlashCount);
    private sealed record Face(int Number, Corner[] Corners, string? Object, string? Group,
        string? Material, string? Smoothing);
    private sealed class Document
    {
        internal readonly List<string> Positions = [];
        internal readonly List<string> Textures = [];
        internal readonly List<string> Normals = [];
        internal readonly List<Face> Faces = [];
        internal readonly List<string> Libraries = [];
        internal readonly HashSet<string> Materials = new(StringComparer.Ordinal);
    }

    /// <summary>Rechecks a published extraction immediately before a consumer uses it.</summary>
    public static ObjExtractionManifest Verify(string sourcePath, string extractionDirectory)
    {
        string source = Path.GetFullPath(sourcePath);
        string directory = Path.GetFullPath(extractionDirectory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        if (new FileInfo(manifestPath).Length > ObjInspection.MaxBytes)
            throw new IOException("Manifiesto demasiado grande.");
        ObjExtractionManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ObjExtractionManifest>(File.ReadAllText(manifestPath))
            ?? throw new IOException("Manifiesto de componentes vacío."); }
        catch (JsonException ex) { throw new IOException("Manifiesto de componentes inválido.", ex); }
        var topology = ObjStructure.AnalyzeWithFaces(source);
        if (manifest.FormatVersion != 1 || manifest.SourceSha256 != Hash(source) ||
            manifest.ComponentCount != topology.Structure.Components.Count ||
            manifest.Components is null || manifest.Components.Count != manifest.ComponentCount ||
            manifest.AuxiliaryFiles is null || manifest.GlobalBox != GlobalBox(topology.Structure.Components.Select(c => c.Box)))
            throw new IOException("El manifiesto no corresponde al OBJ original.");
        Document original;
        try { original = Parse(source); }
        catch (DecoderFallbackException ex) { throw new IOException("El OBJ no contiene texto UTF-8 válido.", ex); }
        if (original.Faces.Count != topology.FaceComponents.Count)
            throw new IOException("El OBJ cambió durante la validación.");
        IReadOnlyList<string> expectedLibraries = original.Materials.Count == 0 ? [] : original.Libraries;
        if (manifest.AuxiliaryFiles.Count != expectedLibraries.Count)
            throw new IOException("Faltan bibliotecas MTL requeridas.");
        for (int i = 0; i < expectedLibraries.Count; i++)
        {
            var asset = manifest.AuxiliaryFiles[i];
            if (asset is null || asset.FileName != expectedLibraries[i] || !SafeLeaf(asset.FileName))
                throw new IOException("Referencia MTL inválida en el manifiesto.");
            string path = Path.Combine(directory, asset.FileName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(path).Length != asset.Bytes || Hash(path) != asset.Sha256)
                throw new IOException($"MTL alterado o ausente: {asset.FileName}.");
        }
        for (int i = 0; i < manifest.Components.Count; i++)
        {
            var component = topology.Structure.Components[i];
            var piece = manifest.Components[i];
            string id = $"component_{i + 1:000}";
            if (piece is null || piece.Id != id || piece.FileName != id + ".obj" ||
                piece.Box != component.Box || piece.Faces != component.Faces || piece.Triangles != component.Triangles)
                throw new IOException($"Metadatos inconsistentes para {id}.");
            string path = Path.Combine(directory, piece.FileName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Hash(path) != piece.Sha256)
                throw new IOException($"OBJ alterado o ausente: {id}.");
            int[] faceNumbers = Enumerable.Range(0, original.Faces.Count)
                .Where(n => topology.FaceComponents[n] == component.Number).ToArray();
            var sourceFaces = faceNumbers.Select(n => original.Faces[n]).ToArray();
            int[] positions = sourceFaces.SelectMany(f => f.Corners.Select(c => c.Position)).Distinct().Order().ToArray();
            int[] textures = sourceFaces.SelectMany(f => f.Corners.Where(c => c.Texture.HasValue).Select(c => c.Texture!.Value)).Distinct().Order().ToArray();
            int[] normals = sourceFaces.SelectMany(f => f.Corners.Where(c => c.Normal.HasValue).Select(c => c.Normal!.Value)).Distinct().Order().ToArray();
            if (piece.OriginalFaceNumbers is null || !piece.OriginalFaceNumbers.SequenceEqual(faceNumbers.Select(n => n + 1)) ||
                piece.OriginalVertexNumbers is null || !piece.OriginalVertexNumbers.SequenceEqual(positions.Select(n => n + 1)) ||
                piece.Vertices != positions.Length || piece.TextureVertices != textures.Length || piece.Normals != normals.Length)
                throw new IOException($"Correspondencia original inválida: {id}.");
            ValidatePiece(path, original, sourceFaces, positions, textures, normals, component);
        }
        if (manifest.SourceSha256 != Hash(source)) throw new IOException("El OBJ cambió durante la validación.");
        return manifest;
    }

    public static ObjExtractionResult Extract(string sourcePath, string outputDirectory)
    {
        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("La carpeta de extracción ya existe; no se sobrescribirá.");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Indique una carpeta de salida válida.");
        var leaf = Path.GetFileName(target);
        if (string.IsNullOrWhiteSpace(leaf)) throw new IOException("Indique una carpeta de salida distinta de la raíz.");

        string sourceHash = Hash(source);
        var topology = ObjStructure.AnalyzeWithFaces(source);
        Document document;
        try { document = Parse(source); }
        catch (DecoderFallbackException ex) { throw new IOException("El OBJ no contiene texto UTF-8 válido.", ex); }
        if (document.Faces.Count != topology.FaceComponents.Count ||
            document.Positions.Count != topology.Structure.Vertices)
            throw new IOException("El OBJ cambió durante el análisis estructural.");
        var warnings = topology.Structure.Caveats
            .Where(c => !c.StartsWith("mtllib y usemtl son referencias declaradas", StringComparison.Ordinal)).ToList();
        IReadOnlyList<Library> libraries;
        try { libraries = CheckLibraries(source, document, warnings); }
        catch (DecoderFallbackException ex) { throw new IOException("El MTL no contiene texto UTF-8 válido.", ex); }
        if (Hash(source) != sourceHash) throw new IOException("El OBJ original cambió durante la preparación.");
        var facesByComponent = Enumerable.Range(0, topology.Structure.Components.Count + 1)
            .Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < document.Faces.Count; i++)
            facesByComponent[topology.FaceComponents[i]].Add(i);

        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{leaf}.bfai-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var assets = new List<ObjExtractionAsset>();
            foreach (var library in libraries)
            {
                string destination = Path.Combine(staging, library.Name);
                using (var input = new FileStream(library.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (Hash(destination) != library.Sha256)
                    throw new IOException("Un archivo MTL cambió durante su copia.");
                assets.Add(new ObjExtractionAsset(library.Name, library.Sha256, new FileInfo(destination).Length));
            }

            var pieces = new List<ObjExtractionPiece>();
            foreach (var component in topology.Structure.Components)
            {
                int number = component.Number;
                var faceNumbers = facesByComponent[number].ToArray();
                var sourceFaces = faceNumbers.Select(i => document.Faces[i]).ToArray();
                var positionNumbers = sourceFaces.SelectMany(f => f.Corners.Select(c => c.Position)).Distinct().Order().ToArray();
                var textureNumbers = sourceFaces.SelectMany(f => f.Corners.Where(c => c.Texture.HasValue).Select(c => c.Texture!.Value))
                    .Distinct().Order().ToArray();
                var normalNumbers = sourceFaces.SelectMany(f => f.Corners.Where(c => c.Normal.HasValue).Select(c => c.Normal!.Value))
                    .Distinct().Order().ToArray();
                var positionMap = NumberMap(positionNumbers);
                var textureMap = NumberMap(textureNumbers);
                var normalMap = NumberMap(normalNumbers);
                string id = $"component_{number:000}";
                string fileName = id + ".obj";
                string file = Path.Combine(staging, fileName);
                Write(file, document, sourceFaces, positionNumbers, textureNumbers, normalNumbers,
                    positionMap, textureMap, normalMap, libraries.Select(l => l.Name));
                ValidatePiece(file, document, sourceFaces, positionNumbers, textureNumbers, normalNumbers, component);
                pieces.Add(new ObjExtractionPiece(id, fileName, Hash(file), positionNumbers.Length,
                    textureNumbers.Length, normalNumbers.Length, sourceFaces.Length, component.Triangles,
                    component.Box, faceNumbers.Select(i => i + 1).ToArray(), positionNumbers.Select(i => i + 1).ToArray()));
            }

            if (pieces.Sum(p => p.Faces) != document.Faces.Count ||
                pieces.SelectMany(p => p.OriginalFaceNumbers).Order().Where((n, i) => n != i + 1).Any())
                throw new IOException("La extracción perdió o duplicó caras.");
            var global = GlobalBox(topology.Structure.Components.Select(c => c.Box));
            if (GlobalBox(pieces.Select(p => p.Box)) != global)
                throw new IOException("La caja global cambió durante la extracción.");
            if (Hash(source) != sourceHash) throw new IOException("El OBJ original cambió durante la extracción.");

            var manifest = new ObjExtractionManifest(1, sourceHash, pieces.Count,
                "Coordenadas OBJ originales; unidad física y eje vertical no declarados", global,
                pieces, assets, warnings);
            string manifestPath = Path.Combine(staging, "manifest.json");
            using (var writer = new StreamWriter(new FileStream(manifestPath, FileMode.CreateNew), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("La carpeta de extracción apareció durante la operación; no se sobrescribirá.");
            Directory.Move(staging, target);
            return new ObjExtractionResult(target, manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static Dictionary<int, int> NumberMap(int[] indices) =>
        indices.Select((index, ordinal) => (index, ordinal)).ToDictionary(x => x.index, x => x.ordinal + 1);

    private static void Write(string path, Document document, Face[] faces, int[] positions, int[] textures,
        int[] normals, Dictionary<int, int> positionMap, Dictionary<int, int> textureMap,
        Dictionary<int, int> normalMap, IEnumerable<string> libraries)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew), new UTF8Encoding(false));
        foreach (var library in libraries) writer.WriteLine("mtllib " + library);
        foreach (int index in positions) writer.WriteLine(document.Positions[index]);
        foreach (int index in textures) writer.WriteLine(document.Textures[index]);
        foreach (int index in normals) writer.WriteLine(document.Normals[index]);
        string? currentObject = null, currentGroup = null, currentMaterial = null, currentSmoothing = null;
        foreach (var face in faces)
        {
            State(face.Object, ref currentObject);
            State(face.Group, ref currentGroup);
            State(face.Material, ref currentMaterial);
            State(face.Smoothing, ref currentSmoothing);
            writer.Write("f");
            foreach (var corner in face.Corners)
            {
                writer.Write(' ');
                writer.Write(positionMap[corner.Position]);
                if (corner.SlashCount >= 1)
                {
                    writer.Write('/');
                    if (corner.Texture.HasValue) writer.Write(textureMap[corner.Texture.Value]);
                }
                if (corner.SlashCount == 2)
                {
                    writer.Write('/');
                    if (corner.Normal.HasValue) writer.Write(normalMap[corner.Normal.Value]);
                }
            }
            writer.WriteLine();
        }
        void State(string? next, ref string? current)
        {
            if (next == current) return;
            if (next is null) throw new IOException("No se puede restaurar un estado OBJ no declarado.");
            writer.WriteLine(next);
            current = next;
        }
    }

    private static void ValidatePiece(string file, Document original, Face[] expected, int[] positions,
        int[] textures, int[] normals, ObjComponent component)
    {
        ObjInspection.Inspect(file);
        var piece = Parse(file);
        if (piece.Faces.Count != expected.Length || piece.Positions.Count != positions.Length ||
            piece.Textures.Count != textures.Length || piece.Normals.Count != normals.Length)
            throw new IOException("La pieza generada cambió su número de elementos.");
        for (int i = 0; i < positions.Length; i++)
            if (piece.Positions[i] != original.Positions[positions[i]]) throw new IOException("Coordenadas alteradas.");
        for (int i = 0; i < textures.Length; i++)
            if (piece.Textures[i] != original.Textures[textures[i]]) throw new IOException("UV alteradas.");
        for (int i = 0; i < normals.Length; i++)
            if (piece.Normals[i] != original.Normals[normals[i]]) throw new IOException("Normales alteradas.");
        for (int i = 0; i < expected.Length; i++)
        {
            var a = expected[i]; var b = piece.Faces[i];
            if (a.Corners.Length != b.Corners.Length || a.Object != b.Object || a.Group != b.Group ||
                a.Material != b.Material || a.Smoothing != b.Smoothing)
                throw new IOException("Una cara perdió atributos o declaraciones.");
            for (int j = 0; j < a.Corners.Length; j++)
            {
                var x = a.Corners[j]; var y = b.Corners[j];
                if (x.Position != positions[y.Position] || x.Texture !=
                    (y.Texture.HasValue ? textures[y.Texture.Value] : null) || x.Normal !=
                    (y.Normal.HasValue ? normals[y.Normal.Value] : null) || x.SlashCount != y.SlashCount)
                    throw new IOException("Cambió el orden, orientación o asociación UV/normal de una cara.");
            }
        }
        var inspected = ObjStructure.Analyze(file);
        if (inspected.Components.Count != 1 || inspected.Faces != component.Faces ||
            inspected.Triangles != component.Triangles || inspected.Components[0].Box != component.Box)
            throw new IOException("La topología o caja de una pieza cambió.");
    }

    private static Document Parse(string path)
    {
        var document = new Document();
        string? objectState = null, groupState = null, materialState = null, smoothingState = null;
        int lineNumber = 0;
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            string state = string.Join(' ', fields);
            switch (fields[0])
            {
                case "v": document.Positions.Add(state); break;
                case "vt":
                    if (fields.Length is < 2 or > 4 || document.Textures.Count >= ObjInspection.MaxFaces)
                        throw Invalid(lineNumber, "coordenada UV inválida o excesiva");
                    foreach (var field in fields.Skip(1)) Finite(field, lineNumber);
                    document.Textures.Add(state);
                    break;
                case "vn":
                    if (fields.Length != 4 || document.Normals.Count >= ObjInspection.MaxFaces)
                        throw Invalid(lineNumber, "normal inválida o excesiva");
                    foreach (var field in fields.Skip(1)) Finite(field, lineNumber);
                    document.Normals.Add(state);
                    break;
                case "f":
                    if (fields.Length < 4) throw Invalid(lineNumber, "cara incompleta");
                    var corners = fields.Skip(1).Select(field => ParseCorner(field, document, lineNumber)).ToArray();
                    if (materialState is not null) document.Materials.Add(materialState["usemtl ".Length..]);
                    document.Faces.Add(new Face(document.Faces.Count + 1, corners,
                        objectState, groupState, materialState, smoothingState));
                    break;
                case "o": objectState = state; break;
                case "g": groupState = state; break;
                case "s":
                    if (fields.Length != 2) throw Invalid(lineNumber, "suavizado inválido");
                    smoothingState = state;
                    break;
                case "usemtl":
                    if (fields.Length < 2) throw Invalid(lineNumber, "material sin nombre");
                    materialState = state;
                    break;
                case "mtllib":
                    if (fields.Length < 2) throw Invalid(lineNumber, "biblioteca MTL sin nombre");
                    foreach (var name in fields.Skip(1))
                    {
                        if (!SafeLeaf(name)) throw Invalid(lineNumber, "ruta MTL insegura");
                        if (!document.Libraries.Contains(name, StringComparer.Ordinal)) document.Libraries.Add(name);
                    }
                    break;
                default: throw Invalid(lineNumber, $"directiva OBJ no conservable: {fields[0]}");
            }
        }
        foreach (var face in document.Faces)
            foreach (var corner in face.Corners)
                if (corner.Position >= document.Positions.Count || corner.Texture >= document.Textures.Count ||
                    corner.Normal >= document.Normals.Count)
                    throw new IOException($"La cara {face.Number} referencia índices inexistentes.");
        return document;
    }

    private static Corner ParseCorner(string field, Document document, int line)
    {
        var parts = field.Split('/');
        if (parts.Length is < 1 or > 3 || parts[0].Length == 0 ||
            parts.Length == 2 && parts[1].Length == 0 ||
            parts.Length == 3 && parts[2].Length == 0 ||
            parts.Length == 3 && parts[1].Length == 0 && parts[2].Length == 0)
            throw Invalid(line, "referencia de cara inválida");
        int position = Index(parts[0], document.Positions.Count, line);
        int? texture = parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], document.Textures.Count, line) : null;
        int? normal = parts.Length > 2 && parts[2].Length > 0 ? Index(parts[2], document.Normals.Count, line) : null;
        return new Corner(position, texture, normal, parts.Length - 1);
    }

    private static int Index(string text, int declared, int line)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
            throw Invalid(line, "índice de cara inválido");
        long resolved = value < 0 ? (long)declared + value : (long)value - 1;
        if (resolved < 0 || resolved >= ObjInspection.MaxFaces) throw Invalid(line, "índice fuera de límites");
        return (int)resolved;
    }

    private static void Finite(string text, int line)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || Math.Abs(value) > 1e8)
            throw Invalid(line, "atributo numérico inválido");
    }

    private static bool SafeLeaf(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            !string.Equals(Path.GetExtension(name), ".mtl", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\') ||
            name.Contains(':') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string stem = Path.GetFileNameWithoutExtension(name).Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Library(string Name, string Path, string Sha256);
    private static IReadOnlyList<Library> CheckLibraries(string source, Document document, List<string> warnings)
    {
        if (document.Materials.Count == 0)
        {
            string originDirectory = Path.GetDirectoryName(source)!;
            foreach (var name in document.Libraries)
                warnings.Add(File.Exists(Path.Combine(originDirectory, name))
                    ? $"MTL {name}: existe, pero ninguna cara usa usemtl; se omite."
                    : $"MTL {name}: falta el archivo local, pero ninguna cara usa usemtl; se omite.");
            return [];
        }
        if (document.Libraries.Count == 0) throw new IOException("Hay usemtl sin biblioteca mtllib local.");
        var libraries = new List<Library>();
        var definitions = new HashSet<string>(StringComparer.Ordinal);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        foreach (var name in document.Libraries)
        {
            string path = Path.Combine(sourceDirectory, name);
            if (!File.Exists(path)) throw new IOException($"Falta el MTL local requerido: {name}.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"No se siguen enlaces MTL: {name}.");
            if (new FileInfo(path).Length > ObjInspection.MaxBytes)
                throw new IOException($"MTL demasiado grande: {name}.");
            int lineNumber = 0;
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (line.Length > 65536) throw new IOException($"MTL {name}: línea demasiado larga.");
                var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0) continue;
                if (fields[0] == "newmtl" && fields.Length > 1)
                    definitions.Add(string.Join(' ', fields.Skip(1)));
                else if (fields[0] is not ("Ka" or "Kd" or "Ks" or "Ke" or "Ns" or "Ni" or "d" or "Tr" or "illum" or "Tf" or "sharpness"))
                    throw new IOException($"MTL {name}, línea {lineNumber}: directiva no conservable sin auxiliares: {fields[0]}.");
            }
            libraries.Add(new Library(name, path, Hash(path)));
        }
        foreach (var name in document.Materials)
            if (!definitions.Contains(name)) throw new IOException($"El material {name} no está definido en los MTL locales.");
        return libraries;
    }

    private static ObjBox GlobalBox(IEnumerable<ObjBox> boxes)
    {
        var array = boxes.ToArray();
        var min = new ObjPoint(array.Min(b => b.Min.X), array.Min(b => b.Min.Y), array.Min(b => b.Min.Z));
        var max = new ObjPoint(array.Max(b => b.Max.X), array.Max(b => b.Max.Y), array.Max(b => b.Max.Z));
        return new ObjBox(min, max, new ObjPoint((min.X + max.X) / 2,
            (min.Y + max.Y) / 2, (min.Z + max.Z) / 2));
    }

    private static IOException Invalid(int line, string reason) => new($"Línea OBJ {line}: {reason}.");
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
