using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BimFamilyAI.Core;
using BimFamilyAI.Revit;

// Explicit temporary Revit integration runner. Never installed by the normal installer.
public sealed class AssemblyChecks : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    { application.Idling += Run; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private void Run(object? sender, IdlingEventArgs args)
    {
        var ui = (UIApplication)sender!;
        ui.Idling -= Run;
        var folder = new DirectoryInfo(Path.GetDirectoryName(typeof(AssemblyChecks).Assembly.Location)!);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "BIMFamilyAI.sln"))) folder = folder.Parent;
        if (folder is null) throw new DirectoryNotFoundException("No se encontró la solución para las pruebas de ensamblaje.");
        string root = folder.FullName;
        string output = Path.Combine(root, "artifacts", "stage5d-b2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var log = new List<object>();
        int passed = 0;
        void Check(bool condition, string title)
        { if (!condition) throw new Exception(title); passed++; log.Add(new { pass = title }); }
        Document? doc = null;
        try
        {
            string chair = Path.Combine(root, "artifacts", "stage5probe", "chair.obj");
            if (!File.Exists(chair)) throw new FileNotFoundException("Falta el OBJ local de silla.", chair);
            string chairPieces = Path.Combine(output, "chair-pieces");
            var chairExtraction = ObjComponentExtractor.Extract(chair, chairPieces);
            Check(chairExtraction.Manifest.ComponentCount == 6 && chairExtraction.Manifest.Components.Sum(c => c.Faces) == 4172,
                "Chair extraction 6 pieces / 4172 faces");
            var chairRequest = new ObjImportRequest(chair, ObjUnit.Millimeters, ObjVerticalAxis.Y, 750,
                "Stage5D_B2_Chair", "Integration chair", true);
            var colors = new[] { new ComponentColor(1, 240, 20, 20), new ComponentColor(2, 0, 40, 255) };
            var chairAssembly = new ObjAssemblyRequest(chairPieces, colors);
            var chairOutcome = FamilyFileWorkflow.CreateFamily(ui.Application, false, chairRequest, false, chairAssembly);
            Check(chairOutcome.Status == Result.Succeeded && File.Exists(chairOutcome.OutputPath),
                "Chair workflow created and reopened RFA: " + chairOutcome.Message);
            log.Add(new { chairRfa = chairOutcome.OutputPath, extraction = chairPieces });
            doc = ui.Application.OpenDocumentFile(chairOutcome.OutputPath!);
            ObjAssemblyImporter.ValidateSaved(doc, chairRequest, chairAssembly);
            var imported = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
            Check(imported.Length == 6 && imported.All(i => !i.IsLinked && !i.ViewSpecific), "Chair six embedded selectable imports");
            Check(imported.SelectMany(i => ImportedMaterials.InspectElement(doc, i)).Select(r => r.MaterialId).Distinct().Count() >= 2,
                "Chair independently colored material references");
            using (var tx = new Transaction(doc, "Shaded 3D validation view"))
            {
                tx.Start();
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = "Stage5D_B2_Shaded"; view.DisplayStyle = DisplayStyle.Shading;
                tx.Commit();
            }
            string visualRfa = Path.Combine(output, "Stage5D_B2_Chair_Visual.rfa");
            doc.SaveAs(visualRfa, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false); doc = null;
            log.Add(new { visualRfa });
            var secondRequest = chairRequest with { FamilyName = "Stage5D_B2_Chair_Second" };
            var secondAssembly = new ObjAssemblyRequest(chairPieces,
                [new ComponentColor(1, 0, 180, 40), new ComponentColor(2, 220, 180, 0)]);
            var secondOutcome = FamilyFileWorkflow.CreateFamily(ui.Application, false, secondRequest, false, secondAssembly);
            Check(secondOutcome.Status == Result.Succeeded && File.Exists(secondOutcome.OutputPath),
                "Second chair with distinct colors: " + secondOutcome.Message);

            string synthetic = Path.Combine(output, "two-offset-cubes.obj");
            File.WriteAllText(synthetic, TwoCubes(), new UTF8Encoding(false));
            string pieces = Path.Combine(output, "two-cube-pieces");
            ObjComponentExtractor.Extract(synthetic, pieces);
            foreach (var axis in new[] { ObjVerticalAxis.Z, ObjVerticalAxis.Y, ObjVerticalAxis.X })
            {
                var request = new ObjImportRequest(synthetic, ObjUnit.Meters, axis, 800,
                    "Stage5D_B2_" + axis, "Global transform check");
                var assembly = new ObjAssemblyRequest(pieces,
                    [new ComponentColor(1, 240, 20, 20), new ComponentColor(2, 0, 40, 255)]);
                var outcome = FamilyFileWorkflow.CreateFamily(ui.Application, false, request, false, assembly);
                Check(outcome.Status == Result.Succeeded && File.Exists(outcome.OutputPath),
                    $"Two-piece {axis} workflow: {outcome.Message}");
                doc = ui.Application.OpenDocumentFile(outcome.OutputPath!);
                ObjAssemblyImporter.ValidateSaved(doc, request, assembly);
                Check(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount() == 2,
                    $"Two-piece {axis} persisted elements and positions");
                doc.Close(false); doc = null;
                log.Add(new { axis = axis.ToString(), rfa = outcome.OutputPath });
            }
            var simple = FamilyFileWorkflow.CreateFamily(ui.Application, false,
                new ObjImportRequest(Path.Combine(root, "tests", "fixtures", "cube_500mm.obj"),
                    ObjUnit.Millimeters, ObjVerticalAxis.Z, 500, "Stage5D_B2_Simple", "Simple regression"));
            Check(simple.Status == Result.Succeeded, "Simple OBJ workflow regression: " + simple.Message);
            var parametric = FamilyFileWorkflow.CreateFamily(ui.Application, true);
            Check(parametric.Status == Result.Succeeded, "Parametric workflow regression: " + parametric.Message);

            doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            using (var tx = new Transaction(doc, "Load and place component families"))
            {
                tx.Start();
                foreach (var (path, x) in new[] { (visualRfa, 0), (secondOutcome.OutputPath!, 4) })
                {
                    Check(doc.LoadFamily(path, out var family), "Load assembled chair in RVT");
                    var symbol = (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
                    symbol.Activate(); doc.Regenerate();
                    var instance = doc.Create.NewFamilyInstance(new XYZ(x, 0, 0), symbol, StructuralType.NonStructural);
                    Check(instance is not null, "Place assembled chair instance");
                }
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = "Stage5D_B2_ProjectShaded"; view.DisplayStyle = DisplayStyle.Shading;
                tx.Commit();
            }
            string project = Path.Combine(output, "Stage5D_B2_Assembly.rvt");
            doc.SaveAs(project, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false); doc = null;
            doc = ui.Application.OpenDocumentFile(project);
            var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToArray();
            Check(instances.Length >= 2, "RVT reopened with two assembled chair families");
            HashSet<string> Colors(FamilyInstance instance)
            {
                var values = new HashSet<string>();
                void Walk(GeometryElement geometry)
                {
                    foreach (var item in geometry)
                    {
                        if (item is GeometryInstance nested) Walk(nested.GetInstanceGeometry());
                        if (item is Mesh mesh && doc.GetElement(mesh.MaterialElementId) is Material material)
                            values.Add($"{material.Color.Red},{material.Color.Green},{material.Color.Blue}");
                    }
                }
                Walk(instance.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
                return values;
            }
            var placedColors = instances.ToDictionary(i => i.Symbol.Family.Name, Colors);
            Check(placedColors.Values.Any(c => c.Contains("240,20,20") && c.Contains("0,40,255")),
                "First chair red and blue persist in reopened RVT");
            Check(placedColors.Values.Any(c => c.Contains("0,180,40") && c.Contains("220,180,0")),
                "Second chair green and yellow persist independently in reopened RVT");
            doc.Close(false); doc = null;
            log.Add(new { project });
        }
        catch (Exception ex) { log.Add(new { error = ex.ToString() }); }
        finally
        {
            try { doc?.Close(false); } catch { }
            File.WriteAllText(Path.Combine(output, "results.json"),
                JsonSerializer.Serialize(new { passed, log }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string TwoCubes()
    {
        var text = new StringBuilder();
        void Cube(double x, double y, double z)
        {
            int offset = text.ToString().Split('\n').Count(line => line.StartsWith("v "));
            foreach (var (dx, dy, dz) in new[] { (0,0,0),(1,0,0),(1,1,0),(0,1,0),
                (0,0,1),(1,0,1),(1,1,1),(0,1,1) })
                text.AppendLine($"v {x+dx} {y+dy} {z+dz}");
            int[][] faces = [[1,3,2],[1,4,3],[5,6,7],[5,7,8],[1,2,6],[1,6,5],
                [2,3,7],[2,7,6],[3,4,8],[3,8,7],[4,1,5],[4,5,8]];
            foreach (var f in faces) text.AppendLine($"f {f[0]+offset} {f[1]+offset} {f[2]+offset}");
        }
        Cube(0, 0, 0); Cube(3, 2, 1);
        return text.ToString();
    }
}
