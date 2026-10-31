namespace Paramaja.Core.Ripping.Cd;

/// <summary>A disc in the drive, before ripping: what it is, what the databases know of it, the drive's offset if listed.</summary>
public sealed record CdDisc(
    CdToc Toc,
    DriveIdentity Drive,
    DriveFeatures Features,
    CdText? CdText,
    string? Mcn,
    IReadOnlyList<AlbumTags> Releases,
    IReadOnlyList<AccurateRipPressing> AccurateRip,
    int? ListedOffset,
    IReadOnlyDictionary<int, int> OffsetCounts)
{
    /// <summary>The tags to start from: MusicBrainz's first release, else CD-Text, else placeholders.</summary>
    public AlbumTags DefaultTags => Releases.Count > 0 ? Releases[0] : AlbumTags.Placeholder(Toc, CdText);
}

public sealed record CdRipOptions
{
    public required AlbumTags Tags { get; init; }
    public byte[]? Cover { get; init; }
    public CdRipFormat Format { get; init; } = CdRipFormat.Flac;
    /// <summary>The folder the album's folder goes in (the Music folder).</summary>
    public required string OutputRoot { get; init; }
    /// <summary>A read offset set by hand; null to use AccurateRip's (listed, or found with the disc).</summary>
    public int? Offset { get; init; }
    public string AppVersion { get; init; } = "";
    /// <summary>The reference FLAC encoder (flac), when ripping to FLAC.</summary>
    public string? Flac { get; init; }
}

/// <summary>Where the read offset correction came from, as the log says it.</summary>
public enum OffsetSource { Manual, DriveList, Detected, Unknown }

public sealed record CdTrackResult(TrackTags Tags, string File, int Sectors, int Pregap, TrackRead Read, AccurateRipResult AccurateRip);

public sealed record CdRipResult(string Folder, IReadOnlyList<CdTrackResult> Tracks, string LogPath, string CuePath, int Score,
    int Offset, OffsetSource OffsetSource, bool GapsDetected)
{
    public bool AllAccurate => Tracks.All(t => t.AccurateRip.Accurate);
    public bool AllOk => Tracks.All(t => t.Read.Ok);
}

