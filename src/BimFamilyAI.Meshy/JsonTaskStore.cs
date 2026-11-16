using System.Text.Json;
using BimFamilyAI.Core;

namespace BimFamilyAI.Meshy;

public enum SubmissionState { Uncertain, Known, Rejected }
public enum DownloadState { None, Validated, Failed }
public sealed record MeshyTaskRecord
{
    public required string Fingerprint { get; init; }
    public string Provider { get; init; } = "Meshy";
    public string? TaskId { get; init; }
    public string? ImageHash { get; init; }
    public MeshyOptions? Options { get; init; }
    public DateTimeOffset? AttemptedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public SubmissionState Submission { get; init; }
    public ProcessingState? State { get; init; }
    public int? Progress { get; init; }
    public string? ObjPath { get; init; }
    public string? ObjSha256 { get; init; }
    public DownloadState Download { get; init; }
    public MeshyError? LastError { get; init; }
    // Only documented, allowlisted provider codes; never a raw response/message.
    public string? ProviderError { get; init; }
}

/// <summary>One atomic journal, locked across processes. Corruption fails closed to protect reservations.</summary>
public sealed class JsonTaskStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root { get; }
    public MeshyMode Mode { get; }
    public JsonTaskStore(MeshyMode mode = MeshyMode.Simulated, string? dataDirectory = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        Root = Path.GetFullPath(Path.Combine(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIM Family AI", "Meshy"), mode.ToString()));
    }
    public Task<IReadOnlyList<MeshyTaskRecord>> ReadAsync(CancellationToken token = default)
        => ChangeAsync<IReadOnlyList<MeshyTaskRecord>>(records => records.ToArray(), false, token);

    internal async Task<T> ChangeAsync<T>(Func<List<MeshyTaskRecord>, T> change, bool write = true, CancellationToken token = default)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Root);
            await using var lease = await LockAsync(token).ConfigureAwait(false);
            var path = Path.Combine(Root, "tasks.json");
            List<MeshyTaskRecord> records = [];
            if (File.Exists(path))
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (input.Length > 4 * 1024 * 1024) throw new JsonException();
                records = await JsonSerializer.DeserializeAsync<List<MeshyTaskRecord>>(input, Json, token).ConfigureAwait(false) ?? throw new JsonException();
                ValidateRecords(records);
            }
            var result = change(records);
            if (write)
            {
                ValidateRecords(records);
                temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, records, Json, token).ConfigureAwait(false);
                    if (output.Length > 4 * 1024 * 1024) throw new JsonException();
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(true);
                }
                File.Move(temporary, path, true);
                temporary = null;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new MeshyException(MeshyError.Storage, "No se pudo leer o guardar el registro local. Si está corrupto, consérvelo y reconcilie las tareas antes de generar de nuevo."); }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ValidateRecords(List<MeshyTaskRecord> records)
    {
        if (records.Count > 10000 || records.Any(r => r is null || !IsHash(r.Fingerprint) ||
            r.Provider != "Meshy" || r.TaskId is { } id && !MeshyClient.IsTaskId(id) ||
            !Enum.IsDefined(r.Submission) || !Enum.IsDefined(r.Download) ||
            r.State is { } state && !Enum.IsDefined(state) ||
            r.LastError is { } error && !Enum.IsDefined(error) ||
            r.Progress is < 0 or > 100 || r.ImageHash is { } hash && !IsHash(hash) ||
            r.ObjSha256 is { } objHash && !IsHash(objHash) ||
            r.ObjPath is { } obj && !IsOwnedObj(obj) ||
            r.Options is { } options && options.TargetPolycount is < 100 or > 15000 ||
            r.ProviderError is { } code && !MeshyClient.IsProviderCode(code)) ||
            records.Select(r => r.Fingerprint).Distinct().Count() != records.Count ||
            records.Where(r => r.TaskId != null).Select(r => r.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count(r => r.TaskId != null))
            throw new JsonException();
    }
    internal bool IsOwnedObj(string path) => Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        Path.GetDirectoryName(Path.GetFullPath(path)) == Root && Path.GetExtension(path) == ".obj";
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(Root, "tasks.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
}
