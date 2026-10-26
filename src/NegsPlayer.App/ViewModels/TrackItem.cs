using CommunityToolkit.Mvvm.ComponentModel;
using NegsPlayer.Core.Playback;

namespace NegsPlayer.App.ViewModels;

public sealed partial class TrackItem(TrackInfo? track, string label) : ObservableObject
{
    public TrackInfo? Track { get; } = track;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected = track?.Selected ?? false;

    public static TrackItem Off(bool selected) => new(null, "Off") { IsSelected = selected };
}

public sealed record TorrentFileItem(int Index, string Name, string Size)
{
    public override string ToString() => $"{Name}  ({Size})";
}
