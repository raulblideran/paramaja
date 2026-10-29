using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Playback.Sacd;

/// <summary>
/// One track of an SACD area as a DSDIFF (.dff) file, made on the fly from the image, which
/// FFmpeg reads: plain DSD in a "DSD " chunk, DST-compressed frames as "DSTF" chunks (FFmpeg
/// decodes both). Nothing is extracted to disk.
/// <para>
/// An SACD keeps its audio in frames of 1/75 s spread over 2048-byte sectors: each sector starts
/// with a header (its packet and frame-start counts, and whether it's DST), then each packet's
/// size and type (audio, supplementary, padding), then each frame start's time code, then the
/// packets. A frame is the audio packets from one marked as a frame start up to the next.
/// <see cref="FrameIndex"/> finds where each frame's pieces are, reading forward as far as asked.
/// </para>
/// </summary>
public sealed class SacdTrackStream : Stream
{
    private readonly Stream _image;
    private readonly FrameIndex _index;
    private readonly SacdArea _area;
    private readonly byte[] _header;
    private readonly int _frameBytes; // plain DSD: every frame is this size
    private long _position;
    private int _cachedFrame = -1;
    private byte[] _cache = [];

    public SacdTrackStream(Stream image, SacdArea area, SacdTrack track)
    {
        _image = image;
        _area = area;
        _index = new FrameIndex(image, track, area.Channels);
        _frameBytes = area.Channels * SacdImage.DsdBytesPerChannel;
        _header = Header(area, track.Frames, _frameBytes, track.LengthSectors);
    }

    /// <summary>The DSDIFF header: format version, sample rate, channels, compression, and the data chunk's own header.</summary>
    internal static byte[] Header(SacdArea area, int frames, int frameBytes, long lengthSectors)
    {
        var prop = new MemoryStream();
        Chunk(prop, "FS  ", w => w.Write(BigEndian((uint)area.SampleRate)));
        Chunk(prop, "CHNL", w =>
        {
            w.Write(BigEndian((ushort)area.Channels));
            foreach (var id in ChannelIds(area)) w.Write(Encoding.ASCII.GetBytes(id));
        });
        Chunk(prop, "CMPR", w =>
        {
            var name = area.Dst ? "DST Encoded" : "not compressed";
            w.Write(Encoding.ASCII.GetBytes(area.Dst ? "DST " : "DSD "));
            w.Write((byte)name.Length);
            w.Write(Encoding.ASCII.GetBytes(name));
        });

        var body = new MemoryStream();
        body.Write("DSD "u8);
        Chunk(body, "FVER", w => w.Write(BigEndian(0x01050000u)));
        Chunk(body, "PROP", w =>
        {
            w.Write("SND "u8);
            w.Write(prop.ToArray());
        });
        if (area.Dst)
        {
            // Every frame takes its compressed size plus a 12-byte chunk header (and a pad byte):
            // far less than the track's sectors hold, so the size given is a ceiling.
            var ceiling = 18 + lengthSectors * SacdImage.SectorSize + frames * 13L;
            body.Write("DST "u8);
            body.Write(BigEndian((ulong)ceiling));
            Chunk(body, "FRTE", w =>
            {
                w.Write(BigEndian((uint)frames));
                w.Write(BigEndian((ushort)SacdImage.FrameRate));
            });
        }
        else
        {
            body.Write("DSD "u8);
            body.Write(BigEndian((ulong)frames * (ulong)frameBytes));
        }

        var head = new MemoryStream();
        head.Write("FRM8"u8);
        var dataSize = area.Dst ? 18 + lengthSectors * SacdImage.SectorSize + frames * 13L : (long)frames * frameBytes;
        head.Write(BigEndian((ulong)(body.Length + dataSize - (area.Dst ? 18 : 0))));
        head.Write(body.ToArray());
        return head.ToArray();
    }

    /// <summary>DSDIFF's loudspeaker names, in the SACD's channel order (FFmpeg maps these to 2.0, 5.0 and 5.1).</summary>
    private static string[] ChannelIds(SacdArea area) => area.Channels switch
    {
        2 when area.Kind == SacdAreaKind.Stereo => ["SLFT", "SRGT"],
        2 => ["MLFT", "MRGT"],
        3 => ["MLFT", "MRGT", "C   "],
        4 => ["MLFT", "MRGT", "LS  ", "RS  "],
        5 => ["MLFT", "MRGT", "C   ", "LS  ", "RS  "],
        _ => ["MLFT", "MRGT", "C   ", "LFE ", "LS  ", "RS  "],
    };

