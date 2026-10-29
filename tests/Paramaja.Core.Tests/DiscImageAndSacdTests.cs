using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Paramaja.Core.Input;
using Paramaja.Core.Playback;
using Paramaja.Core.Playback.Sacd;

namespace Paramaja.Core.Tests;

public class DiscImageTests
{
    private const int S = DiscImage.SectorSize;

    /// <summary>An image with an ISO 9660 root holding <paramref name="folders"/>, and/or a UDF volume of <paramref name="udfRevision"/>.</summary>
    internal static MemoryStream Image(string[]? folders, int udfRevision = 0)
    {
        var data = new byte[300 * S];
        if (folders is not null)
        {
            var pvd = data.AsSpan(16 * S);
            pvd[0] = 1;
            "CD001"u8.CopyTo(pvd[1..]);
            BinaryPrimitives.WriteUInt32LittleEndian(pvd[(156 + 2)..], 20); // root directory at sector 20
            BinaryPrimitives.WriteUInt32LittleEndian(pvd[(156 + 10)..], S);
            var dir = data.AsSpan(20 * S);
            var pos = 0;
            foreach (var name in new[] { "\0", "\u0001" }.Concat(folders))
            {
                var bytes = Encoding.ASCII.GetBytes(name);
                var length = 33 + bytes.Length + (bytes.Length % 2 == 0 ? 1 : 0);
                dir[pos] = (byte)length;
                dir[pos + 32] = (byte)bytes.Length;
                bytes.CopyTo(dir[(pos + 33)..]);
                pos += length;
            }
        }
        if (udfRevision > 0)
        {
            var avdp = data.AsSpan(256 * S);
            BinaryPrimitives.WriteUInt16LittleEndian(avdp, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(avdp[16..], 4 * S);
            BinaryPrimitives.WriteUInt32LittleEndian(avdp[20..], 32); // the volume descriptor sequence
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32 * S), 1); // primary volume descriptor
            var lvd = data.AsSpan(33 * S);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd, 6);
            "*OSTA UDF Compliant"u8.CopyTo(lvd[217..]);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd[240..], (ushort)udfRevision);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34 * S), 8);
        }
        return new MemoryStream(data);
    }

    [Fact]
    public void Dvd_and_bluray_images_are_told_by_their_folders_and_udf()
    {
        Assert.Equal(DiscImageKind.Dvd, DiscImage.Probe(Image(["AUDIO_TS", "VIDEO_TS;1"], 0x0102)));
        Assert.Equal(DiscImageKind.Bluray, DiscImage.Probe(Image(["BDMV", "CERTIFICATE"], 0x0250)));
        Assert.Equal(DiscImageKind.Bluray, DiscImage.Probe(Image(null, 0x0250)));   // UDF 2.50 alone: a Blu-ray
        Assert.Equal(DiscImageKind.Bluray, DiscImage.Probe(Image(null, 0x0260)));
        Assert.Equal(DiscImageKind.Dvd, DiscImage.Probe(Image(null, 0x0102)));      // UDF 1.02 alone: a DVD
        Assert.Equal(DiscImageKind.Unknown, DiscImage.Probe(Image(["SOURCES", "BOOT"], 0x0102))); // an installer ISO
        Assert.Equal(DiscImageKind.Unknown, DiscImage.Probe(Image(["DATA"])));
        Assert.Equal(DiscImageKind.Unknown, DiscImage.Probe(new MemoryStream(new byte[100])));
    }

    [Fact]
    public void Images_play_as_their_discs_and_others_as_files()
    {
        var dir = TestEnvironment.TempDir();
        var dvd = Path.Combine(dir, "Concert.iso");
        File.WriteAllBytes(dvd, Image(["VIDEO_TS"], 0x0102).ToArray());
        var bd = Path.Combine(dir, "Film.ISO");
        File.WriteAllBytes(bd, Image(null, 0x0250).ToArray());
        var other = Path.Combine(dir, "Windows.iso");
        File.WriteAllBytes(other, Image(["SOURCES"]).ToArray());
        var sacd = SacdTestImage.Write(Path.Combine(dir, "Album.iso"), "Album", null,
            new SacdTestImage.Area(true, 2, [new("One", 3)]));

        Assert.Equal((InputKind.Dvd, dvd), InputClassifier.Classify(dvd));
        Assert.Equal((InputKind.Bluray, bd), InputClassifier.Classify(bd));
        Assert.Equal((InputKind.File, other), InputClassifier.Classify(other));
        Assert.Equal((InputKind.Sacd, sacd), InputClassifier.Classify(sacd));
        Assert.Equal("Concert", PlayerService.DiscTitle(dvd));
    }

    [Fact]
    public void A_cd_images_cue_sheet_plays_as_an_audio_cd()
    {
        var dir = TestEnvironment.TempDir();
        File.WriteAllBytes(Path.Combine(dir, "Album.bin"), new byte[2352 * 10]);
        var cue = Path.Combine(dir, "Album.cue");
        File.WriteAllText(cue, "TITLE \"Album\"\nFILE \"Album.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:00:05\n");
        var sheet = CueSheet.Read(cue);
        Assert.Equal("BINARY", sheet.Tracks[0].FileType);
        Assert.Equal("AUDIO", sheet.Tracks[1].Mode);
        Assert.True(CueSheet.IsCdImage(sheet));
        Assert.Equal((InputKind.AudioCd, cue), InputClassifier.Classify(cue));
        Assert.Equal((InputKind.AudioCd, cue), InputClassifier.Classify(Path.Combine(dir, "Album.bin"))); // its .bin picked
        Assert.Equal((InputKind.AudioCd, cue), InputClassifier.Classify(dir));                            // its folder

        // A rip's cue sheet (WAVE files) still plays as one, through mpv.
        Assert.False(CueSheet.IsCdImage(CueSheet.Parse(["FILE \"01.wav\" WAVE", "TRACK 01 AUDIO", "INDEX 01 00:00:00"])));
        Assert.False(CueSheet.IsCdImage(CueSheet.Parse(["FILE data.bin BINARY", "TRACK 01 MODE1/2352", "INDEX 01 00:00:00"])));
        Assert.Equal("BINARY", CueSheet.Parse(["FILE data.bin BINARY", "TRACK 01 AUDIO", "INDEX 01 00:00:00"]).Tracks[0].FileType);
    }
}

