namespace Paramaja.Core.Playback;

public enum TrackType { Video, Audio, Subtitle }

public sealed record TrackInfo(
    int Id,
    TrackType Type,
    string? Title,
    string? Language,
    string? Codec,
    bool Selected,
    bool External,
    bool Default,
    int? Channels)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            if (!string.IsNullOrWhiteSpace(Language)) parts.Add($"[{Language}]");
            if (!string.IsNullOrWhiteSpace(Codec)) parts.Add(Codec!.ToUpperInvariant());
            if (Channels is { } ch) parts.Add(ChannelLabel(ch));
            if (External) parts.Add("(external)");
            return parts.Count == 0 ? $"Track {Id}" : $"{Id}: {string.Join(" ", parts)}";
        }
    }

    public static string ChannelLabel(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}ch",
    };
}

/// <summary>A chapter of the playing video (or of the disc title being played).</summary>
public sealed record ChapterInfo(int Index, string Title, double Time)
{
    public string Label => $"{Index + 1}. {Title}  ·  {TimeFormat.Format(Time)}";
}

/// <summary>
/// A title of a DVD or Blu-ray (the main film, the extras…). mpv lists them as editions;
/// a file with several editions (some MKVs) shows up the same way.
/// </summary>
public sealed record DiscTitleInfo(int Id, string Title, bool Selected);
