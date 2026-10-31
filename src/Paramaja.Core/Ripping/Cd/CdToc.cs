using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>A track as the disc's table of contents lists it.</summary>
/// <param name="StartLba">Where its INDEX 01 is, in sectors from the start of the program area (LBA 0 = 00:02:00).</param>
/// <param name="PreEmphasis">Mastered with pre-emphasis (control bit 0): a player is meant to de-emphasise it.</param>
/// <param name="CopyPermitted">The disc's "digital copy permitted" bit (control bit 1).</param>
public sealed record CdTrack(int Number, int StartLba, bool IsAudio, bool PreEmphasis = false, bool CopyPermitted = false);

/// <summary>
/// An Audio CD's table of contents, and the IDs the online databases know a disc by: AccurateRip
/// (its checksums of everyone's rips), MusicBrainz (the release) and FreeDB/CDDB.
/// </summary>
public sealed class CdToc
{
    /// <summary>Audio CD sectors: 2352 bytes, 588 stereo 16-bit samples, 75 to the second.</summary>
    public const int SectorBytes = 2352;
    public const int SamplesPerSector = 588;
    public const int SectorsPerSecond = 75;
    /// <summary>The 2-second lead-in pre-gap: LBA 0 is the disc's time 00:02:00.</summary>
    public const int LeadInSectors = 150;
    /// <summary>
    /// The gap a multisession disc (CD-Extra: audio, then a data session) has between its
    /// sessions: the audio session's lead-out plus the next session's lead-in and pre-gap.
    /// </summary>
    public const int SessionGapSectors = 11_400;

    public CdToc(IReadOnlyList<CdTrack> tracks, int leadOutLba)
    {
        if (tracks.Count == 0) throw new ArgumentException("a disc has at least one track", nameof(tracks));
        Tracks = tracks;
        LeadOutLba = leadOutLba;
    }

    public IReadOnlyList<CdTrack> Tracks { get; }
    /// <summary>Where the disc's (last session's) lead-out starts.</summary>
    public int LeadOutLba { get; }

    public IEnumerable<CdTrack> AudioTracks => Tracks.Where(t => t.IsAudio);
    public int AudioTrackCount => Tracks.Count(t => t.IsAudio);
    public CdTrack FirstAudio => Tracks.First(t => t.IsAudio);
    public CdTrack LastAudio => Tracks.Last(t => t.IsAudio);

    /// <summary>
    /// A CD-Extra (Enhanced CD): its last track is data, in a session of its own after the audio.
    /// </summary>
    public bool IsEnhanced => Tracks.Count > 1 && !Tracks[^1].IsAudio && Tracks[^2].IsAudio;

    /// <summary>Where the audio ends: the lead-out, or on a CD-Extra the end of the audio session.</summary>
    public int AudioLeadOutLba => IsEnhanced ? Tracks[^1].StartLba - SessionGapSectors : LeadOutLba;

    /// <summary>The sector after the track's last one (pre-gaps of the next track included, as on the disc).</summary>
    public int EndLba(CdTrack track)
    {
        var i = IndexOf(track);
        if (i + 1 >= Tracks.Count) return LeadOutLba;
        var next = Tracks[i + 1];
        return track.IsAudio && !next.IsAudio && IsEnhanced ? next.StartLba - SessionGapSectors : next.StartLba;
    }

    public int Sectors(CdTrack track) => EndLba(track) - track.StartLba;

    public CdTrack this[int number] => Tracks.First(t => t.Number == number);

    private int IndexOf(CdTrack track)
    {
        for (var i = 0; i < Tracks.Count; i++)
            if (Tracks[i].Number == track.Number) return i;
        throw new ArgumentException($"track {track.Number} isn't on this disc", nameof(track));
    }

    // ---- AccurateRip -------------------------------------------------------------------------

