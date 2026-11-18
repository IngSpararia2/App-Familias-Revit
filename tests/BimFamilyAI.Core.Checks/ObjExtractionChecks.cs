using System.Security.Cryptography;
using BimFamilyAI.Core;

internal static class ObjExtractionChecks
{
    internal static int Run(string fixtures, string directory)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        string Source(string name, string content)
        {
            string path = Path.Combine(directory, name + ".obj");
            File.WriteAllText(path, content);
            return path;
        }
        ObjExtractionResult Extract(string source, string name)
        {
            var before = SHA256.HashData(File.ReadAllBytes(source));
            var result = ObjComponentExtractor.Extract(source, Path.Combine(directory, name));
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "original unchanged " + name);
            Check(result.Manifest.SourceSha256 == Convert.ToHexStringLower(before), "source hash " + name);
            Check(result.Manifest.Components.Sum(p => p.Faces) == ObjStructure.Analyze(source).Faces, "face total " + name);
            return result;
        }
        void Reject(string source, string name, string? output = null)
        {
            output ??= Path.Combine(directory, name + "-output");
            bool existed = Directory.Exists(output);
            byte[]? priorManifest = existed ? File.ReadAllBytes(Path.Combine(output, "manifest.json")) : null;
            try { ObjComponentExtractor.Extract(source, output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Check(existed
                    ? Directory.Exists(output) && priorManifest!.SequenceEqual(File.ReadAllBytes(Path.Combine(output, "manifest.json")))
                    : !Directory.Exists(output), "rejected without partial output " + name);
                return;
            }
            throw new Exception("Accepted invalid extraction: " + name);
        }

        var cube = Extract(Path.Combine(fixtures, "cube_500mm.obj"), "cube-extracted");
        Check(cube.Manifest.ComponentCount == 1 && cube.Manifest.Components[0].Faces == 12, "single component cube");
        Check(cube.Manifest.GlobalBox == new ObjBox(new(0, 0, 0), new(500, 500, 500), new(250, 250, 250)), "cube global box");
        Check(cube.Manifest.Components[0].Sha256 == Convert.ToHexStringLower(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(cube.OutputDirectory, "component_001.obj")))) &&
            !File.ReadAllText(Path.Combine(cube.OutputDirectory, "manifest.json")).Contains(Path.GetFullPath(fixtures)),
            "manifest hashes and no private absolute source path");
        var again = Extract(Path.Combine(fixtures, "cube_500mm.obj"), "cube-repeated");
        Check(cube.Manifest.Components[0].Sha256 == again.Manifest.Components[0].Sha256 &&
            File.ReadAllBytes(Path.Combine(cube.OutputDirectory, "manifest.json"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(again.OutputDirectory, "manifest.json"))),
            "deterministic OBJ and manifest");
        Reject(Path.Combine(fixtures, "cube_500mm.obj"), "existing output", cube.OutputDirectory);
        var prism = Extract(Path.Combine(fixtures, "offset_prism_m.obj"), "prism-extracted");
        Check(prism.Manifest.ComponentCount == 1 && prism.Manifest.GlobalBox.Center == new ObjPoint(10.5, 20.3, 30.375),
            "offset prism retains original placement");

        var regions = Extract(Path.Combine(fixtures, "material_regions.obj"), "regions-extracted");
        Check(regions.Manifest.ComponentCount == 1 && regions.Manifest.AuxiliaryFiles.Count == 1, "shared-vertex groups remain one piece with MTL");
        Check(File.ReadAllBytes(Path.Combine(regions.OutputDirectory, "multi.mtl"))
            .SequenceEqual(File.ReadAllBytes(Path.Combine(fixtures, "multi.mtl"))), "MTL copied byte-exact");
        var shared = Extract(Path.Combine(fixtures, "shared_material.obj"), "shared-extracted");
        Check(shared.Manifest.ComponentCount == 1, "shared material groups remain one piece");

        const string positions = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 1\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 1\n";
        var separateSource = Source("separate-parts", positions +
            "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n" +
            "vn 0 0 1\no Example\ng First\nusemtl Red\ns 1\n" +
            "f -8/1/1 -7/2/1 -6/3/1 -5/4/1\n" +
            "g Second\ns off\nf 5//1 6//1 7//1\nf 5/1 7/3 8/4\n");
        File.WriteAllText(Path.Combine(directory, "multi.mtl"), "newmtl Red\nKd 1 0 0\n");
        File.AppendAllText(separateSource, "mtllib multi.mtl\n");
        var separate = Extract(separateSource, "separate-extracted");
        Check(separate.Manifest.ComponentCount == 2 && separate.Manifest.Components.Sum(p => p.Faces) == 3, "two disconnected overlapping boxes");
        Check(separate.Manifest.Components[0].Faces == 1 && separate.Manifest.Components[0].Triangles == 2 &&
            separate.Manifest.Components[1].Faces == 2, "polygon and triangle counts");
        Check(separate.Manifest.Components[0].Box == separate.Manifest.Components[1].Box, "overlap and duplicate coordinates do not merge");
        Check(separate.Manifest.Components[0].OriginalFaceNumbers.SequenceEqual([1]) &&
            separate.Manifest.Components[1].OriginalFaceNumbers.SequenceEqual([2, 3]), "stable original face correspondence");
        Check(File.ReadAllText(Path.Combine(separate.OutputDirectory, "component_001.obj")).Contains("f 1/1/1 2/2/1 3/3/1 4/4/1") &&
            File.ReadAllText(Path.Combine(separate.OutputDirectory, "component_002.obj")).Contains("f 1//1 2//1 3//1"),
            "UV, normal, face order and slash forms retained");
        Check(File.ReadAllText(Path.Combine(separate.OutputDirectory, "component_001.obj")).Contains("s 1") &&
            File.ReadAllText(Path.Combine(separate.OutputDirectory, "component_002.obj")).Contains("s off"), "smoothing states retained");

        var forward = Source("forward-extract", "f 1 2 3\nv 0 0 0\nv 2 0 0\nv 0 3 1\n");
        Check(Extract(forward, "forward-extracted").Manifest.Components[0].Faces == 1, "forward positive face indices");
        var attributes = Source("negative-attributes", "v 0 0 0\nv 2 0 0\nv 0 3 1\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf -3/-3/-1 -2/-2/-1 -1/-1/-1\n");
        var attributed = Extract(attributes, "negative-attributes-extracted");
        Check(File.ReadAllText(Path.Combine(attributed.OutputDirectory, "component_001.obj"))
            .Contains("f 1/1/1 2/2/1 3/3/1"), "negative UV and normal indices remapped");
        var forwardAttributes = Source("forward-attributes", "f 1/1/1 2/2/1 3/3/1\nv 0 0 0\nv 2 0 0\nv 0 3 1\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\n");
        Check(Extract(forwardAttributes, "forward-attributes-extracted").Manifest.Components[0].Normals == 1,
            "forward UV and normal indices");
        var unusedMtl = Source("unused-mtl", "mtllib missing.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n");
        var unused = Extract(unusedMtl, "unused-mtl-extracted");
        Check(unused.Manifest.Warnings.Any(w => w.Contains("missing.mtl")) &&
            !File.ReadAllText(Path.Combine(unused.OutputDirectory, "component_001.obj")).Contains("mtllib"),
            "missing but unused MTL is reported and omitted");

        Reject(Source("missing-material", "mtllib absent.mtl\nusemtl Red\nv 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n"), "missing required MTL");
        Reject(Source("undefined-material", "mtllib multi.mtl\nusemtl Blue\nv 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n"), "undefined material");
        Reject(Source("bad-vt", "v 0 0 0\nv 1 0 0\nv 0 1 1\nvt 0 0\nf 1/2 2/2 3/2\n"), "invalid vt index");
        Reject(Source("bad-vn", "v 0 0 0\nv 1 0 0\nv 0 1 1\nvn 0 0 1\nf 1//2 2//2 3//2\n"), "invalid vn index");
        Reject(Source("unsupported", "v 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\nl 1 2\n"), "unsupported geometry directive");
        Reject(Source("unsafe-mtl", "mtllib ../external.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n"), "unsafe MTL path");
        File.WriteAllText(Path.Combine(directory, "textured.mtl"), "newmtl Red\nKd 1 0 0\nmap_Kd texture.png\n");
        Reject(Source("texture-map", "mtllib textured.mtl\nusemtl Red\nv 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n"), "unsupported texture map");
        Reject(Source("partial-piece", "v 0 0 0\nv 1 0 0\nv 0 1 1\nv 2 2 2\nv 2 2 2\nv 2 2 2\nf 1 2 3\nf 4 5 6\n"), "invalid component rollback");
        var badUtf8 = Path.Combine(directory, "invalid-utf8.obj");
        File.WriteAllBytes(badUtf8, [.. System.Text.Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 1\nf 1 2 3\n#"), 0xFF, 0x0A]);
        Reject(badUtf8, "invalid UTF-8");
        Reject(Path.Combine(directory, "empty.obj"), "empty OBJ");
        Reject(Path.Combine(directory, "huge.obj"), "OBJ size limit");
        var blocked = Path.Combine(directory, "blocked-parent");
        File.WriteAllText(blocked, "not a directory");
        Reject(Path.Combine(fixtures, "cube_500mm.obj"), "write failure", Path.Combine(blocked, "out"));
        Check(!Directory.EnumerateDirectories(directory, "*.bfai-*.tmp").Any(), "no staging directories after rejection");
        return passed;
    }
}
