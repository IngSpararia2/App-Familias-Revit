namespace BimFamilyAI.Meshy;

public sealed partial class MeshyClient
{
    /// <summary>The user must verify association in Meshy. GET proves existence, not image ownership.</summary>
    public async Task<MeshyTaskRecord> ReconcileAsync(string fingerprint, string taskId, bool confirmedEvidence,
        CancellationToken cancellationToken = default)
    {
        if (!confirmedEvidence) throw new MeshyException(MeshyError.InvalidInput, "Debe comprobar en Meshy que el ID corresponde a la fotografía y configuración de la reserva.");
        var before = (await store.ReadAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(r => r.Fingerprint == fingerprint);
        if (before?.Submission != SubmissionState.Uncertain || before.TaskId is not null)
            throw new MeshyException(MeshyError.InvalidInput, "Seleccione una reserva incierta sin ID.");
        await QueryAsync(taskId, cancellationToken).ConfigureAwait(false); // No POST, validates known ID/provider access.
        return await store.ChangeAsync(records =>
        {
            int source = records.FindIndex(r => r.Fingerprint == fingerprint);
            var recovered = records.Single(r => r.TaskId == taskId);
            if (source < 0 || records[source].Submission != SubmissionState.Uncertain || records[source].TaskId is not null ||
                recovered.ImageHash is not null || recovered.Options is not null)
                throw new MeshyException(MeshyError.InvalidInput, "La reserva cambió o el ID ya está asociado a otra imagen. No se modificó la conciliación.");
            var reservation = records[source];
            var linked = recovered with { Fingerprint = reservation.Fingerprint, ImageHash = reservation.ImageHash,
                Options = reservation.Options, AttemptedAt = reservation.AttemptedAt,
                Audit = [.. reservation.Audit, new(DateTimeOffset.UtcNow, TaskAuditAction.LinkedRecoveredId, reservation.LastError, taskId)] };
            records.Remove(recovered);
            records[records.FindIndex(r => r.Fingerprint == fingerprint)] = linked;
            return linked;
        }, token: cancellationToken).ConfigureAwait(false);
    }
}