    /// <summary>
    /// The three IDs AccurateRip files a disc under. As CUETools computes them: the audio tracks'
    /// offsets, with the disc's lead-out (a CD-Extra's data track counts only in the CDDB ID).
    /// </summary>
    public (uint Id1, uint Id2, uint Cddb) AccurateRipIds
    {
        get
        {
            uint id1 = 0, id2 = 0, n = 0;
            foreach (var t in AudioTracks)
            {
                id1 += (uint)t.StartLba;
                id2 += (uint)Math.Max(t.StartLba, 1) * ++n;
            }
            id1 += (uint)LeadOutLba;
            id2 += (uint)Math.Max(LeadOutLba, 1) * ++n;
            return (id1, id2, FreedbId);
        }
    }

    /// <summary>The disc's entry in AccurateRip's database (a small binary file of every pressing's checksums).</summary>
    public string AccurateRipUrl
    {
        get
        {
            var (id1, id2, cddb) = AccurateRipIds;
            return string.Create(CultureInfo.InvariantCulture,
                $"http://www.accuraterip.com/accuraterip/{id1 & 0xF:x}/{(id1 >> 4) & 0xF:x}/{(id1 >> 8) & 0xF:x}/dBAR-{AudioTrackCount:000}-{id1:x8}-{id2:x8}-{cddb:x8}.bin");
        }
    }

    // ---- FreeDB / CDDB ------------------------------------------------------------------------

    /// <summary>The FreeDB (CDDB) disc ID, over every track (data tracks too) and the lead-out.</summary>
    public uint FreedbId
    {
        get
        {
            static int DigitSum(int n)
            {
                var sum = 0;
                for (; n > 0; n /= 10) sum += n % 10;
                return sum;
            }
            var checksum = Tracks.Sum(t => DigitSum((t.StartLba + LeadInSectors) / SectorsPerSecond));
            var seconds = (LeadOutLba + LeadInSectors) / SectorsPerSecond - (Tracks[0].StartLba + LeadInSectors) / SectorsPerSecond;
            return (uint)((checksum % 255) << 24 | seconds << 8 | Tracks.Count);
        }
    }

    // ---- MusicBrainz ----------------------------------------------------------------------------

    /// <summary>
    /// The MusicBrainz disc ID: a SHA-1 over the audio session's TOC (first and last audio track,
    /// its lead-out, 99 track offsets), in base64 with '.', '_' and '-' for '+', '/' and '='.
    /// </summary>
    public string MusicBrainzDiscId
    {
        get
        {
            var audio = AudioTracks.ToList();
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{audio[0].Number:X2}{audio[^1].Number:X2}{AudioLeadOutLba + LeadInSectors:X8}");
            for (var n = 1; n <= 99; n++)
            {
                var track = audio.FirstOrDefault(t => t.Number == n);
                text.Append(CultureInfo.InvariantCulture, $"{(track is null ? 0 : track.StartLba + LeadInSectors):X8}");
            }
            var hash = SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString()));
            return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }
    }

    /// <summary>The TOC as MusicBrainz takes it for a fuzzy lookup: first track, last track, lead-out, offsets (all +150).</summary>
    public string MusicBrainzToc
    {
        get
        {
            var audio = AudioTracks.ToList();
            return string.Join("+", new[] { audio[0].Number, audio[^1].Number, AudioLeadOutLba + LeadInSectors }
                .Concat(audio.Select(t => t.StartLba + LeadInSectors))
                .Select(n => n.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // ---- Times -------------------------------------------------------------------------------------

    /// <summary>Sectors as m:ss.ff (frames, 75 to the second), as rip logs show lengths and positions.</summary>
    public static string Time(int sectors)
    {
        var frames = sectors % SectorsPerSecond;
        var seconds = sectors / SectorsPerSecond;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}.{frames:00}");
    }

    /// <summary>Sectors as mm:ss:ff, as cue sheets write positions.</summary>
    public static string CueTime(int sectors)
    {
        var frames = sectors % SectorsPerSecond;
        var seconds = sectors / SectorsPerSecond;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}:{frames:00}");
    }
}
