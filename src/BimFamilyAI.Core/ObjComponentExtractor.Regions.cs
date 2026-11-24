using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BimFamilyAI.Core;

public sealed record ObjRegionColor(byte Red, byte Green, byte Blue);
public sealed record ObjFaceRegion(string RegionId, string? Name, IReadOnlyList<int> OriginalFaceNumbers,
    ObjRegionColor Color);
public sealed record ObjComponentRegionPlan(string ComponentId, IReadOnlyList<ObjFaceRegion> Regions,
    ObjRegionColor? FallbackColor = null);
public sealed record ObjRegionPlan(int FormatVersion, string SourceSha256,
    IReadOnlyList<ObjComponentRegionPlan> Components);
public sealed record ObjDerivedMaterial(string RegionId, string MaterialName, ObjRegionColor Color,
    bool IsFallback);
public sealed record ObjRegionDerivedPiece(string ComponentId, string OriginalPieceSha256, string FileName,
    string DerivedSha256, string MaterialLibrary, string MaterialLibrarySha256,
    IReadOnlyList<ObjDerivedMaterial> Materials);
public sealed record ObjRegionManifest(int FormatVersion, string SourceSha256, string ExtractionManifestSha256,
    string PlanSha256, IReadOnlyList<ObjRegionDerivedPiece> Components,
    IReadOnlyList<ObjExtractionAsset> AuxiliaryFiles);
public sealed record ObjRegionDerivationResult(string OutputDirectory, ObjRegionManifest Manifest);

public static partial class ObjComponentExtractor
{
    public const int MaxRegionComponents = 256;
    private static readonly JsonSerializerOptions RegionJson = new() { WriteIndented = true };

