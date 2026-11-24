using System.Numerics;
using BimFamilyAI.Core;

internal static class ObjRegionEditorChecks
{
    internal static int Run(string directory)
    {
        int passed = 0;
        void Check(bool valid, string title)
        {
            if (!valid) throw new Exception(title);
            Console.WriteLine("PASS editor " + title);
            passed++;
        }
        void Reject(Action action, string title)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { Check(true, title); return; }
            throw new Exception("Expected rejection: " + title);
        }

        string source = Path.Combine(directory, "editor.obj");
        const string geometry = """
            v 0 0 0
            v 2 0 0
            v 2 2 0
            v 1 1 0
            v 0 2 0
            v 0 0 1
            v 0 0 0.5
            v 2 0 0.5
            v 2 2 0.5
            v 0 2 0.5
            f -10 -9 -8 -7 -6
            f 2 1 6
            f 7 8 9 10
            """;
        File.WriteAllText(source, geometry);
        var extracted = ObjComponentExtractor.Extract(source, Path.Combine(directory, "editor-pieces"));
        var preview = ObjComponentExtractor.ReadRegionPreview(source, extracted.OutputDirectory, ObjVerticalAxis.Z);
        Check(preview.Triangles.Count(t => t.OriginalFace == 1) == 3 &&
              preview.Triangles.Count(t => t.OriginalFace == 2) == 1 &&
              preview.Triangles.Count(t => t.OriginalFace == 3) == 2, "negative indices and concave polygon keep original face numbers");
        var centerRay = new Vector3(.25f, -.25f, 2);
        Check(preview.Pick(centerRay, -Vector3.UnitZ, new HashSet<string>())?.OriginalFace == 3, "foremost component occludes rear face");
        var hidden = new HashSet<string> { "component_002" };
        Check(preview.Pick(centerRay, -Vector3.UnitZ, hidden)?.OriginalFace == 1, "hidden foreground permits picking rear face");
        Check(preview.Pick(new(0,.4f,2), -Vector3.UnitZ, hidden) is null, "concave notch does not select phantom triangle");
        var rotated = ObjComponentExtractor.ReadRegionPreview(source, extracted.OutputDirectory, ObjVerticalAxis.Y);
        Check(rotated.Pick(new(.25f,-2,.25f), Vector3.UnitY, new HashSet<string>())?.OriginalFace == 3,
            "Y-up orientation preserves front-face identity from rotated viewpoint");
        var selected = new HashSet<int>();
        ObjRegionEdits.Select(selected, 1, false);
        Check(selected.SetEquals([1]), "single click selection");
        ObjRegionEdits.Select(selected, 2, true);
        Check(selected.SetEquals([1, 2]), "modifier adds face");
        ObjRegionEdits.Select(selected, 2, true);
        Check(selected.SetEquals([1]), "modifier removes face");
        Check(preview.Grow(selected, 25, new HashSet<int>()).SetEquals([1]), "angular threshold stops at right angle");
        Check(preview.Grow(selected, 100, new HashSet<int>()).SetEquals([1, 2]), "growth shares an edge and ignores overlapping components");
        Check(preview.Grow(selected, 100, new HashSet<int> { 2 }).SetEquals([1]), "growth stops at a claimed face");

