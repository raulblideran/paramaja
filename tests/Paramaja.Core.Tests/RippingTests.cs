using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Paramaja.Core.Ripping;
using Paramaja.Core.Ripping.Cd;
using Paramaja.Core.Ripping.Dvd;

namespace Paramaja.Core.Tests;

/// <summary>A synthetic Audio CD: pseudo-random audio (seeded), so every sample can be checked.</summary>
internal static class TestDisc
{
    public static byte[] Audio(int sectors, int seed = 7)
    {
        var data = new byte[sectors * CdToc.SectorBytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static CdToc Toc(params int[] lengths)
    {
        var tracks = new List<CdTrack>();
        var at = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            tracks.Add(new CdTrack(i + 1, at, true));
            at += lengths[i];
        }
        return new CdToc(tracks, at);
    }

    public static ImageCdDrive Drive(byte[] audio, CdToc toc, int offset = 0) =>
        new(new MemoryStream(audio), toc) { ReadOffset = offset };
}

public class CdTocTests
{
    // Dangerous (Epic, 1991), as its EAC log gives the TOC.
    private static readonly CdToc Dangerous = new(
        new[] { 0, 25445, 49825, 79242, 95892, 113935, 136587, 165430, 184635, 214310, 238967, 273475, 300290, 315495 }
            .Select((s, i) => new CdTrack(i + 1, s, true)).ToList(), 347015);

    [Fact]
    public void The_disc_ids_are_the_databases_own()
    {
        Assert.Equal("http://www.accuraterip.com/accuraterip/f/f/3/dBAR-014-0026c3ff-019f0e06-be12120e.bin", Dangerous.AccurateRipUrl);
        Assert.Equal(0xbe12120eu, Dangerous.FreedbId);
        Assert.Equal("VmgNnaUMmy5W.g6LBuggcuAmBCs-", Dangerous.MusicBrainzDiscId); // MusicBrainz finds Dangerous by it
        Assert.StartsWith("1+14+347165+150+25595+", Dangerous.MusicBrainzToc);
        Assert.Equal(25445, Dangerous.Sectors(Dangerous[1]));
        Assert.Equal(31520, Dangerous.Sectors(Dangerous[14]));
    }

    [Fact]
    public void A_cd_extras_audio_ends_before_its_data_session()
    {
        var toc = new CdToc([new CdTrack(1, 0, true), new CdTrack(2, 20000, true), new CdTrack(3, 60000, false)], 90000);
        Assert.True(toc.IsEnhanced);
        Assert.Equal(60000 - CdToc.SessionGapSectors, toc.AudioLeadOutLba);
        Assert.Equal(60000 - CdToc.SessionGapSectors - 20000, toc.Sectors(toc[2]));
        Assert.Equal(2, toc.AudioTrackCount);
        Assert.Contains("dBAR-002-", toc.AccurateRipUrl);
    }

    [Theory]
    [InlineData(0, "0:00.00", "00:00:00")]
    [InlineData(25445, "5:39.20", "05:39:20")]
    [InlineData(347015, "77:06.65", "77:06:65")]
    public void Times(int sectors, string log, string cue)
    {
        Assert.Equal(log, CdToc.Time(sectors));
        Assert.Equal(cue, CdToc.CueTime(sectors));
    }
}

public class ChecksumTests
{
    [Fact]
    public void Crc32_is_zlibs()
    {
        Assert.Equal(0xCBF43926u, Crc32Ieee.Compute("123456789"u8));
        var big = TestDisc.Audio(10);
        uint slow = 0xFFFFFFFF;
        foreach (var b in big)
        {
            slow ^= b;
            for (var k = 0; k < 8; k++) slow = (slow & 1) != 0 ? 0xEDB88320 ^ (slow >> 1) : slow >> 1;
        }
        Assert.Equal(~slow, Crc32Ieee.Compute(big));
    }

    /// <summary>AccurateRip's checksums written out the plain way, to hold the streaming ones against.</summary>
    private static (uint V1, uint V2, uint F450) Reference(byte[] pcm, bool first, bool last)
    {
        var n = pcm.Length / 4;
        uint v1 = 0, v2 = 0, f450 = 0;
        for (var i = 0; i < n; i++)
        {
            ulong mult = (ulong)i + 1, sample = BinaryPrimitives.ReadUInt32LittleEndian(pcm.AsSpan(i * 4));
            if ((!first || mult >= 2940) && (!last || mult <= (ulong)(n - 2940)))
            {
                v1 += (uint)(sample * mult);
                v2 += (uint)(sample * mult) + (uint)((sample * mult) >> 32);
            }
            if (i >= 450 * 588 && i < 451 * 588) f450 += (uint)sample * (uint)(i - 450 * 588 + 1);
        }
        return (v1, v2, f450);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AccurateRip_v1_v2_and_the_offset_frame(bool first, bool last)
    {
        var pcm = TestDisc.Audio(600);
        var c = new TrackChecksums(pcm.Length / 4, first, last);
        foreach (var chunk in pcm.Chunk(7 * 4 * 101)) c.Add(chunk); // pieces that don't fall on sectors
        Assert.True(c.Complete);
        var (v1, v2, f450) = Reference(pcm, first, last);
        Assert.Equal(v1, c.AccurateRipV1);
        Assert.Equal(v2, c.AccurateRipV2);
        Assert.Equal(f450, c.OffsetFindingCrc);
        Assert.Equal(Crc32Ieee.Compute(pcm), c.Crc32);
    }

    [Fact]
    public void Peak_is_the_loudest_sample()
    {
        var pcm = new byte[588 * 4];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(10), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), 8000);
        var c = new TrackChecksums(588, false, false);
        c.Add(pcm);
        Assert.Equal(0.5, c.Peak);
    }
}

