using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// A rip's log, laid out as rip logs are (EAC's layout: settings, TOC, each track's CRCs and
/// AccurateRip result, the summary), so whoever reads logs finds everything where they look. It
/// says it's Paramaja's: tracker log checkers accept only EAC's, XLD's and whipper's own (signed)
/// logs, so this one makes no pretence of being one of them. Its score applies the deductions
/// those checkers make, from the same facts, and a SHA-256 of the text ends it.
/// </summary>
public sealed class RipLog
{
    public RipLog(CdDisc disc, AlbumTags tags, CdRipOptions options, IReadOnlyList<CdTrackResult> tracks, int offset,
        OffsetSource source, IReadOnlyDictionary<int, int>? gaps, DateTime when)
    {
        Deductions = Deduct(disc, tracks, source, gaps);
        Score = Math.Max(0, 100 - Deductions.Sum(d => d.Points));
        var body = Build(disc, tags, options, tracks, offset, source, gaps, when);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        Text = body + $"\r\n==== Log checksum (SHA-256) {hash} ====\r\n";
    }

    public string Text { get; }
    public int Score { get; }
    public IReadOnlyList<(string Reason, int Points)> Deductions { get; }

    internal static List<(string, int)> Deduct(CdDisc disc, IReadOnlyList<CdTrackResult> tracks, OffsetSource source,
        IReadOnlyDictionary<int, int>? gaps)
    {
        var d = new List<(string, int)>();
        if (source == OffsetSource.Unknown) d.Add(("The drive's read offset is unknown: not corrected", 5));
        if (!disc.Features.AccurateStream) d.Add(("The drive doesn't state an accurate stream", 20));
        if (gaps is null) d.Add(("Gaps not detected (the drive doesn't read the sub-channel)", 10));
        foreach (var t in tracks)
        {
            if (t.Read.TestCrc != t.Read.Copy.Crc32) d.Add(($"Track {t.Tags.Number}: test and copy CRCs differ", 30));
            if (t.Read.Suspicious.Count > 0) d.Add(($"Track {t.Tags.Number}: suspicious positions", 20));
        }
        return d;
    }