    private static void Chunk(Stream to, string id, Action<BinaryWriter> write)
    {
        var data = new MemoryStream();
        using (var w = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true)) write(w);
        to.Write(Encoding.ASCII.GetBytes(id));
        to.Write(BigEndian((ulong)data.Length));
        to.Write(data.ToArray());
        if (data.Length % 2 == 1) to.WriteByte(0); // chunks are padded to even sizes
    }

    private static byte[] BigEndian(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] BigEndian(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] BigEndian(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }

    // ---- Stream ---------------------------------------------------------------------------------

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    /// <summary>
    /// Plain DSD: exact. DST frames vary in size, so the length is known only once every frame has
    /// been found; until then, null.
    /// </summary>
    public long? KnownLength => _area.Dst
        ? _index.Complete ? _header.Length + DstFrameOffset(_index.Count) : null
        : _header.Length + (long)_index.ExpectedFrames * _frameBytes;

    /// <summary>For DST, finds every frame first (reading the whole track); mpv asks <see cref="KnownLength"/> instead.</summary>
    public override long Length
    {
        get
        {
            if (KnownLength is { } known) return known;
            _index.EnsureFrames(int.MaxValue);
            return KnownLength ?? 0;
        }
    }

    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0, value);
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => Length + offset,
        _ => offset,
    };

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0)
        {
            var n = ReadAt(_position, buffer);
            if (n <= 0) break;
            _position += n;
            total += n;
            buffer = buffer[n..];
        }
        return total;
    }

    /// <summary>Some of the bytes at <paramref name="position"/> (at most to the end of a frame); 0 at the end.</summary>
    private int ReadAt(long position, Span<byte> buffer)
    {
        if (position < _header.Length)
        {
            var n = (int)Math.Min(buffer.Length, _header.Length - position);
            _header.AsSpan((int)position, n).CopyTo(buffer);
            return n;
        }
        var data = position - _header.Length;
        return _area.Dst ? ReadDst(data, buffer) : ReadDsd(data, buffer);
    }

    private int ReadDsd(long data, Span<byte> buffer)
    {
        var frame = data / _frameBytes;
        if (frame >= _index.ExpectedFrames) return 0;
        var within = (int)(data % _frameBytes);
        var bytes = Frame((int)frame);
        var n = Math.Min(buffer.Length, _frameBytes - within);
        if (bytes is null || bytes.Length != _frameBytes)
            buffer[..n].Fill(DsdSilence); // a frame the image lacks or that is damaged
        else
            bytes.AsSpan(within, n).CopyTo(buffer);
        return n;
    }

    /// <summary>DSD's idle pattern: as many ones as zeros, which decodes to silence.</summary>
    internal const byte DsdSilence = 0x69;

    /// <summary>Where frame <paramref name="frame"/>'s DSTF chunk starts, from the end of the header.</summary>
    private long DstFrameOffset(int frame) => _index.DstOffset(frame);

    private int ReadDst(long data, Span<byte> buffer)
    {
        var frame = _index.DstFrameAt(data);
        if (frame < 0) return 0;
        var start = DstFrameOffset(frame);
        var bytes = Frame(frame)!;
        // Its chunk: "DSTF", the size, the frame, a pad byte to an even size.
        var chunk = new byte[12 + bytes.Length + (bytes.Length & 1)];
        Encoding.ASCII.GetBytes("DSTF").CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt64BigEndian(chunk.AsSpan(4), (ulong)bytes.Length);
        bytes.CopyTo(chunk, 12);
        var within = (int)(data - start);
        var n = Math.Min(buffer.Length, chunk.Length - within);
        chunk.AsSpan(within, n).CopyTo(buffer);
        return n;
    }

    private byte[]? Frame(int frame)
    {
        if (frame == _cachedFrame) return _cache;
        var bytes = _index.ReadFrame(frame);
        if (bytes is null) return null;
        _cachedFrame = frame;
        _cache = bytes;
        return bytes;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image.Dispose();
        base.Dispose(disposing);
    }

    // ---- Where each frame is --------------------------------------------------------------------

    /// <summary>
    /// The track's frames: for each, the pieces (file offset, length) it's made of. Built as far as
    /// it's needed, a few hundred sectors at a time, from the track's first sector on: what comes
    /// before its first frame start belongs to the track before. A frame is complete when it
    /// holds a whole 1/75 s (plain DSD) or as many packets as its frame info said (DST); one that
    /// isn't (a damaged sector) is left out.
    /// </summary>
    internal sealed class FrameIndex
    {
        private const int ChunkSectors = 256;
        private const int MaxPacket = 2045;

        private readonly Stream _image;
        private readonly int _channels;
        private readonly long _firstSector;
        /// <summary>The track's last frame may end a little past its last sector.</summary>
        private readonly long _endSector;

        private readonly List<(int First, int Count, int Size)> _frames = [];
        private readonly List<(long Offset, int Length)> _pieces = [];
        /// <summary>
        /// DST: where each frame's chunk starts, from the end of the header (which ends with the
        /// FRTE chunk); one more than there are frames: the last is where the data ends.
        /// </summary>
        private readonly List<long> _dstOffsets = [0];

        private long _nextSector;
        // The frame being put together.
        private bool _started, _dst;
        private int _firstPiece, _size, _packetsLeft;

        public int ExpectedFrames { get; }
        public int Count => _frames.Count;
        public bool Complete { get; private set; }

        public FrameIndex(Stream image, SacdTrack track, int channels)
        {
            _image = image;
            _channels = channels;
            _firstSector = _nextSector = track.StartSector;
            _endSector = track.StartSector + track.LengthSectors + 32;
            ExpectedFrames = track.Frames > 0 ? track.Frames : int.MaxValue;
        }

        /// <summary>Finds frames until there are <paramref name="count"/> (or the track ends); false if there aren't that many.</summary>
        public bool EnsureFrames(int count)
        {
            while (_frames.Count < count && !Complete) ScanMore();
            return _frames.Count >= count;
        }

        public byte[]? ReadFrame(int frame)
        {
            if (frame < 0 || !EnsureFrames(frame + 1)) return null;
            var (first, pieces, size) = _frames[frame];
            var bytes = new byte[size];
            var at = 0;
            for (var i = first; i < first + pieces; i++)
            {
                var (offset, length) = _pieces[i];
                _image.Position = offset;
                _image.ReadExactly(bytes, at, length);
                at += length;
            }
            return bytes;
        }

        /// <summary>DST: the start of frame <paramref name="frame"/>'s chunk, from the end of the header.</summary>
        public long DstOffset(int frame)
        {
            EnsureFrames(frame);
            return _dstOffsets[Math.Min(frame, _dstOffsets.Count - 1)];
        }

        /// <summary>DST: the frame whose chunk holds byte <paramref name="data"/> (counted from the end of the header); -1 past the last.</summary>
        public int DstFrameAt(long data)
        {
            while (_dstOffsets[^1] <= data && !Complete) ScanMore();
            if (data >= _dstOffsets[^1]) return -1;
            var i = _dstOffsets.BinarySearch(data);
            return i >= 0 ? i : ~i - 1;
        }

        private void ScanMore()
        {
            if (_nextSector >= _endSector || _frames.Count >= ExpectedFrames) { Finish(); return; }
            var count = (int)Math.Min(ChunkSectors, _endSector - _nextSector);
            var offset = _nextSector * SacdImage.SectorSize;
            var available = (int)Math.Min(count, Math.Max(0, (_image.Length - offset) / SacdImage.SectorSize));
            if (available <= 0) { Finish(); return; }
            var buffer = new byte[available * SacdImage.SectorSize];
            _image.Position = offset;
            _image.ReadExactly(buffer);
            for (var s = 0; s < available && _frames.Count < ExpectedFrames; s++)
                Sector(buffer.AsSpan(s * SacdImage.SectorSize, SacdImage.SectorSize), offset + (long)s * SacdImage.SectorSize);
            _nextSector += available;
            if (_frames.Count >= ExpectedFrames || _nextSector >= _endSector) Finish();
        }

        private void Finish() => Complete = true;

        /// <summary>One audio sector's packets (see sacd-ripper's scarletbook_process_frames).</summary>
        private void Sector(ReadOnlySpan<byte> sector, long sectorOffset)
        {
            var header = sector[0];
            var dst = (header & 1) != 0;
            var frameInfos = (header >> 2) & 7;
            var packets = (header >> 5) & 7;
            var p = 1;
            Span<(bool Start, int Type, int Length)> info = stackalloc (bool, int, int)[7];
            for (var i = 0; i < packets; i++, p += 2)
                info[i] = ((sector[p] & 0x80) != 0, (sector[p] >> 3) & 7, (sector[p] & 7) << 8 | sector[p + 1]);
            // Frame infos: a time code (3 bytes), and for DST a fourth with the frame's packet count.
            Span<int> frameSectors = stackalloc int[7];
            for (var i = 0; i < frameInfos; i++)
            {
                if (dst) frameSectors[i] = (sector[p + 3] >> 2) & 0x1F;
                p += dst ? 4 : 3;
            }
            var frameInfo = 0;
            for (var i = 0; i < packets; i++)
            {
                var (start, type, length) = info[i];
                if (length > MaxPacket || p + length > sector.Length) { _started = false; return; } // damaged
                if (type == 2) // audio
                {
                    if (start)
                    {
                        _started = true;
                        _dst = dst;
                        _size = 0;
                        _firstPiece = _pieces.Count;
                        _packetsLeft = frameInfo < frameInfos ? frameSectors[frameInfo] : 0;
                        frameInfo++;
                    }
                    if (_started)
                    {
                        _pieces.Add((sectorOffset + p, length));
                        _size += length;
                        if (_dst) _packetsLeft--;
                        if (_dst ? _packetsLeft <= 0 : _size >= _channels * SacdImage.DsdBytesPerChannel) EndFrame();
                    }
                }
                p += length;
            }
        }

        private void EndFrame()
        {
            _started = false;
            var pieces = _pieces.Count - _firstPiece;
            if (!_dst && _size != _channels * SacdImage.DsdBytesPerChannel)
            {
                _pieces.RemoveRange(_firstPiece, pieces); // more than a frame: damaged
                return;
            }
            _frames.Add((_firstPiece, pieces, _size));
            _dstOffsets.Add(_dstOffsets[^1] + 12 + _size + (_size & 1));
        }
    }
}
