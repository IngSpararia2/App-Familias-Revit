using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

internal sealed record PhotoRevitRequest(ObjImportRequest? Import, string? OpenRfaPath, bool EditMaterials = false);

/// <summary>One immutable request at a time. All Autodesk work stays in Execute.</summary>
internal sealed class PhotoFamilyExternalEvent : IExternalEventHandler
{
    private readonly UIApplication owner;
    private ExternalEvent? externalEvent;
    private PhotoRevitRequest? pending;
    private TaskCompletionSource<FamilyWorkflowOutcome>? completion;
    private bool closing, disposed, shuttingDown;
    internal bool IsExecuting { get; private set; }
    internal Action? CloseWindowForShutdown { get; set; }

    // Construction and event subscription occur only inside IExternalCommand.Execute.
    internal PhotoFamilyExternalEvent(UIApplication application)
    {
        owner=application;
        externalEvent=ExternalEvent.Create(this);
        owner.ApplicationClosing+=OnApplicationClosing;
    }
    internal Task<FamilyWorkflowOutcome> Enqueue(PhotoRevitRequest request)
    {
        if (closing || disposed) throw new InvalidOperationException("La ventana se está cerrando.");
        if (pending is not null || IsExecuting) throw new InvalidOperationException("Ya hay una operación Revit pendiente.");
        if ((request.Import is null)==(request.OpenRfaPath is null)) throw new ArgumentException("Solicitud Revit incompleta.");
        pending=request;
        completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task=completion.Task;
        try
        {
            var raised=externalEvent!.Raise();
            if (raised is not (ExternalEventRequest.Accepted or ExternalEventRequest.Pending))
                throw new InvalidOperationException("Revit no aceptó la solicitud. Termine el comando activo y vuelva a intentar con el mismo OBJ.");
        }
        catch {pending=null;completion=null;throw;}
        return task;
    }
    public string GetName()=>"BIM Family AI — importar fotografía mediante ExternalEvent";
    public void Execute(UIApplication application)
    {
        if (closing) {ReleaseInApiContext();return;}
        var request=pending;var result=completion;
        if (request is null || result is null) return;
        IsExecuting=true;
        try
        {
            if (request.Import is { } import)
                result.TrySetResult(FamilyFileWorkflow.CreateFamily(application.Application,false,import,request.EditMaterials));
            else
            {
                string path=request.OpenRfaPath!;
                if (!System.IO.File.Exists(path) || !string.Equals(System.IO.Path.GetExtension(path),".rfa",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("El RFA no existe.");
                application.OpenAndActivateDocument(path);
                result.TrySetResult(new(Result.Succeeded,path,"Familia abierta en Revit."));
            }
        }
        catch (Exception ex) {result.TrySetResult(new(Result.Failed,null,ex.Message));}
        finally {IsExecuting=false;pending=null;completion=null;}
    }
    internal void RequestClose()
    {
        if(disposed)return;
        closing=true;CloseWindowForShutdown=null;
        completion?.TrySetCanceled();pending=null;completion=null;
        // The cleanup itself is also queued into API context (unsubscribe/dispose).
        if(!shuttingDown)
        {
            try {externalEvent?.Raise();}
            catch(Autodesk.Revit.Exceptions.InvalidOperationException) { /* ApplicationClosing will release it if shutdown has started. */ }
        }
    }
    private void OnApplicationClosing(object? sender,ApplicationClosingEventArgs args)
    {
        shuttingDown=true;
        CloseWindowForShutdown?.Invoke();
        ReleaseInApiContext();
    }
    internal void ReleaseInApiContext()
    {
        if(disposed)return;
        disposed=true;closing=true;
        completion?.TrySetCanceled();pending=null;completion=null;
        owner.ApplicationClosing-=OnApplicationClosing;
        CloseWindowForShutdown=null;
        externalEvent?.Dispose();externalEvent=null;
    }
}