public class DvdAspectTests
{
    /// <summary>VIDEO_TS.IFO with a title table, and a VTS IFO per title set with its shape (0 is 4:3, 3 is 16:9).</summary>
    private static Dictionary<string, byte[]> Ifos(int[] titleSets, Dictionary<int, int> setShapes)
    {
        var vmg = new byte[2 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(vmg, 0);
        BinaryPrimitives.WriteUInt32BigEndian(vmg.AsSpan(0xC4), 1); // the title table at its second sector
        BinaryPrimitives.WriteUInt16BigEndian(vmg.AsSpan(2048), (ushort)titleSets.Length);
        for (var t = 0; t < titleSets.Length; t++) vmg[2048 + 8 + 12 * t + 6] = (byte)titleSets[t];
        var files = new Dictionary<string, byte[]> { ["VIDEO_TS.IFO"] = vmg };
        foreach (var (set, shape) in setShapes)
        {
            var vts = new byte[2048];
            Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(vts, 0);
            vts[0x200] = (byte)(0x40 | 0x10 | shape << 2); // MPEG-2, PAL, the shape
            files[$"VTS_{set:00}_0.IFO"] = vts;
        }
        return files;
    }

    [Fact]
    public void Each_titles_shape_comes_from_its_title_set()
    {
        var files = Ifos([1, 2, 2, 3], new() { [1] = 0, [2] = 3 }); // set 3's IFO is missing
        var aspects = DvdAspect.Read(name => files.GetValueOrDefault(name));
        Assert.Equal(4.0 / 3, aspects[1]);
        Assert.Equal(16.0 / 9, aspects[2]);
        Assert.Equal(16.0 / 9, aspects[3]);
        Assert.False(aspects.ContainsKey(4));
        Assert.Empty(DvdAspect.Read(_ => null));
        Assert.Empty(DvdAspect.TitleAspects(Path.Combine(TestEnvironment.TempDir(), "missing")));
    }

    [Fact]
    public void A_dvd_folder_is_read_too()
    {
        var root = TestEnvironment.TempDir();
        var videoTs = Directory.CreateDirectory(Path.Combine(root, "video_ts")).FullName; // any case
        foreach (var (name, bytes) in Ifos([1], new() { [1] = 3 })) File.WriteAllBytes(Path.Combine(videoTs, name.ToLowerInvariant()), bytes);
        Assert.Equal(16.0 / 9, DvdAspect.TitleAspects(root)[1]);
    }
}

public class SacdTests
{
    private static readonly SacdTestImage.Area Stereo = new(true, 2, [new("Wanna Be Startin' Somethin'", 40), new("Baby Be Mine", 25), new("Café", 10)]);
    private static readonly SacdTestImage.Area Surround = new(false, 6, [new("Thriller", 12), new("Beat It", 8)]);