/// <summary>
/// Rips an Audio CD: secure reads (test &amp; copy, offset-corrected), each track checked with
/// AccurateRip, tagged and saved as FLAC (level 8, verified) or WAV, with a cue sheet, the cover and
/// a log in the layout rip logs use.
/// </summary>
public sealed class CdRipper(HttpClient http, string cacheFolder)
{
    /// <summary>
    /// Reads the disc's TOC, texts and catalogue number, and looks it up (MusicBrainz, AccurateRip,
    /// the drive's offset), unless <paramref name="online"/> is off. Lookups that fail are skipped.
    /// </summary>
    public async Task<CdDisc> ReadDiscAsync(ICdDrive drive, bool online = true, CancellationToken ct = default)
    {
        var (toc, text, mcn) = await Task.Run(() => (drive.ReadToc(), drive.ReadCdText(), drive.ReadMcn()), ct).ConfigureAwait(false);
        if (toc.AudioTrackCount == 0) throw new InvalidDataException("this disc has no audio tracks");
        IReadOnlyList<AlbumTags> releases = [];
        IReadOnlyList<AccurateRipPressing> pressings = [];
        IReadOnlyDictionary<string, int> offsets = new Dictionary<string, int>();
        if (online)
        {
            async Task<T> Try<T>(Func<Task<T>> lookup, T fallback)
            {
                try { return await lookup().ConfigureAwait(false); }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException
                                              && !ct.IsCancellationRequested) { return fallback; }
            }
            var mb = Try(() => CdMetadata.LookupAsync(http, toc, ct), releases);
            var ar = Try(() => AccurateRip.FetchAsync(http, toc, ct), pressings);
            var list = Try(() => DriveOffsets.LoadAsync(http, Path.Combine(cacheFolder, "DriveOffsets.bin"), ct), offsets);
            releases = await mb.ConfigureAwait(false);
            pressings = await ar.ConfigureAwait(false);
            offsets = await list.ConfigureAwait(false);
        }
        return new CdDisc(toc, drive.Identity, drive.Features, text, mcn, releases, pressings,
            DriveOffsets.Find(offsets, drive.Identity.Vendor, drive.Identity.Product), DriveOffsets.Counts(offsets));
    }

    public async Task<CdRipResult> RipAsync(ICdDrive drive, CdDisc disc, CdRipOptions o, IProgress<RipProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (o.Format == CdRipFormat.Flac && o.Flac is null) throw new InvalidOperationException("the FLAC encoder (flac) isn't installed");
        var toc = disc.Toc;
        var audio = toc.AudioTracks.ToList();
        var tags = o.Tags;
        var written = new List<string>();
        var folderName = RipNames.Safe(tags.Year is { } y ? $"{tags.Album} ({y})" : tags.Album)
                         + (tags.DiscTotal > 1 ? $" (CD {tags.DiscNumber})" : "");
        var folder = RipNames.NewFolder(Path.Combine(o.OutputRoot, RipNames.Safe(tags.AlbumArtist)), folderName);
        Directory.CreateDirectory(folder);
        try
        {
            progress?.Report(new RipProgress("Reading the disc", 0, audio.Count, 0));
            var (offset, source) = await Task.Run(() => ReadOffset(drive, disc, o, progress, ct), ct).ConfigureAwait(false);
            progress?.Report(new RipProgress("Finding the gaps between tracks", 0, audio.Count, 0));
            var gaps = await Task.Run(() => GapDetector.Detect(drive, toc, ct), ct).ConfigureAwait(false);
            var isrcs = await Task.Run(() => audio.ToDictionary(t => t.Number, t => drive.ReadIsrc(t.Number)), ct).ConfigureAwait(false);

            string? cover = null;
            if (o.Cover is { Length: > 0 } image)
            {
                cover = Path.Combine(folder, "cover.jpg");
                await File.WriteAllBytesAsync(cover, image, ct).ConfigureAwait(false);
                written.Add(cover);
            }

            var reader = new SecureReader(drive, toc, offset);
            var totalSectors = audio.Sum(toc.Sectors);
            long doneSectors = 0;
            var results = new List<CdTrackResult>();
            var encodes = new List<Task>();
            var extension = o.Format == CdRipFormat.Flac ? ".flac" : ".wav";
            foreach (var (track, index) in audio.Select((t, i) => (t, i)))
            {
                ct.ThrowIfCancellationRequested();
                var tt = tags.Tracks.FirstOrDefault(x => x.Number == track.Number) ?? new TrackTags(track.Number, $"Track {track.Number:00}", tags.AlbumArtist);
                // The disc's own ISRC (its sub-channel) over MusicBrainz's, which may be another release's.
                if (isrcs.GetValueOrDefault(track.Number) is { } isrc) tt = tt with { Isrc = isrc };
                var stem = $"{track.Number:00} - " + RipNames.Safe(tt.Artist != tags.AlbumArtist ? $"{tt.Artist} - {tt.Title}" : tt.Title);
                var file = RipNames.NewFile(folder, stem, extension);
                var wav = o.Format == CdRipFormat.Flac ? RipNames.NewFile(folder, stem, ".tmp.wav") : file;
                written.Add(wav);
                if (wav != file) written.Add(file);
                var sectors = toc.Sectors(track);
                var trackProgress = new Progress<ReadProgress>(p => progress?.Report(new RipProgress(
                    p.Rereads > 0 ? "Re-reading" : p.Pass == ReadPass.Test ? "Test" : "Copy", index + 1, audio.Count,
                    (doneSectors + sectors * ((p.Pass == ReadPass.Test ? 0 : 1) + p.Fraction) / 2) / totalSectors, p.Speed, p.Rereads)));
                var read = await Task.Run(() =>
                {
                    using var stream = new FileStream(wav, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
                    stream.Write(new byte[WavFile.HeaderBytes]);
                    var r = reader.ReadTrack(track, stream, trackProgress, ct);
                    WavFile.Finish(stream, (long)sectors * CdToc.SectorBytes,
                        o.Format == CdRipFormat.Wav ? WavFile.InfoChunk(WavFile.InfoTags(tags, tt)) : null);
                    return r;
                }, ct).ConfigureAwait(false);
                doneSectors += sectors;
                var ar = AccurateRip.Verify(disc.AccurateRip, index, read.Copy.AccurateRipV1, read.Copy.AccurateRipV2);
                // Track 1's "pre-gap" is any audio before it on the disc: a hidden track, left on the disc.
                results.Add(new CdTrackResult(tt, file, sectors, gaps?.GetValueOrDefault(track.Number) ?? 0, read, ar));
                if (o.Format == CdRipFormat.Flac)
                {
                    // The encoder runs while the next track is read.
                    var flacTags = FlacEncoder.Tags(tags, tt, audio.Count).ToList();
                    encodes.Add(Task.Run(async () =>
                    {
                        await FlacEncoder.EncodeAsync(o.Flac!, wav, file, flacTags, cover, ct).ConfigureAwait(false);
                        File.Delete(wav);
                    }, ct));
                }
            }
            progress?.Report(new RipProgress("Encoding", audio.Count, audio.Count, 1));
            await Task.WhenAll(encodes).ConfigureAwait(false);

            var baseName = RipNames.Safe($"{tags.AlbumArtist} - {tags.Album}");
            var cuePath = RipNames.NewFile(folder, baseName, ".cue");
            var comment = $"Paramaja {o.AppVersion}".Trim();
            var cue = CueWriter.Write(tags, results.Select(r => new CueWriter.Entry(r.Tags, Path.GetFileName(r.File), r.Sectors, r.Pregap,
                toc[r.Tags.Number].PreEmphasis)).ToList(), disc.Mcn, toc.FreedbId, comment);
            written.Add(cuePath);
            await File.WriteAllTextAsync(cuePath, cue, new System.Text.UTF8Encoding(true), ct).ConfigureAwait(false);

            var log = new RipLog(disc, tags, o, results, offset, source, gaps, DateTime.Now);
            var logPath = RipNames.NewFile(folder, baseName, ".log");
            written.Add(logPath);
            await File.WriteAllTextAsync(logPath, log.Text, new System.Text.UTF8Encoding(true), ct).ConfigureAwait(false);
            return new CdRipResult(folder, results, logPath, cuePath, log.Score, offset, source, gaps is not null);
        }
        catch
        {
            // A rip that didn't finish leaves nothing behind.
            foreach (var f in written)
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                var artist = Path.GetDirectoryName(folder)!;
                if (!Directory.EnumerateFileSystemEntries(artist).Any()) Directory.Delete(artist);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// The read offset: set by hand, else AccurateRip's for this drive, else found by sliding a
    /// middle track's checksum against the disc's AccurateRip entry, else none (0).
    /// </summary>
    private static (int Offset, OffsetSource Source) ReadOffset(ICdDrive drive, CdDisc disc, CdRipOptions o,
        IProgress<RipProgress>? progress, CancellationToken ct)
    {
        if (o.Offset is { } manual) return (manual, OffsetSource.Manual);
        if (disc.ListedOffset is { } listed) return (listed, OffsetSource.DriveList);
        var audio = disc.Toc.AudioTracks.ToList();
        if (disc.AccurateRip.Count > 0 && audio.Count >= 3)
        {
            progress?.Report(new RipProgress("Finding the drive's read offset", 0, audio.Count, 0));
            // The shortest track that is neither the first nor the last (their ends aren't checked).
            var (track, index) = audio.Select((t, i) => (t, i)).Skip(1).SkipLast(1).MinBy(x => disc.Toc.Sectors(x.t));
            const int margin = 2940;
            var raw = new SecureReader(drive, disc.Toc, 0).ReadRaw(track, margin, ct);
            var candidates = disc.AccurateRip.Select(p => p.Tracks[index]).ToArray();
            if (AccurateRip.DetectOffset(raw, margin, candidates, n => disc.OffsetCounts.GetValueOrDefault(n)) is { } found)
                return (found, OffsetSource.Detected);
        }
        return (0, OffsetSource.Unknown);
    }
}
