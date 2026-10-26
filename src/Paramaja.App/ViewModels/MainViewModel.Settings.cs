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
            OnPropertyChanged();
            Changed(() => _player?.SetDownmix(value));
        }
    }

    public bool HdrPassthrough
    {
        get => Settings.HdrMode == HdrMode.Passthrough;
        set
        {
            var mode = value ? HdrMode.Passthrough : HdrMode.TonemapToSdr;
            if (Settings.HdrMode == mode) return;
            Settings.HdrMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HdrTonemap));
            Changed(() => _player?.SetHdrMode(mode));
        }
    }

    public bool HdrTonemap
    {
        get => !HdrPassthrough;
        set => HdrPassthrough = !value;
    }

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
            Changed(() => _player?.ApplySubtitles());
        }
    }

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
