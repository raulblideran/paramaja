using CommunityToolkit.Mvvm.ComponentModel;
using Paramaja.Core.Input;

namespace Paramaja.App.ViewModels;

public sealed partial class MagnetDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Hint), nameof(Normalized))]
    private string _text = "";

    public MagnetDialogViewModel(string? initial = null)
    {
        // Pre-fill from the clipboard when it already holds a magnet.
        if (InputClassifier.Classify(initial).Kind == InputKind.Magnet) _text = initial!.Trim();
    }

    public bool IsValid => InputClassifier.Classify(Text).Kind == InputKind.Magnet;
    public string? Normalized => IsValid ? InputClassifier.Classify(Text).Value : null;

    public string Hint => string.IsNullOrWhiteSpace(Text) ? ""
        : IsValid ? "Looks good — press Enter to play."
        : "That doesn't look like a magnet link (it should start with magnet:?xt=urn:btih:).";
}