    /// <summary>Publishes material-only derivatives of verified B1 pieces. Face numbers always refer to the original OBJ.</summary>
    public static ObjRegionDerivationResult DeriveRegions(string sourcePath, string extractionDirectory,
        ObjRegionPlan plan, string outputDirectory)
    {
        var source = Path.GetFullPath(sourcePath);
        var extraction = Path.GetFullPath(extractionDirectory);
        var target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("La carpeta de regiones ya existe; no se sobrescribirá.");
        var baseManifest = Verify(source, extraction);
        var normalized = NormalizeRegionPlan(plan, baseManifest, extraction);
        string planJson = JsonSerializer.Serialize(normalized, RegionJson);
        string planSha = HashText(planJson);
        string extractionSha = Hash(Path.Combine(extraction, "manifest.json"));
        string leaf = Path.GetFileName(target);
        string parent = Path.GetDirectoryName(target) ?? throw new IOException("Destino de regiones inválido.");
        if (string.IsNullOrWhiteSpace(leaf)) throw new IOException("Destino de regiones inválido.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{leaf}.bfai-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var assets = CopyRegionLibraries(baseManifest, extraction, staging);
            var pieces = new List<ObjRegionDerivedPiece>();
            foreach (var componentPlan in normalized.Components)
            {
                var originalPiece = baseManifest.Components.Single(p => p.Id == componentPlan.ComponentId);
                string originalPath = Path.Combine(extraction, originalPiece.FileName);
                var original = Parse(originalPath);
                var expected = MaterialChoices(componentPlan, originalPiece, original, planSha);
                var materials = expected.Materials;
                string mtlName = $"regions_{planSha[..16]}_{componentPlan.ComponentId}.mtl";
                string mtlPath = Path.Combine(staging, mtlName);
                WriteRegionMtl(mtlPath, materials);
                string derivedPath = Path.Combine(staging, originalPiece.FileName);
                WriteRegionObj(originalPath, derivedPath, original, expected.FaceMaterials, mtlName);
                ValidateRegionGeometry(originalPath, derivedPath, expected.FaceMaterials, originalPiece);
                var derived = Parse(derivedPath);
                CheckLibraries(derivedPath, derived, []);
                pieces.Add(new(componentPlan.ComponentId, originalPiece.Sha256, originalPiece.FileName,
                    Hash(derivedPath), mtlName, Hash(mtlPath), materials));
            }
            if (Hash(source) != baseManifest.SourceSha256 ||
                Hash(Path.Combine(extraction, "manifest.json")) != extractionSha)
                throw new IOException("El OBJ o la extracción cambiaron durante la preparación de regiones.");
            var manifest = new ObjRegionManifest(1, baseManifest.SourceSha256, extractionSha,
                planSha, pieces, assets);
            File.WriteAllText(Path.Combine(staging, "plan.json"), planJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, RegionJson), new UTF8Encoding(false));
            VerifyRegions(source, extraction, staging);
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("La carpeta de regiones apareció durante la operación; no se sobrescribirá.");
            Directory.Move(staging, target);
            return new(target, manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Rechecks source, B1 manifest, plan, derived assets, face mapping and every geometric record.</summary>
    public static ObjRegionManifest VerifyRegions(string sourcePath, string extractionDirectory,
        string regionDirectory)
    {
        var source = Path.GetFullPath(sourcePath);
        var extraction = Path.GetFullPath(extractionDirectory);
        var directory = Path.GetFullPath(regionDirectory);
        var baseManifest = Verify(source, extraction);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string planPath = Path.Combine(directory, "plan.json");
        if (new FileInfo(manifestPath).Length > ObjInspection.MaxBytes ||
            new FileInfo(planPath).Length > ObjInspection.MaxBytes)
            throw new IOException("Manifiesto o plan de regiones demasiado grande.");
        ObjRegionManifest manifest;
        ObjRegionPlan plan;
        try
        {
            manifest = JsonSerializer.Deserialize<ObjRegionManifest>(File.ReadAllText(manifestPath))
                ?? throw new IOException("Manifiesto de regiones vacío.");
            plan = JsonSerializer.Deserialize<ObjRegionPlan>(File.ReadAllText(planPath))
                ?? throw new IOException("Plan de regiones vacío.");
        }
        catch (JsonException ex) { throw new IOException("Plan o manifiesto de regiones inválido.", ex); }
        var normalized = NormalizeRegionPlan(plan, baseManifest, extraction);
        string planJson = JsonSerializer.Serialize(normalized, RegionJson);
        string planSha = HashText(planJson);
        if (manifest.FormatVersion != 1 || manifest.SourceSha256 != baseManifest.SourceSha256 ||
            manifest.ExtractionManifestSha256 != Hash(Path.Combine(extraction, "manifest.json")) ||
            manifest.PlanSha256 != planSha || manifest.Components is null ||
            manifest.Components.Count != normalized.Components.Count || manifest.AuxiliaryFiles is null ||
            !File.ReadAllText(planPath).Equals(planJson, StringComparison.Ordinal))
            throw new IOException("La derivación de regiones no corresponde al origen y al plan.");
        if (manifest.AuxiliaryFiles.Count != baseManifest.AuxiliaryFiles.Count)
            throw new IOException("Faltan MTL originales en la derivación.");
        foreach (var originalAsset in baseManifest.AuxiliaryFiles)
        {
            var asset = manifest.AuxiliaryFiles.SingleOrDefault(a => a.FileName == originalAsset.FileName);
            if (asset != originalAsset) throw new IOException("MTL original alterado en la derivación.");
            CheckRegionAsset(Path.Combine(directory, asset.FileName), asset.Sha256, asset.Bytes);
        }
        for (int i = 0; i < normalized.Components.Count; i++)
        {
            var componentPlan = normalized.Components[i];
            var originalPiece = baseManifest.Components.Single(p => p.Id == componentPlan.ComponentId);
            var derivedPiece = manifest.Components[i];
            string expectedMtl = $"regions_{planSha[..16]}_{componentPlan.ComponentId}.mtl";
            if (derivedPiece.ComponentId != componentPlan.ComponentId ||
                derivedPiece.OriginalPieceSha256 != originalPiece.Sha256 ||
                derivedPiece.FileName != originalPiece.FileName || derivedPiece.MaterialLibrary != expectedMtl)
                throw new IOException($"Procedencia incorrecta para {componentPlan.ComponentId}.");
            var originalPath = Path.Combine(extraction, originalPiece.FileName);
            var original = Parse(originalPath);
            var expected = MaterialChoices(componentPlan, originalPiece, original, planSha);
            if (derivedPiece.Materials is null || !derivedPiece.Materials.SequenceEqual(expected.Materials))
                throw new IOException($"Materiales alterados para {componentPlan.ComponentId}.");
            string mtlPath = Path.Combine(directory, derivedPiece.MaterialLibrary);
            string objPath = Path.Combine(directory, derivedPiece.FileName);
            CheckRegionAsset(mtlPath, derivedPiece.MaterialLibrarySha256, ObjInspection.MaxBytes);
            CheckRegionAsset(objPath, derivedPiece.DerivedSha256, ObjInspection.MaxBytes);
            if (File.ReadAllText(mtlPath) != RegionMtlText(expected.Materials))
                throw new IOException("El MTL derivado no coincide con el plan.");
            ValidateRegionGeometry(originalPath, objPath, expected.FaceMaterials, originalPiece);
            var derived = Parse(objPath);
            CheckLibraries(objPath, derived, []);
        }
        return manifest;
    }

    private static ObjRegionPlan NormalizeRegionPlan(ObjRegionPlan? plan, ObjExtractionManifest manifest,
        string extractionDirectory)
    {
        if (plan is null || plan.FormatVersion != 1 || plan.SourceSha256 != manifest.SourceSha256 ||
            plan.Components is null || plan.Components.Count is < 1 or > MaxRegionComponents)
            throw new IOException("Plan de regiones inválido o perteneciente a otro OBJ.");
        var known = manifest.Components.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ordered = new List<ObjComponentRegionPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in plan.Components)
        {
            if (component is null || !known.TryGetValue(component.ComponentId, out var piece) ||
                !seen.Add(component.ComponentId) || component.Regions is null ||
                component.Regions.Count > 256)
                throw new IOException("Componente de regiones inexistente, duplicada o vacía.");
            var validFaces = piece.OriginalFaceNumbers.ToHashSet();
            var claimed = new HashSet<int>();
            var regionIds = new HashSet<string>(StringComparer.Ordinal);
            var regions = new List<ObjFaceRegion>();
            foreach (var region in component.Regions)
            {
                if (region is null || !ValidRegionId(region.RegionId) ||
                    !regionIds.Add(region.RegionId) || region.Color is null ||
                    region.OriginalFaceNumbers is null || region.OriginalFaceNumbers.Count == 0 ||
                    region.OriginalFaceNumbers.Count > piece.Faces ||
                    region.RegionId == "fallback" || region.Name is { Length: > 120 } ||
                    region.Name?.Any(char.IsControl) == true ||
                    region.Name is not null && string.IsNullOrWhiteSpace(region.Name))
                    throw new IOException($"{component.ComponentId}: región inválida o sin caras.");
                foreach (int face in region.OriginalFaceNumbers)
                    if (face < 1 || face > ObjInspection.MaxFaces || !validFaces.Contains(face) ||
                        !claimed.Add(face))
                        throw new IOException($"{component.ComponentId}: cara {face} inexistente, ajena o reclamada dos veces.");
                regions.Add(region with { OriginalFaceNumbers = region.OriginalFaceNumbers.Order().ToArray() });
            }
            var original = Parse(Path.Combine(extractionDirectory, piece.FileName));
            if (regions.Count == 0 && (component.FallbackColor is null || original.Faces.All(face => face.Material is not null)))
                throw new IOException($"{component.ComponentId}: no hay caras a las que aplicar el respaldo.");
            for (int i = 0; i < piece.Faces; i++)
                if (!claimed.Contains(piece.OriginalFaceNumbers[i]) && original.Faces[i].Material is null &&
                    component.FallbackColor is null)
                    throw new IOException($"{component.ComponentId}: indique color de respaldo para las caras sin material.");
            ordered.Add(component with { Regions = regions.OrderBy(r => r.RegionId, StringComparer.Ordinal).ToArray() });
        }
        return plan with { Components = ordered.OrderBy(c => c.ComponentId, StringComparer.Ordinal).ToArray() };
    }

    private static bool ValidRegionId(string? id) => id is { Length: > 0 and <= 64 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static (string[] FaceMaterials, IReadOnlyList<ObjDerivedMaterial> Materials) MaterialChoices(
        ObjComponentRegionPlan plan, ObjExtractionPiece piece, Document original, string planSha)
    {
        var faceMaterials = new string[piece.Faces];
        var localFaces = piece.OriginalFaceNumbers.Select((number, index) => (number, index))
            .ToDictionary(x => x.number, x => x.index);
        var materials = new List<ObjDerivedMaterial>();
        foreach (var (region, index) in plan.Regions.Select((value, index) => (value, index)))
        {
            string name = $"BFAI_R_{planSha[..12]}_{plan.ComponentId}_{index + 1:000}";
            materials.Add(new(region.RegionId, name, region.Color, false));
            foreach (int originalFace in region.OriginalFaceNumbers)
            {
                if (!localFaces.TryGetValue(originalFace, out int local))
                    throw new IOException("La región referencia una cara ajena a la pieza.");
                faceMaterials[local] = name;
            }
        }
        string fallbackName = $"BFAI_F_{planSha[..12]}_{plan.ComponentId}";
        bool fallbackUsed = false;
        for (int i = 0; i < faceMaterials.Length; i++)
        {
            if (faceMaterials[i] is not null) continue;
            string? originalMaterial = original.Faces[i].Material;
            if (originalMaterial is not null)
            {
                faceMaterials[i] = originalMaterial["usemtl ".Length..];
            }
            else
            {
                if (plan.FallbackColor is null) throw new IOException("Falta material de respaldo.");
                faceMaterials[i] = fallbackName;
                fallbackUsed = true;
            }
        }
        if (fallbackUsed) materials.Add(new("fallback", fallbackName, plan.FallbackColor!, true));
        if (materials.Any(m => original.Materials.Contains(m.MaterialName)))
            throw new IOException("Un nombre de material derivado coincide con un material original.");
        return (faceMaterials, materials);
    }

    private static IReadOnlyList<ObjExtractionAsset> CopyRegionLibraries(ObjExtractionManifest manifest,
        string extraction, string staging)
    {
        var copied = new List<ObjExtractionAsset>();
        foreach (var asset in manifest.AuxiliaryFiles)
        {
            if (!SafeLeaf(asset.FileName)) throw new IOException("Referencia MTL original insegura.");
            string destination = Path.Combine(staging, asset.FileName);
            File.Copy(Path.Combine(extraction, asset.FileName), destination, false);
            CheckRegionAsset(destination, asset.Sha256, asset.Bytes);
            copied.Add(asset);
        }
        return copied;
    }

    private static void WriteRegionMtl(string path, IReadOnlyList<ObjDerivedMaterial> materials) =>
        File.WriteAllText(path, RegionMtlText(materials), new UTF8Encoding(false));

    private static string RegionMtlText(IReadOnlyList<ObjDerivedMaterial> materials)
    {
        var builder = new StringBuilder();
        foreach (var item in materials)
        {
            builder.Append("newmtl ").Append(item.MaterialName).Append('\n');
            builder.Append("Kd ").Append(Shade(item.Color.Red)).Append(' ')
                .Append(Shade(item.Color.Green)).Append(' ').Append(Shade(item.Color.Blue)).Append('\n');
        }
        return builder.ToString();
        static string Shade(byte component) => (component / 255.0).ToString("0.000000000", CultureInfo.InvariantCulture);
    }

    private static void WriteRegionObj(string originalPath, string derivedPath, Document original,
        IReadOnlyList<string> faceMaterials, string mtlName)
    {
        using var reader = new StreamReader(originalPath, new UTF8Encoding(false, true), true);
        using var writer = new StreamWriter(new FileStream(derivedPath, FileMode.CreateNew), new UTF8Encoding(false));
        foreach (var library in original.Libraries) writer.WriteLine("mtllib " + library);
        writer.WriteLine("mtllib " + mtlName);
        int face = 0;
        string? active = null;
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) { writer.WriteLine(line); continue; }
            if (fields[0] is "mtllib" or "usemtl") continue;
            if (fields[0] == "f")
            {
                if (face >= faceMaterials.Count) throw new IOException("El OBJ cambió durante la derivación.");
                string wanted = faceMaterials[face++];
                if (wanted != active) { writer.WriteLine("usemtl " + wanted); active = wanted; }
            }
            writer.WriteLine(line);
        }
        if (face != faceMaterials.Count) throw new IOException("Cambió el número de caras durante la derivación.");
    }