public class AccurateRipTests
{
    private static byte[] Entry(params (int Confidence, uint Crc)[][] pressings)
    {
        var b = new List<byte>();
        foreach (var p in pressings)
        {
            b.Add((byte)p.Length);
            b.AddRange(new byte[12]); // the disc's IDs
            foreach (var (confidence, crc) in p)
            {
                b.Add((byte)confidence);
                b.AddRange(BitConverter.GetBytes(crc));
                b.AddRange(BitConverter.GetBytes(0u));
            }
        }
        return b.ToArray();
    }

    [Fact]
    public void Pressings_parse_and_a_track_matches_v2_before_v1()
    {
        var data = Entry([(200, 0x11111111), (200, 0x22222222)], [(12, 0x33333333), (12, 0x44444444)], [(1, 0x55555555)]);
        var pressings = AccurateRip.Parse(data, 2);
        Assert.Equal(2, pressings.Count); // the one-track entry is another disc
        Assert.Equal(new AccurateRipResult(2, 12, 212, true), AccurateRip.Verify(pressings, 1, v1: 0x22222222, v2: 0x44444444));
        Assert.Equal(new AccurateRipResult(1, 200, 212, true), AccurateRip.Verify(pressings, 1, v1: 0x22222222, v2: 0));
        Assert.False(AccurateRip.Verify(pressings, 0, 1, 2).Accurate);
        Assert.True(AccurateRip.Verify(pressings, 0, 1, 2).InDatabase);
        Assert.Equal(AccurateRipResult.NotInDatabase, AccurateRip.Verify([], 0, 1, 2));
    }

    /// <summary>A track read by a drive with <paramref name="driveOffset"/>, with 2940 samples' margin.</summary>
    private static (uint[] Raw, uint V1) Read(byte[] disc, int start, int length, int driveOffset)
    {
        var samples = MemoryMarshal.Cast<byte, uint>(disc).ToArray();
        uint At(long i) => i < 0 || i >= samples.Length ? 0 : samples[i];
        var raw = new uint[length + 2 * 2940];
        for (var k = 0; k < raw.Length; k++) raw[k] = At(start - 2940 + k - driveOffset);
        uint v1 = 0;
        for (var i = 0; i < length; i++) v1 += (uint)(i + 1) * samples[start + i];
        return (raw, v1);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(-472)]
    [InlineData(667)]
    [InlineData(0)]
    [InlineData(2940)]
    public void A_drives_offset_is_found_from_a_match(int driveOffset)
    {
        var disc = TestDisc.Audio(400);
        var (raw, v1) = Read(disc, 100 * 588, 200 * 588, driveOffset);
        Assert.Equal(driveOffset, AccurateRip.DetectOffset(raw, 2940, [new AccurateRipTrack(5, v1, 0)]));
        Assert.Null(AccurateRip.DetectOffset(raw, 2940, [new AccurateRipTrack(5, v1 + 1, 0)]));
    }

    [Fact]
    public void Of_two_pressings_the_offset_drives_have_wins()
    {
        // Pressing B is pressing A's audio 931 samples later; a +48 drive matches B at 48 and A at 979.
        var disc = TestDisc.Audio(400);
        var (raw, v1) = Read(disc, 100 * 588, 200 * 588, 48);
        var (_, shifted) = Read(disc, 100 * 588 + 931, 200 * 588, 48);
        AccurateRipTrack[] both = [new(200, shifted, 0), new(10, v1, 0)];
        Assert.Equal(48, AccurateRip.DetectOffset(raw, 2940, both, o => o == 48 ? 417 : 0));
        Assert.Equal(48, AccurateRip.DetectOffset(raw, 2940, both)); // the smaller shift, without the list
    }

