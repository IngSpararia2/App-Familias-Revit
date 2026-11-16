using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BimFamilyAI.Meshy;

/// <summary>Bounded container/header inspection, not a replacement for a full image decoder.</summary>
public sealed class LocalImage
{
    public const int MaxBytes = 20_000_000;
    public const long MaxPixels = 178_956_970;
    private readonly byte[] bytes;
    public string MediaType { get; }
    public int Width { get; }
    public int Height { get; }
    public string Sha256 { get; }
    private LocalImage(byte[] bytes, string mediaType, int width, int height)
    {
        this.bytes = bytes; MediaType = mediaType; Width = width; Height = height;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    public string ToDataUri() => $"data:{MediaType};base64,{Convert.ToBase64String(bytes)}";
    public override string ToString() => $"{MediaType} {Width}x{Height} SHA256={Sha256}";

    public static async Task<LocalImage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length is <= 0 or > MaxBytes) throw Invalid();
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var (mime, width, height) = Inspect(bytes);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!(mime == "image/png" && extension == ".png" || mime == "image/jpeg" && extension is ".jpg" or ".jpeg" ||
                  mime == "image/webp" && extension == ".webp")) throw Invalid();
            if (width < 32 || height < 32 || (long)width * height > MaxPixels) throw Invalid();
            return new(bytes, mime, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { throw Invalid(); }
    }

    private static MeshyException Invalid() => new(MeshyError.InvalidInput,
        "Imagen ilegible o inválida: JPG/PNG/WebP, máximo 20 MB, mínimo 32 × 32 y máximo 178956970 píxeles.");
    private static (string, int, int) Inspect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 33 && b[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }))
        {
            int p = 8, width = 0, height = 0; bool data = false;
            while (p <= b.Length - 12)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
                if (size > (uint)(b.Length - p - 12)) throw Invalid();
                if (Crc(b.Slice(p+4,(int)size+4)) != BinaryPrimitives.ReadUInt32BigEndian(b[(p+8+(int)size)..])) throw Invalid();
                var kind = b.Slice(p+4,4);
                if (p == 8)
                {
                    if (!kind.SequenceEqual("IHDR"u8) || size != 13) throw Invalid();
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(b[(p+8)..]));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(b[(p+12)..]));
                    byte depth = b[p+16], color = b[p+17];
                    bool validDepth = color switch { 0 => depth is 1 or 2 or 4 or 8 or 16,
                        2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
                    if (!validDepth || b[p+18] != 0 || b[p+19] != 0 || b[p+20] > 1) throw Invalid();
                }
                if (kind.SequenceEqual("IDAT"u8) && size > 0) data = true;
                p += (int)size + 12;
                if (kind.SequenceEqual("IEND"u8))
                {
                    if (!data || size != 0 || p != b.Length) throw Invalid();
                    return ("image/png", width, height);
                }
            }
            throw Invalid();
        }
        if (b.Length >= 4 && b[0] == 255 && b[1] == 216 && b[^2] == 255 && b[^1] == 217)
        {
            int p = 2, width = 0, height = 0;
            while (p < b.Length - 2)
            {
                if (b[p++] != 255) throw Invalid();
                while (p < b.Length && b[p] == 255) p++;
                if (p >= b.Length - 2) throw Invalid();
                byte marker = b[p++];
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                int size = BinaryPrimitives.ReadUInt16BigEndian(b[p..]);
                if (size < 2 || p + size > b.Length - 2) throw Invalid();
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (size < 8) throw Invalid();
                    height = BinaryPrimitives.ReadUInt16BigEndian(b[(p+3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(b[(p+5)..]);
                }
                if (marker == 0xda && width > 0 && height > 0 && p + size < b.Length - 2)
                    return ("image/jpeg", width, height);
                p += size;
            }
            throw Invalid();
        }
        if (b.Length >= 30 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8,4).SequenceEqual("WEBP"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) == b.Length - 8)
        {
            int p = 12, width = 0, height = 0; bool image = false;
            while (p <= b.Length - 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[(p+4)..]);
                if (size > (uint)(b.Length - p - 8)) throw Invalid();
                var kind = b.Slice(p,4); var chunk = b.Slice(p+8,(int)size);
                if (kind.SequenceEqual("VP8X"u8))
                {
                    if (size != 10 || (chunk[0] & 2) != 0) throw Invalid(); // No animated WebP.
                    width = 1 + U24(chunk[4..]); height = 1 + U24(chunk[7..]);
                }
                else if (kind.SequenceEqual("VP8 "u8))
                {
                    if (size < 10 || !chunk.Slice(3,3).SequenceEqual(new byte[] { 157,1,42 })) throw Invalid();
                    int w = BinaryPrimitives.ReadUInt16LittleEndian(chunk[6..]) & 0x3fff;
                    int h = BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]) & 0x3fff;
                    if (width != 0 && (width != w || height != h)) throw Invalid();
                    width = w; height = h; image = true;
                }
                else if (kind.SequenceEqual("VP8L"u8))
                {
                    if (size < 5 || chunk[0] != 47) throw Invalid();
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(chunk[1..]);
                    if (bits >> 29 != 0) throw Invalid();
                    int w = 1 + (int)(bits & 0x3fff), h = 1 + (int)((bits >> 14) & 0x3fff);
                    if (width != 0 && (width != w || height != h)) throw Invalid();
                    width = w; height = h; image = true;
                }
                p = checked(p + 8 + (int)size + (int)(size & 1));
            }
            if (image && p == b.Length) return ("image/webp", width, height);
        }
        throw Invalid();
    }
    private static int U24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte b in bytes) { crc ^= b; for (int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
        return ~crc;
    }
}
