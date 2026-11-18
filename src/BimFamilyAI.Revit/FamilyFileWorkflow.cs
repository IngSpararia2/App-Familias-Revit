using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace BimFamilyAI.Revit;

internal static class FamilyFileWorkflow
{
    internal static Result ExecuteFamily(ExternalCommandData commandData, ref string message, bool parametric, BimFamilyAI.Core.ObjImportRequest? obj = null)
    {
        var outcome = CreateFamily(commandData.Application.Application, parametric, obj);
        message = outcome.Message;
        if (outcome.Status == Result.Cancelled) return Result.Cancelled;
        TaskDialog.Show("BIM Family AI", outcome.OutputPath is not null
            ? "Familia creada: " + outcome.OutputPath + (obj is not null ? "\nOBJ importado y verificado después de reabrir. Dimensiones informativas; no controlan la malla.\n" : parametric ? "\nFamilia paramétrica: dos tipos y material asociados. Validaciones geométricas superadas.\n" : "\nCubo nativo de 500 × 500 × 500 mm.\n") + message
            : "No se pudo generar la familia.\n" + message);
        return outcome.Status;
    }

    // Must be called exclusively from a valid Revit command or ExternalEvent.Execute context.
    internal static FamilyWorkflowOutcome CreateFamily(Autodesk.Revit.ApplicationServices.Application app,
        bool parametric, BimFamilyAI.Core.ObjImportRequest? obj = null, bool editMaterials = false,
        ObjAssemblyRequest? assembly = null)
    {
        Document? family = null;
        string? staging = null;
        string? output = null;
        var errors = new List<string>();
        var result = Result.Failed;
        ImportedMaterialDialog.Choice? materialChoice = null;
        IReadOnlyList<ImportedMaterials.SavedMaterial>? multipleExpected = null;
        try
        {
            var template = SelectTemplate(app.FamilyTemplatePath);
            if (template is null) return new(Result.Cancelled, null, "Selección de plantilla cancelada.");
            if (!File.Exists(template) || !string.Equals(Path.GetExtension(template), ".rft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La plantilla RFT no existe. Instale las plantillas de Revit 2027 o seleccione otra.");

            family = app.NewFamilyDocument(template)
                ?? throw new InvalidOperationException("Revit no pudo crear el documento de familia con esta plantilla.");
            if (!family.IsFamilyDocument || family.OwnerFamily.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_GenericModel))
                throw new InvalidOperationException("Seleccione una plantilla de Modelo genérico, sin anfitrión ni componentes adaptativos.");

            if (obj is not null)
            {
                var originalMaterials = new FilteredElementCollector(family).OfClass(typeof(Material)).ToElementIds().ToHashSet();
                if (assembly is not null)
                {
                    if (editMaterials) throw new InvalidOperationException("Use los colores por componente en esta modalidad.");
                    ObjAssemblyImporter.Import(family, obj, assembly, originalMaterials);
                }
                else ObjFamilyImporter.Import(family, obj);
                if (editMaterials && assembly is null)
                {
                    var dialog = new ImportedMaterialDialog(family, obj.FamilyName, originalMaterials);
                    if (dialog.ShowDialog() != true) return new(Result.Cancelled,null,"Creación cancelada; OBJ conservado.");
                    materialChoice = dialog.Selected;
                    if (materialChoice is { } m) ImportedMaterials.Apply(family,m.Name,m.Red,m.Green,m.Blue,originalMaterials,obj);
                    if (dialog.MultipleSelected is { } assignments)
                    {
                        var edited=assignments.Select(a=>a.Id).ToHashSet();
                        var untouched=ImportedMaterials.EditableMaterials(family).Where(m=>!edited.Contains(m.Id))
                            .ToDictionary(m=>m.Id,m=>{var material=(Material)family.GetElement(m.Id);return (m.Red,m.Green,m.Blue,material.Transparency,material.UseRenderAppearanceForShading,m.References);});
                        ImportedMaterials.ApplyMultiple(family,assignments,originalMaterials);
                        multipleExpected=ImportedMaterials.Snapshot(family);
                        if(ImportedMaterials.EditableMaterials(family).Where(m=>untouched.ContainsKey(m.Id)).Any(m=>
                        {
                            var old=untouched[m.Id];var material=(Material)family.GetElement(m.Id);
                            return m.Red!=old.Red || m.Green!=old.Green || m.Blue!=old.Blue || material.Transparency!=old.Transparency || material.UseRenderAppearanceForShading!=old.UseRenderAppearanceForShading || m.References!=old.References;
                        }))
                            throw new InvalidOperationException("Un material no seleccionado cambió durante la edición.");
                    }
                    ObjFamilyImporter.ValidateSaved(family,obj);
                }
            }
            else if (parametric) ParametricBoxBuilder.CreateAndValidate(family);
            else
            {
                double side = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
                double half = side / 2;
                using (var transaction = new Transaction(family, "Crear cubo de 500 mm"))
                {
                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("No se pudo iniciar la transacción.");
                    XYZ[] points = [new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0)];
                    var profile = new CurveArray();
                    for (int i = 0; i < points.Length; i++)
                        profile.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
                    var profiles = new CurveArrArray();
                    profiles.Append(profile);
                    using var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    var sketch = SketchPlane.Create(family, plane);
                    var cube = family.FamilyCreate.NewExtrusion(true, profiles, sketch, side);
                    family.Regenerate();
                    var box = cube.get_BoundingBox(null);
                    if (box is null || (box.Min - new XYZ(-half, -half, 0)).GetLength() > 1e-6 ||
                        (box.Max - new XYZ(half, half, side)).GetLength() > 1e-6)
                        throw new InvalidOperationException("La extrusión no tiene las dimensiones o posición esperadas.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit no confirmó la transacción. No se guardó la familia.");
                }

            }
            string baseName = obj?.FamilyName ?? (parametric ? "BIMFamilyAI_ParametricBox" : "BIMFamilyAI_TestCube_500mm");
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) throw new IOException("Windows no devolvió una carpeta Documentos válida.");
            var directory = Path.Combine(documents, "BIM Family AI", "Generated");
            Directory.CreateDirectory(directory);
            staging = Path.Combine(directory, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var temporary = Path.Combine(staging, baseName + ".rfa");
            using var options = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 };
            family.SaveAs(temporary, options);
            if (!File.Exists(temporary)) throw new IOException("Revit no creó el archivo RFA.");
            if (!family.Close(false)) throw new IOException("No se pudo cerrar el documento de familia.");
            family = null;
            if (parametric || obj is not null)
            {
                family = app.OpenDocumentFile(temporary);
                if (obj is not null && assembly is not null) ObjAssemblyImporter.ValidateSaved(family, obj, assembly);
                else if (obj is not null) ObjFamilyImporter.ValidateSaved(family, obj);
                else ParametricBoxBuilder.ValidateSaved(family);
                if (materialChoice is { } m) ImportedMaterials.Validate(family,m.Red,m.Green,m.Blue);
                if (multipleExpected is not null) ImportedMaterials.ValidateSnapshot(family,multipleExpected);
                if (!family.Close(false)) throw new IOException("No se pudo cerrar la familia reabierta.");
                family = null;
            }
            for (int index = 0; ; index++)
            {
                var suffix = index == 0 ? "" : "_" + index;
                var candidate = Path.Combine(directory, baseName + suffix + ".rfa");
                try { File.Move(temporary, candidate, false); output = candidate; break; }
                catch (IOException) when (File.Exists(candidate)) { }
            }
            result = Result.Succeeded;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            if (family is not null)
            {
                try { if (!family.Close(false)) errors.Add("No se pudo cerrar el documento de familia."); }
                catch (Exception ex) { errors.Add("Error al cerrar: " + ex.Message); }
            }
            if (staging is not null)
            {
                try { Directory.Delete(staging, true); }
                catch (Exception ex) { errors.Add("No se pudo limpiar " + staging + ": " + ex.Message); }
            }
        }
        return new(result, output, string.Join(Environment.NewLine, errors));
    }

    private static string? SelectTemplate(string configuredDirectory)
    {
        var explicitPath = Environment.GetEnvironmentVariable("BIMFAMILYAI_TEMPLATE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        if (Directory.Exists(configuredDirectory))
        {
            string[] names = ["Metric Generic Model.rft", "Modelo genérico métrico.rft"];
            var candidates = Directory.EnumerateFiles(configuredDirectory, "*.rft", SearchOption.AllDirectories)
                .Where(path => names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1) return candidates[0];
        }
        var dialog = new OpenFileDialog
        {
            Title = "Seleccione una plantilla Revit 2027 de Modelo genérico sin anfitrión",
            Filter = "Plantilla de familia Revit (*.rft)|*.rft",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(configuredDirectory) ? configuredDirectory : ""
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

internal sealed record FamilyWorkflowOutcome(Result Status, string? OutputPath, string Message);

