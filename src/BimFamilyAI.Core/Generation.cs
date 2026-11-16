namespace BimFamilyAI.Core;

public sealed record ImageGenerationRequest(string ImagePath);
public enum ProcessingState { Pending, InProgress, Succeeded, Failed, Canceled }
public sealed record GenerationProgress(string TaskId, ProcessingState State, int? Percent, string? ErrorCode);

/// <summary>The photo and OBJ do not establish physical units. The caller must supply them.</summary>
public sealed record GeneratedObjResult(string Provider, string TaskId, string LocalPath, ObjBounds Bounds)
{
    public ObjImportRequest ToImportRequest(ObjUnit unit, ObjVerticalAxis verticalAxis,
        double? targetHeightMm, string familyName, string description)
    {
        if (!Enum.IsDefined(unit) || !Enum.IsDefined(verticalAxis)) throw new ArgumentException("Unidades o eje inválidos.");
        if (targetHeightMm is { } h && (!double.IsFinite(h) || h <= 0)) throw new ArgumentException("Altura inválida.");
        ObjInspection.ValidateName(familyName);
        ObjInspection.Inspect(LocalPath);
        return new(LocalPath, unit, verticalAxis, targetHeightMm, familyName, description);
    }
}

public static class PhotoFamilyRequest
{
    public static ObjImportRequest Create(GeneratedObjResult result, double heightMm, ObjVerticalAxis axis,
        string name, string description, bool simulated)
    {
        if (!double.IsFinite(heightMm) || heightMm <= 0) throw new ArgumentException("Confirme una altura física real positiva en milímetros antes de crear el RFA.");
        if (simulated && !name.StartsWith("SIM_", StringComparison.OrdinalIgnoreCase)) name = "SIM_" + name;
        var label = simulated ? "SIMULACIÓN: OBJ de prueba, no generado por IA. " : "Geometría IA; dimensiones informativas. ";
        // With a confirmed target height the arbitrary source-unit factor cancels in uniform scaling.
        return result.ToImportRequest(ObjUnit.Millimeters, axis, heightMm, name, label + description)
            with { SourceUnitsUnspecified = true };
    }
}
