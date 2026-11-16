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
