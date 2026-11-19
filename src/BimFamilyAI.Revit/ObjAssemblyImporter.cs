using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

internal sealed record ComponentColor(int Number, byte Red, byte Green, byte Blue);
internal sealed record ObjAssemblyRequest(string ExtractionDirectory, IReadOnlyList<ComponentColor> Colors,
    string? RegionDirectory = null, IReadOnlyDictionary<ObjComponentIdentity, string>? Labels = null);
internal sealed record ObjComponentIdentity(string ComponentId, string SourceSha256, string PieceSha256);

/// <summary>Imports all extracted pieces in one family coordinate system, exclusively in Revit API context.</summary>
internal static class ObjAssemblyImporter
{
    private static readonly Guid SchemaId = new("45ca17db-d2e5-4c44-9981-2c824f842dd3");
    private static readonly Guid RegionSchemaId = new("4937a4e4-62fa-44f4-a766-b91ad462144a");

    // Read the immutable 5D-B2 identity without creating or changing its published schema.
    internal static ObjComponentIdentity? TryReadIdentity(ImportInstance element)
    {
        var schema = Schema.Lookup(SchemaId);
        if (schema is null) return null;
        try
        {
            var entity = element.GetEntity(schema);
            if (!entity.IsValid()) return null;
            string id = entity.Get<string>("ComponentId");
            string source = entity.Get<string>("SourceSha256");
            string piece = entity.Get<string>("PieceSha256");
            if (id is null || !id.StartsWith("component_", StringComparison.Ordinal) ||
                !int.TryParse(id["component_".Length..], out int number) || number is < 1 or > 128 ||
                source is null || source.Length != 64 || piece is null || piece.Length != 64 ||
                !source.All(Uri.IsHexDigit) || !piece.All(Uri.IsHexDigit)) return null;
            return new(id, source, piece);
        }
        catch (Exception) { return null; }
    }

