using System.Security.Cryptography;
using BimFamilyAI.Core;

internal static class ObjStructureChecks
{
    internal static int Run(string fixtures, string directory)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        ObjStructureResult Fixture(string name)
        {
            var path = Path.Combine(fixtures, name);
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var result = ObjStructure.Analyze(path);
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "unchanged " + name);
            return result;
        }
        var cube = Fixture("cube_500mm.obj");
        Check(cube.Components.Count == 1 && cube.Faces == 12 && cube.Triangles == 12 && cube.Vertices == 8, "cube mesh");
        Check(cube.GroupDeclarations == 0 && cube.ObjectDeclarations == 0, "cube no labels");
        Check(cube.Components[0].Box.Center == new ObjPoint(250, 250, 250), "cube center");
        var prism = Fixture("offset_prism_m.obj");
        Check(prism.Components.Count == 1 && prism.Components[0].Box.Center == new ObjPoint(10.5, 20.3, 30.375), "offset center");
        var regions = Fixture("material_regions.obj");
        Check(regions.GroupDeclarations == 2 && regions.ByGroup.Count == 2 && regions.ByMaterial.Count == 2, "two groups and materials");
        Check(regions.Components.Count == 1 && regions.Components[0].Groups.Count == 2, "groups share topology");
        Check(regions.ByGroup.All(g => g.Faces == 6 && g.Triangles == 6), "group face distribution");
        var shared = Fixture("shared_material.obj");
        Check(shared.GroupDeclarations == 2 && shared.ByMaterial.Count == 1 && shared.Components.Count == 1, "groups share material");

        ObjStructureResult Synthetic(string name, string content)
        {
            var path = Path.Combine(directory, name + ".obj");
            File.WriteAllText(path, content);
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var result = ObjStructure.Analyze(path);
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "unchanged " + name);
            return result;
        }
        const string points = "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nv 5 0 0\nv 6 0 0\nv 5 1 1\n";
        var separate = Synthetic("separate", points + "o A\ng Left\nusemtl Same\nf 1 2 3\no B\ng Right\nusemtl Same\nf 5 6 7\n");
        Check(separate.Components.Count == 2 && separate.ByObject.Count == 2 && separate.ByMaterial.Count == 1, "disconnected same material");
        Check(separate.Components[1].Box.Center == new ObjPoint(5.5, 0.5, 0.5), "second component center");
        Check(separate.OverlappingComponentBoxPairs == 0, "separated boxes");
        var bridge = Synthetic("bridge", points + "g Seat\nf 1 2 3\ng Back\nf 3 4 1\n");
        Check(bridge.Components.Count == 1 && bridge.Components[0].Groups.Count == 2, "connected visual parts");
        var polygon = Synthetic("polygon", "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 1\ng Quad\nf -4/-1/-1 -3/-1/-1 -2/-1/-1 -1/-1/-1\n");
        Check(polygon.Faces == 1 && polygon.Triangles == 2 && polygon.Components.Count == 1, "negative polygon v/vt/vn");
        var forward = Synthetic("forward", "f 1 2 3\nv 0 0 0\nv 1 0 0\nv 0 1 1\n");
        Check(forward.Components.Count == 1, "forward positive indices");
        var duplicate = Synthetic("duplicate", "v 0 0 0\nv 1 0 0\nv 0 1 1\nv 0 0 0\nv 2 0 0\nv 0 2 1\nf 1 2 3\nf 4 5 6\n");
        Check(duplicate.Components.Count == 2 && duplicate.DuplicateCoordinateVertices == 1, "same coordinates different indices");
        var many = new System.Text.StringBuilder();
        for (int i = 0; i < 1001; i++)
        {
            many.Append($"v {i} 0 0\nv {i} 1 0\nv {i} 0 1\n");
            many.Append($"f {3 * i + 1} {3 * i + 2} {3 * i + 3}\n");
        }
        var tinyShells = Synthetic("many-shells", many.ToString());
        Check(tinyShells.Components.Count == 1001 && tinyShells.OverlappingComponentBoxPairs is null, "many shells avoid quadratic boxes");
        foreach (var name in new[] { "missing.obj", "empty.obj", "bad-index.obj", "huge.obj" })
        {
            try { ObjStructure.Analyze(Path.Combine(directory, name)); }
            catch (IOException) { Check(true, "invalid " + name); continue; }
            throw new Exception("Accepted invalid " + name);
        }
        return passed;
    }
}
