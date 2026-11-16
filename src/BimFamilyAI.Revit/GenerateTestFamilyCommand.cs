using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace BimFamilyAI.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class GenerateTestFamilyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => ExecuteFamily(commandData, ref message, false);

    internal static Result ExecuteFamily(ExternalCommandData commandData, ref string message, bool parametric)
    {
        Document? family = null;
        string? staging = null;
        string? output = null;
        var errors = new List<string>();
        var result = Result.Failed;
        try
        {
            var app = commandData.Application.Application;
            var template = SelectTemplate(app.FamilyTemplatePath);
            if (template is null) return Result.Cancelled;
            if (!File.Exists(template) || !string.Equals(Path.GetExtension(template), ".rft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La plantilla RFT no existe. Instale las plantillas de Revit 2027 o seleccione otra.");

            family = app.NewFamilyDocument(template)
                ?? throw new InvalidOperationException("Revit no pudo crear el documento de familia con esta plantilla.");
            if (!family.IsFamilyDocument || family.OwnerFamily.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_GenericModel))
                throw new InvalidOperationException("Seleccione una plantilla de Modelo genérico, sin anfitrión ni componentes adaptativos.");

            if (parametric) ParametricBoxBuilder.CreateAndValidate(family);
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
            string baseName = parametric ? "BIMFamilyAI_ParametricBox" : "BIMFamilyAI_TestCube_500mm";
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
            if (parametric)
            {
                family = app.OpenDocumentFile(temporary);
                ParametricBoxBuilder.ValidateSaved(family);
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
        message = string.Join(Environment.NewLine, errors);
        TaskDialog.Show("BIM Family AI", output is not null
            ? "Familia creada: " + output + (parametric ? "\nFamilia paramétrica: dos tipos y material asociados. Validaciones geométricas superadas.\n" : "\nCubo nativo de 500 × 500 × 500 mm.\n") + message
            : "No se pudo generar la familia.\n" + message);
        return result;
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
