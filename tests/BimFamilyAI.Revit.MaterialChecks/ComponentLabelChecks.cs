using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BimFamilyAI.Revit;

// Temporary API runner: loaded only by an explicit local test manifest, never by the installer.
public sealed class ComponentLabelChecks : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    { application.Idling += Run; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private void Run(object? sender, IdlingEventArgs args)
    {
        var ui = (UIApplication)sender!;
        ui.Idling -= Run;
        var folder = new DirectoryInfo(Path.GetDirectoryName(typeof(ComponentLabelChecks).Assembly.Location)!);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "BIMFamilyAI.sln"))) folder = folder.Parent;
        if (folder is null) throw new DirectoryNotFoundException("No se encontró la solución.");
        string root = folder.FullName;
        string output = Path.Combine(root, "artifacts", "stage5d-c1-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        int passed = 0;
        var log = new List<object>();
        void Check(bool condition, string title)
        { if (!condition) throw new Exception(title); passed++; log.Add(new { pass = title }); }
        void Reject<T>(Action action, string title) where T : Exception
        { try { action(); throw new Exception("Expected rejection: " + title); }
          catch (T) { Check(true, title); } }
        try
        {
            string original = Path.Combine(root, "artifacts", "stage5d-b2-20261117-210119", "Stage5D_B2_Chair_Visual.rfa");
            string labeled = Path.Combine(output, "Stage5D_C1_Chair_Labeled.rfa");
            File.Copy(original, labeled, false);
            var uiDoc = ui.OpenAndActivateDocument(labeled);
            var doc = uiDoc.Document;
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>().ToDictionary(i => ObjAssemblyImporter.TryReadIdentity(i)?.ComponentId
                    ?? throw new Exception("Import without 5D-B2 identity"));
            Check(imports.Count == 6, "existing 5D-B2 chair has six stable identities");
            var identities = imports.ToDictionary(p => p.Key, p => ObjAssemblyImporter.TryReadIdentity(p.Value)!);
            var boxes = imports.ToDictionary(p => p.Key, p =>
                (Min: p.Value.get_BoundingBox(null).Min, Max: p.Value.get_BoundingBox(null).Max));
            var colors = imports.ToDictionary(p => p.Key, p => ImportedMaterials.InspectElement(doc, p.Value)
                .Select(r => doc.GetElement(r.MaterialId) is Material m
                    ? $"{m.Name}:{m.Color.Red},{m.Color.Green},{m.Color.Blue}" : "none")
                .OrderBy(v => v).ToArray());
            Check(ComponentLabels.ReadAll(doc).All(i => i.Name is null), "older RFA readable without label schema");

            uiDoc.Selection.SetElementIds([]);
            Reject<InvalidOperationException>(() => ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)),
                "empty selection rejected");
            uiDoc.Selection.SetElementIds([imports["component_001"].Id, imports["component_003"].Id]);
            Reject<InvalidOperationException>(() => ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)),
                "multiple selection rejected");
            var foreign = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).FirstOrDefault();
            if (foreign is not null)
            {
                uiDoc.Selection.SetElementIds([foreign.Id]);
                Reject<InvalidOperationException>(() => ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)),
                    "non import rejected");
            }
            uiDoc.Selection.SetElementIds([imports["component_001"].Id]);
            var first = ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)).Selected!;
            Check(first.ComponentId == "component_001" && first.Name is null, "selected element resolves technical identity");
            Reject<InvalidOperationException>(() => ComponentLabels.Execute(ui,
                new(ComponentLabelAction.Set,"incorrect-id",first.ComponentId,"Pieza alfa")),
                "stale selection cannot be labeled");
            foreach (string invalid in new[] { "", "   ", "Linea\nNueva", new string('x',121) })
                Reject<ArgumentException>(() => ComponentLabels.ValidateName(invalid), "invalid label rejected");
            var setFirst = ComponentLabels.Execute(ui,
                new(ComponentLabelAction.Set,first.ElementUniqueId,first.ComponentId,"Pieza alfa"));
            Check(setFirst.Selected!.Name == "Pieza alfa", "assign label to component 001");
            var renamed = ComponentLabels.Execute(ui,
                new(ComponentLabelAction.Set,first.ElementUniqueId,first.ComponentId,"Estructura y asiento"));
            Check(renamed.Selected!.Name == "Estructura y asiento", "rename selected component");

            uiDoc.Selection.SetElementIds([imports["component_003"].Id]);
            var third = ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)).Selected!;
            var setThird = ComponentLabels.Execute(ui,
                new(ComponentLabelAction.Set,third.ElementUniqueId,third.ComponentId,"Respaldo"));
            Check(setThird.Selected!.Name == "Respaldo", "second component assigned independently");
            uiDoc.Selection.SetElementIds([imports["component_002"].Id]);
            var second = ComponentLabels.Execute(ui,new(ComponentLabelAction.Selected)).Selected!;
            ComponentLabels.Execute(ui,new(ComponentLabelAction.Set,second.ElementUniqueId,second.ComponentId,"Respaldo"));
            Check(ComponentLabels.ReadAll(doc).Count(i => i.Name == "Respaldo") == 2,
                "duplicate manual names preserve distinct IDs");
            var cleared = ComponentLabels.Execute(ui,
                new(ComponentLabelAction.Clear,second.ElementUniqueId,second.ComponentId));
            Check(cleared.Selected!.Name is null && ObjAssemblyImporter.TryReadIdentity(imports["component_002"]) == identities["component_002"],
                "remove label without deleting identity");

            foreach (var (id, element) in imports)
            {
                var box = element.get_BoundingBox(null);
                Check(box.Min.IsAlmostEqualTo(boxes[id].Min) && box.Max.IsAlmostEqualTo(boxes[id].Max) &&
                    ObjAssemblyImporter.TryReadIdentity(element) == identities[id],
                    $"geometry and identity unchanged {id}");
                var now = ImportedMaterials.InspectElement(doc, element)
                    .Select(r => doc.GetElement(r.MaterialId) is Material m
                        ? $"{m.Name}:{m.Color.Red},{m.Color.Green},{m.Color.Blue}" : "none")
                    .OrderBy(v => v).ToArray();
                Check(now.SequenceEqual(colors[id]), $"materials unchanged {id}");
            }
            doc.Save();
            string persistedCopy = Path.Combine(output, "Stage5D_C1_Persistence_Copy.rfa");
            File.Copy(labeled, persistedCopy, false);
            Document? reopenedDocument = null;
            try
            {
                reopenedDocument = ui.Application.OpenDocumentFile(persistedCopy);
                var reopened = ComponentLabels.ReadAll(reopenedDocument).ToDictionary(i => i.ComponentId);
                Check(reopened.Count == 6 && reopened["component_001"].Name == "Estructura y asiento" &&
                    reopened["component_003"].Name == "Respaldo" && reopened["component_002"].Name is null,
                    "two labels persist on correct components after reopening saved copy");
                Check(new FilteredElementCollector(reopenedDocument).OfClass(typeof(ImportInstance)).GetElementCount() == 6,
                    "reopened RFA keeps six import instances");
                foreach (var element in new FilteredElementCollector(reopenedDocument).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                {
                    var id = ObjAssemblyImporter.TryReadIdentity(element)?.ComponentId
                        ?? throw new Exception("Reopened import lost identity");
                    var box = element.get_BoundingBox(null);
                    Check(box.Min.IsAlmostEqualTo(boxes[id].Min) && box.Max.IsAlmostEqualTo(boxes[id].Max) &&
                        ObjAssemblyImporter.TryReadIdentity(element) == identities[id],
                        $"reopened geometry and identity unchanged {id}");
                    var currentColors = ImportedMaterials.InspectElement(reopenedDocument, element)
                        .Select(r => reopenedDocument.GetElement(r.MaterialId) is Material m
                            ? $"{m.Name}:{m.Color.Red},{m.Color.Green},{m.Color.Blue}" : "none")
                        .OrderBy(v => v).ToArray();
                    Check(currentColors.SequenceEqual(colors[id]), $"reopened materials unchanged {id}");
                }
            }
            finally { reopenedDocument?.Close(false); }
            log.Add(new { labeledRfa = labeled });

            string simple = Path.Combine(root, "artifacts", "stage5a-20261116-145711", "Stage5A_Original.rfa");
            Document? other = null;
            try
            {
                other = ui.Application.OpenDocumentFile(simple);
                Reject<InvalidOperationException>(() => ComponentLabels.ReadAll(other),
                    "older simple RFA without component identity rejected");
            }
            finally { other?.Close(false); other = null; }
            string project = Path.Combine(root, "artifacts", "stage5d-b2-20261117-210119", "Stage5D_B2_Assembly.rvt");
            try
            {
                other = ui.Application.OpenDocumentFile(project);
                Reject<InvalidOperationException>(() => ComponentLabels.ReadAll(other),
                    "project RVT rejected for family labeling");
            }
            finally { other?.Close(false); }
        }
        catch (Exception ex) { log.Add(new { error = ex.ToString() }); }
        finally
        {
            File.WriteAllText(Path.Combine(output, "results.json"),
                JsonSerializer.Serialize(new { passed, log }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
