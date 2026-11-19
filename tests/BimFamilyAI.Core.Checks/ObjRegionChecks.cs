using System.Security.Cryptography;
using BimFamilyAI.Core;

internal static class ObjRegionChecks
{
    internal static int Run(string fixtures, string directory)
    {
        int passed = 0;
        void Check(bool ok, string title)
        { if (!ok) throw new Exception(title); Console.WriteLine("PASS regions " + title); passed++; }
        void Reject(string title, string source, string extraction, ObjRegionPlan plan)
        {
            string output = Path.Combine(directory, "regions-reject-" + Guid.NewGuid().ToString("N"));
            try { ObjComponentExtractor.DeriveRegions(source, extraction, plan, output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Check(!Directory.Exists(output), title + " no partial publication"); return; }
            throw new Exception("Accepted invalid region plan: " + title);
        }
        static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string[] Geometry(string path) => File.ReadAllLines(path)
            .Where(line => line.StartsWith("v ", StringComparison.Ordinal) ||
                line.StartsWith("vt ", StringComparison.Ordinal) ||
                line.StartsWith("vn ", StringComparison.Ordinal) ||
                line.StartsWith("f ", StringComparison.Ordinal) ||
                line.StartsWith("o ", StringComparison.Ordinal) ||
                line.StartsWith("g ", StringComparison.Ordinal) ||
                line.StartsWith("s ", StringComparison.Ordinal)).ToArray();

        string cube = Path.Combine(fixtures, "cube_500mm.obj");
        string cubeSha = Sha(cube);
        var extracted = ObjComponentExtractor.Extract(cube, Path.Combine(directory, "regions-cube-extracted"));
        string piece = Path.Combine(extracted.OutputDirectory, "component_001.obj");
        ObjRegionPlan Plan(params ObjFaceRegion[] regions) => new(1, cubeSha,
            [new("component_001", regions, new ObjRegionColor(90, 90, 90))]);
        var red = new ObjRegionColor(240, 20, 20);
        var blue = new ObjRegionColor(0, 40, 255);
        var plan = Plan(new("seat", "Selección manual de prueba", [1, 12], red),
            new("edge", null, [2], blue));
        string output = Path.Combine(directory, "regions-cube-derived");
        var derived = ObjComponentExtractor.DeriveRegions(cube, extracted.OutputDirectory, plan, output);
        var verified = ObjComponentExtractor.VerifyRegions(cube, extracted.OutputDirectory, output);
        Check(verified.SourceSha256 == derived.Manifest.SourceSha256 &&
            verified.PlanSha256 == derived.Manifest.PlanSha256 &&
            verified.Components[0].DerivedSha256 == derived.Manifest.Components[0].DerivedSha256,
            "published derivation verified");
        Check(derived.Manifest.Components.Count == 1 && derived.Manifest.Components[0].Materials.Count == 3,
            "one connected component has two regions and explicit fallback");
        Check(Geometry(piece).SequenceEqual(Geometry(Path.Combine(output, "component_001.obj"))),
            "all geometry records, polygon corners and order byte-equivalent as text");
        Check(ObjStructure.Analyze(Path.Combine(output, "component_001.obj")).ByMaterial.Sum(x => x.Faces) == 12,
            "every face assigned exactly once");
        Check(Sha(cube) == cubeSha && Sha(piece) == extracted.Manifest.Components[0].Sha256,
            "source and geometric piece untouched");
        Check(verified.Components[0].OriginalPieceSha256 == extracted.Manifest.Components[0].Sha256 &&
            verified.Components[0].DerivedSha256 != verified.Components[0].OriginalPieceSha256,
            "original and derived hashes kept distinct");
        Check(!File.ReadAllText(Path.Combine(output, "manifest.json")).Contains(Path.GetFullPath(directory)) &&
            !File.ReadAllText(Path.Combine(output, "plan.json")).Contains(Path.GetFullPath(directory)),
            "manifests contain no private absolute paths");
        var again = ObjComponentExtractor.DeriveRegions(cube, extracted.OutputDirectory, plan,
            Path.Combine(directory, "regions-cube-repeated"));
        Check(again.Manifest.Components[0].DerivedSha256 == derived.Manifest.Components[0].DerivedSha256 &&
            File.ReadAllBytes(Path.Combine(output, "manifest.json"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(again.OutputDirectory, "manifest.json"))),
            "repeated output deterministic");
        try { ObjComponentExtractor.DeriveRegions(cube, extracted.OutputDirectory, plan, output); throw new Exception("Overwrite accepted"); }
        catch (IOException) { Check(true, "existing output not overwritten"); }

        var one = new ObjRegionPlan(1, cubeSha, [new("component_001",
            [new("all", null, Enumerable.Range(1, 12).ToArray(), red)])]);
        var single = ObjComponentExtractor.DeriveRegions(cube, extracted.OutputDirectory, one,
            Path.Combine(directory, "regions-one-material"));
        Check(single.Manifest.Components[0].Materials.Count == 1,
            "all selected faces need no fallback and use one material");
        Reject("missing fallback", cube, extracted.OutputDirectory,
            new(1, cubeSha, [new("component_001", [new("part", null, [1], blue)])]));
        Reject("overlapping faces", cube, extracted.OutputDirectory,
            Plan(new("a", null, [1], red), new("b", null, [1], blue)));
        Reject("duplicate face in region", cube, extracted.OutputDirectory,
            Plan(new ObjFaceRegion("a", null, [1, 1], red)));
        Reject("out of range face", cube, extracted.OutputDirectory,
            Plan(new ObjFaceRegion("a", null, [500001], red)));
        Reject("invalid region ID", cube, extracted.OutputDirectory,
            Plan(new ObjFaceRegion("../escape", null, [1], red)));
        Reject("empty region", cube, extracted.OutputDirectory,
            Plan(new ObjFaceRegion("empty", null, [], red)));
        Reject("wrong source hash", cube, extracted.OutputDirectory,
            plan with { SourceSha256 = new string('0', 64) });

        string existing = Path.Combine(fixtures, "material_regions.obj");
        var existingExtraction = ObjComponentExtractor.Extract(existing,
            Path.Combine(directory, "regions-existing-extracted"));
        var existingPlan = new ObjRegionPlan(1, Sha(existing), [new("component_001",
            [new("manual-blue", null, [1, 9], blue)])]);
        var existingDerived = ObjComponentExtractor.DeriveRegions(existing, existingExtraction.OutputDirectory,
            existingPlan, Path.Combine(directory, "regions-existing-derived"));
        var structure = ObjStructure.Analyze(Path.Combine(existingDerived.OutputDirectory, "component_001.obj"));
        Check(existingDerived.Manifest.Components[0].Materials.Count == 1 && structure.ByMaterial.Count == 3,
            "existing Red/Green MTL preserved alongside a noncontiguous blue region");
        Check(File.ReadAllBytes(Path.Combine(existingDerived.OutputDirectory, "multi.mtl"))
            .SequenceEqual(File.ReadAllBytes(Path.Combine(existingExtraction.OutputDirectory, "multi.mtl"))),
            "existing MTL copied without alteration");
        Check(Geometry(Path.Combine(existingExtraction.OutputDirectory, "component_001.obj"))
            .SequenceEqual(Geometry(Path.Combine(existingDerived.OutputDirectory, "component_001.obj"))),
            "existing groups and shared-vertex geometry preserved");
        var alteredMtl = Path.Combine(existingDerived.OutputDirectory, "multi.mtl");
        File.AppendAllText(alteredMtl, "#tampered\n");
        try { ObjComponentExtractor.VerifyRegions(existing, existingExtraction.OutputDirectory, existingDerived.OutputDirectory); throw new Exception("Tampered MTL accepted"); }
        catch (IOException) { Check(true, "tampered copied MTL rejected"); }

        string negative = Path.Combine(directory, "regions-negative.obj");
        File.WriteAllText(negative, "v 0 0 0\nv 2 0 0\nv 0 2 1\nv 2 2 1\n" +
            "vt 0 0\nvt 1 0\nvt 0 1\nvt 1 1\nvn 0 0 1\n" +
            "f -4/-4/-1 -3/-3/-1 -2/-2/-1\nf -3/-3/-1 -1/-1/-1 -2/-2/-1\n");
        var negativeExtraction = ObjComponentExtractor.Extract(negative,
            Path.Combine(directory, "regions-negative-extracted"));
        var negativePlan = new ObjRegionPlan(1, Sha(negative), [new("component_001",
            [new("triangle", null, [1], red)], blue)]);
        var negativeDerived = ObjComponentExtractor.DeriveRegions(negative, negativeExtraction.OutputDirectory,
            negativePlan, Path.Combine(directory, "regions-negative-derived"));
        Check(Geometry(Path.Combine(negativeExtraction.OutputDirectory, "component_001.obj"))
            .SequenceEqual(Geometry(Path.Combine(negativeDerived.OutputDirectory, "component_001.obj"))),
            "negative source indices, UV and normals preserved after B1 remap");

        string polygon = Path.Combine(directory, "regions-polygon.obj");
        File.WriteAllText(polygon, "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\n" +
            "f 1 2 3 4\nf 1 4 5\n");
        var polygonExtraction = ObjComponentExtractor.Extract(polygon,
            Path.Combine(directory, "regions-polygon-extracted"));
        var polygonPlan = new ObjRegionPlan(1, Sha(polygon), [new("component_001",
            [new("quad", null, [1], red)], blue)]);
        var polygonDerived = ObjComponentExtractor.DeriveRegions(polygon, polygonExtraction.OutputDirectory,
            polygonPlan, Path.Combine(directory, "regions-polygon-derived"));
        Check(ObjComponentExtractor.VerifyRegions(polygon, polygonExtraction.OutputDirectory,
            polygonDerived.OutputDirectory).Components.Count == 1 &&
            Geometry(Path.Combine(polygonExtraction.OutputDirectory, "component_001.obj"))
                .SequenceEqual(Geometry(Path.Combine(polygonDerived.OutputDirectory, "component_001.obj"))),
            "quadrilateral and triangle retain original corners and orientation");

        string two = Path.Combine(directory, "regions-two-components.obj");
        File.WriteAllText(two, "v 0 0 0\nv 1 0 0\nv 0 1 1\nv 1 1 1\n" +
            "v 4 0 0\nv 5 0 0\nv 4 1 1\nf 1 2 3\nf 2 4 3\nf 5 6 7\n");
        var twoExtraction = ObjComponentExtractor.Extract(two,
            Path.Combine(directory, "regions-two-extracted"));
        var twoPlan = new ObjRegionPlan(1, Sha(two), [new("component_001",
            [new("first", null, [1], red)], blue)]);
        var twoDerived = ObjComponentExtractor.DeriveRegions(two, twoExtraction.OutputDirectory,
            twoPlan, Path.Combine(directory, "regions-two-derived"));
        Check(twoDerived.Manifest.Components.Count == 1 && twoExtraction.Manifest.ComponentCount == 2 &&
            twoDerived.Manifest.Components[0].ComponentId == "component_001",
            "only selected component derived in two-component assembly");
        Reject("face belongs to another component", two, twoExtraction.OutputDirectory,
            twoPlan with { Components = [new("component_001", [new("foreign", null, [3], red)], blue)] });
        string changedSource = Path.Combine(directory, "regions-source-changed.obj");
        File.Copy(two, changedSource);
        var changedExtraction = ObjComponentExtractor.Extract(changedSource,
            Path.Combine(directory, "regions-source-changed-extracted"));
        File.AppendAllText(changedSource, "# changed\n");
        Reject("source changed after plan", changedSource, changedExtraction.OutputDirectory,
            twoPlan with { SourceSha256 = changedExtraction.Manifest.SourceSha256 });

        File.AppendAllText(Path.Combine(output, "component_001.obj"), "# tampered derivative\n");
        try { ObjComponentExtractor.VerifyRegions(cube, extracted.OutputDirectory, output); throw new Exception("Tampered derived OBJ accepted"); }
        catch (IOException) { Check(true, "tampered derived OBJ rejected by hash"); }
        string repeatPlan = Path.Combine(again.OutputDirectory, "plan.json");
        File.AppendAllText(repeatPlan, " ");
        try { ObjComponentExtractor.VerifyRegions(cube, extracted.OutputDirectory, again.OutputDirectory); throw new Exception("Tampered region plan accepted"); }
        catch (IOException) { Check(true, "tampered canonical plan rejected"); }
        return passed;
    }
}
