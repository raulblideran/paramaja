using Paramaja.Core.Settings;

namespace Paramaja.App.ViewModels;

/// <summary>
/// The settings as a file, to carry to another install: exported from Settings → Interface, and
/// taken over by importing it there or dropping it on the player (an old install's settings.json
/// works the same way).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Writes the settings to <paramref name="path"/>; false (and says why) if it can't.</summary>
    public bool ExportSettings(string path)
    {
        try
        {
            SettingsStore.Export(Settings, path);
            SettingsFileStatus = $"Saved to {path}";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SettingsFileStatus = $"Couldn't save the settings: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Takes over the settings in <paramref name="path"/>, after asking when <paramref name="ask"/>
    /// (a dropped file: it could have been dropped by mistake). This machine's own settings (its
    /// graphics card choice, exclusive audio, refresh rate, torrent port) stay as they are.
    /// </summary>
    public async Task<bool> ImportSettingsAsync(string path, bool ask)
    {
        if (SettingsStore.ReadFile(path) is not { } other)
        {
            SettingsFileStatus = $"{Path.GetFileName(path)} isn't a Paramaja settings file.";
            ShowStatus(SettingsFileStatus);
            return false;
        }
        if (ask && Dialogs is { } dialogs && !await dialogs.ConfirmAsync("Use these settings?",
                $"Use the settings in {Path.GetFileName(path)}? They replace the ones here (this machine's graphics card, "
                + "refresh rate and audio device choices stay as they are).", "Use them"))
            return false;
        Settings.CopyFrom(other);
        ApplyAllSettings();
        SaveSettings();
        SettingsFileStatus = $"Settings taken from {Path.GetFileName(path)}";
        ShowStatus(SettingsFileStatus);
        return true;
    }

    /// <summary>The player and every binding take the settings as they now are.</summary>
    private void ApplyAllSettings()
    {
        if (_player is { } p)
        {
            p.ApplyAudio();
            p.ApplyHdr();
            p.ApplySubtitles();
            p.ApplyTiming();
            p.ApplyRenderQuality();
            p.ApplyStreamQuality();
            p.SetVolume(Settings.Volume);
        }
        foreach (var c in ControlChoices) c.IsShown = Shows(c.Id);
        OnPropertyChanged(string.Empty);
        UpdateAudioBadge();
        UpdateHdrBadge();
    }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _settingsFileStatus = "";

    /// <summary>
    /// Once the window is up: tells (with the command to fix it) when video can't use the graphics
    /// card, as with an NVIDIA driver whose Flatpak extension isn't installed.
    /// </summary>
    public async Task CheckGraphicsAsync(Func<Paramaja.Core.Native.GraphicsProblem?>? check = null)
    {
        if ((check ?? Paramaja.Core.Native.GraphicsDriverCheck.MissingNvidiaExtension)() is not { } problem || Dialogs is not { } dialogs)
            return;
        if (await dialogs.ConfirmAsync("Graphics driver missing",
                $"{problem.Summary}\n\nInstall it from a terminal, then start Paramaja again:\n\n{problem.Command}\n\n"
                + "(Once Flathub has built it, 'flatpak update' installs it too. If it says there's no such extension yet, "
                + "Flathub hasn't caught up with your driver: it usually does within days.)", "Copy the command"))
            await dialogs.CopyToClipboardAsync(problem.Command);
    }
}
