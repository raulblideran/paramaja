#!/usr/bin/env bash
# Generates small, legally-clean test clips used by the integration tests.
set -euo pipefail
OUT="$(cd "$(dirname "$0")" && pwd)/media"
mkdir -p "$OUT"
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }

# HEVC Main10 HDR10 (PQ / BT.2020) with stereo audio
[ -f "$OUT/hdr10_hevc.mkv" ] || ff -f lavfi -i testsrc2=size=1280x720:rate=24:duration=3 \
  -f lavfi -i sine=frequency=440:duration=3 -ac 2 \
  -vf "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format=yuv420p10le" \
  -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le \
  -x265-params "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400" \
  -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc \
  -c:a aac "$OUT/hdr10_hevc.mkv"

# H.264 SDR baseline
[ -f "$OUT/sdr_h264.mp4" ] || ff -f lavfi -i testsrc=size=640x360:rate=25:duration=3 \
  -f lavfi -i sine=frequency=880:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac "$OUT/sdr_h264.mp4"

# Interlaced 1080i (top field first)
[ -f "$OUT/interlaced_1080i.ts" ] || ff -f lavfi -i testsrc=size=1920x1080:rate=25:duration=2 \
  -vf "tinterlace=interleave_top,fieldorder=tff" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -flags +ilme+ildct \
  -x264-params tff=1 "$OUT/interlaced_1080i.ts"

# 5.1 and 7.1 audio
[ -f "$OUT/audio_51.mkv" ] || ff -f lavfi -i testsrc=size=320x240:rate=25:duration=2 \
  -f lavfi -i "sine=frequency=300:duration=2" -filter_complex "[1:a]aformat=channel_layouts=5.1[a]" \
  -map 0:v -map "[a]" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a ac3 "$OUT/audio_51.mkv"
[ -f "$OUT/audio_71.mkv" ] || ff -f lavfi -i testsrc=size=320x240:rate=25:duration=2 \
  -f lavfi -i "sine=frequency=300:duration=2" -filter_complex "[1:a]aformat=channel_layouts=7.1[a]" \
  -map 0:v -map "[a]" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a flac "$OUT/audio_71.mkv"

# Subtitles (external srt + ass) and a clip with an embedded subtitle track
cat > "$OUT/subs.srt" <<'EOF'
1
00:00:00,000 --> 00:00:02,500
Hello from Paramaja
EOF
cat > "$OUT/subs.ass" <<'EOF'
[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,28,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:02.50,Default,,0,0,0,,{\b1}ASS subtitle
EOF
[ -f "$OUT/embedded_subs.mkv" ] || ff -i "$OUT/sdr_h264.mp4" -i "$OUT/subs.srt" -map 0 -map 1 -c copy -c:s srt "$OUT/embedded_subs.mkv"

# 4K HEVC (short) for hwdec checks
[ -f "$OUT/uhd_hevc.mkv" ] || ff -f lavfi -i testsrc2=size=3840x2160:rate=24:duration=1 \
  -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le -x265-params log-level=error "$OUT/uhd_hevc.mkv"

# Uncompressed AVI (8-bit 4:2:2 and 10-bit v210, the usual capture formats) and a DVD-style VOB
[ -f "$OUT/uncompressed_uyvy.avi" ] || ff -f lavfi -i testsrc=size=720x576:rate=25:duration=1 \
  -f lavfi -i sine=frequency=500:duration=1 -c:v rawvideo -pix_fmt uyvy422 -c:a pcm_s16le "$OUT/uncompressed_uyvy.avi"
[ -f "$OUT/uncompressed_v210.avi" ] || ff -f lavfi -i testsrc=size=720x576:rate=25:duration=1 \
  -c:v v210 "$OUT/uncompressed_v210.avi"
[ -f "$OUT/dvd_mpeg2.vob" ] || ff -f lavfi -i testsrc=size=720x576:rate=25:duration=2 \
  -f lavfi -i sine=frequency=600:duration=2 -target pal-dvd -f vob "$OUT/dvd_mpeg2.vob"

echo "Fixtures in $OUT:"; ls -1 "$OUT"
