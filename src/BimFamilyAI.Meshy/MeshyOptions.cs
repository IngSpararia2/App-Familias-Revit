using System.Security.Cryptography;
using System.Text;

namespace BimFamilyAI.Meshy;

public enum MeshyMode { Simulated, Real }
public sealed record MeshyOptions(int TargetPolycount = 4000)
{
    public string ModelType => "smart-topology";
    public string AiModel => "meshy-t2";
    public bool ShouldTexture => false;
    public void Validate()
    {
        if (TargetPolycount is < 100 or > 15000)
            throw new MeshyException(MeshyError.InvalidInput, "Meshy T2 admite de 100 a 15000 caras.");
    }
    public string Fingerprint(string imageHash)
    {
        Validate();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"image-to-3d|{imageHash}|{ModelType}|{AiModel}|{TargetPolycount}|no-texture|obj")));
    }
}

/// <summary>Future UI must call this only after showing cost and obtaining explicit confirmation.</summary>
public sealed class PaidGenerationApproval
{
    private readonly string fingerprint;
    private int consumed;
    private PaidGenerationApproval(string fingerprint) => this.fingerprint = fingerprint;
    public static PaidGenerationApproval ConfirmNewPaidGeneration(string imageHash, MeshyOptions options)
        => new(options.Fingerprint(imageHash));
    internal bool Consume(string expected) => fingerprint == expected && Interlocked.Exchange(ref consumed, 1) == 0;
}

public enum MeshyError
{
    InvalidInput, MissingCredential, InvalidCredential, NoCredits, Forbidden, NotFound, BadRequest,
    RateLimited, ServerError, NetworkTimeout, NetworkFailure, InvalidJson, UnknownState,
    GenerationFailed, GenerationCanceled, MissingResult, UntrustedUrl, ExpiredUrl,
    IncompleteDownload, DownloadTooLarge, InvalidObj, Storage, CreationUncertain, Duplicate, RealModeDisabled
}

/// <summary>Messages are local constants, never raw HTTP bodies, URLs, tokens or image data.</summary>
public sealed class MeshyException(MeshyError code, string message, string? taskId = null,
    int? httpStatus = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public MeshyError Code { get; } = code;
    public string? TaskId { get; } = taskId;
    public int? HttpStatus { get; } = httpStatus;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>No network handler is constructed on the simulated path.</summary>
public abstract class SimulatedMeshyHandler : HttpMessageHandler;