    internal static void Import(Document doc, ObjImportRequest request, ObjAssemblyRequest assembly,
        IReadOnlySet<ElementId> templateMaterials)
    {
        if (!OptionalFunctionalityUtils.IsOBJImportLinkAvailable())
            throw new InvalidOperationException("La instalación de Revit no dispone del módulo OBJ.");
        ObjInspection.ValidateName(request.FamilyName);
        var manifest = ObjComponentExtractor.Verify(request.Path, assembly.ExtractionDirectory);
        var regionManifest = assembly.RegionDirectory is null ? null :
            ObjComponentExtractor.VerifyRegions(request.Path, assembly.ExtractionDirectory, assembly.RegionDirectory);
        if (manifest.ComponentCount < 2 && regionManifest is null)
            throw new InvalidOperationException("El OBJ tiene una sola componente; use OBJ completo.");
        if (manifest.ComponentCount > 128)
            throw new InvalidOperationException("Más de 128 componentes: revise el modelo antes de importar en Revit.");
        if (assembly.Colors.Select(c => c.Number).Distinct().Count() != assembly.Colors.Count ||
            assembly.Colors.Any(c => c.Number < 1 || c.Number > manifest.ComponentCount))
            throw new InvalidOperationException("Asignación de color a componentes inválida.");
        if (regionManifest is not null && assembly.Colors.Any(c => regionManifest.Components.Any(p =>
            p.ComponentId == $"component_{c.Number:000}")))
            throw new InvalidOperationException("Una pieza con regiones no admite además un color uniforme.");
        var pose = Pose(request, manifest.GlobalBox);
        using var transaction = new Transaction(doc, "Importar componentes OBJ");
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("No se pudo iniciar la importación de componentes.");
        var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
        var schema = ComponentSchema();
        var regionSchema = regionManifest is null ? null : RegionSchema();
        var usedMaterials = new HashSet<ElementId>();
        foreach (var piece in manifest.Components)
        {
            int number = int.Parse(piece.Id["component_".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var color = assembly.Colors.SingleOrDefault(c => c.Number == number);
            var regionPiece = regionManifest?.Components.SingleOrDefault(p => p.ComponentId == piece.Id);
            string path = Path.Combine(assembly.ExtractionDirectory, piece.FileName);
            using var prepared = color is null ? null : UniformObjMaterial.Prepare(path, color.Red, color.Green, color.Blue);
            string importPath = regionPiece is null ? prepared?.Path ?? path :
                Path.Combine(assembly.RegionDirectory!, regionPiece.FileName);
            using var options = new OBJImportOptions
            {
                Unit = ImportUnit.Millimeter,
                CustomScale = pose.MillimetersPerSourceUnit,
                Placement = ImportPlacement.Origin, ThisViewOnly = false, OrientToView = false,
                ColorMode = ImportColorMode.Preserved, AutoCorrectAlmostVHLines = false
            };
            var id = doc.Import(importPath, options, view);
            if (doc.GetElement(id) is not ImportInstance element || element.IsLinked || element.ViewSpecific)
                throw new InvalidOperationException($"Revit no creó una importación embebida para {piece.Id}.");
            if (element.Pinned) element.Pinned = false;
            if (request.VerticalAxis != ObjVerticalAxis.Z)
            {
                var axis = request.VerticalAxis == ObjVerticalAxis.Y ? XYZ.BasisX : XYZ.BasisY;
                var angle = request.VerticalAxis == ObjVerticalAxis.Y ? Math.PI / 2 : -Math.PI / 2;
                ElementTransformUtils.RotateElement(doc, id, Line.CreateUnbound(XYZ.Zero, axis), angle);
            }
            var shift = pose.TranslationMm;
            ElementTransformUtils.MoveElement(doc, id, Mm(new ObjPoint(shift.X, shift.Y, shift.Z)));
            doc.Regenerate();
            ValidateElement(element, pose.Transform(piece.Box), piece.Id, pose.GlobalBoxMm);
            using (var entity = new Entity(schema))
            {
                entity.Set("ComponentId", piece.Id);
                entity.Set("SourceSha256", manifest.SourceSha256);
                entity.Set("PieceSha256", piece.Sha256);
                element.SetEntity(entity);
            }
            var comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is { IsReadOnly: false, StorageType: StorageType.String })
                comments.Set("Componente_" + number.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            var identity = new ObjComponentIdentity(piece.Id, manifest.SourceSha256, piece.Sha256);
            if (assembly.Labels?.TryGetValue(identity, out var label) == true)
                ComponentLabels.WriteName(element, piece.Id, label);
            if (color is not null) ApplyColor(doc, element, piece.Id, color, templateMaterials, usedMaterials);
            if (regionPiece is not null)
            {
                ApplyRegionColors(doc, element, piece.Id, regionPiece, templateMaterials, usedMaterials);
                using var regionEntity = new Entity(regionSchema!);
                regionEntity.Set("ComponentId", piece.Id);
                regionEntity.Set("SourceSha256", manifest.SourceSha256);
                regionEntity.Set("OriginalPieceSha256", piece.Sha256);
                regionEntity.Set("PlanSha256", regionManifest!.PlanSha256);
                regionEntity.Set("DerivedObjSha256", regionPiece.DerivedSha256);
                regionEntity.Set("FormatVersion", 1);
                element.SetEntity(regionEntity);
            }
        }
        ObjFamilyImporter.SetParameters(doc, request, Size(pose.GlobalBoxMm));
        ValidateSaved(doc, request, assembly, manifest, regionManifest);
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit no confirmó el ensamblaje OBJ.");
        ValidateSaved(doc, request, assembly, manifest, regionManifest);
    }

    internal static void ValidateSaved(Document doc, ObjImportRequest request, ObjAssemblyRequest assembly,
        ObjExtractionManifest? knownManifest = null, ObjRegionManifest? knownRegions = null)
    {
        var manifest = knownManifest ?? ObjComponentExtractor.Verify(request.Path, assembly.ExtractionDirectory);
        var regionManifest = knownRegions ?? (assembly.RegionDirectory is null ? null :
            ObjComponentExtractor.VerifyRegions(request.Path, assembly.ExtractionDirectory, assembly.RegionDirectory));
        var pose = Pose(request, manifest.GlobalBox);
        if (!doc.IsFamilyDocument || doc.OwnerFamily.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_GenericModel))
            throw new InvalidOperationException("El RFA por componentes no es una familia de Modelo genérico.");
        var elements = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
        if (elements.Length != manifest.ComponentCount)
            throw new InvalidOperationException("El número de componentes importados no coincide con el manifiesto.");
        var schema = ComponentSchema();
        var byId = new Dictionary<string, ImportInstance>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var entity = element.GetEntity(schema);
            if (!entity.IsValid() || entity.Get<string>("SourceSha256") != manifest.SourceSha256)
                throw new InvalidOperationException("La importación perdió su identidad de origen.");
            string id = entity.Get<string>("ComponentId");
            if (!byId.TryAdd(id, element)) throw new InvalidOperationException("Identidad de componente duplicada.");
        }
        foreach (var piece in manifest.Components)
        {
            if (!byId.TryGetValue(piece.Id, out var element) || element.IsLinked || element.ViewSpecific)
                throw new InvalidOperationException($"Falta la importación embebida {piece.Id}.");
            var entity = element.GetEntity(schema);
            if (entity.Get<string>("PieceSha256") != piece.Sha256)
                throw new InvalidOperationException($"Cambió la correspondencia de {piece.Id}.");
            var identity = new ObjComponentIdentity(piece.Id, manifest.SourceSha256, piece.Sha256);
            if (assembly.Labels?.TryGetValue(identity, out var label) == true && ComponentLabels.ReadName(element, piece.Id) != label)
                throw new InvalidOperationException($"No se conservó la etiqueta de {piece.Id}.");
            ValidateElement(element, pose.Transform(piece.Box), piece.Id, pose.GlobalBoxMm);
            int number = int.Parse(piece.Id["component_".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var color = assembly.Colors.SingleOrDefault(c => c.Number == number);
            if (color is not null) ValidateColor(doc, element, color, piece.Id);
            var regionPiece = regionManifest?.Components.SingleOrDefault(p => p.ComponentId == piece.Id);
            if (regionPiece is not null)
            {
                ValidateRegionColors(doc, element, piece.Id, regionPiece);
                var regionEntity = element.GetEntity(RegionSchema());
                if (!regionEntity.IsValid() || regionEntity.Get<int>("FormatVersion") != 1 ||
                    regionEntity.Get<string>("ComponentId") != piece.Id ||
                    regionEntity.Get<string>("SourceSha256") != manifest.SourceSha256 ||
                    regionEntity.Get<string>("OriginalPieceSha256") != piece.Sha256 ||
                    regionEntity.Get<string>("PlanSha256") != regionManifest!.PlanSha256 ||
                    regionEntity.Get<string>("DerivedObjSha256") != regionPiece.DerivedSha256)
                    throw new InvalidOperationException($"{piece.Id}: se perdió la procedencia de regiones.");
            }
        }
        var actual = Union(elements.Select(ObjFamilyImporter.Bounds));
        AssertBox(actual, pose.GlobalBoxMm, "conjunto", pose.GlobalBoxMm);
        var manager = doc.FamilyManager;
        foreach (var (name, expected) in new[] { ("Ancho", Size(pose.GlobalBoxMm).X),
            ("Profundidad", Size(pose.GlobalBoxMm).Y), ("Altura", Size(pose.GlobalBoxMm).Z) })
        {
            var parameter = manager.get_Parameter(name);
            if (parameter is null || manager.CurrentType.AsDouble(parameter) is not { } actualValue ||
                Math.Abs(actualValue - expected) > Tolerance(pose.GlobalBoxMm))
                throw new InvalidOperationException($"La dimensión BIM {name} no coincide con el ensamblaje.");
        }
    }

    private static ObjAssemblyTransform Pose(ObjImportRequest request, ObjBox box)
    {
        var unit = request.Unit switch
        {
            ObjUnit.Millimeters => UnitTypeId.Millimeters, ObjUnit.Centimeters => UnitTypeId.Centimeters,
            ObjUnit.Meters => UnitTypeId.Meters, ObjUnit.Inches => UnitTypeId.Inches, _ => UnitTypeId.Feet
        };
        double feetPerUnit = UnitUtils.ConvertToInternalUnits(1, unit);
        double mmPerUnit = UnitUtils.ConvertFromInternalUnits(feetPerUnit, UnitTypeId.Millimeters);
        return ObjAssemblyTransform.Create(box, request.VerticalAxis, mmPerUnit, request.TargetHeightMm);
    }

    private static XYZ Mm(ObjPoint point) => new(
        UnitUtils.ConvertToInternalUnits(point.X, UnitTypeId.Millimeters),
        UnitUtils.ConvertToInternalUnits(point.Y, UnitTypeId.Millimeters),
        UnitUtils.ConvertToInternalUnits(point.Z, UnitTypeId.Millimeters));
    private static XYZ Size(ObjBox box) => Mm(new ObjPoint(box.Max.X - box.Min.X,
        box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
    private static double Tolerance(ObjBox global) => Math.Max(UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Millimeters),
        Size(global).GetLength() * 1e-6);
    private static void ValidateElement(ImportInstance element, ObjBox expected, string id, ObjBox global)
    {
        ObjFamilyImporter.ValidateMeshPresent(element);
        AssertBox(ObjFamilyImporter.Bounds(element), expected, id, global);
    }
    private static void AssertBox(BoundingBoxXYZ actual, ObjBox expected, string id, ObjBox global)
    {
        double tolerance = Tolerance(global);
        if ((actual.Min - Mm(expected.Min)).GetLength() > tolerance ||
            (actual.Max - Mm(expected.Max)).GetLength() > tolerance)
            throw new InvalidOperationException($"{id}: posición, escala u orientación distintas de la transformación global.");
    }
    private static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> boxes)
    {
        var array = boxes.ToArray();
        return new BoundingBoxXYZ
        {
            Min = new XYZ(array.Min(b => b.Min.X), array.Min(b => b.Min.Y), array.Min(b => b.Min.Z)),
            Max = new XYZ(array.Max(b => b.Max.X), array.Max(b => b.Max.Y), array.Max(b => b.Max.Z))
        };
    }

    private static void ApplyColor(Document doc, ImportInstance element, string id, ComponentColor color,
        IReadOnlySet<ElementId> templateMaterials, HashSet<ElementId> alreadyUsed)
    {
        var ids = ImportedMaterials.InspectElement(doc, element).Select(r => r.MaterialId).Distinct().ToArray();
        if (ids.Length != 1 || doc.GetElement(ids[0]) is not Material material ||
            templateMaterials.Contains(ids[0]) || !alreadyUsed.Add(ids[0]))
            throw new InvalidOperationException($"{id}: Revit no expuso un material exclusivo y editable.");
        string name = $"BFAI_{id}_{Guid.NewGuid():N}";
        if (!Material.IsNameUnique(doc, name)) throw new InvalidOperationException($"{id}: nombre de material ocupado.");
        material.Name = name;
        material.UseRenderAppearanceForShading = false;
        material.Color = new Color(color.Red, color.Green, color.Blue);
        material.Transparency = 0;
        ValidateColor(doc, element, color, id);
    }
    private static void ApplyRegionColors(Document doc, ImportInstance element, string id,
        ObjRegionDerivedPiece piece, IReadOnlySet<ElementId> templateMaterials,
        HashSet<ElementId> alreadyUsed)
    {
        var ids = ImportedMaterials.InspectElement(doc, element).Select(r => r.MaterialId).Distinct().ToArray();
        if (ids.Length < piece.Materials.Count || ids.Any(materialId =>
            templateMaterials.Contains(materialId) || !alreadyUsed.Add(materialId) ||
            doc.GetElement(materialId) is not Material))
            throw new InvalidOperationException($"{id}: Revit no expuso materiales exclusivos para las regiones.");
        var materials = ids.Select(materialId => (Material)doc.GetElement(materialId)).ToArray();
        var assigned = new HashSet<ElementId>();
        foreach (var expected in piece.Materials)
        {
            var matches = materials.Where(m => m.Name == expected.MaterialName).ToArray();
            if (matches.Length != 1 || !assigned.Add(matches[0].Id))
                throw new InvalidOperationException($"{id}: Revit no conservó la referencia de material {expected.RegionId}.");
        }
        foreach (var material in materials)
        {
            var expected = piece.Materials.SingleOrDefault(m => material.Name == m.MaterialName);
            string isolated = $"BFAI_{id}_{(expected?.MaterialName ?? "Original")}_{Guid.NewGuid():N}";
            if (!Material.IsNameUnique(doc, isolated))
                throw new InvalidOperationException($"{id}: no se pudo aislar un material de región.");
            material.Name = isolated;
            if (expected is null) continue;
            material.UseRenderAppearanceForShading = false;
            material.Color = new Color(expected.Color.Red, expected.Color.Green, expected.Color.Blue);
            material.Transparency = 0;
        }
        doc.Regenerate();
        ValidateRegionColors(doc, element, id, piece);
    }
    private static void ValidateRegionColors(Document doc, ImportInstance element, string id,
        ObjRegionDerivedPiece piece)
    {
        var ids = ImportedMaterials.InspectElement(doc, element).Select(r => r.MaterialId).Distinct().ToArray();
        if (ids.Length < piece.Materials.Count || ids.Any(materialId =>
            doc.GetElement(materialId) is not Material m ||
            !m.Name.StartsWith($"BFAI_{id}_", StringComparison.Ordinal)))
            throw new InvalidOperationException($"{id}: faltan referencias gráficas propias de las regiones.");
        foreach (var expected in piece.Materials)
        {
            string prefix = $"BFAI_{id}_{expected.MaterialName}_";
            var matching = ids.Select(materialId => (Material)doc.GetElement(materialId))
                .Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (matching.Length != 1 || matching[0].Color.Red != expected.Color.Red ||
                matching[0].Color.Green != expected.Color.Green ||
                matching[0].Color.Blue != expected.Color.Blue || matching[0].Transparency != 0 ||
                matching[0].UseRenderAppearanceForShading)
                throw new InvalidOperationException($"{id}: RGB de la región {expected.RegionId} no persistió.");
        }
    }
    private static void ValidateColor(Document doc, ImportInstance element, ComponentColor color, string id)
    {
        var ids = ImportedMaterials.InspectElement(doc, element).Select(r => r.MaterialId).Distinct().ToArray();
        if (ids.Length != 1 || doc.GetElement(ids[0]) is not Material material ||
            !material.Name.StartsWith($"BFAI_{id}_", StringComparison.Ordinal) ||
            material.Color.Red != color.Red || material.Color.Green != color.Green || material.Color.Blue != color.Blue ||
            material.UseRenderAppearanceForShading || material.Transparency != 0)
            throw new InvalidOperationException($"{id}: color gráfico independiente no persistió.");
    }

    private static Schema ComponentSchema()
    {
        var schema = Schema.Lookup(SchemaId);
        if (schema is not null) return schema;
        var builder = new SchemaBuilder(SchemaId);
        builder.SetSchemaName("BFAI_ObjComponent_v1");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("ComponentId", typeof(string));
        builder.AddSimpleField("SourceSha256", typeof(string));
        builder.AddSimpleField("PieceSha256", typeof(string));
        return builder.Finish();
    }

    private static Schema RegionSchema()
    {
        var schema = Schema.Lookup(RegionSchemaId);
        if (schema is not null) return schema;
        var builder = new SchemaBuilder(RegionSchemaId);
        builder.SetSchemaName("BFAI_FaceRegions_v1");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("ComponentId", typeof(string));
        builder.AddSimpleField("SourceSha256", typeof(string));
        builder.AddSimpleField("OriginalPieceSha256", typeof(string));
        builder.AddSimpleField("PlanSha256", typeof(string));
        builder.AddSimpleField("DerivedObjSha256", typeof(string));
        builder.AddSimpleField("FormatVersion", typeof(int));
        return builder.Finish();
    }
}
