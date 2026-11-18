using System.Text.Json;
using BimFamilyAI.Core;

internal static class ObjAssemblyChecks
{
    internal static int Run(string fixtures, string directory)
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-7;
        var source = new ObjBox(new(10, 20, 30), new(11, 20.6, 30.75), new(10.5, 20.3, 30.375));
        var z = ObjAssemblyTransform.Create(source, ObjVerticalAxis.Z, 1000, 750);
        Check(Near(z.MillimetersPerSourceUnit, 1000) && Near(z.GlobalBoxMm.Max.Z, 750) &&
            Near(z.GlobalBoxMm.Min.X, -500) && Near(z.GlobalBoxMm.Min.Y, -300) && Near(z.GlobalBoxMm.Min.Z, 0),
            "global Z scale, center and base");
        var y = ObjAssemblyTransform.Create(source, ObjVerticalAxis.Y, 1000, 600);
        var yPoint = y.Transform(source.Min);
        Check(Near(y.GlobalBoxMm.Max.Z, 600) && Near(y.GlobalBoxMm.Max.Y, 375) && Near(yPoint.Y, 375) && Near(yPoint.Z, 0),
            "global Y orientation sign and base");
        var x = ObjAssemblyTransform.Create(source, ObjVerticalAxis.X, 1000, 1000);
        var xPoint = x.Transform(source.Min);
        Check(Near(x.GlobalBoxMm.Max.Z, 1000) && Near(x.GlobalBoxMm.Max.X, 375) && Near(xPoint.X, 375) && Near(xPoint.Z, 0),
            "global X orientation sign and base");
        var noHeight = ObjAssemblyTransform.Create(source, ObjVerticalAxis.Z, 25.4, null);
        Check(Near(noHeight.MillimetersPerSourceUnit, 25.4) && Near(noHeight.GlobalBoxMm.Max.Z, 19.05),
            "source unit without target height");
        var subBox = new ObjBox(new(10, 20, 30), new(10.2, 20.3, 30.4), new(10.1, 20.15, 30.2));
        var mapped = z.Transform(subBox);
        Check(Near(mapped.Min.X, -500) && Near(mapped.Max.X, -300) && Near(mapped.Min.Z, 0) && Near(mapped.Max.Z, 400),
            "component uses global transform without local recentering");
        try { ObjAssemblyTransform.Create(source, ObjVerticalAxis.Z, 1000, 200_000); throw new Exception("Accepted huge assembly"); }
        catch (ArgumentException) { Check(true, "assembly dimension limit"); }

        string obj = Path.Combine(fixtures, "cube_500mm.obj");
        string output = Path.Combine(directory, "assembly-verified");
        ObjComponentExtractor.Extract(obj, output);
        Check(ObjComponentExtractor.Verify(obj, output).ComponentCount == 1, "published manifest verified");
        string manifestPath = Path.Combine(output, "manifest.json");
        string original = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ObjExtractionManifest>(original)!;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { SourceSha256 = new string('0', 64) }));
        try { ObjComponentExtractor.Verify(obj, output); throw new Exception("Accepted bad source hash"); }
        catch (IOException) { Check(true, "source hash tampering rejected"); }
        File.WriteAllText(manifestPath, original);
        string piece = Path.Combine(output, "component_001.obj");
        byte[] pieceBytes = File.ReadAllBytes(piece);
        File.AppendAllText(piece, "# changed\n");
        try { ObjComponentExtractor.Verify(obj, output); throw new Exception("Accepted bad piece hash"); }
        catch (IOException) { Check(true, "piece hash tampering rejected"); }
        File.WriteAllBytes(piece, pieceBytes);
        File.Delete(piece);
        try { ObjComponentExtractor.Verify(obj, output); throw new Exception("Accepted missing component"); }
        catch (IOException) { Check(true, "missing component rejected"); }
        return passed;
    }
}