    private static void ValidateRegionGeometry(string originalPath, string derivedPath,
        IReadOnlyList<string> faceMaterials, ObjExtractionPiece piece)
    {
        ObjInspection.Inspect(derivedPath);
        var original = Parse(originalPath);
        var derived = Parse(derivedPath);
        if (!original.Positions.SequenceEqual(derived.Positions) ||
            !original.Textures.SequenceEqual(derived.Textures) ||
            !original.Normals.SequenceEqual(derived.Normals) ||
            original.Faces.Count != derived.Faces.Count || original.Faces.Count != piece.Faces)
            throw new IOException($"{piece.Id}: se modificaron vértices, UV, normales o cantidad de caras.");
        for (int i = 0; i < original.Faces.Count; i++)
        {
            var before = original.Faces[i]; var after = derived.Faces[i];
            if (before.Object != after.Object || before.Group != after.Group ||
                before.Smoothing != after.Smoothing || after.Material != "usemtl " + faceMaterials[i] ||
                before.Corners.Length != after.Corners.Length)
                throw new IOException($"{piece.Id}: cambió la estructura de la cara original {piece.OriginalFaceNumbers[i]}.");
            for (int j = 0; j < before.Corners.Length; j++)
                if (before.Corners[j] != after.Corners[j])
                    throw new IOException($"{piece.Id}: cambió un índice, UV, normal u orientación de la cara {piece.OriginalFaceNumbers[i]}.");
        }
        var a = ObjStructure.Analyze(originalPath);
        var b = ObjStructure.Analyze(derivedPath);
        if (b.Components.Count != 1 || b.Vertices != a.Vertices || b.Faces != a.Faces ||
            b.Triangles != a.Triangles || b.Components[0].Box != piece.Box)
            throw new IOException($"{piece.Id}: la topología o caja cambió.");
    }

    private static void CheckRegionAsset(string path, string expectedSha, long maximumLength)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > maximumLength || Hash(path) != expectedSha)
            throw new IOException("Archivo de regiones ausente, alterado o inseguro: " + Path.GetFileName(path));
    }

    private static string HashText(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
