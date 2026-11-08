# MediaSorter

Windows tool that sorts photo and video archives by capture date, detects broken files and rebuilds
interrupted / carved MP4 and MOV recordings.

```
Gallery/
  2008/
    Photos/
      03 March/
      05 May/
    Videos/
      03 March/
Broken/
  Photos/
  Videos/
Recovered/
```

## Features

- **Real format detection** by magic bytes (JPEG, PNG, GIF, WebP, HEIC, AVIF, TIFF/RAW, MP4, MOV, 3GP, AVI, MKV, WMV, MPEG-PS/TS, FLV).
  Mismatched extensions are fixed on copy.
- **Date resolution**, first match wins: EXIF / QuickTime / MP4 metadata, then file name patterns
  (`IMG_20170211_155100`, `VID-20170211-WA0001`, `11_February_2017_15.51`, Turkish month names), then last-modified time.
- **Configurable layout** with tokens: `{Year}` `{Month}` `{MonthNum}` `{MonthName}` `{Day}` `{Kind}` `{Source}` `{Format}`.
  `{Source}` expands to `Taken` or `Modified` depending on whether a capture date was found.
- **Health checks**: truncated JPEG/PNG, ISO-BMFF atoms that run past end of file, missing `moov`; optional ffprobe deep verify.
- **Duplicate detection** (size, then SHA-256) against both the run and what is already in the target.
- **Copy by default**, move optional. Every run writes a journal to `.mediasorter/` and can be undone.
- **Video recovery** for H.264 MP4/MOV files without a `moov` atom:
  - walks length-prefixed NAL units through `mdat`, resyncing over interleaved audio chunks,
  - infers slice-header syntax (frame_num / POC width, entropy coder, deblocking control) from the first GOP,
    rejecting candidates that break frame_num continuity or yield an illegal CABAC initial offset,
  - brute-forces profile / transform 8x8 / init QP / resolution using ffmpeg as a decode oracle,
  - decodes interleaved raw AAC by wrapping each chunk in Matroska and letting libavcodec walk the frames,
  - estimates frame rate from keyframe interval and audio length, then muxes a playable MP4.
    Orientation lived in the lost `moov`, so portrait clips need `--rotate 90` (or the Rotate option in the app).

  Verified on a Samsung 4K30 recording and an iPhone 1080p30 recording, both carved without `moov`.

  HEVC and legacy codecs (Sorenson, MPEG-4 Part 2) are reported but not rebuilt.

## Requirements

- .NET 8 SDK (build) / runtime (run)
- ffmpeg + ffprobe for deep verify and recovery: `winget install Gyan.FFmpeg`.
  Looked up next to the exe, on `PATH` and in the WinGet package folder.

## Build

```
dotnet build -c Release
dotnet publish src/MediaSorter.App -c Release -r win-x64 --self-contained false -o publish
```

## CLI

```
mediasorter inspect <file>...
mediasorter plan    --source D:\Phone --source E:\Old --target F:\Photos [--template "{Source}/{Year}/{Kind}/{Month}"]
mediasorter run     --source D:\Phone --target F:\Photos [--move]
mediasorter undo    --target F:\Photos
mediasorter recover broken.mp4 [--out fixed.mp4] [--fps 30] [--size 1920x1080] [--rotate 90]
```
