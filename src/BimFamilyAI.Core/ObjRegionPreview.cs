using System.Numerics;

namespace BimFamilyAI.Core;

public sealed record ObjPreviewTriangle(int OriginalFace, string ComponentId, Vector3 A, Vector3 B, Vector3 C,
    bool HasOriginalMaterial, ObjRegionColor? OriginalColor);

public sealed class ObjRegionPreview
{
    public IReadOnlyList<ObjPreviewTriangle> Triangles { get; }
    private readonly Dictionary<int, List<(int Face, float Cosine)>> neighbors = [];

    internal ObjRegionPreview(IReadOnlyList<ObjPreviewTriangle> triangles,
        IEnumerable<(int Face, string Component, int[] Indices, Vector3 Normal)> faces)
    {
        Triangles = triangles;
        var edges = new Dictionary<(string, int, int), List<(int Face, Vector3 Normal)>>();
        foreach (var face in faces)
        {
            neighbors.Add(face.Face, []);
            for (int i = 0; i < face.Indices.Length; i++)
            {
                int a = face.Indices[i], b = face.Indices[(i + 1) % face.Indices.Length];
                var key = (face.Component, Math.Min(a, b), Math.Max(a, b));
                if (!edges.TryGetValue(key, out var incident)) edges.Add(key, incident = []);
                incident.Add((face.Face, face.Normal));
            }
        }
        foreach (var edge in edges.Values)
        {
            // A non-manifold edge has no unambiguous continuation.
            if (edge.Count != 2) continue;
            var (a, na) = edge[0];
            var (b, nb) = edge[1];
            float cosine = Vector3.Dot(na, nb);
            neighbors[a].Add((b, cosine));
            neighbors[b].Add((a, cosine));
        }
    }

    public HashSet<int> Grow(IEnumerable<int> seeds, double degrees, IReadOnlySet<int> blocked)
    {
        if (!double.IsFinite(degrees) || degrees is < 0 or > 180)
            throw new ArgumentException("Indique un ángulo de 0 a 180 grados.");
        float threshold = (float)Math.Cos(degrees * Math.PI / 180);
        var found = seeds.Where(seed => neighbors.ContainsKey(seed) && !blocked.Contains(seed)).ToHashSet();
        var queue = new Queue<int>(found);
        while (queue.TryDequeue(out int face))
            foreach (var neighbor in neighbors[face])
                if (neighbor.Cosine >= threshold - 1e-6f && !blocked.Contains(neighbor.Face) && found.Add(neighbor.Face))
                    queue.Enqueue(neighbor.Face);
        return found;
    }

    public ObjPreviewTriangle? Pick(Vector3 origin, Vector3 direction, IReadOnlySet<string> hidden)
    {
        ObjPreviewTriangle? closest = null;
        float distance = float.PositiveInfinity;
        foreach (var t in Triangles)
        {
            if (hidden.Contains(t.ComponentId)) continue;
            var ab = t.B - t.A;
            var ac = t.C - t.A;
            var p = Vector3.Cross(direction, ac);
            float determinant = Vector3.Dot(ab, p);
            if (Math.Abs(determinant) <= 1e-8f * ab.Length() * ac.Length()) continue;
            var offset = origin - t.A;
            float u = Vector3.Dot(offset, p) / determinant;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(offset, ab);
            float v = Vector3.Dot(direction, q) / determinant;
            if (v < 0 || u + v > 1) continue;
            float hit = Vector3.Dot(ac, q) / determinant;
            if (hit < 0 || hit >= distance) continue;
            closest = t;
            distance = hit;
        }
        return closest;
    }

    internal static (List<int> Corners, Vector3 Normal) Triangulate(Vector3[] points, int face)
    {
        IOException Invalid() => new($"Cara OBJ {face}: polígono degenerado, no plano o incompatible con la selección.");
        // Work relative to the first corner to avoid cancellation in the normal.
        var origin = points[0];
        var normal = Vector3.Zero;
        for (int i = 1; i + 1 < points.Length; i++)
            normal += Vector3.Cross(points[i] - origin, points[i + 1] - origin);
        float span = points.Max(p => Vector3.Distance(p, origin));
        if (span == 0 || normal.Length() <= span * span * 1e-9f) throw Invalid();
        normal = Vector3.Normalize(normal);
        if (points.Length == 3) return ([0, 1, 2], normal);
        if (points.Any(p => Math.Abs(Vector3.Dot(p - origin, normal)) > span * 1e-5f)) throw Invalid();
        var reference = Math.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        var x = Vector3.Normalize(Vector3.Cross(reference, normal));
        var y = Vector3.Cross(normal, x);
        var flat = points.Select(p => new Vector2(Vector3.Dot(p - origin, x), Vector3.Dot(p - origin, y))).ToArray();
        double epsilon = (double)span * span * 1e-10;
        double Turn(int a, int b, int c) => ((double)flat[b].X - flat[a].X) * ((double)flat[c].Y - flat[a].Y)
            - ((double)flat[b].Y - flat[a].Y) * ((double)flat[c].X - flat[a].X);
        bool OnSegment(int a, int b, int c) => Math.Abs(Turn(a,b,c)) <= epsilon &&
            Vector2.Dot(flat[c] - flat[a], flat[c] - flat[b]) <= epsilon;
        for (int a = 0; a < flat.Length; a++)
        {
            int b = (a + 1) % flat.Length;
            if (Vector2.DistanceSquared(flat[a], flat[b]) <= epsilon) throw Invalid();
            for (int c = a + 2; c < flat.Length; c++)
            {
                int d = (c + 1) % flat.Length;
                if (d == a) continue;
                if (OnSegment(a,b,c) || OnSegment(a,b,d) || OnSegment(c,d,a) || OnSegment(c,d,b) ||
                    (Turn(a,b,c) > 0) != (Turn(a,b,d) > 0) && (Turn(c,d,a) > 0) != (Turn(c,d,b) > 0))
                    throw Invalid();
            }
        }
        var remaining = Enumerable.Range(0, points.Length).ToList();
        var triangles = new List<int>();
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count];
                int b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Turn(a,b,c) <= epsilon || remaining.Any(p => p != a && p != b && p != c &&
                    Turn(a,b,p) >= -epsilon && Turn(b,c,p) >= -epsilon && Turn(c,a,p) >= -epsilon)) continue;
                triangles.AddRange([a,b,c]);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) throw Invalid();
        }
        if (Turn(remaining[0], remaining[1], remaining[2]) <= epsilon) throw Invalid();
        triangles.AddRange(remaining);
        return (triangles, normal);
    }
}