    private static string WriteImage(params SacdTestImage.Area[] areas) =>
        SacdTestImage.Write(Path.Combine(TestEnvironment.TempDir(), "sacd.iso"), "Thriller", "Michael Jackson", areas);

    [Fact]
    public void The_image_lists_its_areas_tracks_and_text()
    {
        var sacd = SacdImage.Read(WriteImage(Surround, Stereo));
        Assert.Equal("Thriller", sacd.AlbumTitle);
        Assert.Equal("Michael Jackson", sacd.AlbumArtist);
        Assert.Equal([SacdAreaKind.Stereo, SacdAreaKind.Multichannel], sacd.Areas.Select(a => a.Kind)); // stereo first
        var stereo = sacd.Areas[0];
        Assert.Equal(2, stereo.Channels);
        Assert.False(stereo.Dst);
        Assert.Equal("Stereo", stereo.Label);
        Assert.Equal(["Wanna Be Startin' Somethin'", "Baby Be Mine", "Café"], stereo.Tracks.Select(t => t.Title));
        Assert.Equal([40, 25, 10], stereo.Tracks.Select(t => t.Frames));
        Assert.Equal(40 / 75.0, stereo.Tracks[0].Seconds, 6);
        Assert.Equal("Multichannel 5.1", sacd.Areas[1].Label);
        Assert.Equal(6, sacd.Areas[1].Channels);
        Assert.Equal(DiscImageKind.Sacd, DiscImage.Probe(sacd.Path));
    }

    [Fact]
    public void Not_an_sacd_is_refused() =>
        Assert.Throws<InvalidDataException>(() => SacdImage.Read(new MemoryStream(new byte[600 * 2048])));

    private static byte[] ReadAll(Stream s)
    {
        var all = new MemoryStream();
        s.CopyTo(all);
        return all.ToArray();
    }

    /// <summary>The DSDIFF chunk ids in order, with their sizes, from the top level and inside PROP.</summary>
    private static List<(string Id, long Size, int DataAt)> Chunks(byte[] dff, int from, int to)
    {
        var list = new List<(string, long, int)>();
        for (var p = from; p + 12 <= to;)
        {
            var id = Encoding.ASCII.GetString(dff, p, 4);
            var size = (long)BinaryPrimitives.ReadUInt64BigEndian(dff.AsSpan(p + 4));
            list.Add((id, size, p + 12));
            if (id is "DSD " or "DST ") break; // the audio runs to the end
            p += 12 + (int)size + (int)(size & 1);
        }
        return list;
    }

