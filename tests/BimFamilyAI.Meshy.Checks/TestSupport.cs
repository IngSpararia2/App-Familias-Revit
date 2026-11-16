using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BimFamilyAI.Meshy;

internal sealed class FakeHandler : SimulatedMeshyHandler
{
    public Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Steps { get; } = new();
    public int Posts { get; private set; }
    public int Gets { get; private set; }
    public int Calls => Posts + Gets;
    public void Add(Func<HttpRequestMessage, HttpResponseMessage> step) => Steps.Enqueue((request, _) => Task.FromResult(step(request)));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Post) Posts++;
        else if (request.Method == HttpMethod.Get) Gets++;
        else throw new Exception("Unexpected HTTP method");
        if (Steps.Count == 0) throw new Exception("Unexpected simulated HTTP request");
        return Steps.Dequeue()(request, token);
    }
    public static HttpResponseMessage Json(object body, int status = 200, int? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (retryAfter is { } seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }
    public static HttpResponseMessage State(string id, string state = "SUCCEEDED", int? progress = 100,
        string? url = "https://assets.meshy.ai/test/model.obj?Expires=123&Signature=private-test-signature", int? retry = null)
        => Json(new { id, status = state, progress, created_at = 1_700_000_000_000L, model_urls = new { obj = url } }, retryAfter: retry);
}

internal sealed class Scenario : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "BFAI-Meshy-check-" + Guid.NewGuid().ToString("N"));
    public JsonTaskStore Store { get; }
    public FakeHandler Api { get; } = new();
    public FakeHandler Files { get; } = new();
    public List<TimeSpan> Waits { get; } = [];
    public MeshyClient Client { get; }
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Image { get; }
    public Scenario(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        Image = Path.Combine(DirectoryPath, "photo.png");
        File.WriteAllBytes(Image, ImageFixture.Png(32,32));
        Store = new(dataDirectory: DirectoryPath);
        Client = MeshyClient.CreateSimulated(Api, Files, Store, delay ?? ((time, token) =>
        { token.ThrowIfCancellationRequested(); Waits.Add(time); return Task.CompletedTask; }));
    }
    public void CreateResponse() => Api.Add(_ => FakeHandler.Json(new { result = Id }, 202));
    public void Dispose() { Client.Dispose(); Directory.Delete(DirectoryPath, true); }
}

internal static class ImageFixture
{
    public static byte[] Jpeg()
    {
        // A baseline, one-component 32x32 gray image: sixteen zero DCT blocks.
        using var output=new MemoryStream();output.Write(new byte[] {255,216});
        output.Write(new byte[] {255,219,0,67,0});output.Write(Enumerable.Repeat((byte)1,64).ToArray());
        output.Write(new byte[] {255,192,0,11,8,0,32,0,32,1,1,17,0});
        output.Write(new byte[] {255,196,0,38});
        foreach(byte table in new byte[] {0,16})
        {output.WriteByte(table);output.WriteByte(1);output.Write(new byte[15]);output.WriteByte(0);}
        output.Write(new byte[] {255,218,0,8,1,1,0,0,63,0,0,0,0,0,255,217});
        return output.ToArray();
    }
    public static byte[] Webp()
    {
        // A 32x32 opaque constant-color VP8L image. Each channel has one Huffman symbol.
        var bits=new List<int>();
        void Write(uint value,int n) {for(int i=0;i<n;i++)bits.Add((int)(value>>i)&1);}
        Write(0,3); // no transforms, color cache or meta-Huffman image
        foreach(uint symbol in new uint[] {128,128,128,255}) {Write(1,1);Write(0,1);Write(1,1);Write(symbol,8);}
        Write(1,1);Write(0,1);Write(0,1);Write(0,1); // unused distance table
        var payload=new byte[5+(bits.Count+7)/8];payload[0]=47;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1),31u|(31u<<14));
        for(int i=0;i<bits.Count;i++)payload[5+i/8]|=(byte)(bits[i]<<(i%8));
        using var output=new MemoryStream();output.Write("RIFF"u8);
        var size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,12+payload.Length+(payload.Length&1));output.Write(size);
        output.Write("WEBPVP8L"u8);BinaryPrimitives.WriteInt32LittleEndian(size,payload.Length);output.Write(size);output.Write(payload);
        if((payload.Length&1)!=0)output.WriteByte(0);return output.ToArray();
    }
    public static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream(); output.Write(new byte[] {137,80,78,71,13,10,26,10});
        var ihdr = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(output,"IHDR",ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            // Oversized dimension fixtures only exercise metadata, without allocating their pixel plane.
            int rows = Math.Min(height,64), columns = Math.Min(width,64);
            var row = new byte[1 + columns * 3];
            for (int y=0;y<rows;y++) zlib.Write(row);
        }
        Chunk(output,"IDAT",compressed.ToArray()); Chunk(output,"IEND",[]); return output.ToArray();
    }
    private static void Chunk(Stream output, string type, byte[] payload)
    {
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length,payload.Length); output.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(payload).ToArray(); output.Write(body);
        uint crc=0xffffffff;
        foreach (byte b in body) { crc ^= b; for(int i=0;i<8;i++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
        BinaryPrimitives.WriteUInt32BigEndian(length,~crc); output.Write(length);
    }
}

internal sealed class BrokenStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("sensitive transport details");
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(new IOException("sensitive transport details"));
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class OversizeStream : Stream
{
    private long remaining = BimFamilyAI.Core.ObjInspection.MaxBytes + 1;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { int n=(int)Math.Min(remaining,count); Array.Clear(buffer,offset,n); remaining-=n; return n; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); int n=(int)Math.Min(remaining,buffer.Length); buffer.Span[..n].Clear(); remaining-=n; return ValueTask.FromResult(n); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
