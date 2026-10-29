using System.Globalization;
using System.Text;

namespace Paramaja.Core.Playback;

/// <summary>
/// A CD rip's cue sheet: the album's tracks, in one audio file or one per track. mpv reads cue
/// sheets itself, but only the tidy ones: rippers such as EAC name the .wav they ripped to (a
/// .flac by the time it's played), may give a Windows sub-folder, or put a track's FILE line
/// between its TRACK and INDEX lines ("gaps appended to the previous track"). So the sheet is read
/// here and played as an EDL of the files really there, with the tracks as titled chapters.
/// </summary>
public static class CueSheet
{
    /// <summary>
    /// A track: its file, where it starts in it (INDEX 01, in seconds) and its title; the file's
    /// type (WAVE, BINARY…) and the track's mode (AUDIO, MODE1/2352…) as the sheet gives them.
    /// </summary>
    public sealed record Track(string File, double Start, string? Title, string? FileType = null, string? Mode = null);

    public sealed record Sheet(string? Title, IReadOnlyList<Track> Tracks);

    private static readonly string[] AudioExtensions =
        [".flac", ".wav", ".ape", ".wv", ".m4a", ".alac", ".mp3", ".ogg", ".opus", ".tta", ".aiff", ".aif", ".wma"];

    public static Sheet Read(string path) => Parse(ReadLines(path));

    /// <summary>Cue sheets are UTF-8 or, from older rippers, the Windows code page; Latin-1 reads the latter's names.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r'));
    }

    public static Sheet Parse(IEnumerable<string> lines)
    {
        string? album = null, file = null, title = null, fileType = null, mode = null;
        var inTrack = false;
        var current = -1; // this TRACK block's entry in tracks, once its INDEX 01 is read
        var tracks = new List<Track>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            var rest = line[(space + 1)..].Trim();
            switch (line[..space].ToUpperInvariant())
            {
                case "FILE":
                    file = Unquote(rest, dropLastWord: true);
                    fileType = LastWord(rest);
                    break;
                case "TRACK":
                    inTrack = true;
                    title = null;
                    current = -1;
                    mode = LastWord(rest);
                    break;
                case "TITLE" when !inTrack:
                    album = Unquote(rest);
                    break;
                case "TITLE":
                    title = Unquote(rest);
                    if (current >= 0) tracks[current] = tracks[current] with { Title = title }; // after its INDEX
                    break;
                case "INDEX" when file is not null && rest.StartsWith("01 ", StringComparison.Ordinal):
                    if (Time(rest[3..].Trim()) is not { } start) break;
                    tracks.Add(new Track(file, start, title, fileType, mode));
                    current = tracks.Count - 1;
                    break;
            }
        }
        return new Sheet(album, tracks);
    }

    /// <summary>"Name.wav" WAVE → Name.wav; a FILE line's last word is the file's type.</summary>
    private static string Unquote(string value, bool dropLastWord = false)
    {
        if (value.StartsWith('"') && value.IndexOf('"', 1) is > 0 and var end) return value[1..end];
        if (dropLastWord && value.LastIndexOf(' ') is > 0 and var last) value = value[..last];
        return value.Trim();
    }

    /// <summary>A FILE line's type or a TRACK line's mode, upper case: its last word, after any quoted name.</summary>
    private static string? LastWord(string value)
    {
        var tail = value.StartsWith('"') && value.IndexOf('"', 1) is > 0 and var end ? value[(end + 1)..] : value;
        var words = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > (value.StartsWith('"') ? 0 : 1) ? words[^1].ToUpperInvariant() : null;
    }

    /// <summary>
    /// A CD image (BIN/CUE): audio tracks kept as raw CD sectors in a .bin, which no player reads as
    /// a file; libVLC's CD reader plays it like a disc in a drive.
    /// </summary>
    public static bool IsCdImage(Sheet sheet) =>
        sheet.Tracks.Any(t => t.Mode == "AUDIO")
        && sheet.Tracks.Where(t => t.Mode == "AUDIO").All(t => t.FileType is "BINARY" or "MOTOROLA");

    /// <summary>The cue sheet is a CD image's (see <see cref="IsCdImage(Sheet)"/>); false if it can't be read.</summary>
    public static bool IsCdImage(string cuePath)
    {
        try { return IsCdImage(Read(cuePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>mm:ss:ff, where ff counts CD frames (75 a second).</summary>
    private static double? Time(string msf)
    {
        var p = msf.Split(':');
        return p.Length == 3 && int.TryParse(p[0], out var m) && int.TryParse(p[1], out var s) && int.TryParse(p[2], out var f)
            ? m * 60 + s + f / 75.0
            : null;
    }

    /// <summary>The name a cue sheet gives, without any folder (which may be a Windows one).</summary>
    public static string FileName(string name) => Path.GetFileName(name.Replace('\\', '/'));

    /// <summary>
    /// The file a cue sheet names, in its folder; else one with the same name and another audio
    /// extension. Letter case doesn't count: the sheet was likely written on Windows, where it doesn't.
    /// </summary>
    public static string? Resolve(string dir, string name)
    {
        name = FileName(name);
        var exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        var stem = Path.GetFileNameWithoutExtension(name);
        var files = Directory.EnumerateFiles(dir).ToList();
        return files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
            ?? AudioExtensions
                .SelectMany(e => files.Where(f => Path.GetExtension(f).Equals(e, StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
    }

    /// <summary>
    /// What to play for a cue sheet: an edl:// address with its tracks, in the files really there;
    /// null where a track's file can't be found, so mpv makes what it can of the sheet itself.
    /// </summary>
    public static string? PlayableUrl(string cuePath)
    {
        Sheet sheet;
        try { sheet = Read(cuePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        if (sheet.Tracks.Count == 0) return null;
        var files = sheet.Tracks.Select(t => Resolve(dir, t.File)).ToList();
        if (files.Any(f => f is null)) return null;

        var entries = new List<string>();
        for (var i = 0; i < sheet.Tracks.Count; i++)
        {
            var t = sheet.Tracks[i];
            var entry = new StringBuilder(Escape(files[i]!));
            // A track runs to the next one in the same file, or to the end of its file.
            var next = i + 1 < sheet.Tracks.Count && sheet.Tracks[i + 1].File == t.File ? sheet.Tracks[i + 1] : null;
            if (t.Start > 0 || next is not null) entry.Append(",start=").Append(Seconds(t.Start));
            if (next is not null) entry.Append(",length=").Append(Seconds(next.Start - t.Start));
            entry.Append(",title=").Append(Escape(string.IsNullOrWhiteSpace(t.Title) ? $"Track {i + 1}" : t.Title));
            entries.Add(entry.ToString());
        }
        return "edl://" + string.Join(';', entries);
    }

    /// <summary>EDL's length-prefixed quoting (%bytes%value), which allows any character.</summary>
    private static string Escape(string value) => $"%{Encoding.UTF8.GetByteCount(value)}%{value}";

    private static string Seconds(double s) => s.ToString("0.######", CultureInfo.InvariantCulture);
}
