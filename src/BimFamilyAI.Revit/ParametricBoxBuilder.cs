using Autodesk.Revit.DB;

namespace BimFamilyAI.Revit;

internal static class ParametricBoxBuilder
{
    private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    internal static void ValidateSaved(Document doc)
    {
        var fm = doc.FamilyManager;
        var solid = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().Single();
        var width = Parameter(fm, "Ancho", GroupTypeId.Geometry, SpecTypeId.Length);
        var depth = Parameter(fm, "Profundidad", GroupTypeId.Geometry, SpecTypeId.Length);
        var height = Parameter(fm, "Altura", GroupTypeId.Geometry, SpecTypeId.Length);
        var material = Parameter(fm, "Material", GroupTypeId.Materials, SpecTypeId.Reference.Material);
        var elementMaterial = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        if (fm.GetAssociatedFamilyParameter(elementMaterial)?.Id != material.Id ||
            fm.GetAssociatedFamilyParameter(solid.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM))?.Id != height.Id)
            throw new InvalidOperationException("Las asociaciones no persistieron al reabrir.");
        using var transaction = new Transaction(doc, "Validar persistencia sin guardar cambios");
        transaction.Start();
        foreach (var values in new[] { ("Cubo_500", 500d, 500d, 500d), ("Prisma_1000x600x750", 1000d, 600d, 750d) })
        {
            fm.CurrentType = fm.Types.Cast<FamilyType>().Single(t => t.Name == values.Item1);
            doc.Regenerate();
            Check(values.Item2, values.Item3, values.Item4);
        }
        fm.Set(width, Mm(1400)); fm.Set(depth, Mm(800)); fm.Set(height, Mm(950));
        doc.Regenerate(); Check(1400, 800, 950);
        transaction.RollBack();