    [Fact]
    public void A_plain_dsd_track_is_a_dsdiff_file_of_its_frames()
    {
        var path = WriteImage(Stereo, Surround);
        var sacd = SacdImage.Read(path);
        foreach (var (area, areaIndex) in new[] { (sacd.Areas[0], 0), (sacd.Areas[1], 1) })
        for (var t = 0; t < area.Tracks.Count; t++)
        {
            using var stream = new SacdTrackStream(File.OpenRead(path), area, area.Tracks[t]);
            var dff = ReadAll(stream);
            Assert.Equal("FRM8", Encoding.ASCII.GetString(dff, 0, 4));
            Assert.Equal("DSD ", Encoding.ASCII.GetString(dff, 12, 4));
            var chunks = Chunks(dff, 16, dff.Length);
            Assert.Equal(["FVER", "PROP", "DSD "], chunks.Select(c => c.Id));
            var prop = chunks[1];
            var props = Chunks(dff, prop.DataAt + 4, prop.DataAt + (int)prop.Size);
            Assert.Equal(["FS  ", "CHNL", "CMPR"], props.Select(c => c.Id));
            Assert.Equal(2_822_400u, BinaryPrimitives.ReadUInt32BigEndian(dff.AsSpan(props[0].DataAt)));
            Assert.Equal(area.Channels, BinaryPrimitives.ReadUInt16BigEndian(dff.AsSpan(props[1].DataAt)));
            Assert.Equal("DSD ", Encoding.ASCII.GetString(dff, props[2].DataAt, 4));

            var frameBytes = area.Channels * SacdTestImage.DsdFrame;
            var data = chunks[2];
            Assert.Equal((long)area.Tracks[t].Frames * frameBytes, data.Size);
            Assert.Equal(dff.Length, data.DataAt + data.Size);
            Assert.Equal(stream.KnownLength, dff.Length);
            Assert.Equal(dff.Length - 12, (long)BinaryPrimitives.ReadUInt64BigEndian(dff.AsSpan(4)));
            for (var f = 0; f < area.Tracks[t].Frames; f++)
                Assert.True(dff.AsSpan(data.DataAt + f * frameBytes, frameBytes)
                        .SequenceEqual(SacdTestImage.FrameBytes(areaIndex, t, f, frameBytes)),
                    $"area {area.Kind}, track {t + 1}, frame {f}");
        }
    }

    [Fact]
    public void Reading_can_start_anywhere_in_a_track()
    {
        var path = WriteImage(Stereo);
        var area = SacdImage.Read(path).Areas[0];
        using var stream = new SacdTrackStream(File.OpenRead(path), area, area.Tracks[0]);
        var whole = ReadAll(stream);
        var frameBytes = 2 * SacdTestImage.DsdFrame;
        var at = whole.Length - 7 * frameBytes - 1234;
        stream.Position = at;
        var buffer = new byte[3 * frameBytes];
        stream.ReadExactly(buffer);
        Assert.True(buffer.AsSpan().SequenceEqual(whole.AsSpan(at, buffer.Length)));
        stream.Position = 5;
        stream.ReadExactly(buffer.AsSpan(0, 100));
        Assert.True(buffer.AsSpan(0, 100).SequenceEqual(whole.AsSpan(5, 100)));
        stream.Position = whole.Length;
        Assert.Equal(0, stream.Read(buffer));
    }

    [Fact]
    public void A_dst_track_is_a_dsdiff_file_of_dst_frames()
    {
        var sizes = new[] { 3000, 2500, 4101, 2049, 6000 };
        var dst = new SacdTestImage.Area(true, 2, [new("One", 9), new("Two", 6)], Dst: true, DstSizes: sizes);
        var path = WriteImage(dst);
        var area = SacdImage.Read(path).Areas[0];
        Assert.True(area.Dst);
        var global = 0;
        for (var t = 0; t < area.Tracks.Count; t++)
        {
            using var stream = new SacdTrackStream(File.OpenRead(path), area, area.Tracks[t]);
            Assert.Null(stream.KnownLength); // not before every frame is found
            var dff = ReadAll(stream);
            Assert.Equal(dff.Length, stream.KnownLength);
            var chunks = Chunks(dff, 16, dff.Length);
            Assert.Equal("DST ", chunks[^1].Id);
            var p = chunks[^1].DataAt;
            Assert.Equal("FRTE", Encoding.ASCII.GetString(dff, p, 4));
            Assert.Equal((uint)area.Tracks[t].Frames, BinaryPrimitives.ReadUInt32BigEndian(dff.AsSpan(p + 12)));
            Assert.Equal(75, BinaryPrimitives.ReadUInt16BigEndian(dff.AsSpan(p + 16)));
            p += 18;
            for (var f = 0; f < area.Tracks[t].Frames; f++, global++)
            {
                Assert.Equal("DSTF", Encoding.ASCII.GetString(dff, p, 4));
                var size = (int)BinaryPrimitives.ReadUInt64BigEndian(dff.AsSpan(p + 4));
                Assert.Equal(sizes[global % sizes.Length], size);
                Assert.True(dff.AsSpan(p + 12, size).SequenceEqual(SacdTestImage.FrameBytes(0, t, f, size)), $"track {t + 1}, frame {f}");
                p += 12 + size + (size & 1);
            }
            Assert.Equal(dff.Length, p);
        }
    }

