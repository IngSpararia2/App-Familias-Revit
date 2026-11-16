using BimFamilyAI.Core;

namespace BimFamilyAI.Meshy;

/// <summary>Application flow with no UI or Autodesk references. Owns one reusable client.</summary>
public sealed class PhotoGenerationSession : IDisposable
{
    private readonly MeshyClient client;
    private readonly JsonTaskStore store;
    private int busy;
    public PhotoGenerationSession(MeshyClient client,JsonTaskStore store)
    {
        if(client.Mode!=store.Mode)throw new ArgumentException("El cliente y el historial deben usar el mismo modo.");
        this.client=client;this.store=store;
    }
    public MeshyMode Mode => client.Mode;
    public string? LastTaskId { get; private set; }
    public GeneratedObjResult? Result { get; private set; }
    public Task<IReadOnlyList<MeshyTaskRecord>> HistoryAsync(CancellationToken token=default) => store.ReadAsync(token);

    public async Task<GeneratedObjResult> GenerateAsync(string path, MeshyOptions options,
        Func<LocalImage,MeshyOptions,Task<PaidGenerationApproval?>>? authorize = null,
        IProgress<GenerationProgress>? progress=null, bool retryRejected=false, CancellationToken cancellationToken=default,
        string? expectedRejectedFingerprint=null)
    {
        Enter();
        try
        {
            LastTaskId=null;Result=null;
            var image=await Task.Run(()=>LocalImage.ReadAsync(path,cancellationToken),cancellationToken).ConfigureAwait(false);
            options.Validate();
            if (retryRejected && expectedRejectedFingerprint is not null && expectedRejectedFingerprint!=options.Fingerprint(image.Sha256))
                throw new MeshyException(MeshyError.InvalidInput,"La fotografía y configuración no corresponden al rechazo seleccionado. Seleccione la fotografía y perfil originales antes de reintentar.");
            var existing=(await store.ReadAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(r=>r.Fingerprint==options.Fingerprint(image.Sha256));
            if (existing?.TaskId is { } known) return await ResumeCoreAsync(known,progress,cancellationToken).ConfigureAwait(false);
            if (existing is not null && !retryRejected)
                throw new MeshyException(existing.Submission==SubmissionState.Uncertain?MeshyError.CreationUncertain:MeshyError.Duplicate,
                    existing.Submission==SubmissionState.Uncertain ? "Meshy puede haber aceptado el envío. No lo repita: compruebe en Meshy y concilie su ID con esta reserva." : "Hay un rechazo confirmado. Corrija el motivo y use Reintentar rechazo corregido.");
            if (retryRejected && existing?.Submission!=SubmissionState.Rejected)
                throw new MeshyException(MeshyError.InvalidInput,"El reintento requiere un rechazo confirmado para esta misma imagen y configuración.");
            if(retryRejected && existing!.RetryNotBefore is { } next && next>DateTimeOffset.UtcNow)
                throw new MeshyException(MeshyError.RateLimited,"Espere el Retry-After del proveedor; aún no se permite otro intento.");
            PaidGenerationApproval? approval=null;
            if (Mode==MeshyMode.Real)
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESHY_API_KEY")))
                    throw new MeshyException(MeshyError.MissingCredential,"Falta MESHY_API_KEY. Configure su clave propia en las variables de usuario de Windows y reinicie Revit. No escriba la clave en esta ventana ni en el chat.");
                approval=authorize is null ? null : await authorize(image,options).ConfigureAwait(false);
                if (approval is null) throw new OperationCanceledException("Generación pagada no autorizada.",cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var task=retryRejected
                ? await client.RetryRejectedAsync(new(path),options,true,approval,cancellationToken).ConfigureAwait(false)
                : await client.CreateAsync(new(path),options,approval,cancellationToken).ConfigureAwait(false);
            LastTaskId=task.TaskId;
            progress?.Report(new(task.TaskId!,ProcessingState.Pending,0,null));
            return await ResumeCoreAsync(task.TaskId!,progress,cancellationToken).ConfigureAwait(false);
        }
        catch (MeshyException ex) { if(ex.TaskId is not null)LastTaskId=ex.TaskId;throw; }
        finally {Exit();}
    }

    public async Task<GeneratedObjResult> ResumeAsync(string taskId,IProgress<GenerationProgress>? progress=null,CancellationToken token=default)
    {
        Enter();try {return await ResumeCoreAsync(taskId,progress,token).ConfigureAwait(false);}finally {Exit();}
    }
    private async Task<GeneratedObjResult> ResumeCoreAsync(string taskId,IProgress<GenerationProgress>? progress,CancellationToken token)
    {
        var record=await client.RecoverAsync(taskId,token).ConfigureAwait(false);LastTaskId=record.TaskId;
        if (record.Download!=DownloadState.Validated && record.State!=ProcessingState.Succeeded)
            await client.TrackAsync(taskId,progress,token).ConfigureAwait(false);
        Result=await client.GetResultAsync(taskId,token).ConfigureAwait(false);return Result;
    }
    public async Task<MeshyStatus> QueryAsync(string id,CancellationToken token=default)
    {
        Enter();try {var status=await client.QueryAsync(id,token).ConfigureAwait(false);LastTaskId=id;return status;}finally {Exit();}
    }
    public async Task<MeshyTaskRecord> ReconcileAsync(string fingerprint,string id,bool evidence,CancellationToken token=default)
    {
        Enter();try {var record=await client.ReconcileAsync(fingerprint,id,evidence,token).ConfigureAwait(false);LastTaskId=record.TaskId;return record;}finally {Exit();}
    }
    private void Enter()
    {if(Interlocked.CompareExchange(ref busy,1,0)!=0)throw new MeshyException(MeshyError.Busy,"Ya hay una operación en curso. Deténgala o espere antes de repetir.");}
    private void Exit()=>Volatile.Write(ref busy,0);
    public void Dispose()=>client.Dispose();
}
