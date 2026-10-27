using Paramaja.Core.Mpv;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>Moves around a DVD or Blu-ray menu.</summary>
public enum MenuNavigation { Up, Down, Left, Right, Activate }

/// <summary>
/// What the UI needs from a playback engine: <see cref="PlayerService"/> (mpv) for everything,
/// and <see cref="VlcDiscPlayer"/> (libVLC) for discs, which have menus mpv can't show.
/// </summary>
public interface IPlayer : IDisposable
{
    PlayerSettings Settings { get; }
    PlayerState State { get; }
    IReadOnlyList<TrackInfo> Tracks { get; }

    event Action<string>? StateChanged;
    event Action? TracksChanged;
    event Action? ChaptersChanged;
    IReadOnlyList<ChapterInfo> Chapters { get; }
    IReadOnlyList<DiscTitleInfo> Titles { get; }
    void GoToChapter(int index);
    void StepChapter(int delta);
    void SelectTitle(int id);

    /// <summary>The disc has menus (DVD, or a Blu-ray with HDMV menus).</summary>
    bool HasDiscMenu { get; }
    /// <summary>A disc menu is on screen: the arrow keys and Enter move through it.</summary>
    bool IsInDiscMenu { get; }
    void NavigateMenu(MenuNavigation direction);
    /// <summary>Back to the disc's root (top) menu.</summary>
    void ShowDiscMenu();
    event Action<string>? KeyPressed;
    event Action? MouseMoved;
    event Action? MouseClicked;
    event Action? FileLoaded;
    event Action<EndFileReason, string?>? EndFile;

    void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false);
    void OpenDisc(string root, bool bluray);
    /// <summary>An Audio CD from its drive; its tracks are the chapters.</summary>
    void OpenAudioCd(string device);
    void Stop();
    void TogglePause();
    void SetPaused(bool paused);
    void SeekRelative(double seconds);
    void SeekAbsolute(double seconds);
    void FrameStep();
    void FrameBackStep();
    void SetVolume(double volume);
    void ChangeVolume(double delta);
    void ToggleMute();
    void ShowText(string text, int durationMs = 1500);
    void SetCursorHidden(bool hidden);
    void ToggleStats();
    string Screenshot(bool includeSubtitles);
    void SetDownmix(bool stereo);
    void SetHdrMode(HdrMode mode);
    void SetDisplayHdr(bool isHdr);
    void SetDeinterlace(DeinterlaceMode mode);
    HealthSample SampleHealth();
    RenderProfile RenderProfile { get; }
    event Action<RenderProfile>? RenderProfileChanged;
    void FallBackToFastRendering();
    void ApplyRenderQuality();
    double StreamBufferSeconds { get; }
    void ApplyHdr();
    /// <summary>
    /// Hardware decoding for the playing video only (see <see cref="PlayerState.HardwareDecodingApplies"/>);
    /// the next video starts with it on again.
    /// </summary>
    void SetHardwareDecoding(bool on);
    void ApplySubtitles();
    void ApplyAudio();
    void ApplyStreamQuality();
    void ApplyDisplayFps();
    void SelectTrack(TrackType type, int? id);
    void CycleTrack(TrackType type);
    void AddSubtitle(string path);
    void ToggleSubtitleVisibility();
    double AdjustSubtitleDelay(double seconds);
}
