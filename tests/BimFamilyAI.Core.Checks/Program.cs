using BimFamilyAI.Core;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--extract")
{
    try
    {
        var result = ObjComponentExtractor.Extract(args[1], args[2]);
        Console.WriteLine($"Extracción validada: {result.Manifest.ComponentCount} componente(s) en {result.OutputDirectory}");
        Console.WriteLine("Manifiesto: " + Path.Combine(result.OutputDirectory, "manifest.json"));
        foreach (var warning in result.Manifest.Warnings) Console.WriteLine("Aviso: " + warning);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine("Extracción rechazada: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 5 && args[0] == "--regions")
{
    try
    {
        var plan = JsonSerializer.Deserialize<ObjRegionPlan>(File.ReadAllText(args[3]))
            ?? throw new IOException("Plan de regiones vacío.");
        var result = ObjComponentExtractor.DeriveRegions(args[1], args[2], plan, args[4]);
        Console.WriteLine($"Derivación validada: {result.Manifest.Components.Count} componente(s) con regiones en {result.OutputDirectory}");
        Console.WriteLine("Manifiesto: " + Path.Combine(result.OutputDirectory, "manifest.json"));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    { Console.Error.WriteLine("Regiones rechazadas: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--analyze")
{
    Console.WriteLine(JsonSerializer.Serialize(ObjStructure.Analyze(args[1]), new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var fixtures = Path.GetFullPath(args.Length > 0 ? args[0] : "tests/fixtures");
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
var cube = ObjInspection.Inspect(Path.Combine(fixtures, "cube_500mm.obj"));
Check(cube == new ObjBounds(500, 500, 500), "cube bounds");
var prism = ObjInspection.Inspect(Path.Combine(fixtures, "offset_prism_m.obj"));
Check(Math.Abs(prism.X-1) < 1e-9 && Math.Abs(prism.Y-0.6) < 1e-9 && Math.Abs(prism.Z-0.75) < 1e-9, "offset prism bounds");
var directory = Path.Combine(Path.GetTempPath(), "BFAI-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    void Reject(string path, string name)
    {
        try { ObjInspection.Inspect(path); } catch (IOException) { Check(true, name); return; }
        throw new Exception("Accepted invalid OBJ: " + name);
    }
    Reject(Path.Combine(directory, "missing.obj"), "missing path");
    foreach (var (name, text) in new[] {
        ("empty", ""), ("no-faces", "v 0 0 0\nv 1 1 1"),
        ("nan", "v NaN 0 0\nf 1 2 3"), ("infinity", "v Infinity 0 0\nf 1 2 3"),
        ("bad-index", "v 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 4"),
        ("zero-size", "v 0 0 0\nv 0 0 0\nv 0 0 0\nf 1 2 3") })
    {
        var path = Path.Combine(directory, name + ".obj"); File.WriteAllText(path, text); Reject(path, name);
    }
    var huge = Path.Combine(directory, "huge.obj");
    using (var stream = File.Create(huge)) stream.SetLength(ObjInspection.MaxBytes+1);
    Reject(huge, "size limit");
    var negative = Path.Combine(directory, "negative.obj");
    File.WriteAllText(negative, "v 0 0 0\nv 2 0 0\nv 0 3 4\nf -3 -2 -1");
    Check(ObjInspection.Inspect(negative) == new ObjBounds(2,3,4), "negative indices");
    passed += ObjStructureChecks.Run(fixtures, directory);
    passed += ObjExtractionChecks.Run(fixtures, directory);
    passed += ObjAssemblyChecks.Run(fixtures, directory);
    passed += ObjRegionChecks.Run(fixtures, directory);
    passed += ObjRegionEditorChecks.Run(directory);
    Console.WriteLine($"{passed} checks passed");
}
finally { Directory.Delete(directory, true); }