    private string Build(CdDisc disc, AlbumTags tags, CdRipOptions o, IReadOnlyList<CdTrackResult> tracks, int offset,
        OffsetSource source, IReadOnlyDictionary<int, int>? gaps, DateTime when)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        void L(string s = "") => b.Append(s).Append("\r\n");
        var toc = disc.Toc;
        L($"Paramaja {o.AppVersion} — CD extraction log".Replace("  ", " "));
        L();
        L($"Extraction logfile from {when.ToString("d. MMMM yyyy, HH:mm", inv)}");
        L();
        L($"{tags.AlbumArtist} / {tags.Album}");
        L();
        L($"Used drive  : {disc.Drive.Name}   Firmware: {disc.Drive.Revision}");
        L();
        L("Read mode               : Secure (test & copy; every block read until two reads agree)");
        L($"Utilize accurate stream : {(disc.Features.AccurateStream ? "Yes" : "No (the drive doesn't state it)")}");
        L("Defeat audio cache      : Yes");
        L("Make use of C2 pointers : No");
        L();
        var offsetText = source switch
        {
            OffsetSource.Manual => " (set by hand)",
            OffsetSource.DriveList => " (AccurateRip's drive list)",
            OffsetSource.Detected => " (found with AccurateRip from this disc)",
            _ => " (unknown: not corrected)",
        };
        L($"Read offset correction                      : {offset.ToString(inv)}{offsetText}");
        L("Overread into Lead-In and Lead-Out          : No");
        L("Fill up missing offset samples with silence : Yes");
        L("Delete leading and trailing silent blocks   : No");
        L("Null samples used in CRC calculations       : Yes");
        L($"Used interface                              : {(OperatingSystem.IsWindows() ? "Native Win32 interface (SPTI)" : "Linux SCSI generic (SG_IO)")}");
        L($"Gap handling                                : {(gaps is null ? "Not detected, thus appended to previous track" : "Appended to previous track")}");
        L();
        L(o.Format == CdRipFormat.Flac
            ? "Used output format              : FLAC, compression level 8 (the reference encoder, flac -8 --verify)"
            : "Used output format              : WAV (RIFF INFO tags)");
        L("Sample format                   : 44.100 Hz; 16 Bit; Stereo");
        L("Add ID3 tag                     : No");
        if (disc.Mcn is { } mcn) L($"Catalogue number (MCN)          : {mcn}");
        L();
        L();
        L("TOC of the extracted CD");
        L();
        L("     Track |   Start  |  Length  | Start sector | End sector ");
        L("    ---------------------------------------------------------");
        foreach (var t in toc.Tracks)
        {
            var length = toc.Sectors(t);
            L(string.Create(inv, $"       {t.Number,2}  | {CdToc.Time(t.StartLba),8} | {CdToc.Time(length),8} |  {t.StartLba,8}    |  {t.StartLba + length - 1,8}   ")
              + (t.IsAudio ? "" : " (data)"));
        }
        L();
        L();
        foreach (var t in tracks)
        {
            L($"Track {t.Tags.Number,2}");
            L();
            L($"     Filename {t.File}");
            L();
            var pregap = t.Tags.Number == toc.FirstAudio.Number
                ? CdToc.LeadInSectors + t.Pregap
                : t.Pregap;
            if (pregap > 0) L($"     Pre-gap length  {PreGapTime(pregap)}" + (t.Tags.Number == toc.FirstAudio.Number && t.Pregap > 0 ? "  (hidden track one audio: left on the disc)" : ""));
            if (pregap > 0) L();
            L(string.Create(inv, $"     Peak level {t.Read.Copy.Peak * 100:0.0} %"));
            L(string.Create(inv, $"     Extraction speed {t.Read.Speed:0.0} X"));
            L(string.Create(inv, $"     Track quality {t.Read.Quality * 100:0.0} %"));
            L(string.Create(inv, $"     Test CRC {t.Read.TestCrc:X8}"));
            L(string.Create(inv, $"     Copy CRC {t.Read.Copy.Crc32:X8}"));
            L($"     {AccurateRipLine(t)}");
            foreach (var position in t.Read.Suspicious) L($"     Suspicious position {PreGapTime(position)}");
            L(t.Read.Ok ? "     Copy OK" : t.Read.TestCrc != t.Read.Copy.Crc32 ? "     Copy finished (test and copy differ)" : "     Copy finished");
            L();
        }
        L(tracks.All(t => t.Read.Ok) ? "No errors occurred" : "There were errors");
        L();
        L();
        L("AccurateRip summary");
        L();
        foreach (var t in tracks) L($"Track {t.Tags.Number,2}  {AccurateRipLine(t)}");
        L();
        L(disc.AccurateRip.Count == 0 ? "None of the tracks are present in the AccurateRip database"
            : tracks.All(t => t.AccurateRip.Accurate) ? "All tracks accurately ripped"
            : tracks.Any(t => t.AccurateRip.Accurate) ? $"{tracks.Count(t => t.AccurateRip.Accurate)} of {tracks.Count} tracks accurately ripped"
            : "No tracks could be verified as accurate");
        L();
        L("End of status report");
        L();
        L($"Log score: {Score} % (Paramaja's own check, with the deductions tracker log checkers make)");
        if (Deductions.Count == 0) L("  Nothing deducted");
        foreach (var (reason, points) in Deductions) L($"  -{points}: {reason}");
        return b.ToString();
    }

    private static string AccurateRipLine(CdTrackResult t)
    {
        var r = t.AccurateRip;
        var crc = (r.Version == 1 ? t.Read.Copy.AccurateRipV1 : t.Read.Copy.AccurateRipV2).ToString("X8", CultureInfo.InvariantCulture);
        if (!r.InDatabase) return "not present in database";
        return r.Accurate
            ? $"Accurately ripped (confidence {r.Confidence})  [{crc}]  (AR v{r.Version})"
            : $"Cannot be verified as accurate (confidence {r.Total})  [{t.Read.Copy.AccurateRipV2:X8}], AccurateRip returned no match";
    }

    /// <summary>Sectors as h:mm:ss.ff, as logs give pre-gaps and positions.</summary>
    private static string PreGapTime(int sectors)
    {
        var frames = sectors % CdToc.SectorsPerSecond;
        var seconds = sectors / CdToc.SectorsPerSecond;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}.{frames:00}");
    }
}
