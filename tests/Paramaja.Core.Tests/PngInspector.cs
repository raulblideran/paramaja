using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Tests;

/// <summary>Minimal PNG reader to verify screenshots really are uncompressed.</summary>
public sealed record PngInfo(int Width, int Height, int BitDepth, int ColorType, int IdatBytes, bool StoredDeflateBlocks, IReadOnlyList<string> Chunks)
{
    public int Channels => ColorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 1 };
    public long RawImageBytes => (long)Height * (1 + (long)Width * Channels * BitDepth / 8);

    public static PngInfo Read(string path)
    {
        var data = File.ReadAllBytes(path);
        ReadOnlySpan<byte> sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (!data.AsSpan(0, 8).SequenceEqual(sig)) throw new InvalidDataException("not a PNG");

        int w = 0, h = 0, depth = 0, type = 0, idat = 0;
        var chunks = new List<string>();
        var idatData = new List<byte>();
        for (var pos = 8; pos + 8 <= data.Length;)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var kind = Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, len);
            chunks.Add(kind);
            if (kind == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(body);
                h = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                depth = body[8];
                type = body[9];
            }
            else if (kind == "IDAT")
            {
                idat += len;
                if (idatData.Count < 16) idatData.AddRange(body[..Math.Min(len, 16)].ToArray());
            }
            pos += 12 + len;
        }
        // zlib header (2 bytes) then the first deflate block header: BTYPE bits 1-2 == 00 means "stored".
        var stored = idatData.Count >= 3 && ((idatData[2] >> 1) & 0b11) == 0;
        return new PngInfo(w, h, depth, type, idat, stored, chunks);
    }
}
