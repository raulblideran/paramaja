namespace Paramaja.App.Services;

/// <summary>Marshals work onto the UI thread (mpv raises events on its own thread).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Everything the view model needs from the windowing layer, so it can be tested headless.</summary>
public interface IDialogService
{
    Task<string?> PickMediaFileAsync();
    Task<string?> PickSubtitleFileAsync();
    Task<string?> AskLinkAsync();
    Task<string?> ReadClipboardTextAsync();
    Task ShowSettingsAsync();
    void OpenFolder(string path);
}

public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}
