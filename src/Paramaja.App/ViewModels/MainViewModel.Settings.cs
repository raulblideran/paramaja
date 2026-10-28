using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.ViewModels;

/// <summary>Live settings: each setter updates <see cref="PlayerSettings"/>, applies it to mpv and saves.</summary>
public sealed partial class MainViewModel
{
    private void Changed(Action apply, string? osd = null)
    {
        apply();
        UpdateHdrBadge();
        if (osd is not null) Osd(osd);
        SaveSettings();
    }

    public bool DownmixToStereo
    {
        get => Settings.DownmixToStereo;
        set
        {
            if (Settings.DownmixToStereo == value) return;
            Settings.DownmixToStereo = value;
            if (value && Settings.AudioPassthrough)
            {
                Settings.AudioPassthrough = false; // a bitstream can't be downmixed
                OnPropertyChanged(nameof(AudioPassthrough));
            }
            OnPropertyChanged();
            Changed(() => _player?.SetDownmix(value));
        }
    }

    /// <summary>
    /// The audio menu's choice for a stereo source: 0 both channels (default), 1 the left one,
    /// 2 the right one, on both speakers. For the playing video only.
    /// </summary>
    public int StereoChannelIndex
    {
        get => (int)(_player?.State.StereoChannel ?? StereoChannel.Both);
        set
        {
            var channel = (StereoChannel)Math.Clamp(value, 0, 2);
            if (_player is null || _player.State.StereoChannel == channel) return;
            _player.SetStereoChannel(channel);
            OnPropertyChanged();
            Osd(channel switch
            {
                StereoChannel.Left => "Left channel only (on both speakers)",
                StereoChannel.Right => "Right channel only (on both speakers)",
                _ => "Both channels",
            });
        }
    }

