using System.Buffers.Binary;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// The checksums a rip log gives each track, worked out as its audio streams past:
/// <list type="bullet">
/// <item>CRC32 of all its bytes, null samples included (EAC's "Copy CRC" / "Test CRC");</item>
/// <item>AccurateRip v1 and v2: sums of each sample (left | right &lt;&lt; 16) times its position in
/// the track, leaving out the disc's first 5 sectors but one sample and its last 5 sectors, which
/// drives with different read offsets can't all read;</item>
/// <item>the AccurateRip "offset finding" checksum of the track's 451st sector, by which a
/// drive's read offset can be found;</item>
/// <item>the peak level.</item>
/// </list>
/// </summary>
public sealed class TrackChecksums
{
    private const int Skip = 5 * CdToc.SamplesPerSector;
    /// <summary>The sector of each track whose own checksum AccurateRip keeps for finding offsets.</summary>
    public const int OffsetFrame = 450;

    private readonly long _samples;
    private readonly long _checkStart;
    private readonly long _checkEnd;
    private long _position; // samples so far; the next one's AccurateRip multiplier is _position + 1
    private uint _v1, _v2, _frame450;
    private uint _crc = 0xFFFFFFFF;
    private int _peak;

    /// <param name="samples">The track's length in samples (588 to a sector).</param>
    /// <param name="first">The disc's first audio track: its first 2939 samples don't count.</param>
    /// <param name="last">Its last audio track: the last 2940 samples don't count.</param>
    public TrackChecksums(long samples, bool first, bool last)
    {
        _samples = samples;
        _checkStart = first ? Skip : 0;
        _checkEnd = last ? samples - Skip : samples;
    }

    public long Samples => _position;
    public uint Crc32 => ~_crc;
    public uint AccurateRipV1 => _v1;
    public uint AccurateRipV2 => _v2;
    public uint OffsetFindingCrc => _frame450;
    /// <summary>The loudest sample, 0 to 1 (1 is full scale).</summary>
    public double Peak => _peak / 32768.0;

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 4 != 0) throw new ArgumentException("whole stereo samples (4 bytes each) only", nameof(pcm));
        _crc = Crc32Ieee.Update(_crc, pcm);
        const long frameStart = OffsetFrame * (long)CdToc.SamplesPerSector;
        for (var i = 0; i < pcm.Length; i += 4)
        {
            var sample = BinaryPrimitives.ReadUInt32LittleEndian(pcm[i..]);
            var mult = (ulong)(++_position);
            if (mult >= (ulong)_checkStart && mult <= (ulong)_checkEnd)
            {
                var product = sample * mult;
                _v1 += (uint)product;
                _v2 += (uint)product + (uint)(product >> 32);
            }
            var inFrame = _position - 1 - frameStart;
            if (inFrame is >= 0 and < CdToc.SamplesPerSector) _frame450 += sample * (uint)(inFrame + 1);
            var left = Math.Abs((int)(short)sample);
            var right = Math.Abs((int)(short)(sample >> 16));
            if (left > _peak) _peak = left;
            if (right > _peak) _peak = right;
        }
    }

    /// <summary>Every sample went through.</summary>
    public bool Complete => _position == _samples;
}

/// <summary>CRC-32 as zlib and EAC compute it (IEEE 802.3, reflected, 0xEDB88320), eight bytes at a time.</summary>
public static class Crc32Ieee
{
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        for (var i = 0; i < 256; i++)
            for (var s = 1; s < 8; s++)
                t[s * 256 + i] = (t[(s - 1) * 256 + i] >> 8) ^ t[t[(s - 1) * 256 + i] & 0xFF];
        return t;
    }

    /// <summary>Runs <paramref name="data"/> through a CRC kept inverted (start with 0xFFFFFFFF, invert at the end).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Table;
        var i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)] ^ t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)]
                ^ t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + ((two >> 8) & 0xFF)] ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
        }
        for (; i < data.Length; i++) crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);
}
