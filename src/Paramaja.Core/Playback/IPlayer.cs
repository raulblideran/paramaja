using Paramaja.Core.Mpv;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>What the UI needs from the playback engine (implemented by <see cref="PlayerService"/>).</summary>
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
    event Action<string>? KeyPressed;
    event Action? MouseMoved;
    event Action? MouseClicked;
    event Action? FileLoaded;
    event Action<EndFileReason, string?>? EndFile;

    void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false);
    void OpenDisc(string root, bool bluray);
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
    void ApplyHardwareDecoding();
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