    [Fact]
    public void Drive_offsets_list()
    {
        static byte[] Record(short offset, string name)
        {
            var r = new byte[69];
            BinaryPrimitives.WriteInt16LittleEndian(r, offset);
            Encoding.ASCII.GetBytes(name).CopyTo(r, 2);
            return r;
        }
        var list = DriveOffsets.Parse([.. Record(48, "PIONEER  - DVD RW  DVR-107D"), .. Record(6, "TSSTcorp - CDDVDW SH-224DB"), .. Record(6, "ASUS     - SH-224DB")]);
        Assert.Equal(48, DriveOffsets.Find(list, "PIONEER ", "DVD RW  DVR-107D"));
        Assert.Equal(6, DriveOffsets.Find(list, "TSSTcorp", "CDDVDW SH-224DB "));
        Assert.Null(DriveOffsets.Find(list, "HL-DT-ST", "BD-RE WH16NS40"));
        Assert.Equal(2, DriveOffsets.Counts(list)[6]);
    }
}

public class MmcTests
{
    [Fact]
    public void Read_cd_asks_for_audio_sectors()
    {
        Assert.Equal([0xBE, 0x04, 0, 1, 0x86, 0xA0, 0, 0, 26, 0x10, 0, 0], Mmc.ReadCd(100_000, 26));
        Assert.Equal(0x02, Mmc.ReadCd(5, 1, subQ: true)[10]);
    }

