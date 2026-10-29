using CommunityToolkit.Mvvm.ComponentModel;
using Paramaja.Core.Playback;

namespace Paramaja.App.ViewModels;

public sealed partial class TrackItem(TrackInfo? track, string label) : ObservableObject
{
    public TrackInfo? Track { get; } = track;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected = track?.Selected ?? false;

    public static TrackItem Off(bool selected) => new(null, "Off") { IsSelected = selected };
}

/// <summary>A row of the Chapters menu: a chapter, or a disc title (Command decides which).</summary>
public sealed partial class ChapterItem(int id, string label) : ObservableObject
{
    public int Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isCurrent;
}

public sealed record TorrentFileItem(int Index, string Name, string Size)
{
    public override string ToString() => $"{Name}  ({Size})";
}

/// <summary>A quality the playing web video offers (the video menu); 0 is "best available".</summary>
public sealed record StreamQualityItem(int Height, string Label)
{
    public override string ToString() => Label;

    /// <summary>"Best available", then the video's heights, highest first.</summary>
    public static IEnumerable<StreamQualityItem> For(IEnumerable<int> heights) =>
        heights.Where(h => h > 0).Distinct().OrderByDescending(h => h)
            .Select(h => new StreamQualityItem(h, h switch
            {
                >= 4320 => $"{h}p (8K)",
                >= 2160 => $"{h}p (4K)",
                _ => $"{h}p",
            }))
            .Prepend(new StreamQualityItem(0, "Best available"));

    /// <summary>The row for a quality asked for: the highest height at or below it (or the lowest there is).</summary>
    public static int IndexFor(IList<StreamQualityItem> items, int height)
    {
        if (items.Count == 0) return -1;
        if (height <= 0) return 0;
        for (var i = 1; i < items.Count; i++)
            if (items[i].Height <= height) return i;
        return items.Count - 1;
    }
}
