using System.Net;
using System.Security.Cryptography;
using BimFamilyAI.Core;

namespace BimFamilyAI.Meshy;

public sealed partial class MeshyClient
{
    public async Task<GeneratedObjResult> GetResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var record = await RecoverAsync(taskId, cancellationToken).ConfigureAwait(false);
        var cached = await CachedAsync(record, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return cached;
        try
        {
            // An expired link can be queried once more, but may remain expired. Never create again.
            for (int refresh = 0; ; refresh++)
            {
                var (status, url) = await QueryWithUrlAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (status.Progress.State == ProcessingState.Failed)
                    throw new MeshyException(MeshyError.GenerationFailed, "La tarea de Meshy falló.", taskId);
                if (status.Progress.State == ProcessingState.Canceled)
                    throw new MeshyException(MeshyError.GenerationCanceled, "La tarea fue cancelada por el proveedor.", taskId);
                if (status.Progress.State != ProcessingState.Succeeded || string.IsNullOrWhiteSpace(url))
                    throw new MeshyException(MeshyError.MissingResult, "La tarea todavía no ofrece un OBJ descargable.", taskId);
                try { return await DownloadAsync(taskId, url, cancellationToken).ConfigureAwait(false); }
                catch (MeshyException ex) when (ex.Code == MeshyError.ExpiredUrl && refresh == 0) { }
            }
        }
        catch (MeshyException ex)
        {
            await UpdateAsync(taskId, r => r with { Download = DownloadState.Failed, LastError = ex.Code }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GeneratedObjResult?> CachedAsync(MeshyTaskRecord record, CancellationToken token)
    {
        if (record.Download != DownloadState.Validated || record.ObjPath is null || record.ObjSha256 is null) return null;
        if (!store.IsOwnedObj(record.ObjPath)) throw new MeshyException(MeshyError.Storage, "Ruta de caché fuera del almacén.", record.TaskId);
        try
        {
            token.ThrowIfCancellationRequested();
            var bounds = ObjInspection.Inspect(record.ObjPath);
            string hash = await HashAsync(record.ObjPath, token).ConfigureAwait(false);
            if (hash == record.ObjSha256) return new("Meshy", record.TaskId!, record.ObjPath, bounds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    private static Uri TrustedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.IdnHost.Equals("assets.meshy.ai", StringComparison.OrdinalIgnoreCase))
            throw new MeshyException(MeshyError.UntrustedUrl, "El OBJ debe proceder de HTTPS assets.meshy.ai, sin redirecciones.");
        return uri;
    }

    private async Task<GeneratedObjResult> DownloadAsync(string taskId, string url, CancellationToken token)
    {
        var uri = TrustedUrl(url);
        string? temporary = null, completed = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Deliberately no Authorization header on the separate download client.
            using var response = await files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } effective && effective != uri)
                throw new MeshyException(MeshyError.UntrustedUrl, "La descarga cambió de destino.", taskId);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new MeshyException(MeshyError.UntrustedUrl, "La descarga intentó redirigir la solicitud.", taskId);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new MeshyException(MeshyError.ExpiredUrl, "El enlace expiró o el archivo dejó de estar disponible. No se generará otro modelo.", taskId);
            if (!response.IsSuccessStatusCode) throw HttpError(response);
            long? expected = response.Content.Headers.ContentLength;
            if (expected > ObjInspection.MaxBytes) throw TooLarge(taskId);
            temporary = Path.Combine(store.Root, Guid.NewGuid().ToString("N") + ".partial.obj");
            await using (var output = OpenOutput(temporary))
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[65536]; long total = 0;
                while (true)
                {
                    int count;
                    try { count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { throw new MeshyException(MeshyError.IncompleteDownload, "La descarga OBJ se interrumpió.", taskId); }
                    if (count == 0) break;
                    total += count;
                    if (total > ObjInspection.MaxBytes) throw TooLarge(taskId);
                    try { await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { throw new MeshyException(MeshyError.Storage, "No se pudo guardar la descarga local.", taskId); }
                }
                if (expected is { } length && total != length)
                    throw new MeshyException(MeshyError.IncompleteDownload, "El tamaño descargado no coincide con el anunciado.", taskId);
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            ObjBounds bounds;
            try { bounds = ObjInspection.Inspect(temporary); }
            catch (IOException) { throw new MeshyException(MeshyError.InvalidObj, "La descarga no contiene un OBJ utilizable por el importador existente.", taskId); }
            string hash = await HashAsync(temporary, timeout.Token).ConfigureAwait(false);
            completed = Path.Combine(store.Root, Guid.NewGuid().ToString("N") + ".obj");
            File.Move(temporary, completed, false); temporary = null;
            await UpdateAsync(taskId, r => r with { ObjPath = completed, ObjSha256 = hash,
                Download = DownloadState.Validated, LastError = null }, CancellationToken.None).ConfigureAwait(false);
            var result = new GeneratedObjResult("Meshy", taskId, completed, bounds);
            completed = null; // Ownership passes to the persistent cache only after commit.
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new MeshyException(MeshyError.NetworkTimeout, "Tiempo de descarga agotado; la tarea conserva su ID.", taskId); }
        catch (HttpRequestException)
        { throw new MeshyException(MeshyError.IncompleteDownload, "No se pudo completar la descarga OBJ.", taskId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new MeshyException(MeshyError.Storage, "Error de almacenamiento durante la descarga OBJ.", taskId); }
        finally
        {
            foreach (var path in new[] { temporary, completed })
                if (path is not null) try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static FileStream OpenOutput(string path)
        => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
    private static MeshyException TooLarge(string id) => new(MeshyError.DownloadTooLarge, "La descarga supera el límite OBJ de 50 MiB.", id);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
    }
}
