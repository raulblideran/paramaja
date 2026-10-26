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

    public DeinterlaceMode Deinterlace
    {
        get => Settings.Deinterlace;
        set
        {
            if (Settings.Deinterlace == value) return;
            Settings.Deinterlace = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeinterlaceIndex));
            Changed(() => _player?.SetDeinterlace(value));
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

    /// <summary>ComboBox-friendly view of <see cref="Deinterlace"/> (Auto / On / Off).</summary>
    public int DeinterlaceIndex
    {
        get => (int)Deinterlace;
        set => Deinterlace = (DeinterlaceMode)Math.Clamp(value, 0, 2);
    }

    public bool HardwareDecoding
    {
        get => Settings.HardwareDecoding;
        set
        {
            if (Settings.HardwareDecoding == value) return;
            Settings.HardwareDecoding = value;
            OnPropertyChanged();
            Changed(() => _player?.ApplyHardwareDecoding());
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