    private static string? Ffprobe(string file, string entries)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-show_entries", entries, "-of", "default=nw=1", file])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // no ffprobe here
    }

    [Fact]
    public void Ffmpeg_reads_the_tracks_dsd_and_dst()
    {
        var path = WriteImage(Stereo, Surround);
        var sacd = SacdImage.Read(path);
        var dir = TestEnvironment.TempDir();
        var stereo = Path.Combine(dir, "stereo.dff");
        using (var s = new SacdTrackStream(File.OpenRead(path), sacd.Areas[0], sacd.Areas[0].Tracks[0]))
            File.WriteAllBytes(stereo, ReadAll(s));
        var info = Ffprobe(stereo, "stream=codec_name,channels,sample_rate:format=duration");
        if (info is null) return; // ffprobe isn't installed: the container is checked byte by byte above
        Assert.Contains("codec_name=dsd_msbf", info);
        Assert.Contains("channels=2", info);
        Assert.Contains("sample_rate=352800", info);

        var surround = Path.Combine(dir, "surround.dff");
        using (var s = new SacdTrackStream(File.OpenRead(path), sacd.Areas[1], sacd.Areas[1].Tracks[0]))
            File.WriteAllBytes(surround, ReadAll(s));
        Assert.Contains("channels=6", Ffprobe(surround, "stream=channels,channel_layout"));
        Assert.Contains("channel_layout=5.1", Ffprobe(surround, "stream=channels,channel_layout"));

        var dstPath = WriteImage(new SacdTestImage.Area(true, 2, [new("One", 20)], Dst: true, DstSizes: [3000, 2500]));
        var dstArea = SacdImage.Read(dstPath).Areas[0];
        var dst = Path.Combine(dir, "dst.dff");
        using (var s = new SacdTrackStream(File.OpenRead(dstPath), dstArea, dstArea.Tracks[0]))
            File.WriteAllBytes(dst, ReadAll(s));
        // The frames are made up (no DST encoder exists to make real ones): FFmpeg takes the
        // container, its frame count and duration; decoding them isn't tried.
        var dstInfo = Ffprobe(dst, "stream=codec_name,channels,duration");
        Assert.Contains("codec_name=dst", dstInfo);
        Assert.Contains("channels=2", dstInfo);
        Assert.Contains("duration=0.26", dstInfo); // 20 frames of 1/75 s
    }

    [Fact]
    public void Urls_and_the_edl_of_an_area()
    {
        var url = SacdProtocol.Url("/media/My Discs/a,b;c.iso", SacdAreaKind.Multichannel, 3);
        Assert.StartsWith("sacd://mch/3?", url);
        Assert.Equal(("/media/My Discs/a,b;c.iso", SacdAreaKind.Multichannel, 3), SacdProtocol.Parse(url));
        Assert.Null(SacdProtocol.Parse("sacd://xx/1?/a.iso"));
        Assert.Null(SacdProtocol.Parse("http://example.com"));

        var sacd = SacdImage.Read(WriteImage(Stereo));
        var edl = SacdProtocol.EdlFor(sacd, sacd.Areas[0]);
        Assert.StartsWith("edl://", edl);
        Assert.Equal(3, edl.Split(";%").Length);
        Assert.Contains(",title=%27%Wanna Be Startin' Somethin'", edl);
        Assert.Contains(",length=0.533,", edl);
    }
}
