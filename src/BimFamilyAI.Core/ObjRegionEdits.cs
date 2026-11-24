namespace BimFamilyAI.Core;

public sealed class ObjRegionEdits(string sourceSha256)
{
    private readonly Dictionary<string, ObjComponentRegionPlan> components = new(StringComparer.Ordinal);
    private readonly HashSet<string> explicitFallbacks = new(StringComparer.Ordinal);
    public ObjRegionPlan Snapshot() => new(1, sourceSha256, components.Values
        .Where(part => part.Regions.Count > 0 || part.FallbackColor is not null)
        .OrderBy(part => part.ComponentId, StringComparer.Ordinal).ToArray());
    public ObjComponentRegionPlan Get(string component) => components.GetValueOrDefault(component)
        ?? new(component, [], new(150, 150, 150));

    public void Load(ObjRegionPlan validated)
    {
        if (validated.SourceSha256 != sourceSha256) throw new ArgumentException("El plan pertenece a otro OBJ.");
        components.Clear();
        explicitFallbacks.Clear();
        foreach (var part in validated.Components)
        {
            components.Add(part.ComponentId, part);
            if (part.FallbackColor is not null) explicitFallbacks.Add(part.ComponentId);
        }
    }

    public void SetFallback(string component, ObjRegionColor? color)
    {
        var part = Get(component);
        if (color is null) explicitFallbacks.Remove(component);
        else explicitFallbacks.Add(component);
        components[component] = part with { FallbackColor = color };
    }

    public void Put(string component, string? id, string name, ObjRegionColor color, IEnumerable<int> faces)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Indique un nombre de región válido de 1 a 120 caracteres.");
        var selected = faces.Distinct().Order().ToArray();
        if (selected.Length == 0) throw new ArgumentException("Seleccione al menos una cara.");
        var part = Get(component);
        var others = part.Regions.Where(region => region.RegionId != id).ToArray();
        if (others.Any(region => string.Equals(region.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ya existe una región con ese nombre en esta componente.");
        var taken = others.SelectMany(region => region.OriginalFaceNumbers).ToHashSet();
        if (selected.Any(taken.Contains)) throw new ArgumentException("La selección incluye caras de otra región.");
        components[component] = part with { Regions = [.. others, new(id ?? Guid.NewGuid().ToString("N"), name, selected, color)] };
    }

    public void Delete(string component, string id)
    {
        var part = Get(component);
        var remaining = part.Regions.Where(region => region.RegionId != id).ToArray();
        if (remaining.Length == 0 && !explicitFallbacks.Contains(component)) components.Remove(component);
        else components[component] = part with { Regions = remaining };
    }

    public static void Select(HashSet<int> selection, int face, bool toggle)
    {
        if (!toggle) selection.Clear();
        if (!selection.Add(face)) selection.Remove(face);
    }
}