    [Fact]
    public void Toc_and_drive_answers_parse()
    {
        var toc = new byte[4 + 8 * 3];
        BinaryPrimitives.WriteUInt16BigEndian(toc, (ushort)(toc.Length - 2));
        void Track(int i, byte control, byte number, int lba)
        {
            toc[4 + 8 * i + 1] = (byte)(0x10 | control);
            toc[4 + 8 * i + 2] = number;
            BinaryPrimitives.WriteInt32BigEndian(toc.AsSpan(4 + 8 * i + 4), lba);
        }
        Track(0, 0x01, 1, 0);       // pre-emphasis
        Track(1, 0x04, 2, 30000);   // data
        Track(2, 0x00, 0xAA, 60000);
        var parsed = Mmc.ParseToc(toc);
        Assert.Equal(60000, parsed.LeadOutLba);
        Assert.True(parsed[1].PreEmphasis);
        Assert.False(parsed[2].IsAudio);

        var inquiry = new byte[96];
        Encoding.ASCII.GetBytes("PIONEER DVD RW  DVR-107D1.20").CopyTo(inquiry, 8);
        Assert.Equal(new DriveIdentity("PIONEER ", "DVD RW  DVR-107D", "1.20"), Mmc.ParseInquiry(inquiry));

        var page = new byte[8 + 20];
        page[8] = 0x2A;
        page[8 + 5] = 0x02 | 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(8 + 12), 2048);
        Assert.Equal(new DriveFeatures(true, true, 2048), Mmc.ParseCapabilities(page));
    }

    [Fact]
    public void Catalogue_numbers_isrcs_and_q_positions()
    {
        var mcn = new byte[48];
        mcn[8] = 0x80;
        Encoding.ASCII.GetBytes("5099746580223").CopyTo(mcn, 9);
        Assert.Equal("5099746580223", Mmc.ParseMcn(mcn));
        mcn[8] = 0;
        Assert.Null(Mmc.ParseMcn(mcn));
        var isrc = new byte[48];
        isrc[8] = 0x80;
        Encoding.ASCII.GetBytes("000000000000").CopyTo(isrc, 9);
        Assert.Null(Mmc.ParseIsrc(isrc)); // what drives say for none
        Encoding.ASCII.GetBytes("USSM19100465").CopyTo(isrc, 9);
        Assert.Equal("USSM19100465", Mmc.ParseIsrc(isrc));
        Assert.Equal(new SubQ(12, 0), Mmc.ParseSubQ([0x01, 0x12, 0x00, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(Mmc.ParseSubQ([0x02, 0x12, 0x00, 0, 0, 0, 0, 0, 0, 0])); // a catalogue frame
    }

    [Fact]
    public void Cd_text_runs_across_packs_and_a_tab_repeats()
    {
        // Titles: album "Dangerous", tracks "Jam", "Why You Wanna Trip On Me"; performer for all, then a tab.
        var packs = new List<byte[]>();
        void Packs(byte type, string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            byte track = 0, sequence = 0;
            for (var i = 0; i < bytes.Length; i += 12)
            {
                var pack = new byte[18];
                pack[0] = type;
                pack[1] = track;
                pack[2] = sequence++;
                bytes.AsSpan(i, Math.Min(12, bytes.Length - i)).CopyTo(pack.AsSpan(4));
                Mmc.SetPackCrc(pack);
                packs.Add(pack);
                track = (byte)text[..Math.Min(text.Length, i + 12)].Count(c => c == '\0'); // the string the next pack starts in
            }
        }
        Packs(0x80, "Dangerous\0Jam\0Why You Wanna Trip On Me\0");
        Packs(0x81, "Michael Jackson\0\t\0\t\0");
        var data = new byte[4 + 18 * packs.Count];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(data.Length - 2));
        for (var i = 0; i < packs.Count; i++) packs[i].CopyTo(data, 4 + 18 * i);
        var text = Mmc.ParseCdText(data)!;
        Assert.Equal("Dangerous", text.Title);
        Assert.Equal("Jam", text.TrackTitles[1]);
        Assert.Equal("Why You Wanna Trip On Me", text.TrackTitles[2]);
        Assert.Equal("Michael Jackson", text.Performer);
        Assert.Equal("Michael Jackson", text.TrackPerformers[2]);

        data[4 + 5] ^= 0xFF; // a pack whose CRC no longer matches is skipped
        Assert.NotEqual("Dangerous", Mmc.ParseCdText(data)?.Title);
    }

    [Fact]
    public void Native_structures_have_the_systems_layout()
    {
        // Linux x86-64 sg_io_hdr, cdrom_read_audio, cdrom_tocentry.
        Assert.Equal(88, Marshal.SizeOf<LinuxSgIo.SgIoHdr>());
        Assert.Equal(16, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>(nameof(LinuxSgIo.SgIoHdr.Dxferp)));
        Assert.Equal(64, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>(nameof(LinuxSgIo.SgIoHdr.Status)));
        Assert.Equal(24, Marshal.SizeOf<LinuxSgIo.CdromReadAudio>());
        Assert.Equal(16, (int)Marshal.OffsetOf<LinuxSgIo.CdromReadAudio>(nameof(LinuxSgIo.CdromReadAudio.Buffer)));
        Assert.Equal(12, Marshal.SizeOf<LinuxSgIo.CdromTocEntry>());
        Assert.Equal(4, (int)Marshal.OffsetOf<LinuxSgIo.CdromTocEntry>(nameof(LinuxSgIo.CdromTocEntry.Lba)));
        // Windows x64 SCSI_PASS_THROUGH_DIRECT (56 bytes), with its sense buffer after a filler; RAW_READ_INFO.
        Assert.Equal(56, Marshal.SizeOf<WindowsScsi.ScsiPassThroughDirect>());
        Assert.Equal(24, (int)Marshal.OffsetOf<WindowsScsi.ScsiPassThroughDirect>(nameof(WindowsScsi.ScsiPassThroughDirect.DataBuffer)));
        Assert.Equal(36, (int)Marshal.OffsetOf<WindowsScsi.ScsiPassThroughDirect>(nameof(WindowsScsi.ScsiPassThroughDirect.Cdb)));
        Assert.Equal(60, (int)Marshal.OffsetOf<WindowsScsi.ScsiPassThroughDirectWithSense>(nameof(WindowsScsi.ScsiPassThroughDirectWithSense.Sense)));
        Assert.Equal(16, Marshal.SizeOf<WindowsScsi.RawReadInfo>());
        Assert.Equal(@"\\.\D:", WindowsScsi.DevicePath(@"d:\"));
        Assert.Equal(@"\\.\E:", WindowsScsi.DevicePath("E:"));
    }

    [Fact]
    public void Sense_data_both_formats()
    {
        var fixedFormat = new byte[18];
        fixedFormat[0] = 0x70;
        fixedFormat[2] = 0x03;  // medium error
        fixedFormat[12] = 0x11; // unrecovered read error
        Assert.Equal(new ScsiResult(false, 3, 0x11, 0), LinuxSgIo.Sense(fixedFormat, 2));
        Assert.Equal(new ScsiResult(false, 5, 0x24, 0), LinuxSgIo.Sense([0x72, 0x05, 0x24, 0x00], 2));
    }
}

public class SecureReaderTests
{
    private static byte[] TrackAudio(byte[] disc, CdToc toc, CdTrack track, int sampleShift = 0)
    {
        var bytes = (long)toc.Sectors(track) * CdToc.SectorBytes;
        var result = new byte[bytes];
        var from = (long)track.StartLba * CdToc.SectorBytes + sampleShift * 4L;
        for (long i = 0; i < bytes; i++) result[i] = from + i >= 0 && from + i < disc.Length ? disc[from + i] : (byte)0;
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(667)]
    [InlineData(-1164)]
    public void Offset_correction_gives_the_discs_audio_with_silence_beyond_its_ends(int offset)
    {
        var disc = TestDisc.Audio(900);
        var toc = TestDisc.Toc(300, 300, 300);
        using var drive = TestDisc.Drive(disc, toc, offset);
        var reader = new SecureReader(drive, toc, offset);
        foreach (var track in toc.Tracks)
        {
            var output = new MemoryStream();
            var read = reader.ReadTrack(track, output);
            Assert.True(read.Ok);
            Assert.Equal(1, read.Quality);
            // The drive's audio past the disc's ends never existed: those samples are silence.
            var expected = TrackAudio(disc, toc, track);
            if (offset > 0 && track.Number == 3) expected.AsSpan(expected.Length - offset * 4).Clear();
            if (offset < 0 && track.Number == 1) expected.AsSpan(0, -offset * 4).Clear();
            Assert.Equal(expected, output.ToArray());
            Assert.Equal(Crc32Ieee.Compute(expected), read.Copy.Crc32);
        }
    }

    [Fact]
    public void A_block_that_reads_wrong_is_read_again_until_two_reads_agree()
    {
        var disc = TestDisc.Audio(400);
        var toc = TestDisc.Toc(400);
        // A small cache: the reads that flush it stay clear of the sectors being re-read.
        using var drive = new ImageCdDrive(new MemoryStream(disc), toc) { Features = new DriveFeatures(true, false, 64) };
        drive.Flaky[100] = 3; // wrong in the test pass, the copy pass and the first re-read
        drive.Unreadable[250] = 2;
        var output = new MemoryStream();
        var read = new SecureReader(drive, toc, 0).ReadTrack(toc[1], output);
        Assert.Equal(disc, output.ToArray());
        Assert.Empty(read.Suspicious);
        Assert.Equal(3, read.Rereads);
        Assert.True(read.Quality < 1);
        Assert.NotEqual(read.TestCrc, read.Copy.Crc32); // the test pass read it wrong: the log says so
    }

    [Fact]
    public void A_block_that_never_reads_the_same_twice_is_suspicious()
    {
        var disc = TestDisc.Audio(200);
        var toc = TestDisc.Toc(200);
        using var drive = TestDisc.Drive(disc, toc);
        drive.Flaky[60] = int.MaxValue;
        var read = new SecureReader(drive, toc, 0).ReadTrack(toc[1], new MemoryStream());
        Assert.Equal([52], read.Suspicious); // its block starts at sector 52
        Assert.False(read.Ok);
        Assert.Equal(SecureReader.MaxReads - 2, read.Rereads);
    }

    [Fact]
    public void Every_sector_is_read_twice_and_short_tracks_get_the_cache_flushed()
    {
        var disc = TestDisc.Audio(3000);
        var toc = TestDisc.Toc(300, 2700);
        using var drive = new ImageCdDrive(new MemoryStream(disc), toc) { Features = new DriveFeatures(true, false, 512) };
        var reader = new SecureReader(drive, toc, 0);
        // A track the 512 KB cache (223 sectors) could nearly hold: read twice, with a flush of twice the cache between.
        reader.ReadTrack(toc[1], new MemoryStream());
        Assert.Equal(2 * 300 + 2 * (512 * 1024 / CdToc.SectorBytes + 1), drive.SectorsRead);
        // A long one: just twice.
        var before = drive.SectorsRead;
        reader.ReadTrack(toc[2], new MemoryStream());
        Assert.Equal(2 * 2700, drive.SectorsRead - before);
    }

    [Fact]
    public void Gaps_come_from_the_q_sub_channel()
    {
        var toc = new CdToc([new CdTrack(1, 33, true), new CdTrack(2, 3000, true), new CdTrack(3, 7000, true), new CdTrack(4, 9000, true)], 12000);
        using var drive = new ImageCdDrive(new MemoryStream(new byte[12000 * CdToc.SectorBytes]), toc)
        {
            Gaps = new Dictionary<int, int> { [2] = 150, [3] = 1234 },
        };
        var gaps = GapDetector.Detect(drive, toc)!;
        Assert.Equal(33, gaps[1]); // audio before track 1: a hidden track
        Assert.Equal(150, gaps[2]);
        Assert.Equal(1234, gaps[3]);
        Assert.Equal(0, gaps[4]);
        using var noSubQ = new ImageCdDrive(new MemoryStream(new byte[12000 * CdToc.SectorBytes]), toc) { CanReadSubQ = false };
        Assert.Null(GapDetector.Detect(noSubQ, toc));
    }
}

public class CdRipTests
{
    private static CdDisc Disc(ICdDrive drive, CdToc toc) =>
        new(toc, drive.Identity, drive.Features, null, "5099746580223", [], [], null, new Dictionary<int, int>());

    private static AlbumTags Tags(CdToc toc) => new()
    {
        Album = "Test: Album?", AlbumArtist = "Test Artist", Date = "1991-11-26", Genre = "Pop",
        Tracks = toc.AudioTracks.Select(t => new TrackTags(t.Number, $"Song {t.Number}", t.Number == 2 ? "Guest" : "Test Artist")).ToList(),
    };

    [Fact]
    public async Task A_wav_rip_has_its_tracks_cue_sheet_and_a_log_that_scores_100()
    {
        var disc = TestDisc.Audio(900);
        var toc = TestDisc.Toc(300, 300, 300);
        using var drive = new ImageCdDrive(new MemoryStream(disc), toc) { ReadOffset = 6, Gaps = new Dictionary<int, int> { [3] = 75 } };
        var root = TestEnvironment.TempDir();
        var ripper = new CdRipper(new HttpClient(), Path.Combine(root, "cache"));
        var result = await ripper.RipAsync(drive, Disc(drive, toc), new CdRipOptions
        {
            Tags = Tags(toc), Format = CdRipFormat.Wav, OutputRoot = root, Offset = 6, AppVersion = "1.10.0",
        });
        Assert.Equal(Path.Combine(root, "Test Artist", "Test - Album (1991)"), result.Folder);
        Assert.Equal(["01 - Song 1.wav", "02 - Guest - Song 2.wav", "03 - Song 3.wav"], result.Tracks.Select(t => Path.GetFileName(t.File)));
        var wav = File.ReadAllBytes(result.Tracks[1].File);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(disc.AsSpan(300 * CdToc.SectorBytes, 300 * CdToc.SectorBytes).ToArray(), wav.AsSpan(44, 300 * CdToc.SectorBytes).ToArray());
        Assert.Contains("INAMSong 2", Encoding.UTF8.GetString(wav, 44 + 300 * CdToc.SectorBytes, wav.Length - 44 - 300 * CdToc.SectorBytes).Replace("\0", "").Replace("\u000b", ""));
        Assert.Equal(100, result.Score);
        var log = File.ReadAllText(result.LogPath);
        Assert.Contains("Read offset correction                      : 6 (set by hand)", log);
        Assert.Contains("Gap handling                                : Appended to previous track", log);
        Assert.Contains("Pre-gap length  0:00:01.00", log); // track 3's
        Assert.Contains($"Copy CRC {result.Tracks[0].Read.Copy.Crc32:X8}", log);
        Assert.Contains("None of the tracks are present in the AccurateRip database", log);
        Assert.Contains("Log score: 100 %", log);
        Assert.Matches("==== Log checksum \\(SHA-256\\) [0-9A-F]{64} ====", log);
        var cue = File.ReadAllText(result.CuePath);
        Assert.Contains("CATALOG 5099746580223", cue);
        Assert.Contains("INDEX 00 00:03:00", cue); // track 3's gap: the last second of track 2's file
        Assert.Contains("FILE \"03 - Song 3.wav\" WAVE", cue);
        Assert.Contains("REM DATE 1991", cue);
    }

    [Fact]
    public async Task Flac_tracks_are_level_8_with_their_tags()
    {
        if (FlacEncoder.Find() is not { } flac) return; // no encoder on this machine
        var disc = TestDisc.Audio(300);
        var toc = TestDisc.Toc(150, 150);
        using var drive = TestDisc.Drive(disc, toc);
        var root = TestEnvironment.TempDir();
        var result = await new CdRipper(new HttpClient(), root).RipAsync(drive, Disc(drive, toc), new CdRipOptions
        {
            Tags = Tags(toc) with { ReleaseId = "65fd65e8-f271-35f5-b839-c12725aab5a5" }, OutputRoot = root, Flac = flac, Offset = 0,
        });
        Assert.All(result.Tracks, t => Assert.EndsWith(".flac", t.File));
        Assert.Empty(Directory.GetFiles(result.Folder, "*.wav"));
        var bytes = File.ReadAllBytes(result.Tracks[0].File);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(bytes, 0, 4));
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        Assert.Contains("TITLE=Song 1", text);
        Assert.Contains("MUSICBRAINZ_ALBUMID=65fd65e8-f271-35f5-b839-c12725aab5a5", text);
        Assert.Contains("TRACKTOTAL=2", text);
    }

    [Fact]
    public async Task A_cancelled_rip_leaves_nothing()
    {
        var disc = TestDisc.Audio(3000);
        var toc = TestDisc.Toc(1000, 1000, 1000);
        using var drive = TestDisc.Drive(disc, toc);
        var root = TestEnvironment.TempDir();
        using var cts = new CancellationTokenSource();
        var progress = new Inline(p => { if (p.Track == 2) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CdRipper(new HttpClient(), root).RipAsync(drive, Disc(drive, toc),
            new CdRipOptions { Tags = Tags(toc), Format = CdRipFormat.Wav, OutputRoot = root, Offset = 0 }, progress, cts.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "Test Artist")));
    }

    [Fact]
    public void The_log_deducts_what_log_checkers_do()
    {
        var toc = TestDisc.Toc(10);
        var disc = new CdDisc(toc, new DriveIdentity("A", "B", "1"), new DriveFeatures(false, false, 0), null, null, [], [], null,
            new Dictionary<int, int>());
        var checksums = new TrackChecksums(0, true, true);
        var bad = new CdTrackResult(new TrackTags(1, "x", "y"), "f", 10, 0, new TrackRead(1, checksums, 0.5, [3], 18, TimeSpan.Zero, 1),
            AccurateRipResult.NotInDatabase);
        var deductions = RipLog.Deduct(disc, [bad], OffsetSource.Unknown, null).Select(d => d.Item2).ToList();
        Assert.Equal([5, 20, 10, 30, 20], deductions);
    }

    [Fact]
    public void Musicbrainz_answers_become_tags()
    {
        var toc = TestDisc.Toc(100, 100);
        var json = $$$"""
        {"releases": [{"id": "r1", "title": "Dangerous", "date": "1991-11-26", "country": "US", "barcode": "07464454002",
          "artist-credit": [{"name": "Michael Jackson", "joinphrase": "", "artist": {"id": "a1"}}],
          "label-info": [{"catalog-number": "EK 45400", "label": {"name": "Epic"}}],
          "release-group": {"id": "g1", "genres": [{"name": "contemporary r&b", "count": 4}, {"name": "pop", "count": 1}]},
          "media": [{"position": 1, "discs": [{"id": "{{{toc.MusicBrainzDiscId}}}"}], "tracks": [
            {"id": "t1", "title": "Jam", "artist-credit": [{"name": "Michael Jackson", "joinphrase": ""}], "recording": {"id": "rec1", "isrcs": ["USSM10020717"]}},
            {"id": "t2", "title": "Why You Wanna Trip on Me", "artist-credit": [{"name": "Michael Jackson", "joinphrase": " feat. "}, {"name": "Slash", "joinphrase": ""}], "recording": {"id": "rec2"}}
          ]}]}]}
        """;
        var album = Assert.Single(CdMetadata.Parse(json, toc));
        Assert.Equal("Michael Jackson — Dangerous · 1991-11-26 US · Epic EK 45400", album.Description);
        Assert.Equal("Contemporary R&B", album.Genre);
        Assert.Equal("1991", album.Year);
        Assert.Equal("Michael Jackson feat. Slash", album.Tracks[1].Artist);
        Assert.Equal("USSM10020717", album.Tracks[0].Isrc);
        Assert.Equal("rec1", album.Tracks[0].RecordingId);
        Assert.Equal("r1", album.ReleaseId);
    }

    [Theory]
    [InlineData("AC/DC: Live", "AC - DC - Live")]
    [InlineData("What?*", "What")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("  ", "_")]
    public void Names_any_system_takes(string name, string safe) => Assert.Equal(safe, RipNames.Safe(name));

    [Fact]
    public void The_flac_encoder_gets_level_8_verify_and_the_tags()
    {
        var args = FlacEncoder.Arguments("in.wav", "out.flac", [("TITLE", "Jam"), ("GENRE", null)], "cover.jpg").ToList();
        Assert.Equal(["-8", "--verify", "--silent", "--force", "-T", "TITLE=Jam", "--picture=cover.jpg", "-o", "out.flac", "in.wav"], args);
    }

    private sealed class Inline(Action<RipProgress> report) : IProgress<RipProgress>
    {
        public void Report(RipProgress value) => report(value);
    }
}

public class DvdRipTests
{
    /// <summary>A VIDEO_TS.IFO and a VTS_01_0.IFO: two titles in set 1, of 1:30:00.12 (two chains) and 0:02:05.</summary>
    internal static Dictionary<string, byte[]> Ifos()
    {
        var vmg = new byte[3 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(vmg, 0);
        BinaryPrimitives.WriteUInt32BigEndian(vmg.AsSpan(0xC4), 1); // TT_SRPT at sector 1
        var tt = 2048;
        BinaryPrimitives.WriteUInt16BigEndian(vmg.AsSpan(tt), 2);
        void Title(int i, byte angles, ushort chapters, byte set, byte inSet)
        {
            var e = tt + 8 + 12 * i;
            vmg[e + 1] = angles;
            BinaryPrimitives.WriteUInt16BigEndian(vmg.AsSpan(e + 2), chapters);
            vmg[e + 6] = set;
            vmg[e + 7] = inSet;
        }
        Title(0, 1, 3, 1, 1);
        Title(1, 2, 1, 1, 2);

        var vts = new byte[4 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(vts, 0);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(0xC8), 1); // VTS_PTT_SRPT at sector 1
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(0xCC), 2); // VTS_PGCI at sector 2
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(0x202), 2); // two audio streams
        vts[0x204] = 4 << 5 | 0x04; vts[0x205] = 1; Encoding.ASCII.GetBytes("en").CopyTo(vts, 0x206);     // LPCM stereo, English
        vts[0x20C] = 0 << 5 | 0x04; vts[0x20D] = 5; Encoding.ASCII.GetBytes("de").CopyTo(vts, 0x20E);     // AC-3 5.1, German
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(0x254), 1);
        vts[0x256] = 0x01; Encoding.ASCII.GetBytes("fr").CopyTo(vts, 0x258);
        // Chapters: title 1 = chain 1 programs 1-2, chain 2 program 1; title 2 = chain 3.
        var ptt = 2048;
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(ptt), 2);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(ptt + 4), 8 + 8 + 12 + 4 - 1);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(ptt + 8), 16);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(ptt + 12), 28);
        ushort[] entries = [1, 1, 1, 2, 2, 1, 3, 1];
        for (var i = 0; i < entries.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(ptt + 16 + 2 * i), entries[i]);
        var pgci = 2 * 2048;
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(pgci), 3);
        byte[][] times = [[0x01, 0x00, 0x00, 0x40 | 0x12], [0x00, 0x30, 0x00, 0x40], [0x00, 0x02, 0x05, 0x40]];
        for (var c = 0; c < 3; c++)
        {
            var at = 8 + 8 * 3 + 16 * c;
            BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(pgci + 8 + 8 * c + 4), (uint)at);
            times[c].CopyTo(vts, pgci + at + 4);
        }
        return new() { ["VIDEO_TS.IFO"] = vmg, ["VTS_01_0.IFO"] = vts };
    }

    [Fact]
    public void Titles_come_from_the_ifos()
    {
        var files = Ifos();
        var titles = DvdInfo.ParseTitles(n => files.GetValueOrDefault(n));
        Assert.Equal(2, titles.Count);
        Assert.Equal(3600 + 12 / 25.0 + 1800, titles[0].Duration.TotalSeconds, 3); // both its chains: 1:00:00.12 and 0:30:00
        Assert.Equal(3, titles[0].Chapters);
        Assert.Equal(TimeSpan.FromSeconds(125), titles[1].Duration);
        Assert.Equal(2, titles[1].Angles);
        Assert.Equal(["LPCM stereo [en]", "AC-3 5.1 [de]"], titles[0].Audio.Select(a => a.Label));
        Assert.Equal(["fr"], titles[0].Subtitles);
        Assert.Equal("Title 1 · 1:30:00 · 3 chapters · LPCM stereo [en], AC-3 5.1 [de] · 1 subtitle", titles[0].Label);
        Assert.Throws<InvalidDataException>(() => DvdInfo.ParseTitles(_ => null));
    }

    [Fact]
    public void A_folder_dvd_is_read_with_its_files()
    {
        var root = Path.Combine(TestEnvironment.TempDir(), "CONCERT_1988");
        var videoTs = Directory.CreateDirectory(Path.Combine(root, "VIDEO_TS")).FullName;
        foreach (var (name, data) in Ifos()) File.WriteAllBytes(Path.Combine(videoTs, name), data);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_1.VOB"), new byte[4096]);
        var info = DvdInfo.Read(root);
        Assert.Equal("CONCERT_1988", info.Name);
        Assert.Equal(1, info.MainTitle!.Number);
        Assert.Contains(("VTS_01_1.VOB", 4096L), info.Files);
        Assert.Equal("The One", DvdInfo.DiscName("/dev/sr0", "THE_ONE"));
        Assert.Equal("DVD", DvdInfo.DiscName("/dev/sr0", null));
    }

    [Theory]
    [InlineData("VIDEO_TS.IFO", 0, DvdRead.Domain.InfoFile, 0)]
    [InlineData("VIDEO_TS.VOB", 0, DvdRead.Domain.MenuVobs, 0)]
    [InlineData("VTS_01_0.BUP", 1, DvdRead.Domain.InfoBackupFile, 0)]
    [InlineData("VTS_12_0.VOB", 12, DvdRead.Domain.MenuVobs, 0)]
    [InlineData("vts_03_4.vob", 3, DvdRead.Domain.TitleVobs, 4)]
    public void Vob_and_ifo_names(string name, int set, DvdRead.Domain domain, int part) =>
        Assert.Equal((set, domain, part), DvdRipper.Parse(name));

    [Theory]
    [InlineData("VTS_0000.SFK")]
    [InlineData("VTS_01_0.IFO.sfk0")]
    [InlineData("VTS_00_1.VOB")]
    public void Other_files_are_not_dvd_files(string name) => Assert.Null(DvdRipper.Parse(name));

    [Fact]
    public void The_mkv_copies_everything_and_turns_lpcm_into_flac()
    {
        var o = new DvdRipOptions { Source = "/dev/sr0", Title = 3, OutputRoot = "/out" };
        var args = DvdRipper.MkvArguments(o, preindex: false, lpcm: [0, 2], "Concert", "/out/Concert.mkv").ToList();
        Assert.Equal(["-f", "dvdvideo", "-title", "3", "-angle", "1", "-i", "/dev/sr0", "-map", "0", "-c", "copy",
            "-c:a:0", "flac", "-compression_level:a:0", "8", "-c:a:2", "flac", "-compression_level:a:2", "8",
            "-metadata", "title=Concert", "-f", "matroska", "/out/Concert.mkv"], args.SkipWhile(a => a != "-f"));
        Assert.Contains("-preindex", DvdRipper.InputArguments(o, preindex: true));
        var (streams, duration) = DvdRipper.ParseProbe("""
            {"streams": [{"codec_name": "mpeg2video", "codec_type": "video"}, {"codec_name": "pcm_dvd", "codec_type": "audio"},
              {"codec_name": "ac3", "codec_type": "audio"}], "format": {"duration": "2382.000000"}}
            """);
        Assert.Equal(("audio", "pcm_dvd"), streams[1]);
        Assert.Equal(2382, duration);
    }
}
