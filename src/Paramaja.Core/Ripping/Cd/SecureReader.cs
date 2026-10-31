using System.Diagnostics;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>Where a track's read is: which pass, how far, how fast.</summary>
/// <param name="Speed">Times real time (1× = 75 sectors a second).</param>
public readonly record struct ReadProgress(ReadPass Pass, double Fraction, double Speed, int Rereads);

public enum ReadPass { Test, Copy }

/// <summary>A track as read: its checksums from both passes, its quality and anything that never read the same twice.</summary>
/// <param name="Quality">The share of reads that were needed, from 1 (each sector read just twice, once per pass) down.</param>
/// <param name="Suspicious">Where, in sectors from the track's start, a block never read the same way twice.</param>
public sealed record TrackRead(uint TestCrc, TrackChecksums Copy, double Quality, IReadOnlyList<int> Suspicious, int Rereads,
    TimeSpan Elapsed, double Speed)
{
    public bool Ok => Suspicious.Count == 0 && TestCrc == Copy.Crc32;
}

/// <summary>
/// Reads a track securely, as EAC's secure mode with test &amp; copy does:
/// <list type="bullet">
/// <item><b>Offset correction</b>: the drive hands out audio a fixed number of samples off (its read
/// offset, from AccurateRip's list), so the track is read that many samples later. What lies
/// outside the disc's audio (before its start, in the lead-out) is silence: no overreading.</item>
/// <item><b>Test &amp; copy</b>: the track is read twice, end to end, and each block's checksum
/// from the first pass is held against the second's. By the time a block is read again the drive
/// has read the rest of the track, far more than its cache holds, so the second read is a real
/// one; a track too short for that gets a read elsewhere on the disc first.</item>
/// <item><b>Re-reads</b>: a block the two passes disagree on (or that failed to read) is read
/// again, each time after flushing the drive's cache, until two reads agree, up to 20 reads. A
/// block that never reads the same twice keeps its most frequent version and is reported as
/// suspicious.</item>
/// </list>
/// </summary>
public sealed class SecureReader
{
    /// <summary>Sectors a read asks for: 61 KB, under the 64 KB some adapters take at a time.</summary>
    public const int BlockSectors = 26;
    public const int MaxReads = 20;

    private readonly ICdDrive _drive;
    private readonly CdToc _toc;
    private readonly int _offset;
    private readonly int _cacheSectors;
    private readonly int _readableEnd;

    /// <param name="readOffset">The drive's read offset, in samples.</param>
    public SecureReader(ICdDrive drive, CdToc toc, int readOffset)
    {
        _drive = drive;
        _toc = toc;
        _offset = readOffset;
        // The drive's buffer as it states it (8 MB where it doesn't), in sectors.
        var bufferBytes = (drive.Features.BufferKb > 0 ? drive.Features.BufferKb : 8192) * 1024L;
        _cacheSectors = (int)Math.Min(bufferBytes / CdToc.SectorBytes + 1, 20_000);
        _readableEnd = toc.AudioLeadOutLba;
    }

