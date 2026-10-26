namespace Paramaja.Core.Playback;

/// <summary>Something to read while the bytes arrive.</summary>
public static class BufferingQuips
{
    public static readonly IReadOnlyList<string> All =
    [
        "Big fast, many speed",
        "Bruney",
        "Asking the seeders nicely…",
        "Warming up the aubergine…",
        "Convincing the pixels to line up…",
        "Downloading more RAM…",
        "Tonemapping the suspense…",
        "Untangling the internet tubes…",
        "Polishing every 10-bit…",
        "Herding packets…",
    ];

    private static readonly Random Rng = new();

    /// <summary>A random quip, never the same one twice in a row.</summary>
    public static string Next(string? previous = null)
    {
        if (All.Count < 2) return All[0];
        string pick;
        do pick = All[Rng.Next(All.Count)]; while (pick == previous);
        return pick;
    }
}
