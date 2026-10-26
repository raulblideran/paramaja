using CommunityToolkit.Mvvm.ComponentModel;
using Paramaja.Core.Input;

namespace Paramaja.App.ViewModels;

/// <summary>One box for every kind of link: magnet, MEGA, YouTube and other sites, or a direct URL.</summary>
public sealed partial class OpenLinkDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Hint), nameof(Normalized))]
    private string _text = "";

    public OpenLinkDialogViewModel(string? initial = null)
    {
        // Pre-fill from the clipboard when it already holds something playable.
        if (IsPlayableLink(InputClassifier.Classify(initial).Kind)) _text = initial!.Trim();
    }

    private static bool IsPlayableLink(InputKind kind) => kind is InputKind.Magnet or InputKind.Url or InputKind.Mega;

    private (InputKind Kind, string Value) Classified => InputClassifier.Classify(Text);

    public bool IsValid => IsPlayableLink(Classified.Kind);
    public string? Normalized => IsValid ? Classified.Value : null;

    public string Hint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text)) return "";
            var (kind, value) = Classified;
            return kind switch
            {
                InputKind.Magnet => "Magnet link: streams over BitTorrent. Press Enter to play.",
                InputKind.Mega => "MEGA file: decrypted as it streams. Press Enter to play.",
                InputKind.Url when IsYouTube(value) => "YouTube: played through yt-dlp. Press Enter to play.",
                InputKind.Url => "Web link: direct video, or any site yt-dlp supports. Press Enter to play.",
                _ => "That isn't a link I can play. Try a magnet, MEGA, YouTube or video URL.",
            };
        }
    }

    internal static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));
}