    public bool NormalizeAudio
    {
        get => Settings.NormalizeAudio;
        set
        {
            if (Settings.NormalizeAudio == value) return;
            Settings.NormalizeAudio = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyAudio(), value ? "Volume normalization: on" : "Volume normalization: off");
        }
    }

    /// <summary>Choices for the stream quality list, highest first; 0 is "best available".</summary>
    public static readonly int[] StreamHeights = [0, 2160, 1440, 1080, 720, 480, 360];

    public int StreamQualityIndex
    {
        get => Math.Max(0, Array.IndexOf(StreamHeights, Settings.StreamMaxHeight));
        set
        {
            var h = StreamHeights[Math.Clamp(value, 0, StreamHeights.Length - 1)];
            if (Settings.StreamMaxHeight == h) return;
            Settings.StreamMaxHeight = h;
            OnPropertyChanged();
            Changed(() => _player?.ApplyStreamQuality());
        }
    }

    /// <summary>
    /// Times frames to the display's refresh instead of the audio clock (Developer tab). Off by
    /// default: with a refresh rate mpv gets wrong it stutters.
    /// </summary>
    public bool SyncToDisplay
    {
        get => Settings.SyncToDisplay;
        set
        {
            if (Settings.SyncToDisplay == value) return;
            Settings.SyncToDisplay = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyTiming());
        }
    }

    /// <summary>The refresh rate display sync times frames for: the monitor's own (0, default) or a fixed one.</summary>
    public static readonly double[] DisplayRates = [0, 60, 50, 75, 120, 144];

    public int DisplayFpsIndex
    {
        get => Math.Max(0, Array.IndexOf(DisplayRates, Settings.DisplayFps));
        set
        {
            var fps = DisplayRates[Math.Clamp(value, 0, DisplayRates.Length - 1)];
            if (Settings.DisplayFps == fps) return;
            Settings.DisplayFps = fps;
            OnPropertyChanged();
            Changed(() => _player?.ApplyTiming());
        }
    }

    /// <summary>Windows laptops with two GPUs: play on the high-performance one. From the next start.</summary>
    public bool PreferHighPerformanceGpu
    {
        get => Settings.PreferHighPerformanceGpu;
        set
        {
            if (Settings.PreferHighPerformanceGpu == value) return;
            Settings.PreferHighPerformanceGpu = value;
            OnPropertyChanged();
            SaveSettings();
            ShowStatus("The GPU choice applies the next time Paramaja starts.");
        }
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Dolby/DTS bitstreams to an AV receiver, untouched. Off by default.</summary>
    public bool AudioPassthrough
    {
        get => Settings.AudioPassthrough;
        set
        {
            if (Settings.AudioPassthrough == value) return;
            Settings.AudioPassthrough = value;
            if (value && Settings.DownmixToStereo)
            {
                Settings.DownmixToStereo = false;
                OnPropertyChanged(nameof(DownmixToStereo));
            }
            OnPropertyChanged();
            Changed(() => _player?.ApplyAudio(), value ? "Audio passthrough: on" : "Audio passthrough: off");
        }
    }

    /// <summary>0 Auto (default), 1 always tonemap, 2 always passthrough.</summary>
    public int HdrModeIndex
    {
        get => (int)Settings.HdrMode;
        set => SetHdrMode((HdrMode)Math.Clamp(value, 0, 2));
    }

    private void SetHdrMode(HdrMode mode)
    {
        if (Settings.HdrMode == mode) return;
        Settings.HdrMode = mode;
        OnPropertyChanged(nameof(HdrModeIndex));
        OnHdrEffectChanged();
        Changed(() => _player?.SetHdrMode(mode));
    }

    private void OnHdrEffectChanged()
    {
        OnPropertyChanged(nameof(HdrPassthrough));
        OnPropertyChanged(nameof(HdrTonemap));
        OnPropertyChanged(nameof(HdrStatusText));
    }

    /// <summary>
    /// Whether HDR video is currently passed through (Auto on an HDR display, or chosen).
    /// Setting it picks that mode by hand, which is what H does.
    /// </summary>
    public bool HdrPassthrough
    {
        get => Settings.EffectiveHdrMode(DisplayIsHdr) == HdrMode.Passthrough;
        set => SetHdrMode(value ? HdrMode.Passthrough : HdrMode.TonemapToSdr);
    }

    /// <summary>HDR is tonemapped to SDR; only then do the tonemapping options apply.</summary>
    public bool HdrTonemap => !HdrPassthrough;

    public string HdrStatusText => (DisplayIsHdr, HdrPassthrough) switch
    {
        (true, true) => "This display is in HDR mode: HDR video is sent to it untouched.",
        (true, false) => "This display is in HDR mode, but HDR video is tonemapped to SDR as chosen.",
        (false, true) => "This display isn't in HDR mode, but HDR video is passed through as chosen.",
        (false, false) => "This display is SDR: HDR video is tonemapped to SDR.",
    };

    public string ToneMapping
    {
        get => Settings.ToneMapping;
        set
        {
            if (Settings.ToneMapping == value || !PlayerSettings.ToneMappingCurves.Contains(value)) return;
            Settings.ToneMapping = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyHdr(), $"Tonemapping: {value}");
        }
    }

    public bool HdrDynamicPeak
    {
        get => Settings.HdrDynamicPeak;
        set
        {
            if (Settings.HdrDynamicPeak == value) return;
            Settings.HdrDynamicPeak = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyHdr());
        }
    }

    public double HdrContrastRecovery
    {
        get => Settings.HdrContrastRecovery;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 2), 2);
            if (Math.Abs(Settings.HdrContrastRecovery - value) < 0.001) return;
            Settings.HdrContrastRecovery = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyHdr());
        }
    }

    /// <summary>
    /// The video menu's switch, there only while <see cref="IsInterlacedVideo"/>. It's on for
    /// every interlaced video; switching it off lasts for the playing video only.
    /// </summary>
    public bool Deinterlacing
    {
        get => _player?.State.DeinterlacingOn != false;
        set
        {
            if (_player is null || !IsInterlacedVideo || value == Deinterlacing) return;
            _player.SetDeinterlacing(value);
            OnPropertyChanged();
            Osd(value ? "Deinterlacing: BWDIF" : "Deinterlacing: off for this video");
        }
    }

    /// <summary>0 Auto (default), 1 High, 2 Balanced, 3 Fast.</summary>
    public int RenderQualityIndex
    {
        get => (int)Settings.RenderQuality;
        set
        {
            var q = (RenderQuality)Math.Clamp(value, 0, 3);
            if (Settings.RenderQuality == q) return;
            Settings.RenderQuality = q;
            OnPropertyChanged();
            Changed(() => _player?.ApplyRenderQuality());
            if (_player is not null) RenderProfileText = DescribeProfile(_player.RenderProfile);
        }
    }

    /// <summary>
    /// The video menu's switch, there only while <see cref="HardwareDecodingApplies"/>. It's on
    /// for every video it applies to; switching it off lasts for the playing video only.
    /// </summary>
    public bool HardwareDecoding
    {
        get => _player?.State.HardwareDecodingOn == true;
        set
        {
            if (_player is null || value == HardwareDecoding) return;
            _player.SetHardwareDecoding(value);
            OnPropertyChanged();
            Osd(value ? "Hardware decoding: on" : "Hardware decoding: off for this video");
        }
    }

    public bool AutoLoadExternalSubtitles
    {
        get => Settings.AutoLoadExternalSubtitles;
        set
        {
            if (Settings.AutoLoadExternalSubtitles == value) return;
            Settings.AutoLoadExternalSubtitles = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplySubtitles());
        }
    }

    public double SubtitleScale
    {
        get => Settings.SubtitleScale;
        set
        {
            value = Math.Round(Math.Clamp(value, 0.25, 4), 2);
            if (Math.Abs(Settings.SubtitleScale - value) < 0.001) return;
            Settings.SubtitleScale = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleSizeText));
            Changed(() => _player?.ApplySubtitles());
        }
    }

    /// <summary>What the subtitle slider is set to, in font size (scaled from mpv's base size).</summary>
    public string SubtitleSizeText =>
        $"Font size {Math.Round(MpvOptionsBuilder.SubtitleFontSize * SubtitleScale):0}  ({SubtitleScale:0.00}×)";

    public string PreferredAudioLanguages
    {
        get => Settings.PreferredAudioLanguages;
        set
        {
            if (Settings.PreferredAudioLanguages == value) return;
            Settings.PreferredAudioLanguages = value.Trim();
            OnPropertyChanged();
            Changed(() => _player?.ApplyAudio());
        }
    }

    public string PreferredSubtitleLanguages
    {
        get => Settings.PreferredSubtitleLanguages;
        set
        {
            if (Settings.PreferredSubtitleLanguages == value) return;
            Settings.PreferredSubtitleLanguages = value.Trim();
            OnPropertyChanged();
            Changed(() => _player?.ApplySubtitles());
        }
    }

    public bool ClearTorrentCacheOnExit
    {
        get => Settings.ClearTorrentCacheOnExit;
        set
        {
            if (Settings.ClearTorrentCacheOnExit == value) return;
            Settings.ClearTorrentCacheOnExit = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public int SeekSeconds
    {
        get => Settings.SeekSeconds;
        set
        {
            value = Math.Clamp(value, 1, 600);
            if (Settings.SeekSeconds == value) return;
            Settings.SeekSeconds = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }
}
