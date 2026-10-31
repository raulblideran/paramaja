namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// The pre-gaps between tracks: the sectors before a track's start (its INDEX 01) that its Q
/// sub-channel already numbers as the track, at index 0. The TOC only lists INDEX 01, so the gaps
/// come from reading the sub-channel of sectors before each start: just before it, then further
/// back in growing steps until the previous track shows, then halving the distance. They're ripped
/// with the track before ("appended to the previous track") and noted in the cue sheet.
/// </summary>
public static class GapDetector
{
    /// <summary>
    /// Each audio track's pre-gap in sectors (for the first track, only audio before it: a
    /// hidden track one), or null for all of them where the drive can't read the sub-channel.
    /// </summary>
    public static IReadOnlyDictionary<int, int>? Detect(ICdDrive drive, CdToc toc, CancellationToken ct = default)
    {
        if (!drive.CanReadSubQ) return null;
        var gaps = new Dictionary<int, int>();
        CdTrack? previous = null;
        foreach (var track in toc.AudioTracks)
        {
            ct.ThrowIfCancellationRequested();
            gaps[track.Number] = previous is null ? track.StartLba : Pregap(drive, track, previous.StartLba) ?? 0;
            previous = track;
        }
        return gaps;
    }

    private static int? Pregap(ICdDrive drive, CdTrack track, int previousStart)
    {
        bool? InGap(int lba)
        {
            // A Q frame now and then carries the MCN or an ISRC instead of the position: try a neighbour.
            foreach (var at in new[] { lba, lba - 1, lba + 1, lba - 2 })
            {
                if (at <= previousStart || at >= track.StartLba) continue;
                if (drive.ReadSubQ(at) is { } q) return q.Track == track.Number && q.Index == 0;
            }
            return null;
        }
        if (InGap(track.StartLba - 1) is not true) return 0;
        // inGap: known to be in the gap; outside: known to be before it.
        var inGap = track.StartLba - 1;
        var outside = previousStart;
        for (var step = 75; ; step *= 2)
        {
            var probe = track.StartLba - step;
            if (probe <= previousStart) break;
            var state = InGap(probe);
            if (state is null) break;
            if (state == true) inGap = probe;
            else { outside = probe; break; }
        }
        while (inGap - outside > 1)
        {
            var middle = outside + (inGap - outside) / 2;
            if (InGap(middle) == true) inGap = middle;
            else outside = middle;
        }
        return track.StartLba - inGap;
    }
}
