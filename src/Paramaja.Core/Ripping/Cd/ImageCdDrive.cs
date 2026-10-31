using Paramaja.Core.Playback;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// A CD image (a .bin of raw sectors with its .cue, or raw audio and a TOC) standing in for a
/// drive: what the ripper is tested on, as no test machine has a drive. It can play a drive's
/// faults: a read offset (it hands out the audio <see cref="ReadOffset"/> samples late, as a
/// drive with that offset does) and sectors that read wrong a few times before they read right.
/// </summary>
public sealed class ImageCdDrive : ICdDrive
{
    private readonly Stream _data;
    private readonly CdToc _toc;
    private readonly Random _noise = new(1);

    public ImageCdDrive(Stream data, CdToc toc, DriveIdentity? identity = null)
    {
        _data = data;
        _toc = toc;
        Identity = identity ?? new DriveIdentity("PARAMAJA", "IMAGE DRIVE", "1.0");
    }

    /// <summary>A BIN/CUE image whose tracks are all in its one .bin.</summary>
    public static ImageCdDrive FromCue(string cuePath, DriveIdentity? identity = null)
    {
        var sheet = CueSheet.Read(cuePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath))!;
        var files = sheet.Tracks.Select(t => t.File).Distinct().ToList();
        if (files.Count != 1) throw new InvalidDataException("only images with all their tracks in one .bin");
        var bin = CueSheet.Resolve(dir, files[0]) ?? throw new FileNotFoundException("the image's .bin isn't there", files[0]);
        var stream = File.OpenRead(bin);
        var tracks = sheet.Tracks.Select((t, i) =>
            new CdTrack(i + 1, (int)Math.Round(t.Start * CdToc.SectorsPerSecond), t.Mode is null or "AUDIO")).ToList();
        return new ImageCdDrive(stream, new CdToc(tracks, (int)(stream.Length / CdToc.SectorBytes)), identity);
    }

    public DriveIdentity Identity { get; }
    public DriveFeatures Features { get; init; } = new(true, false, 2048);
    public int ReadOffset { get; init; }
    public CdText? CdText { get; init; }
    public string? Mcn { get; init; }
    public IReadOnlyDictionary<int, string> Isrcs { get; init; } = new Dictionary<int, string>();
    /// <summary>Each track's pre-gap (INDEX 00 before its INDEX 01), in sectors, as its Q sub-channel tells it.</summary>
    public IReadOnlyDictionary<int, int> Gaps { get; init; } = new Dictionary<int, int>();
    public bool CanReadSubQ { get; init; } = true;

    /// <summary>Sectors that read with noise this many more times before reading right (int.MaxValue: never right).</summary>
    public Dictionary<int, int> Flaky { get; } = [];
    /// <summary>Sectors whose reads fail outright, this many more times.</summary>
    public Dictionary<int, int> Unreadable { get; } = [];
    /// <summary>Every sector handed out, counting re-reads.</summary>
    public long SectorsRead { get; private set; }

    public CdToc ReadToc() => _toc;
    public CdText? ReadCdText() => CdText;
    public string? ReadMcn() => Mcn;
    public string? ReadIsrc(int track) => Isrcs.TryGetValue(track, out var isrc) ? isrc : null;

    public void ReadAudio(int lba, int sectors, Span<byte> buffer)
    {
        if (lba < 0 || lba + sectors > _toc.LeadOutLba) throw new CdReadException($"sectors {lba}–{lba + sectors - 1} are outside the disc");
        for (var s = lba; s < lba + sectors; s++)
            if (Unreadable.TryGetValue(s, out var n) && n > 0)
            {
                Unreadable[s] = n == int.MaxValue ? n : n - 1;
                throw new CdReadException($"sector {s}: read error (simulated)");
            }
        SectorsRead += sectors;
        // A drive with a read offset of +N hands out each sample N samples late: sample j is the disc's j - N.
        var bytes = buffer[..(sectors * CdToc.SectorBytes)];
        bytes.Clear();
        var first = (long)lba * CdToc.SamplesPerSector - ReadOffset;
        var total = _data.Length / 4;
        var from = Math.Max(first, 0);
        var to = Math.Min(first + (long)sectors * CdToc.SamplesPerSector, total);
        if (to > from)
        {
            _data.Position = from * 4;
            _data.ReadExactly(bytes.Slice((int)((from - first) * 4), (int)((to - from) * 4)));
        }
        for (var s = lba; s < lba + sectors; s++)
            if (Flaky.TryGetValue(s, out var n) && n > 0)
            {
                Flaky[s] = n == int.MaxValue ? n : n - 1;
                var sector = bytes.Slice((s - lba) * CdToc.SectorBytes, CdToc.SectorBytes);
                sector[_noise.Next(CdToc.SectorBytes)] ^= (byte)(1 + _noise.Next(255));
            }
    }

    public SubQ? ReadSubQ(int lba)
    {
        if (!CanReadSubQ) return null;
        var tracks = _toc.Tracks;
        for (var i = tracks.Count - 1; i >= 0; i--)
        {
            var t = tracks[i];
            var gap = Gaps.TryGetValue(t.Number, out var g) ? g : 0;
            if (lba >= t.StartLba) return new SubQ(t.Number, 1);
            if (lba >= t.StartLba - gap) return new SubQ(t.Number, 0);
        }
        return new SubQ(tracks[0].Number, 0);
    }

    public void Dispose() => _data.Dispose();
}
