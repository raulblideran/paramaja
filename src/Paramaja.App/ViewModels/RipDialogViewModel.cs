using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paramaja.App.Services;
using Paramaja.Core.Ripping;
using Paramaja.Core.Ripping.Cd;
using Paramaja.Core.Ripping.Dvd;

namespace Paramaja.App.ViewModels;

public enum RipDiscKind { None, Cd, Dvd }

/// <summary>A track's tags as the rip window lets them be edited.</summary>
public sealed partial class RipTrackRow(int number, string title, string artist) : ObservableObject
{
    public int Number { get; } = number;
    [ObservableProperty] private string _title = title;
    [ObservableProperty] private string _artist = artist;
}

/// <summary>A chapter of the DVD title to rip, ticked or not.</summary>
public sealed partial class RipChapterRow(int number, TimeSpan? length) : ObservableObject
{
    public int Number { get; } = number;
    public TimeSpan? Length { get; } = length;
    public string Label => Length is { } l ? $"Chapter {Number} · {RipDialogViewModel.Duration(l)}" : $"Chapter {Number}";
    [ObservableProperty] private bool _isChecked = true;
}

/// <summary>
/// The ripper's window: pick a drive (or, for a DVD, an image or folder), check the album's tags
/// or the DVD's title, and start. An Audio CD is looked up on MusicBrainz and AccurateRip as it's
/// read; the rip itself runs in the main view model.
/// </summary>
public sealed partial class RipDialogViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, ICdDrive> _openCd;
    private readonly Func<string, DvdInfo> _readDvd;
    private ICdDrive? _drive;
    private CdDisc? _disc;
    private DvdInfo? _dvd;
    private CancellationTokenSource? _loading;
    /// <summary>Look discs up (MusicBrainz, AccurateRip); tests stay offline.</summary>
    private readonly bool _online;

    public RipDialogViewModel(MainViewModel main, IReadOnlyList<OpticalDrive> drives, string? drive = null,
        Func<string, ICdDrive>? openCd = null, Func<string, DvdInfo>? readDvd = null, string? flac = null, string? ffmpeg = null,
        bool online = true, string? ffprobe = null, IReadOnlySet<string>? encoders = null)
    {
        Main = main;
        _online = online;
        Drives = drives;
        _openCd = openCd ?? CdDrives.Open;
        _readDvd = readDvd ?? DvdInfo.Read;
        // Null finds the encoder; "" means there's none (tests).
        Flac = flac is null ? FlacEncoder.Find() : flac.Length == 0 ? null : flac;
        Ffmpeg = ffmpeg ?? RipTools.Find("ffmpeg");
        Ffprobe = ffprobe ?? RipTools.Find("ffprobe", Ffmpeg is null ? null : Path.GetDirectoryName(Ffmpeg));
        CdFolder = main.Paths.Music;
        DvdFolder = main.Paths.Videos;
        // Null asks this ffmpeg which encoders it has (tests say).
        if (encoders is not null) UseEncoders(encoders);
        else if (Ffmpeg is { } ff) _ = FindEncodersAsync(ff);
        main.PropertyChanged += OnMainChanged;
        SelectedDrive = drives.FirstOrDefault(d => drive is not null && d.Root == drive) ?? drives.FirstOrDefault(d => d.HasDisc);
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<OpticalDrive> Drives { get; }
    public bool HasDrives => Drives.Count > 0;
    internal string? Flac { get; }
    internal string? Ffmpeg { get; }
    internal string? Ffprobe { get; }

    [ObservableProperty] private OpticalDrive? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCd), nameof(IsDvd))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private RipDiscKind _kind;

    public bool IsCd => Kind == RipDiscKind.Cd;
    public bool IsDvd => Kind == RipDiscKind.Dvd;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _source = "";

    // ---- Audio CD ---------------------------------------------------------------------------

    public ObservableCollection<AlbumTags> Releases { get; } = [];
    public ObservableCollection<RipTrackRow> Tracks { get; } = [];
    [ObservableProperty] private AlbumTags? _selectedRelease;
    [ObservableProperty] private string _albumArtist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private byte[]? _cover;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _coverImage;
    /// <summary>0 FLAC (level 8), 1 WAV.</summary>
    [ObservableProperty] private int _formatIndex;
    [ObservableProperty] private string _cdFolder;
    /// <summary>The drive's read offset, in samples ("+48"); empty to leave it to AccurateRip.</summary>
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private string _offsetNote = "";
    [ObservableProperty] private string _discNote = "";

    public bool HasFlac => Flac is not null;
    /// <summary>Releases to choose from: several, or only similar discs (none preselected).</summary>
    public bool HasReleases => Releases.Count > 1 || Releases.Any(r => !r.ExactDisc);

    // ---- DVD --------------------------------------------------------------------------------------

    public ObservableCollection<DvdTitle> Titles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DvdTitle? _selectedTitle;

    /// <summary>The selected title's chapters, all ticked to begin with.</summary>
    public ObservableCollection<RipChapterRow> Chapters { get; } = [];

    /// <summary>Each ticked chapter as its own MKV (in a folder), rather than all of them in one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChaptersSummary))]
    private bool _chapterFiles;

    /// <summary>Chapters to choose from: the MKV of a title with more than one.</summary>
    public bool ShowChapters => IsMkv && Chapters.Count > 1;

    private IReadOnlyList<int> CheckedChapters => Chapters.Where(c => c.IsChecked).Select(c => c.Number).ToList();

    /// <summary>"All 12 chapters · 1:05:12", "3 of 12 chapters · 12:34, as 3 files".</summary>
    public string ChaptersSummary
    {
        get
        {
            var picked = Chapters.Where(c => c.IsChecked).ToList();
            if (picked.Count == 0) return "No chapters ticked";
            var count = picked.Count == Chapters.Count ? $"All {Chapters.Count} chapters" : $"{picked.Count} of {Chapters.Count} chapters";
            var length = picked.All(c => c.Length is not null) ? $" · {Duration(TimeSpan.FromTicks(picked.Sum(c => c.Length!.Value.Ticks)))}" : "";
            var files = ChapterFiles && picked.Count > 1 ? $", as {picked.Count} files" : "";
            return count + length + files;
        }
    }

    internal static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    partial void OnSelectedTitleChanged(DvdTitle? value)
    {
        foreach (var row in Chapters) row.PropertyChanged -= OnChapterTicked;
        Chapters.Clear();
        if (value is not null)
            for (var c = 1; c <= value.Chapters; c++)
            {
                var row = new RipChapterRow(c, value.ChapterLength(c));
                row.PropertyChanged += OnChapterTicked;
                Chapters.Add(row);
            }
        OnPropertyChanged(nameof(ShowChapters));
        OnPropertyChanged(nameof(ChaptersSummary));
    }

    private void OnChapterTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ChaptersSummary));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AllChapters()
    {
        foreach (var c in Chapters) c.IsChecked = true;
    }

    [RelayCommand]
    private void NoChapters()
    {
        foreach (var c in Chapters) c.IsChecked = false;
    }

    /// <summary>0 MKV (one title), 1 the VIDEO_TS folder decrypted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMkv), nameof(MkvNote), nameof(ShowChapters))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _dvdModeIndex;

    /// <summary>The MKV's video: 0 copied, 1 x264, 2 x265 (<see cref="DvdVideo"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MkvNote))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _dvdVideoIndex;

    [ObservableProperty] private string _dvdFolder;
    private string? _dvdSource;

    /// <summary>
    /// The DVD is a VIDEO_TS folder picked in the window: already decrypted, so the only rip is an
    /// MKV (copying it decrypted again would just copy it).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDecrypt))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isFolderSource;

    public bool ShowDecrypt => !IsFolderSource;

    public bool IsMkv => DvdModeIndex == 0;
    public bool CanMkv => Ffmpeg is not null && Ffprobe is not null;
    public bool CanDecrypt => DvdRead.Available;
    public DvdVideo Video => (DvdVideo)Math.Clamp(DvdVideoIndex, 0, 2);

    [ObservableProperty] private bool _hasX264;
    [ObservableProperty] private bool _hasX265;

    private async Task FindEncodersAsync(string ffmpeg)
    {
        var found = await DvdRipper.FfmpegEncodersAsync(ffmpeg);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UseEncoders(found));
    }

    private void UseEncoders(IReadOnlySet<string> encoders)
    {
        HasX264 = encoders.Contains("libx264");
        HasX265 = encoders.Contains("libx265");
        if (Video == DvdVideo.X264 && !HasX264 || Video == DvdVideo.X265 && !HasX265) DvdVideoIndex = 0;
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanEncode => Video switch
    {
        DvdVideo.X264 => HasX264,
        DvdVideo.X265 => HasX265,
        _ => true,
    };

    /// <summary>What the MKV will hold, for the choice made.</summary>
    public string MkvNote => Video switch
    {
        DvdVideo.X264 =>
            "The video is encoded with x264 (CRF 16): it looks the same as the disc's in less space. Interlaced video is "
            + "deinterlaced with BWDIF, as Paramaja plays DVDs: each field becomes a frame (50 a second, 59.94 for NTSC), so "
            + "motion stays as smooth as on the disc, at CRF 14, in about three quarters of the space. Every audio and subtitle track keeps its language, the chapters are kept, "
            + "LPCM audio becomes FLAC (the same audio bit for bit). x264 is quick: on a recent PC, a fraction of the film's length.",
        DvdVideo.X265 =>
            "The video is encoded with x265 (CRF 18): it looks the same as the disc's in about half the space. Interlaced video "
            + "is deinterlaced with BWDIF to 50 frames a second (59.94 for NTSC), at CRF 16, in about two thirds of the space. "
            + "Every audio and subtitle track and the chapters are kept (LPCM becomes FLAC). x265 is slow: about as long as the "
            + "film, twice that for an interlaced one (twice the frames), longer on an older PC.",
        _ =>
            "The longest title, the main film, is picked. The MKV keeps the MPEG-2 video as it is, every audio and subtitle track "
            + "with its language, and the chapters; LPCM audio becomes FLAC, the same audio bit for bit.",
    };

    // ---- Loading a disc ------------------------------------------------------------------------------

    partial void OnSelectedDriveChanged(OpticalDrive? value)
    {
        if (value is null) return;
        if (!value.HasDisc) { Reset(RipDiscKind.None, "No disc in this drive."); return; }
        if (value.DiscLabel == "Audio CD") _ = LoadCdAsync(value);
        else LoadDvd(value.Root, RipSource(value));
    }

    /// <summary>What to rip from: "D:" on Windows, the raw drive on Linux (a mount point doesn't take libdvdcss's keys).</summary>
    internal static string RipSource(OpticalDrive drive) => OperatingSystem.IsWindows() ? drive.Name : "/dev/" + drive.Name;

    /// <summary>A DVD image (.iso) or a VIDEO_TS folder, picked in the window.</summary>
    public void UseDvdSource(string path)
    {
        SelectedDrive = null;
        // A drive's mount point is a folder too, but drives come through OnSelectedDriveChanged.
        LoadDvd(path, path, folder: Directory.Exists(path));
    }

    private void Reset(RipDiscKind kind, string message)
    {
        _loading?.Cancel();
        _drive?.Dispose();
        _drive = null;
        _disc = null;
        _dvd = null;
        Releases.Clear();
        Tracks.Clear();
        Titles.Clear();
        SelectedTitle = null;
        Cover = null;
        CoverImage = null;
        Kind = kind;
        Message = message;
        OnPropertyChanged(nameof(HasReleases));
    }

    private async Task LoadCdAsync(OpticalDrive drive)
    {
        Reset(RipDiscKind.None, "Reading the CD and looking it up (MusicBrainz, AccurateRip)…");
        var loading = _loading = new CancellationTokenSource();
        IsBusy = true;
        Source = drive.Description;
        try
        {
            var device = OperatingSystem.IsWindows() ? drive.Root : "/dev/" + drive.Name;
            var cd = await Task.Run(() => _openCd(device), loading.Token);
            if (loading.IsCancellationRequested) { cd.Dispose(); return; }
            _drive = cd;
            var disc = await Main.CdRipper.ReadDiscAsync(cd, _online, loading.Token);
            if (loading.IsCancellationRequested) return;
            _disc = disc;
            foreach (var r in disc.Releases) Releases.Add(r);
            OnPropertyChanged(nameof(HasReleases));
            Apply(disc.DefaultTags);
            SelectedRelease = disc.Releases.FirstOrDefault(r => r.ExactDisc);
            OffsetText = disc.ListedOffset is { } o ? o.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "";
            OffsetNote = disc.ListedOffset is not null ? $"{disc.Drive.Name}, from AccurateRip's drive list"
                : disc.AccurateRip.Count > 0 ? $"{disc.Drive.Name} isn't in AccurateRip's list: the offset is found from this disc when the rip starts"
                : $"{disc.Drive.Name} isn't in AccurateRip's list, nor this disc: type the drive's offset if you know it";
            DiscNote = $"{disc.Toc.AudioTrackCount} tracks, {CdToc.Time(disc.Toc.AudioTracks.Sum(disc.Toc.Sectors))}"
                       + (disc.AccurateRip.Count > 0 ? $" · in AccurateRip ({disc.AccurateRip.Count} pressings)" : " · not in AccurateRip")
                       + (disc.Releases.Any(r => r.ExactDisc) ? " · found on MusicBrainz"
                          : disc.Releases.Count > 0 ? " · MusicBrainz only knows similar discs: pick one above if it's this one"
                          : disc.CdText is not null ? " · tags from its CD-Text" : " · not found on MusicBrainz: type the tags in");
            Kind = RipDiscKind.Cd;
            Message = HasFlac ? "" : "FLAC needs the flac encoder, which isn't installed: WAV only.";
            if (!HasFlac) FormatIndex = 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException
                                      or DllNotFoundException)
        {
            Reset(RipDiscKind.None, $"Can't read this CD: {e.Message}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_loading, loading)) IsBusy = false;
        }
    }

    partial void OnSelectedReleaseChanged(AlbumTags? value)
    {
        if (value is not null) Apply(value);
    }

    private void Apply(AlbumTags tags)
    {
        AlbumArtist = tags.AlbumArtist;
        Album = tags.Album;
        Date = tags.Date ?? "";
        Genre = tags.Genre ?? "";
        Tracks.Clear();
        foreach (var t in tags.Tracks) Tracks.Add(new RipTrackRow(t.Number, t.Title, t.Artist));
        _ = LoadCoverAsync(tags);
    }

    private int _coverRequest;

    private async Task LoadCoverAsync(AlbumTags tags)
    {
        var request = ++_coverRequest;
        Cover = null;
        CoverImage = null;
        if (tags.ReleaseId is null) return;
        var image = _online ? await CdMetadata.CoverAsync(MainViewModel.RipHttp, tags) : null;
        if (request != _coverRequest) return; // another release was picked meanwhile
        Cover = image;
        try { CoverImage = image is null ? null : new Avalonia.Media.Imaging.Bitmap(new MemoryStream(image)); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { CoverImage = null; }
    }

    /// <summary>The tags as edited, over the release they started from (its IDs, label, ISRCs).</summary>
    internal AlbumTags EditedTags()
    {
        var start = SelectedRelease ?? _disc?.DefaultTags ?? new AlbumTags();
        string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var byNumber = start.Tracks.ToDictionary(t => t.Number);
        return start with
        {
            AlbumArtist = Clean(AlbumArtist) ?? "Unknown Artist",
            Album = Clean(Album) ?? "Unknown Album",
            Date = Clean(Date),
            Genre = Clean(Genre),
            Tracks = Tracks.Select(row => (byNumber.TryGetValue(row.Number, out var t) ? t : new TrackTags(row.Number, "", "")) with
            {
                Title = Clean(row.Title) ?? $"Track {row.Number:00}",
                Artist = Clean(row.Artist) ?? Clean(AlbumArtist) ?? "Unknown Artist",
            }).ToList(),
        };
    }

    /// <summary>The offset typed in ("+48", "-472"), if any; null leaves it to AccurateRip.</summary>
    internal int? Offset => int.TryParse(OffsetText.Trim().TrimStart('+'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o)
                            && (_disc?.ListedOffset != o)
        ? o
        : null;

    private void LoadDvd(string infoSource, string ripSource, bool folder = false)
    {
        Reset(RipDiscKind.None, "");
        IsFolderSource = folder;
        Source = ripSource;
        try
        {
            var dvd = _readDvd(infoSource);
            if (dvd.Titles.Count == 0) throw new InvalidDataException("it lists no titles");
            _dvd = dvd;
            _dvdSource = ripSource;
            foreach (var t in dvd.Titles) Titles.Add(t);
            SelectedTitle = dvd.MainTitle;
            Kind = RipDiscKind.Dvd;
            DvdModeIndex = CanMkv || folder ? 0 : 1;
            Message = folder ? (CanMkv ? "" : "This folder is already a decrypted VIDEO_TS; an MKV of a title needs FFmpeg, which isn't installed.")
                : !CanMkv && !CanDecrypt ? "This build can't rip DVDs: it has neither FFmpeg's DVD reader nor libdvdread."
                : !CanMkv ? "MKV needs FFmpeg, which isn't installed: the VIDEO_TS copy only."
                : !CanDecrypt ? "The VIDEO_TS copy needs libdvdread, which isn't installed: MKV only." : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Reset(RipDiscKind.None, $"That isn't an Audio CD or a DVD-Video: {e.Message}");
        }
    }

    // ---- Starting -------------------------------------------------------------------------------------

    private bool CanStart() => !IsBusy && !Main.IsRipping && Kind switch
    {
        RipDiscKind.Cd => _disc is not null && _drive is not null,
        RipDiscKind.Dvd => _dvd is not null && (IsMkv
            ? CanMkv && CanEncode && SelectedTitle is not null && (Chapters.Count == 0 || Chapters.Any(c => c.IsChecked))
            : CanDecrypt && !IsFolderSource),
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (Kind == RipDiscKind.Cd && _drive is { } drive && _disc is { } disc)
        {
            _drive = null; // the rip has it now, and closes it
            _ = Main.StartCdRipAsync(drive, disc, new CdRipOptions
            {
                Tags = EditedTags(),
                Cover = Cover,
                Format = FormatIndex == 1 || !HasFlac ? CdRipFormat.Wav : CdRipFormat.Flac,
                OutputRoot = CdFolder,
                Offset = Offset,
                AppVersion = AppInfo.Display,
                Flac = Flac,
            });
        }
        else if (Kind == RipDiscKind.Dvd && _dvd is { } dvd && DvdOptions() is { } options)
        {
            _ = Main.StartDvdRipAsync(dvd, options);
        }
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The DVD rip as chosen in the window; null without a DVD.</summary>
    internal DvdRipOptions? DvdOptions() => _dvd is { } dvd && _dvdSource is { } source
        ? new DvdRipOptions
        {
            Source = source,
            Mode = IsMkv ? DvdRipMode.Mkv : DvdRipMode.Decrypt,
            Video = IsMkv ? Video : DvdVideo.Copy,
            Title = SelectedTitle?.Number ?? dvd.MainTitle?.Number ?? 1,
            // Every chapter in one file is the whole title, read as it always was.
            Chapters = IsMkv && Chapters.Count > 1 && (ChapterFiles || Chapters.Any(c => !c.IsChecked)) ? CheckedChapters : null,
            ChapterFiles = IsMkv && ChapterFiles,
            OutputRoot = DvdFolder,
            Ffmpeg = Ffmpeg,
            Ffprobe = Ffprobe,
        }
        : null;

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRipping)) StartCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        Main.PropertyChanged -= OnMainChanged;
        _loading?.Cancel();
        _drive?.Dispose(); // read but not ripped
        _drive = null;
    }
}
