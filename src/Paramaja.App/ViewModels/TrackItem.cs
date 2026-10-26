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
