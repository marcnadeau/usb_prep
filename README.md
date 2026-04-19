# Audio File Analyzer & FLAC Converter

A cross-platform desktop application built with Avalonia and .NET 10 to prepare audio files for USB playback in a car head unit.

## Goal

Prepare a clean, car-friendly USB music library by:
- Scanning a source music folder and a target USB drive
- Comparing source and target by track metadata to identify missing files
- Transferring selected or missing tracks to the USB drive
- Converting non-MP3/M4A formats (e.g. FLAC) to MP3 at 320kbps before copying
- Grouping albums in consistent folders and renaming files in a predictable order
- Cleaning up empty folders after moves

## Features

- **Dual Folder Selection**: Separate **Source** and **Target** browse buttons
- **Audio File Detection**: Detects MP3, M4A, FLAC, WAV, AAC, OGG, WMA, AIFF, and ALAC files
- **Metadata Display**: Grid shows Artist, Album, Title, Format, Size, and Compare status for each file
- **Source/Target Comparison**:
  - Scans both folders and matches tracks by `artist | album | title` metadata key
  - Falls back to filename-based key when tags are incomplete
  - Classifies each source file as **Missing**, **On target**, or **Unknown tags**
  - Shows summary counts in the status bar after compare
- **Smart Transfer**:
  - Transfer **selected rows** (multi-select in the file grid) or all **Missing** files after a compare
  - If no selection and no compare results, offers to transfer all scanned files
  - MP3 and M4A files are copied directly (no re-encoding)
  - Other formats trigger a single **Convert before copy?** prompt per transfer operation
  - **Per-file conflict prompt** when a file already exists at the destination: Overwrite / Skip / Cancel transfer
  - Preserves source folder structure under the target root
  - Progress bar, file counter, and Stop/cancel support
  - Transfer summary: Copied / Converted / Skipped / Failed counts
- **FLAC to MP3 Conversion** (standalone):
  - Converts FLAC files to MP3 at 320kbps
  - Uses FFmpeg for reliable conversion
  - Shows real-time FFmpeg logs in a dedicated console window
  - Stop button with immediate process kill
  - Optionally deletes original FLAC files after successful conversion
- **Picard-Style Organization**:
  - Uses metadata to rename and move files into album folders
  - Folder: `AlbumArtist/Album` (fallbacks: artist → "Unknown Artist"; album → "Singles")
  - File: `TrackNumber - Title` (multi-disc: `DiscNumber-TrackNumber - Title`)
  - Detects compilations and groups them correctly
  - Cleans up now-empty source directories
- **Statistics Dashboard**: Total files, MP3 count, FLAC count, total size
- **Recursive Scanning**: Scans all subfolders within the selected directory

## Supported Formats

### Scan & Compare (source and target)
MP3, M4A, FLAC, WAV, AAC, OGG, WMA, AIFF, ALAC

### Direct Copy (no conversion)
MP3 (.mp3), M4A (.m4a)

### Converted to MP3 on Transfer
FLAC, WAV, AAC, OGG, WMA, AIFF, ALAC → MP3 at 320kbps

## Prerequisites

- .NET 10.0 SDK or later
- Windows or Linux
- **FFmpeg** — required only when converting non-MP3/M4A files
  - Download from: https://ffmpeg.org/download.html
  - Make sure `ffmpeg` is in your system PATH

## Building & Running

```bash
dotnet restore
dotnet build
dotnet run
```

## How to Use

### Basic scan & convert (existing workflow)
1. Click **Source Browse...** and select your music folder
2. Click **Scan**
3. If FLAC files are found, click **Convert FLAC to MP3 (320kbps)**
4. Use **Rename All Files (Picard)** to organize files into `Artist/Album/Track - Title` structure

### Transfer to USB (new workflow)
1. Click **Source Browse...** → select your music library folder → **Scan**
2. Click **Target Browse...** → select the USB drive (or any destination folder)
3. Click **Compare** to see which source tracks are missing on the target
4. The **Compare** column in the grid updates to *Missing*, *On target*, or *Unknown tags*
5. Optionally select specific rows (Ctrl+click / Shift+click) to transfer a subset
6. Click **Transfer Selected/Missing to Target**
7. If unsupported formats are included, choose **Yes** (convert to MP3) or **No** (skip them)
8. Resolve any file conflicts per-file: **Yes** overwrite, **No** skip, **Cancel** stop transfer
9. A summary dialog reports Copied / Converted / Skipped / Failed counts

## Installation Notes

### FFmpeg on Linux
```bash
sudo apt install ffmpeg        # Debian/Ubuntu
sudo dnf install ffmpeg        # Fedora
sudo pacman -S ffmpeg          # Arch
```

### FFmpeg on Windows
```powershell
winget install FFmpeg
# or
choco install ffmpeg
```

## Project Structure

- `MainWindow.axaml` / `MainWindow.axaml.cs` — UI and all scan, compare, transfer, convert, rename workflows
- `FileNamer.cs` — Picard-style naming, compilation detection, empty-folder cleanup
- `FfmpegConsoleWindow.axaml` / `FfmpegConsoleWindow.axaml.cs` — Real-time FFmpeg log viewer
- `MediaAnalyzer.cs` — Audio file analysis
- `App.axaml` / `App.axaml.cs` — Application entry point
- `MediaFileAnalyzer.csproj` — Project configuration (net10.0, Avalonia)

## Conversion Details

- **Quality**: 320kbps (maximum MP3 quality)
- **Codec**: MP3 (MPEG-1 Layer 3)
- **Metadata**: Preserved from source file by FFmpeg
- **Processing**: Sequential (one file at a time for stability and cancellability)

## Car USB Tips

- Format USB drives as `FAT32` (or `exFAT` if your car supports it)
- Keep folder depth reasonable and filenames simple
- Prefer ID3v2.3-compatible tags for older head units
- Run **Compare** first to only transfer what's actually missing

## Troubleshooting

### FFmpeg not found
- Ensure FFmpeg is installed and `ffmpeg -version` works in a terminal
- The app will warn you before attempting any conversion

### Compare shows everything as "Unknown tags"
- Your source files may have missing or incomplete ID3/metadata tags
- Use a tag editor (e.g. MusicBrainz Picard, beets) to add Artist, Album, and Title tags
- Files with unknown tags still appear in the grid and can be selected for transfer manually

### Transfer fails or shows errors
- Check write permissions on the target folder/USB drive
- Ensure sufficient free space on the target
- Review the FFmpeg console window for conversion-specific errors

## License

This project is open source and available for personal and commercial use.