        var edits = new ObjRegionEdits(extracted.Manifest.SourceSha256);
        var blue = new ObjRegionColor(0, 80, 220);
        var green = new ObjRegionColor(20, 170, 90);
        edits.Put("component_001", null, "Cojín", blue, selected);
        string id = edits.Get("component_001").Regions.Single().RegionId;
        Reject(() => edits.Put("component_001", null, "Otra", green, [1]), "overlap rejected");
        Reject(() => edits.Put("component_001", null, "COJÍN", green, [2]), "duplicate name rejected");
        Reject(() => edits.Put("component_001", null, "Vacía", green, []), "empty selection rejected");
        edits.Put("component_001", id, "Tapizado", green, [2]);
        edits.Put("component_001", null, "Estructura", blue, [1]);
        edits.Put("component_002", null, "Otra componente", green, [3]);
        string saved = Path.Combine(directory, "editor-plan.json");
        var validated = ObjComponentExtractor.ValidateRegionPlan(source, extracted.OutputDirectory, edits.Snapshot());
        ObjComponentExtractor.SaveNewRegionPlan(saved, validated);
        var recovered = ObjComponentExtractor.ReadRegionPlan(saved, source, extracted.OutputDirectory);
        Check(recovered.Components.Count == 2 && recovered.Components[0].Regions.Count == 2 &&
            recovered.Components[0].Regions.Any(r => r.RegionId == id && r.Name == "Tapizado" &&
                r.Color == green && r.OriginalFaceNumbers.SequenceEqual([2])), "round trip preserves edit, original ID, RGB and multiple parts");
        Reject(() => ObjComponentExtractor.SaveNewRegionPlan(saved, validated), "cannot overwrite an existing plan");
        var derived = ObjComponentExtractor.DeriveRegions(source, extracted.OutputDirectory, recovered, Path.Combine(directory, "editor-derived"));
        Check(ObjComponentExtractor.VerifyRegions(source, extracted.OutputDirectory, derived.OutputDirectory).Components.Count == 2,
            "C2A verifies all coordinates, corners and colors of the saved editor plan");
        edits.Delete("component_001", id);
        Check(edits.Get("component_001").Regions.Single().Name == "Estructura" && File.ReadAllText(source) == geometry &&
            ObjComponentExtractor.ReadRegionPlan(saved, source, extracted.OutputDirectory).Components[0].Regions.Count == 2,
            "discarded edits leave source and saved plan unchanged");
        var fallbackOnly = new ObjRegionEdits(extracted.Manifest.SourceSha256);
        fallbackOnly.SetFallback("component_002", green);
        string fallbackFile = Path.Combine(directory, "editor-fallback.json");
        ObjComponentExtractor.SaveNewRegionPlan(fallbackFile,
            ObjComponentExtractor.ValidateRegionPlan(source, extracted.OutputDirectory, fallbackOnly.Snapshot()));
        var fallbackPlan = ObjComponentExtractor.ReadRegionPlan(fallbackFile, source, extracted.OutputDirectory);
        var fallbackPiece = ObjComponentExtractor.DeriveRegions(source, extracted.OutputDirectory, fallbackPlan,
            Path.Combine(directory, "editor-fallback-derived")).Manifest.Components.Single();
        Check(fallbackPiece.ComponentId == "component_002" && fallbackPiece.Materials.Single().IsFallback &&
            fallbackPiece.Materials.Single().Color == green, "fallback alone persists and derives exactly the chosen RGB");
        fallbackOnly.Put("component_001", null, "Temporal", blue, [1]);
        fallbackOnly.Delete("component_001", fallbackOnly.Get("component_001").Regions.Single().RegionId);
        Check(fallbackOnly.Snapshot().Components.Single().ComponentId == "component_002",
            "deleting the only region does not activate the implicit default fallback");
        fallbackOnly.SetFallback("component_002", null);
        Check(fallbackOnly.Snapshot().Components.Count == 0, "clearing an explicit fallback removes the fallback-only edit");
        string other = Path.Combine(directory, "editor-other.obj");
        File.WriteAllText(other, geometry + "\n# other");
        var otherExtraction = ObjComponentExtractor.Extract(other, Path.Combine(directory, "editor-other-pieces"));
        Reject(() => ObjComponentExtractor.ReadRegionPlan(saved, other, otherExtraction.OutputDirectory), "incompatible plan rejected");
        File.AppendAllText(source, "\n# source changed");
        Reject(() => ObjComponentExtractor.ReadRegionPlan(saved, source, extracted.OutputDirectory), "changed source hash rejected");

        string chair = Path.GetFullPath("artifacts/stage5probe/chair.obj");
        if (File.Exists(chair))
        {
            var chairParts = ObjComponentExtractor.Extract(chair, Path.Combine(directory, "editor-chair"));
            var chairPreview = ObjComponentExtractor.ReadRegionPreview(chair, chairParts.OutputDirectory, ObjVerticalAxis.Y);
            Check(chairPreview.Triangles.Select(t => t.OriginalFace).Distinct().Count() == 4172 &&
                  chairPreview.Triangles.Select(t => t.ComponentId).Distinct().Count() == 6, "real chair preview maps its 4172 faces and six components");
        }
        return passed;
    }
}