    /// <summary>
    /// Reads <paramref name="track"/> and writes its audio (offset-corrected, 16-bit stereo
    /// little-endian) to <paramref name="output"/>.
    /// </summary>
    public TrackRead ReadTrack(CdTrack track, Stream output, IProgress<ReadProgress>? progress = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        long first = (long)track.StartLba * CdToc.SamplesPerSector + _offset;
        long samples = (long)_toc.Sectors(track) * CdToc.SamplesPerSector;
        var startSector = (int)Math.Floor(first / (double)CdToc.SamplesPerSector);
        var endSector = (int)Math.Ceiling((first + samples) / (double)CdToc.SamplesPerSector);
        var skip = (int)(first - (long)startSector * CdToc.SamplesPerSector) * 4;
        var blocks = (endSector - startSector + BlockSectors - 1) / BlockSectors;
        var buffer = new byte[BlockSectors * CdToc.SectorBytes];

        // Test pass: every block's checksum, and the track's.
        var testCrcs = new uint[blocks];
        var testFailed = new bool[blocks];
        var testCrc = new Assembler(skip, samples * 4, null, null);
        for (var b = 0; b < blocks; b++)
        {
            ct.ThrowIfCancellationRequested();
            var (lba, count) = Block(startSector, endSector, b);
            testFailed[b] = !ReadBlock(lba, count, buffer);
            var data = buffer.AsSpan(0, count * CdToc.SectorBytes);
            testCrcs[b] = Crc32Ieee.Compute(data);
            testCrc.Add(data);
            Report(progress, ReadPass.Test, b + 1, blocks, watch, 0);
        }

        // A track the drive's cache could hold whole: read something else first.
        if (endSector - startSector < 2 * _cacheSectors) FlushCache(startSector, buffer, ct);

        // Copy pass: blocks that read as in the test pass are taken; the others are read until two reads agree.
        var checksums = new TrackChecksums(samples, track.Number == _toc.FirstAudio.Number, track.Number == _toc.LastAudio.Number);
        var copy = new Assembler(skip, samples * 4, output, checksums);
        var suspicious = new List<int>();
        long reads = 0;
        var rereads = 0;
        var versions = new Dictionary<uint, (int Count, byte[]? Data)>();
        for (var b = 0; b < blocks; b++)
        {
            ct.ThrowIfCancellationRequested();
            var (lba, count) = Block(startSector, endSector, b);
            var size = count * CdToc.SectorBytes;
            var ok = ReadBlock(lba, count, buffer);
            reads += 2;
            var crc = Crc32Ieee.Compute(buffer.AsSpan(0, size));
            if (ok && !testFailed[b] && crc == testCrcs[b])
            {
                copy.Add(buffer.AsSpan(0, size));
            }
            else
            {
                versions.Clear();
                if (!testFailed[b]) versions[testCrcs[b]] = (1, null);
                if (ok) Count(versions, crc, buffer.AsSpan(0, size));
                byte[]? agreed = null;
                for (var n = 2; n < MaxReads && agreed is null; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    FlushCache(lba, buffer, ct);
                    rereads++;
                    reads++;
                    if (!ReadBlock(lba, count, buffer)) continue;
                    crc = Crc32Ieee.Compute(buffer.AsSpan(0, size));
                    Count(versions, crc, buffer.AsSpan(0, size));
                    if (versions[crc].Count >= 2) agreed = versions[crc].Data;
                    Report(progress, ReadPass.Copy, b, blocks, watch, rereads);
                }
                if (agreed is null)
                {
                    // Never the same twice: the most frequent version read, zeros if none read at all.
                    agreed = versions.Values.Where(v => v.Data is not null).OrderByDescending(v => v.Count).Select(v => v.Data).FirstOrDefault()
                             ?? new byte[size];
                    suspicious.Add(Math.Max(0, lba - track.StartLba));
                }
                copy.Add(agreed.AsSpan(0, size));
            }
            Report(progress, ReadPass.Copy, b + 1, blocks, watch, rereads);
        }
        if (!checksums.Complete) throw new InvalidOperationException("the track's audio came out short");
        var elapsed = watch.Elapsed;
        var sectorsRead = (double)(endSector - startSector) * 2;
        return new TrackRead(testCrc.Crc, checksums, Math.Min(1, blocks * 2.0 / Math.Max(1, reads)), suspicious, rereads, elapsed,
            sectorsRead / CdToc.SectorsPerSecond / Math.Max(0.001, elapsed.TotalSeconds));
    }

