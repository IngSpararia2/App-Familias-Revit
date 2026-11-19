using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BimFamilyAI.Core;
using BimFamilyAI.Revit;

// Explicit local integration runner. Register with a temporary .addin only for the test session.
public sealed class FaceRegionChecks : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    { application.Idling += Run; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private void Run(object? sender, IdlingEventArgs args)
    {
        var ui = (UIApplication)sender!;
        ui.Idling -= Run;
        var folder = new DirectoryInfo(Path.GetDirectoryName(typeof(FaceRegionChecks).Assembly.Location)!);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "BIMFamilyAI.sln"))) folder = folder.Parent;
        if (folder is null) throw new DirectoryNotFoundException("No se encontró la solución.");
        string root = folder.FullName;
        string output = Path.Combine(root, "artifacts", "stage5d-c2a-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        int passed = 0;
        var log = new List<object>();
        void Check(bool condition, string title)
        { if (!condition) throw new Exception(title); passed++; log.Add(new { pass = title }); }
        static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string Rgb(Material material) => $"{material.Color.Red},{material.Color.Green},{material.Color.Blue}";
        static HashSet<string> Colors(Document doc, ImportInstance imported) =>
            ImportedMaterials.InspectElement(doc, imported).Select(r => doc.GetElement(r.MaterialId))
                .OfType<Material>().Select(Rgb).ToHashSet();
        static string MeshSignature(Document doc)
        {
            var parts = new List<string>();
            void Walk(GeometryElement geometry)
            {
                foreach (var item in geometry)
                {
                    if (item is GeometryInstance nested) Walk(nested.GetInstanceGeometry());
                    if (item is Mesh mesh)
                    {
                        parts.Add(mesh.NumTriangles.ToString());
                        parts.AddRange(mesh.Vertices.Select(v => v.ToString()));
                    }
                }
            }
            foreach (ImportInstance imported in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
                Walk(imported.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));
        }
        Document? doc = null;
        try
        {
            string cube = Path.Combine(root, "tests", "fixtures", "cube_500mm.obj");
            string pieces = Path.Combine(output, "cube-pieces");
            var extracted = ObjComponentExtractor.Extract(cube, pieces);
            var cubeSha = Sha(cube);
            Check(extracted.Manifest.ComponentCount == 1 && extracted.Manifest.Components[0].Faces == 12,
                "controlled cube has one connected import and twelve faces");
            ObjRegionPlan CubePlan(ObjRegionColor top, ObjRegionColor rest) => new(1, cubeSha,
                [new("component_001", [new("top", "Dos caras superiores elegidas en fixture", [3, 4], top)], rest)]);
            string firstRegions = Path.Combine(output, "cube-regions-blue-gray");
            string secondRegions = Path.Combine(output, "cube-regions-red-green");
            var firstPlan = CubePlan(new(0, 40, 255), new(90, 90, 90));
            var secondPlan = CubePlan(new(240, 20, 20), new(0, 180, 40));
            var firstDerivation = ObjComponentExtractor.DeriveRegions(cube, pieces, firstPlan, firstRegions);
            ObjComponentExtractor.DeriveRegions(cube, pieces, secondPlan, secondRegions);
            Check(firstDerivation.Manifest.Components[0].Materials.Count == 2 &&
                ObjComponentExtractor.VerifyRegions(cube, pieces, firstRegions).PlanSha256 == firstDerivation.Manifest.PlanSha256,
                "face regions and geometric provenance verified before Revit");
            var requestA = new ObjImportRequest(cube, ObjUnit.Millimeters, ObjVerticalAxis.Z, 500,
                "Stage5D_C2A_BlueGray", "Two face materials in one import");
            var requestB = requestA with { FamilyName = "Stage5D_C2A_RedGreen" };
            var assemblyA = new ObjAssemblyRequest(pieces, [], firstRegions);
            var assemblyB = new ObjAssemblyRequest(pieces, [], secondRegions);
            var resultA = FamilyFileWorkflow.CreateFamily(ui.Application, false, requestA, false, assemblyA);
            Check(resultA.Status == Result.Succeeded && File.Exists(resultA.OutputPath),
                "first RFA created and internally reopened: " + resultA.Message);
            var resultB = FamilyFileWorkflow.CreateFamily(ui.Application, false, requestB, false, assemblyB);
            Check(resultB.Status == Result.Succeeded && File.Exists(resultB.OutputPath),
                "second RFA with other colors created: " + resultB.Message);
            doc = ui.Application.OpenDocumentFile(resultA.OutputPath!);
            ObjAssemblyImporter.ValidateSaved(doc, requestA, assemblyA);
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
            Check(imports.Length == 1 && !imports[0].IsLinked && !imports[0].ViewSpecific,
                "one embedded ImportInstance contains both regions");
            var identity = ObjAssemblyImporter.TryReadIdentity(imports[0]);
            Check(identity is not null && identity.ComponentId == "component_001" &&
                identity.PieceSha256 == extracted.Manifest.Components[0].Sha256,
                "original 5D-B2 identity and geometric hash preserved");
            var labels = ComponentLabels.ReadAll(doc);
            Check(labels.Count == 1 && labels[0].ComponentId == "component_001",
                "5D-C1 manual labeling can address the region-colored import");
            Check(Colors(doc, imports[0]).SetEquals(["0,40,255", "90,90,90"]),
                "two independent RGB colors inside one ImportInstance");
            string geometryA = MeshSignature(doc);
            using (var tx = new Transaction(doc, "Vista Sombreado de regiones"))
            {
                tx.Start();
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = "Stage5D_C2A_Shaded"; view.DisplayStyle = DisplayStyle.Shading;
                tx.Commit();
            }
            string visual = Path.Combine(output, "Stage5D_C2A_BlueGray_Visual.rfa");
            doc.SaveAs(visual, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false); doc = null;
            doc = ui.Application.OpenDocumentFile(visual);
            ObjAssemblyImporter.ValidateSaved(doc, requestA, assemblyA);
            Check(MeshSignature(doc) == geometryA && Colors(doc,
                new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single())
                .SetEquals(["0,40,255", "90,90,90"]),
                "visual RFA reopens with same mesh and two RGB references");
            doc.Close(false); doc = null;
            log.Add(new { visualRfa = visual, firstRfa = resultA.OutputPath, secondRfa = resultB.OutputPath });

            // Real chair: face numbers are an explicit synthetic technical sample, never a cushion classification.
            string chair = Path.Combine(root, "artifacts", "stage5probe", "chair.obj");
            if (File.Exists(chair))
            {
                string chairPieces = Path.Combine(output, "chair-pieces");
                var chairExtraction = ObjComponentExtractor.Extract(chair, chairPieces);
                Check(chairExtraction.Manifest.ComponentCount == 6 &&
                    chairExtraction.Manifest.Components.Sum(p => p.Faces) == 4172,
                    "real chair extraction keeps six components and 4172 faces");
                var selectedFaces = chairExtraction.Manifest.Components.Single(p => p.Id == "component_001")
                    .OriginalFaceNumbers.Take(40).ToArray();
                var chairPlan = new ObjRegionPlan(1, Sha(chair), [new("component_001",
                    [new("technical-a", "Primeros 20 IDs de cara originales de component_001", selectedFaces.Take(20).ToArray(), new(0, 40, 255)),
                     new("technical-b", "Siguientes 20 IDs de cara originales de component_001", selectedFaces.Skip(20).ToArray(), new(240, 20, 20))],
                    new(90, 90, 90))]);
                string chairRegions = Path.Combine(output, "chair-regions");
                ObjComponentExtractor.DeriveRegions(chair, chairPieces, chairPlan, chairRegions);
                var chairRequest = new ObjImportRequest(chair, ObjUnit.Millimeters, ObjVerticalAxis.Y, 750,
                    "Stage5D_C2A_Chair_Technical", "Synthetic face regions, not cushion identification", true);
                var chairAssembly = new ObjAssemblyRequest(chairPieces, [new ComponentColor(3, 0, 180, 40)], chairRegions);
                var chairOutcome = FamilyFileWorkflow.CreateFamily(ui.Application, false, chairRequest, false, chairAssembly);
                Check(chairOutcome.Status == Result.Succeeded && File.Exists(chairOutcome.OutputPath),
                    "real chair with technical regions created and reopened: " + chairOutcome.Message);
                doc = ui.Application.OpenDocumentFile(chairOutcome.OutputPath!);
                ObjAssemblyImporter.ValidateSaved(doc, chairRequest, chairAssembly);
                var chairImports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
                var first = chairImports.Single(i => ObjAssemblyImporter.TryReadIdentity(i)?.ComponentId == "component_001");
                Check(chairImports.Length == 6 && Colors(doc, first).SetEquals(["0,40,255", "240,20,20", "90,90,90"]),
                    "real chair has six original pieces and three colors in component 001");
                Check(ComponentLabels.ReadAll(doc).Count == 6,
                    "chair identities remain available to 5D-C1");
                doc.Close(false); doc = null;
                log.Add(new { chairRfa = chairOutcome.OutputPath, chairRegions });
            }
            else log.Add(new { skipped = "chair.obj local not available" });

            // Targeted regressions of old paths and independent explicit OBJ materials.
            var simple = FamilyFileWorkflow.CreateFamily(ui.Application, false,
                requestA with { FamilyName = "Stage5D_C2A_Simple" });
            Check(simple.Status == Result.Succeeded, "simple OBJ workflow unchanged: " + simple.Message);
            var native = FamilyFileWorkflow.CreateFamily(ui.Application, false);
            Check(native.Status == Result.Succeeded, "native 500 mm cube workflow unchanged: " + native.Message);
            var parametric = FamilyFileWorkflow.CreateFamily(ui.Application, true);
            Check(parametric.Status == Result.Succeeded, "parametric workflow unchanged: " + parametric.Message);
            string olderLabels = Path.Combine(root, "artifacts", "stage5d-c1-20261118-000937", "Stage5D_C1_Chair_Labeled.rfa");
            if (File.Exists(olderLabels))
            {
                doc = ui.Application.OpenDocumentFile(olderLabels);
                var existing = ComponentLabels.ReadAll(doc).ToDictionary(i => i.ComponentId);
                Check(existing.Count == 6 && existing["component_001"].Name == "Estructura y asiento" &&
                    existing["component_003"].Name == "Respaldo curvo",
                    "existing 5D-C1 labels remain readable without migration");
                doc.Close(false); doc = null;
            }
            string explicitObj = Path.Combine(root, "tests", "fixtures", "material_regions.obj");
            doc = ui.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft");
            var templateMaterials = new FilteredElementCollector(doc).OfClass(typeof(Material)).ToElementIds().ToHashSet();
            var oldRequest = new ObjImportRequest(explicitObj, ObjUnit.Millimeters, ObjVerticalAxis.Z, 500,
                "Stage5D_C2A_Old5C", "5C regression");
            ObjFamilyImporter.Import(doc, oldRequest);
            var editable = ImportedMaterials.EditableMaterials(doc);
            Check(editable.Count == 2, "5C explicit material regions still visible");
            ImportedMaterials.ApplyMultiple(doc,
                [new(editable[0].Id, editable[0].Name, 240, 20, 20),
                 new(editable[1].Id, editable[1].Name, 0, 40, 255)], templateMaterials);
            Check(ImportedMaterials.Snapshot(doc).Count == 2, "5C separate material edit unchanged");
            doc.Close(false); doc = null;

            doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            using (var tx = new Transaction(doc, "Load two face-region families"))
            {
                tx.Start();
                foreach (var (path, x) in new[] { (visual, 0.0), (resultB.OutputPath!, 4.0) })
                {
                    Check(doc.LoadFamily(path, out var family), "load region family into RVT");
                    var symbol = (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
                    symbol.Activate(); doc.Regenerate();
                    Check(doc.Create.NewFamilyInstance(new XYZ(x, 0, 0), symbol,
                        StructuralType.NonStructural) is not null, "place region family instance");
                }
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = "Stage5D_C2A_ProjectShaded"; view.DisplayStyle = DisplayStyle.Shading;
                tx.Commit();
            }
            string project = Path.Combine(output, "Stage5D_C2A_Regions.rvt");
            doc.SaveAs(project, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false); doc = null;
            doc = ui.Application.OpenDocumentFile(project);
            var placed = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol.Family.Name.StartsWith("Stage5D_C2A_", StringComparison.Ordinal)).ToArray();
            HashSet<string> PlacedColors(FamilyInstance instance)
            {
                var result = new HashSet<string>();
                void Walk(GeometryElement geometry)
                {
                    foreach (var item in geometry)
                    {
                        if (item is GeometryInstance nested) Walk(nested.GetInstanceGeometry());
                        if (item is Mesh mesh && doc.GetElement(mesh.MaterialElementId) is Material material)
                            result.Add(Rgb(material));
                    }
                }
                Walk(instance.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
                return result;
            }
            var colors = placed.ToDictionary(i => i.Symbol.Family.Name, PlacedColors);
            Check(colors.Count == 2 && colors.Values.Any(c => c.SetEquals(["0,40,255", "90,90,90"])) &&
                colors.Values.Any(c => c.SetEquals(["240,20,20", "0,180,40"])),
                "two families keep independent region colors after RVT reopen");
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
}
