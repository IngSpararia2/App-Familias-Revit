using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BimFamilyAI.Core;

namespace BimFamilyAI.Meshy;

public sealed record MeshyStatus(GenerationProgress Progress, TimeSpan? RetryAfter);

public sealed partial class MeshyClient : IDisposable
{
    private const string Endpoint = "https://api.meshy.ai/openapi/v1/image-to-3d";
    private readonly HttpClient api, files;
    private readonly JsonTaskStore store;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public MeshyMode Mode => store.Mode;
    private MeshyClient(HttpClient api, HttpClient files, JsonTaskStore store,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        this.api = api; this.files = files; this.store = store;
        this.delay = delay ?? ((time, token) => Task.Delay(time, token));
        api.Timeout = files.Timeout = Timeout.InfiniteTimeSpan;
    }
    public static MeshyClient CreateSimulated(SimulatedMeshyHandler api, SimulatedMeshyHandler files,
        JsonTaskStore? store = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        store ??= new();
        if (store.Mode != MeshyMode.Simulated) throw new ArgumentException("Use un almacén simulado.");
        return new(new HttpClient(api), new HttpClient(files), store, delay);
    }
    public static MeshyClient CreateReal(bool enableRealNetwork = false, JsonTaskStore? store = null)
    {
        if (!enableRealNetwork) throw new MeshyException(MeshyError.RealModeDisabled, "El modo real está desactivado por defecto.");
        store ??= new(MeshyMode.Real);
        if (store.Mode != MeshyMode.Real) throw new ArgumentException("Use un almacén real separado.");
        // No redirects, cookies, shared Authorization headers, or credentials on storage requests.
        static HttpClient Transport() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        return new(Transport(), Transport(), store, null);
    }
    // Policy checks also use fake handlers: a regression must never contact production from tests.
    internal static MeshyClient CreateRealForChecks(SimulatedMeshyHandler api, SimulatedMeshyHandler files, JsonTaskStore store)
    {
        if (store.Mode != MeshyMode.Real) throw new ArgumentException("Use un almacén de comprobación real separado.");
        return new(new HttpClient(api), new HttpClient(files), store, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    }
    public void Dispose() { api.Dispose(); files.Dispose(); }
    internal static bool IsTaskId(string value) => Guid.TryParseExact(value, "D", out _);
    internal static bool IsProviderCode(string code) => code is "invalid_input" or "timeout" or "service_unavailable" or
        "server_error" or "image_too_complex" or "model_missing_uv" or "model_insufficient_uv" or
        "model_missing_texture" or "textured_split_refused" or "moderation_blocked" or "moderation_unavailable" or
        "queue_timeout" or "format_conversion_failed";

    public async Task<MeshyTaskRecord> CreateAsync(ImageGenerationRequest request, MeshyOptions? options = null,
        PaidGenerationApproval? approval = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        var image = await LocalImage.ReadAsync(request.ImagePath, cancellationToken).ConfigureAwait(false);
        string fingerprint = options.Fingerprint(image.Sha256);
        // Credential and approval checks occur before reserving, but never before local validation.
        string? credential = null;
        var existing = (await store.ReadAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(r => r.Fingerprint == fingerprint);
        if (existing is not null) return Existing(existing);
        if (Mode == MeshyMode.Real)
        {
            credential = Credential();
            if (approval is null || !approval.Consume(fingerprint))
                throw new MeshyException(MeshyError.RealModeDisabled, "La nueva generación pagada necesita confirmación explícita para esta imagen y configuración.");
        }
        var reservation = new MeshyTaskRecord { Fingerprint = fingerprint, ImageHash = image.Sha256, Options = options,
            AttemptedAt = DateTimeOffset.UtcNow, Submission = SubmissionState.Uncertain };
        bool reserved = await store.ChangeAsync(records =>
        {
            if (records.Any(r => r.Fingerprint == fingerprint)) return false;
            records.Add(reservation); return true;
        }, token: cancellationToken).ConfigureAwait(false);
        if (!reserved) return Existing((await store.ReadAsync(cancellationToken).ConfigureAwait(false)).Single(r => r.Fingerprint == fingerprint));
        string? knownId = null;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            Authorize(message, credential);
            message.Content = JsonContent.Create(new { image_url = image.ToDataUri(), model_type = options.ModelType,
                ai_model = options.AiModel, should_texture = options.ShouldTexture,
                target_polycount = options.TargetPolycount, target_formats = new[] { "obj" } });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await api.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Accepted) throw HttpError(response);
            using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            knownId = String(json.RootElement, "result");
            if (knownId is null || !IsTaskId(knownId)) { knownId = null; throw InvalidJson(); }
            // Keep a known ID even if the caller canceled immediately after the server accepted.
            var record = reservation with { TaskId = knownId, Submission = SubmissionState.Known, State = ProcessingState.Pending };
            await ReplaceAsync(record, CancellationToken.None).ConfigureAwait(false);
            return record;
        }
        catch (Exception ex) when (ex is MeshyException or HttpRequestException or OperationCanceledException or IOException)
        {
            var error = Normalize(ex);
            if (knownId is not null) throw new MeshyException(MeshyError.Storage,
                "Meshy aceptó la tarea, pero falló el guardado. Conserve el ID y recupérela sin volver a crear.", knownId);
            var state = error.HttpStatus is >= 400 and < 500 ? SubmissionState.Rejected : SubmissionState.Uncertain;
            await ReplaceAsync(reservation with { Submission = state, LastError = error.Code }, CancellationToken.None).ConfigureAwait(false);
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw error;
        }
    }
    private static MeshyTaskRecord Existing(MeshyTaskRecord record)
    {
        if (record.TaskId is not null) return record;
        throw new MeshyException(record.Submission == SubmissionState.Uncertain ? MeshyError.CreationUncertain : MeshyError.Duplicate,
            "Ya existe un intento para esta imagen y configuración. Reconcilie el intento anterior; no se enviará otro POST.");
    }
    private string Credential()
    {
        var value = Environment.GetEnvironmentVariable("MESHY_API_KEY");
        if (string.IsNullOrWhiteSpace(value)) throw new MeshyException(MeshyError.MissingCredential, "Falta MESHY_API_KEY.");
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            throw new MeshyException(MeshyError.InvalidCredential, "MESHY_API_KEY tiene un formato inválido.");
        return value;
    }
    private void Authorize(HttpRequestMessage request, string? credential = null)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Mode == MeshyMode.Simulated ? "simulation-only" : credential ?? Credential());

    public async Task<MeshyTaskRecord> RecoverAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ValidateId(taskId);
        return await store.ChangeAsync(records =>
        {
            var existing = records.SingleOrDefault(r => r.TaskId == taskId);
            if (existing is not null) return existing;
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("recovered|" + taskId)));
            var record = new MeshyTaskRecord { Fingerprint = fingerprint, TaskId = taskId, Submission = SubmissionState.Known };
            records.Add(record); return record;
        }, token: cancellationToken).ConfigureAwait(false);
    }

    public async Task<MeshyStatus> QueryAsync(string taskId, CancellationToken cancellationToken = default)
        => (await QueryWithUrlAsync(taskId, cancellationToken).ConfigureAwait(false)).Status;

    public async Task<GenerationProgress> TrackAsync(string taskId, IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await QueryAsync(taskId, cancellationToken).ConfigureAwait(false);
            progress?.Report(status.Progress);
            if (status.Progress.State == ProcessingState.Failed)
                throw new MeshyException(MeshyError.GenerationFailed, "Falló la generación en Meshy. Consulte el código de proveedor guardado.", taskId);
            if (status.Progress.State == ProcessingState.Canceled)
                throw new MeshyException(MeshyError.GenerationCanceled, "Meshy informó una tarea cancelada.", taskId);
            if (status.Progress.State == ProcessingState.Succeeded) return status.Progress;
            await delay(PollWait(status.RetryAfter), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(MeshyStatus Status, string? Url)> QueryWithUrlAsync(string taskId, CancellationToken token)
    {
        await RecoverAsync(taskId, token).ConfigureAwait(false);
        try
        {
            using var response = await GetWithRetryAsync(taskId, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            var root = json.RootElement;
            if (String(root, "id") != taskId) throw InvalidJson();
            var state = String(root, "status") switch
            {
                "PENDING" => ProcessingState.Pending, "IN_PROGRESS" => ProcessingState.InProgress,
                "SUCCEEDED" => ProcessingState.Succeeded, "FAILED" => ProcessingState.Failed,
                "CANCELED" => ProcessingState.Canceled,
                _ => throw new MeshyException(MeshyError.UnknownState, "Meshy devolvió un estado desconocido.", taskId)
            };
            int? percent = null;
            if (root.TryGetProperty("progress", out var p) && p.ValueKind != JsonValueKind.Null)
            {
                if (!p.TryGetInt32(out int number) || number is < 0 or > 100) throw InvalidJson();
                percent = number;
            }
            string? code = null;
            if (root.TryGetProperty("task_error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                string? candidate = String(e, "code");
                code = candidate is not null && IsProviderCode(candidate) ? candidate : String(e, "type");
                if (code is not null && !IsProviderCode(code)) code = null;
            }
            DateTimeOffset? created = null;
            if (root.TryGetProperty("created_at", out var c) && c.TryGetInt64(out long millis))
            {
                try { created = DateTimeOffset.FromUnixTimeMilliseconds(millis); } catch (ArgumentOutOfRangeException) { throw InvalidJson(); }
            }
            await UpdateAsync(taskId, record => record with { State = state, Progress = percent, ProviderError = code,
                CreatedAt = created ?? record.CreatedAt, LastError = state == ProcessingState.Failed ? MeshyError.GenerationFailed : null }, token).ConfigureAwait(false);
            string? url = root.TryGetProperty("model_urls", out var urls) && urls.ValueKind == JsonValueKind.Object ? String(urls, "obj") : null;
            return (new(new(taskId, state, percent, code), RetryAfter(response)), url);
        }
        catch (Exception ex) when (ex is MeshyException or HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or FormatException)
        {
            if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
            var error = Normalize(ex);
            await UpdateAsync(taskId, r => r with { LastError = error.Code }, CancellationToken.None).ConfigureAwait(false);
            throw error;
        }
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string id, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/" + id);
                Authorize(request);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                var error = HttpError(response); response.Dispose();
                if (attempt >= 2 || error.HttpStatus != 429 && error.HttpStatus < 500) throw error;
                await delay(PollWait(error.RetryAfter, attempt), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                if (attempt >= 2) throw Normalize(ex);
                await delay(PollWait(null, attempt), token).ConfigureAwait(false);
            }
        }
    }
    private static TimeSpan PollWait(TimeSpan? retryAfter, int attempt = 0)
        => TimeSpan.FromSeconds(Math.Max(retryAfter?.TotalSeconds ?? 0, 5 * Math.Pow(2, attempt)));
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var value = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
        return value is { } time && time > TimeSpan.Zero ? time : null;
    }
    private static MeshyException HttpError(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        var (code, message) = status switch
        {
            400 or 422 => (MeshyError.BadRequest, "Meshy rechazó la solicitud o sus datos."),
            401 => (MeshyError.InvalidCredential, "Meshy no acepta la credencial."),
            402 => (MeshyError.NoCredits, "Meshy informa créditos insuficientes."),
            403 => (MeshyError.Forbidden, "Meshy denegó el acceso."),
            404 => (MeshyError.NotFound, "La tarea no existe o ya no está disponible."),
            429 => (MeshyError.RateLimited, "Se alcanzó el límite de solicitudes de Meshy."),
            >= 500 => (MeshyError.ServerError, "Meshy informó un error de servidor."),
            _ => (MeshyError.BadRequest, "Respuesta HTTP inesperada de Meshy.")
        };
        return new(code, message, httpStatus: status, retryAfter: RetryAfter(response));
    }
    private static MeshyException Normalize(Exception ex) => ex switch
    {
        MeshyException known => known,
        OperationCanceledException => new(MeshyError.NetworkTimeout, "Tiempo de espera agotado. Conserve el ID; no repita la creación."),
        InvalidOperationException or FormatException => InvalidJson(),
        _ => new(MeshyError.NetworkFailure, "Fallo de conexión. Una creación puede haber sido aceptada; no se repetirá automáticamente.")
    };
    private static void ValidateId(string id)
    { if (!IsTaskId(id)) throw new MeshyException(MeshyError.InvalidInput, "Identificador de tarea inválido."); }
    private static MeshyException InvalidJson() => new(MeshyError.InvalidJson, "Meshy devolvió JSON inválido o incompleto.");
    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > 512 * 1024) throw InvalidJson();
                output.Write(buffer, 0, count);
            }
            var json = JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) { json.Dispose(); throw InvalidJson(); }
            return json;
        }
        catch (JsonException) { throw InvalidJson(); }
    }
    private Task<MeshyTaskRecord> UpdateAsync(string id, Func<MeshyTaskRecord, MeshyTaskRecord> update, CancellationToken token)
        => store.ChangeAsync(records =>
        {
            int index = records.FindIndex(r => r.TaskId == id);
            if (index < 0) throw new MeshyException(MeshyError.Storage, "No se encuentra la tarea en el registro local.", id);
            return records[index] = update(records[index]);
        }, token: token);
    private Task<MeshyTaskRecord> ReplaceAsync(MeshyTaskRecord record, CancellationToken token)
        => store.ChangeAsync(records =>
        {
            int index = records.FindIndex(r => r.Fingerprint == record.Fingerprint);
            if (index < 0) throw new MeshyException(MeshyError.Storage, "Se perdió la reserva local.", record.TaskId);
            return records[index] = record;
        }, token: token);
}