    /// <summary>
    /// The track as the drive hands it out, uncorrected, with <paramref name="margin"/> samples more
    /// on either side (silence beyond the disc): for finding the drive's read offset.
    /// </summary>
    public uint[] ReadRaw(CdTrack track, int margin, CancellationToken ct = default)
    {
        long first = (long)track.StartLba * CdToc.SamplesPerSector - margin;
        long samples = (long)_toc.Sectors(track) * CdToc.SamplesPerSector + 2L * margin;
        var startSector = (int)Math.Floor(first / (double)CdToc.SamplesPerSector);
        var endSector = (int)Math.Ceiling((first + samples) / (double)CdToc.SamplesPerSector);
        var result = new uint[samples];
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan());
        var buffer = new byte[BlockSectors * CdToc.SectorBytes];
        var skip = (int)(first - (long)startSector * CdToc.SamplesPerSector) * 4;
        var at = 0;
        for (var b = 0; b * BlockSectors < endSector - startSector; b++)
        {
            ct.ThrowIfCancellationRequested();
            var (lba, count) = Block(startSector, endSector, b);
            ReadBlock(lba, count, buffer);
            var data = buffer.AsSpan(0, count * CdToc.SectorBytes);
            if (skip > 0)
            {
                var drop = Math.Min(skip, data.Length);
                data = data[drop..];
                skip -= drop;
            }
            var take = Math.Min(data.Length, bytes.Length - at);
            data[..take].CopyTo(bytes[at..]);
            at += take;
        }
        return result;
    }

    private static (int Lba, int Count) Block(int start, int end, int b)
    {
        var lba = start + b * BlockSectors;
        return (lba, Math.Min(BlockSectors, end - lba));
    }

    /// <summary>
    /// One block into <paramref name="buffer"/>: sectors outside the disc's audio are silence.
    /// After a failed read the block is tried again, then sector by sector; a sector that won't
    /// read is left as silence, and the block counts as failed.
    /// </summary>
    private bool ReadBlock(int lba, int count, byte[] buffer)
    {
        var span = buffer.AsSpan(0, count * CdToc.SectorBytes);
        span.Clear();
        var from = Math.Max(lba, 0);
        var to = Math.Min(lba + count, _readableEnd);
        if (to <= from) return true;
        var target = span.Slice((from - lba) * CdToc.SectorBytes, (to - from) * CdToc.SectorBytes);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _drive.ReadAudio(from, to - from, target);
                return true;
            }
            catch (CdReadException) { }
        }
        var ok = true;
        for (var s = from; s < to; s++)
        {
            var sector = span.Slice((s - lba) * CdToc.SectorBytes, CdToc.SectorBytes);
            var read = false;
            for (var attempt = 0; attempt < 3 && !read; attempt++)
            {
                try
                {
                    _drive.ReadAudio(s, 1, sector);
                    read = true;
                }
                catch (CdReadException) { sector.Clear(); }
            }
            ok &= read;
        }
        return ok;
    }

    /// <summary>
    /// Fills the drive's cache with sectors far from <paramref name="lba"/>, so what it hands out
    /// next for <paramref name="lba"/> is read from the disc again rather than from its memory.
    /// </summary>
    private void FlushCache(int lba, byte[] buffer, CancellationToken ct)
    {
        var length = Math.Min(_cacheSectors * 2, _readableEnd);
        var start = lba < _readableEnd / 2 ? _readableEnd - length : 0;
        for (var s = start; s < start + length; s += BlockSectors)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(BlockSectors, start + length - s);
            try { _drive.ReadAudio(s, count, buffer.AsSpan(0, count * CdToc.SectorBytes)); }
            catch (CdReadException) { }
        }
    }

    private static void Count(Dictionary<uint, (int Count, byte[]? Data)> versions, uint crc, ReadOnlySpan<byte> data)
    {
        versions.TryGetValue(crc, out var v);
        versions[crc] = (v.Count + 1, v.Data ?? data.ToArray());
    }

    private static void Report(IProgress<ReadProgress>? progress, ReadPass pass, int done, int blocks, Stopwatch watch, int rereads)
    {
        if (progress is null || (done % 8 != 0 && done != blocks)) return;
        var seconds = watch.Elapsed.TotalSeconds;
        var sectors = (pass == ReadPass.Copy ? blocks + done : done) * (double)BlockSectors;
        progress.Report(new ReadProgress(pass, done / (double)blocks, seconds > 0 ? sectors / CdToc.SectorsPerSecond / seconds : 0, rereads));
    }

    /// <summary>
    /// Turns the drive's blocks, as they come, into the track's audio: the first bytes up to the
    /// offset-corrected start are dropped, and exactly the track's length goes on.
    /// </summary>
    private sealed class Assembler(int skip, long length, Stream? output, TrackChecksums? checksums)
    {
        private int _skip = skip;
        private long _left = length;
        private uint _crc = 0xFFFFFFFF;

        public uint Crc => ~_crc;

        public void Add(ReadOnlySpan<byte> data)
        {
            if (_skip > 0)
            {
                var drop = Math.Min(_skip, data.Length);
                data = data[drop..];
                _skip -= drop;
            }
            var take = (int)Math.Min(data.Length, _left);
            if (take <= 0) return;
            var audio = data[..take];
            _left -= take;
            if (checksums is not null) checksums.Add(audio);
            else _crc = Crc32Ieee.Update(_crc, audio);
            output?.Write(audio);
        }
    }
}
