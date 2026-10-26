using Paramaja.App.Services;
using Paramaja.Core.Mpv;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;
using Paramaja.Core.Torrent;

namespace Paramaja.App.Tests;

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Records every call the UI makes so tests can assert on behaviour without libmpv.</summary>
public sealed class FakePlayer(PlayerSettings settings) : IPlayer
{
    public List<string> Calls { get; } = [];
    public PlayerSettings Settings { get; } = settings;
    public PlayerState State { get; } = new();
    public IReadOnlyList<TrackInfo> Tracks { get; set; } = [];
    public bool Disposed { get; private set; }

    public event Action<string>? StateChanged;
    public event Action? TracksChanged;
    public event Action<string>? KeyPressed;
    public event Action? MouseMoved;
    public event Action? FileLoaded;
    public event Action<EndFileReason, string?>? EndFile;

    public void RaiseState(string name) => StateChanged?.Invoke(name);
    public void RaiseTracks() => TracksChanged?.Invoke();
    public void RaiseKey(string key) => KeyPressed?.Invoke(key);
    public void RaiseMouse() => MouseMoved?.Invoke();
    public void RaiseEnd(EndFileReason r, string? e) => EndFile?.Invoke(r, e);
    public void RaiseLoaded() => FileLoaded?.Invoke();

    private void Log(string call) => Calls.Add(call);

    public void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false) =>
        Log($"Open {pathOrUrl} {displayTitle}".TrimEnd() + (streaming ? " [stream]" : ""));
    public void Stop() => Log("Stop");
    public void TogglePause() => Log("TogglePause");
    public void SetPaused(bool paused) => Log($"SetPaused {paused}");
    public void SeekRelative(double seconds) => Log($"SeekRelative {seconds}");
    public void SeekAbsolute(double seconds) => Log($"SeekAbsolute {seconds}");
    public void FrameStep() => Log("FrameStep");
    public void FrameBackStep() => Log("FrameBackStep");
    public void SetVolume(double volume) { Settings.Volume = volume; Log($"SetVolume {volume}"); }
    public void ChangeVolume(double delta) { Settings.Volume += delta; Log($"ChangeVolume {delta}"); }
    public void ToggleMute() => Log("ToggleMute");
    public void ShowText(string text, int durationMs = 1500) => Log($"ShowText {text}");
    public void ToggleStats() => Log("ToggleStats");
    public string Screenshot(bool includeSubtitles) { Log($"Screenshot {includeSubtitles}"); return "/tmp/shot.png"; }
    public void SetDownmix(bool stereo) => Log($"SetDownmix {stereo}");
    public void SetHdrMode(HdrMode mode) => Log($"SetHdrMode {mode}");
    public void SetDeinterlace(DeinterlaceMode mode) => Log($"SetDeinterlace {mode}");
    public void SetDeinterlaceAlgorithm(DeinterlaceAlgorithm algorithm) => Log($"SetDeinterlaceAlgorithm {algorithm}");
    public void ApplyHdr() => Log("ApplyHdr");
    public void ApplyHardwareDecoding() => Log("ApplyHardwareDecoding");
    public void ApplySubtitles() => Log("ApplySubtitles");
    public void ApplyAudio() => Log("ApplyAudio");
    public void SelectTrack(TrackType type, int? id) => Log($"SelectTrack {type} {id?.ToString() ?? "off"}");
    public void CycleTrack(TrackType type) => Log($"CycleTrack {type}");
    public void AddSubtitle(string path) => Log($"AddSubtitle {path}");
    public void ToggleSubtitleVisibility() => Log("ToggleSubtitleVisibility");
    public void AdjustSubtitleDelay(double seconds) => Log($"AdjustSubtitleDelay {seconds}");
    public void Dispose() => Disposed = true;
}

public sealed class FakeTorrents : ITorrentService
{
    public bool IsStarted { get; private set; }
    public TorrentEngineConfig? Config { get; private set; }
    public List<string> Calls { get; } = [];
    public Func<string, TorrentInfo> OnAdd { get; set; } = _ => Sample;
    public TorrentStats Stats { get; set; } = new("live", null, false, 50, 100, [0, 50], 3.5, 0.1, 7, 2, 40);

    public static TorrentInfo Sample { get; } = new(1, "abc", "Sintel", [
        new TorrentFile(0, "Sintel/Sintel.en.srt", 1000, false),
        new TorrentFile(1, "Sintel/Sintel.mp4", 100, true),
        new TorrentFile(2, "Sintel/Extras/Trailer.mkv", 50, true),
    ], 1, "http://127.0.0.1:9/stream/1/1");

    public TorrentEngineInfo Start(TorrentEngineConfig config)
    {
        IsStarted = true;
        Config = config;
        return new("127.0.0.1:9", null);
    }

    public Task<TorrentInfo> AddAsync(string magnet, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add($"Add {magnet}");
        return Task.FromResult(OnAdd(magnet));
    }

    public Func<string, MegaInfo> OnMega { get; set; } = _ => new MegaInfo(0, "Clip.mkv", 1000, "http://127.0.0.1:9/mega/0");

    public Task<MegaInfo> AddMegaAsync(string link, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add($"Mega {link}");
        return Task.FromResult(OnMega(link));
    }

    public string SelectFile(int torrentId, int fileIndex)
    {
        Calls.Add($"Select {torrentId} {fileIndex}");
        return $"http://127.0.0.1:9/stream/{torrentId}/{fileIndex}";
    }

    public TorrentStats GetStats(int torrentId) => Stats;
    public void Remove(int torrentId, bool deleteFiles) => Calls.Add($"Remove {torrentId} {deleteFiles}");
    public void Dispose() { Calls.Add("Dispose"); IsStarted = false; }
}

public sealed class FakeDialogs : IDialogService
{
    public string? NextFile { get; set; }
    public string? NextSubtitle { get; set; }
    public string? NextLink { get; set; }
    public string? Clipboard { get; set; }
    public List<string> OpenedFolders { get; } = [];
    public int SettingsShown { get; private set; }

    public Task<string?> PickMediaFileAsync() => Task.FromResult(NextFile);
    public Task<string?> PickSubtitleFileAsync() => Task.FromResult(NextSubtitle);
    public Task<string?> AskLinkAsync() => Task.FromResult(NextLink);
    public Task<string?> ReadClipboardTextAsync() => Task.FromResult(Clipboard);
    public Task ShowSettingsAsync() { SettingsShown++; return Task.CompletedTask; }
    public void OpenFolder(string path) => OpenedFolders.Add(path);
}