        void Check(double x, double y, double z)
        {
            var box = solid.get_BoundingBox(null);
            if (box is null || (box.Min - new XYZ(-Mm(x)/2, -Mm(y)/2, 0)).GetLength() > 1e-6 ||
                (box.Max - new XYZ(Mm(x)/2, Mm(y)/2, Mm(z))).GetLength() > 1e-6)
                throw new InvalidOperationException("La geometría paramétrica no persistió al reabrir.");
            if (elementMaterial.AsElementId() != fm.CurrentType.AsElementId(material))
                throw new InvalidOperationException("El material no persistió al reabrir.");
        }
    }

    internal static void CreateAndValidate(Document doc)
    {
        using var transaction = new Transaction(doc, "Crear prisma paramétrico");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("No se inició la transacción paramétrica.");
        // Failures must roll back instead of leaving a pending modal transaction.
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(new RejectErrors()).SetClearAfterRollback(true));
        var fm = doc.FamilyManager;
        var width = Parameter(fm, "Ancho", GroupTypeId.Geometry, SpecTypeId.Length);
        var depth = Parameter(fm, "Profundidad", GroupTypeId.Geometry, SpecTypeId.Length);
        var height = Parameter(fm, "Altura", GroupTypeId.Geometry, SpecTypeId.Length);
        var material = Parameter(fm, "Material", GroupTypeId.Materials, SpecTypeId.Reference.Material);
        var halfWidth = Parameter(fm, "BFAI_Semiancho", GroupTypeId.Constraints, SpecTypeId.Length);
        var halfDepth = Parameter(fm, "BFAI_Semiprofundidad", GroupTypeId.Constraints, SpecTypeId.Length);
        var cubeType = fm.NewType("Cubo_500");
        fm.Set(width, Mm(500)); fm.Set(depth, Mm(500)); fm.Set(height, Mm(500));
        fm.SetFormula(halfWidth, "Ancho / 2"); fm.SetFormula(halfDepth, "Profundidad / 2");
        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
            ?? throw new InvalidOperationException("La plantilla debe contener una vista de planta.");

        ReferencePlane MakePlane(string name, bool xAxis, double coordinate)
        {
            var start = xAxis ? new XYZ(coordinate, -Mm(2000), 0) : new XYZ(-Mm(2000), coordinate, 0);
            var end = xAxis ? new XYZ(coordinate, Mm(2000), 0) : new XYZ(Mm(2000), coordinate, 0);
            var reference = doc.FamilyCreate.NewReferencePlane(start, end, XYZ.BasisZ, plan);
            reference.Name = name;
            return reference;
        }
        // Fixed central references anchor the box to the family origin.
        var cx = MakePlane("BFAI_CentroX", true, 0); cx.Pinned = true;
        var cy = MakePlane("BFAI_CentroY", false, 0); cy.Pinned = true;
        var left = MakePlane("BFAI_Izquierda", true, -Mm(250));
        var right = MakePlane("BFAI_Derecha", true, Mm(250));
        var front = MakePlane("BFAI_Frente", false, -Mm(250));
        var back = MakePlane("BFAI_Fondo", false, Mm(250));
        doc.Regenerate();
        void Label(ReferencePlane a, ReferencePlane b, bool xAxis, FamilyParameter label)
        {
            var refs = new ReferenceArray(); refs.Append(a.GetReference()); refs.Append(b.GetReference());
            var line = xAxis ? Line.CreateBound(new XYZ(-Mm(2000), -Mm(1500), 0), new XYZ(Mm(2000), -Mm(1500), 0))
                : Line.CreateBound(new XYZ(-Mm(1500), -Mm(2000), 0), new XYZ(-Mm(1500), Mm(2000), 0));
            doc.FamilyCreate.NewDimension(plan, line, refs).FamilyLabel = label;
        }
        Label(left, cx, true, halfWidth); Label(cx, right, true, halfWidth);
        Label(front, cy, false, halfDepth); Label(cy, back, false, halfDepth);
        XYZ[] points = [new(-Mm(250), -Mm(250), 0), new(Mm(250), -Mm(250), 0), new(Mm(250), Mm(250), 0), new(-Mm(250), Mm(250), 0)];
        var loop = new CurveArray();
        for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
        var loops = new CurveArrArray(); loops.Append(loop);
        using var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
        var solid = doc.FamilyCreate.NewExtrusion(true, loops, SketchPlane.Create(doc, plane), Mm(500));
        doc.Regenerate();
        var sketch = solid.Sketch;
        var edges = sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().ToArray();
        if (edges.Length != 4) throw new InvalidOperationException("El perfil debe tener cuatro aristas.");
        foreach (var edge in edges)
        {
            var curve = edge.GeometryCurve;
            var midpoint = curve.Evaluate(0.5, true);
            var target = Math.Abs(midpoint.X + Mm(250)) < 1e-6 ? left : Math.Abs(midpoint.X - Mm(250)) < 1e-6 ? right
                : midpoint.Y < 0 ? front : back;
            doc.FamilyCreate.NewAlignment(plan, target.GetReference(), curve.Reference);
        }
        Associate(fm, solid.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), height);
        var materialElement = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        Associate(fm, materialElement, material);
        var red = Material.Create(doc, "BFAI_Rojo"); ((Material)doc.GetElement(red)).Color = new Color(210, 60, 60);
        var blue = Material.Create(doc, "BFAI_Azul"); ((Material)doc.GetElement(blue)).Color = new Color(60, 90, 210);
        void SetAndValidate(double x, double y, double z, ElementId mat)
        {
            fm.Set(width, Mm(x)); fm.Set(depth, Mm(y)); fm.Set(height, Mm(z)); fm.Set(material, mat);
            doc.Regenerate();
            var box = solid.get_BoundingBox(null);
            if (box is null || (box.Min - new XYZ(-Mm(x)/2, -Mm(y)/2, 0)).GetLength() > 1e-6 ||
                (box.Max - new XYZ(Mm(x)/2, Mm(y)/2, Mm(z))).GetLength() > 1e-6)
                throw new InvalidOperationException($"La geometría no responde a {x} × {y} × {z} mm.");
            if (materialElement.AsElementId() != mat || !solid.GetMaterialIds(false).Contains(mat))
                throw new InvalidOperationException("El material del sólido no responde al parámetro Material.");
        }
        SetAndValidate(500, 500, 500, red);
        fm.NewType("Prisma_1000x600x750"); SetAndValidate(1000, 600, 750, blue);
        // Flex the same geometry with arbitrary values, then restore the delivered types.
        SetAndValidate(1400, 800, 950, red);
        SetAndValidate(1000, 600, 750, blue);
        fm.CurrentType = cubeType; SetAndValidate(500, 500, 500, red);
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit rechazó las restricciones. No se guardó la familia.");
    }

    private static FamilyParameter Parameter(FamilyManager fm, string name, ForgeTypeId group, ForgeTypeId spec)
    {
        var p = fm.get_Parameter(name) ?? fm.AddParameter(name, group, spec, false);
        if (p.IsInstance || p.Definition.GetDataType() != spec) throw new InvalidOperationException("Parámetro incompatible: " + name);
        return p;
    }

    private static void Associate(FamilyManager fm, Parameter element, FamilyParameter family)
    {
        if (element is null || !fm.CanElementParameterBeAssociated(element) || element.Definition.GetDataType() != family.Definition.GetDataType())
            throw new InvalidOperationException("No se puede asociar " + family.Definition.Name);
        fm.AssociateElementParameterToFamilyParameter(element, family);
        if (fm.GetAssociatedFamilyParameter(element)?.Id != family.Id) throw new InvalidOperationException("Asociación fallida.");
    }

    private sealed class RejectErrors : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
            => failures.GetFailureMessages().Any(f => f.GetSeverity() == FailureSeverity.Error)
                ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
