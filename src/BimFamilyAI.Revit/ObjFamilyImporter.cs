using Autodesk.Revit.DB;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

internal static class ObjFamilyImporter
{
    internal static void Import(Document doc, ObjImportRequest request)
    {
        if (!OptionalFunctionalityUtils.IsOBJImportLinkAvailable())
            throw new InvalidOperationException("La instalación de Revit no dispone del módulo OBJ. Revise la instalación oficial de Autodesk.");
        ObjInspection.ValidateName(request.FamilyName);
        var source = ObjInspection.Inspect(request.Path);
        var raw = request.VerticalAxis switch
        {
            ObjVerticalAxis.Y => new XYZ(source.X, source.Z, source.Y),
            ObjVerticalAxis.X => new XYZ(source.Z, source.Y, source.X),
            _ => new XYZ(source.X, source.Y, source.Z)
        };
        var unit = request.Unit switch
        {
            ObjUnit.Millimeters => UnitTypeId.Millimeters, ObjUnit.Centimeters => UnitTypeId.Centimeters,
            ObjUnit.Meters => UnitTypeId.Meters, ObjUnit.Inches => UnitTypeId.Inches, _ => UnitTypeId.Feet
        };
        double feetPerUnit = UnitUtils.ConvertToInternalUnits(1, unit);
        double factor = 1;
        if (request.TargetHeightMm is { } target)
        {
            if (!double.IsFinite(target) || target <= 0 || raw.Z <= 1e-9)
                throw new InvalidOperationException("La altura objetivo y la altura original deben ser positivas y finitas.");
            factor = UnitUtils.ConvertToInternalUnits(target, UnitTypeId.Millimeters) / (raw.Z * feetPerUnit);
        }
        var expected = raw * (feetPerUnit * factor);
        if (!double.IsFinite(expected.GetLength()) || expected.GetLength() > UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Meters) ||
            expected.GetLength() < UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Millimeters))
            throw new InvalidOperationException("Dimensiones finales fuera del rango admitido: diagonal entre 0.1 mm y 100 m.");
        using var transaction = new Transaction(doc, "Importar OBJ como familia");
        transaction.Start();
        var failures = new ImportFailures();
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
        using var options = new OBJImportOptions
        {
            Unit = ImportUnit.Millimeter,
            CustomScale = UnitUtils.ConvertFromInternalUnits(feetPerUnit * factor, UnitTypeId.Millimeters),
            Placement = ImportPlacement.Origin, ThisViewOnly = false, OrientToView = false,
            ColorMode = ImportColorMode.Preserved, AutoCorrectAlmostVHLines = false
        };
        // OBJ CustomScale is expressed against millimetres in the installed 2027 importer.
        // Set it explicitly even without a target: Unit alone was ignored by the installed OBJ module.
        var id = doc.Import(request.Path, options, view);
        if (id == ElementId.InvalidElementId || doc.GetElement(id) is not { } element)
            throw new InvalidOperationException("Revit no devolvió un elemento importado válido.");
        if (element is ImportInstance { IsLinked: true } || element.ViewSpecific)
            throw new InvalidOperationException("Se requiere importación 3D embebida, no vínculo ni elemento exclusivo de una vista.");
        if (element.Pinned) element.Pinned = false;
        if (request.VerticalAxis != ObjVerticalAxis.Z)
        {
            var axis = request.VerticalAxis == ObjVerticalAxis.Y ? XYZ.BasisX : XYZ.BasisY;
            var angle = request.VerticalAxis == ObjVerticalAxis.Y ? Math.PI/2 : -Math.PI/2;
            ElementTransformUtils.RotateElement(doc, id, Line.CreateUnbound(XYZ.Zero, axis), angle);
        }
        doc.Regenerate();
        var box = Bounds(element);
        ElementTransformUtils.MoveElement(doc, id, new XYZ(-(box.Min.X+box.Max.X)/2, -(box.Min.Y+box.Max.Y)/2, -box.Min.Z));
        doc.Regenerate();
        ValidateGeometry(element, expected);
        SetParameters(doc, request, expected);
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit rechazó la importación: " + failures.Error);
        ValidateGeometry(doc.GetElement(id), expected);
    }

    internal static void SetParameters(Document doc, ObjImportRequest request, XYZ expected)
    {
        var fm = doc.FamilyManager;
        if (fm.CurrentType is null) fm.NewType("Importado");
        void Text(string name, string value)
        {
            var p = fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
            if (p.IsInstance || p.Definition.GetDataType() != SpecTypeId.String.Text) throw new InvalidOperationException("Parámetro incompatible: " + name);
            fm.Set(p, value);
        }
        Text("Nombre del modelo", request.FamilyName); Text("Descripción", request.Description);
        Text("Archivo de origen", System.IO.Path.GetFileName(request.Path));
        Text("Unidades originales", request.SourceUnitsUnspecified ? "No declaradas; escala por altura confirmada" : request.Unit.ToString());
        var sizes = new[] { ("Ancho", expected.X), ("Profundidad", expected.Y), ("Altura", expected.Z) };
        foreach (var (name, size) in sizes)
        {
            var p = fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            if (p.IsInstance || p.Definition.GetDataType() != SpecTypeId.Length) throw new InvalidOperationException("Parámetro incompatible: " + name);
            fm.Set(p, size);
        }
    }

    internal static void ValidateSaved(Document doc, ObjImportRequest request)
    {
        if (!doc.IsFamilyDocument || doc.OwnerFamily.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_GenericModel))
            throw new InvalidOperationException("El RFA reabierto no es una familia de Modelo genérico.");
        var fm = doc.FamilyManager;
        double Size(string name)
        {
            var p = fm.get_Parameter(name) ?? throw new InvalidOperationException("Falta el parámetro " + name);
            if (p.Definition.GetDataType() != SpecTypeId.Length || p.IsInstance) throw new InvalidOperationException("Dimensión BIM inválida.");
            return fm.CurrentType.AsDouble(p) ?? throw new InvalidOperationException("Dimensión BIM vacía.");
        }
        var expected = new XYZ(Size("Ancho"), Size("Profundidad"), Size("Altura"));
        var imported = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
        if (imported.Length != 1 || imported[0].IsLinked || imported[0].ViewSpecific)
            throw new InvalidOperationException("No persistió una única importación 3D embebida.");
        ValidateGeometry(imported[0], expected);
        foreach (var name in new[] { "Nombre del modelo", "Descripción", "Archivo de origen", "Unidades originales" })
            if (fm.get_Parameter(name) is null) throw new InvalidOperationException("No persistió " + name);
    }

    internal static BoundingBoxXYZ Bounds(Element element)
        => element.get_BoundingBox(null) ?? throw new InvalidOperationException("El OBJ no produjo una caja geométrica.");

    private static void ValidateGeometry(Element element, XYZ expected)
    {
        var box = Bounds(element);
        double tolerance = Math.Max(UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Millimeters), expected.GetLength()*1e-6);
        if ((box.Max-box.Min-expected).GetLength() > tolerance || Math.Abs(box.Min.Z) > tolerance ||
            Math.Abs(box.Min.X+box.Max.X) > tolerance*2 || Math.Abs(box.Min.Y+box.Max.Y) > tolerance*2)
            throw new InvalidOperationException($"Dimensiones o posición inesperadas. Obtenido (mm): {UnitUtils.ConvertFromInternalUnits(box.Max.X-box.Min.X, UnitTypeId.Millimeters):F3} × {UnitUtils.ConvertFromInternalUnits(box.Max.Y-box.Min.Y, UnitTypeId.Millimeters):F3} × {UnitUtils.ConvertFromInternalUnits(box.Max.Z-box.Min.Z, UnitTypeId.Millimeters):F3}.");
        ValidateMeshPresent(element);
    }

    internal static void ValidateMeshPresent(Element element)
    {
        bool HasGeometry(GeometryElement? geometry) => geometry is not null && geometry.Any(g =>
            g is Mesh mesh && mesh.NumTriangles > 0 || g is Solid solid && solid.Faces.Size > 0 ||
            g is GeometryInstance instance && HasGeometry(instance.GetInstanceGeometry()));
        using var options = new Options { DetailLevel = ViewDetailLevel.Fine };
        if (!HasGeometry(element.get_Geometry(options))) throw new InvalidOperationException("El elemento importado no contiene malla ni sólido utilizable.");
    }

    private sealed class ImportFailures : IFailuresPreprocessor
    {
        public string Error { get; private set; } = "";
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var errors = accessor.GetFailureMessages().Where(m => m.GetSeverity() == FailureSeverity.Error).ToArray();
            Error = string.Join("; ", errors.Select(m => m.GetDescriptionText()));
            return errors.Length > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
