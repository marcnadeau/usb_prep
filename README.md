# Audio File Analyzer & FLAC to MP3 Converter

A Windows desktop application built with WPF and .NET 8 to prepare audio files for USB playback in a car head unit.

## Goal

Prepare a clean, car-friendly USB music library by:
- Converting FLAC tracks to MP3 (320kbps)
- Grouping albums in consistent folders
- Renaming files in a predictable track order format
- Cleaning up empty folders after moves

## Features

- **Folder Browser**: Easy-to-use folder selection dialog
- **Audio File Detection**: Automatically detects MP3, M4A, and FLAC files
- **File Analysis**: Extracts file details including:
  - File name and path
  - File type (Audio)
  - File size
  - File format
- **FLAC to MP3 Conversion**: 
  - Converts FLAC files to MP3 with the highest quality (320kbps bitrate)
  - Uses FFmpeg for reliable conversion
   - Shows real-time FFmpeg logs in a dedicated console window
   - Supports cancellation with a Stop button
   - Optionally deletes original FLAC files after successful conversion
- **Picard-Style Organization**:
   - Uses metadata to rename and move files into album folders
   - Naming logic:
      - Folder: `AlbumArtist/Album` (fallbacks: artist, then "Unknown Artist"; album fallback: "Singles")
      - File: `TrackNumber - Title`
      - Multi-disc albums: `DiscNumber-TrackNumber - Title`
   - Keeps compilations and featured tracks together under one album artist folder
   - Cleans up now-empty source directories
- **Statistics Dashboard**: Real-time display of:
  - Total files found
  - Number of MP3 files
  - Number of FLAC files
  - Total size of all audio files
- **Recursive Scanning**: Scans all subfolders within the selected directory
- **Modern UI**: Clean, professional interface with color-coded statistics

## Supported Formats

### Input Formats
- MP3 (.mp3)
- M4A (.m4a) - iTunes compatible
- FLAC (.flac) - Lossless audio format (for conversion)

### Output Format (Conversion)
- MP3 (.mp3) at 320kbps (high quality, broad car stereo compatibility)

## Prerequisites

- .NET 8.0 SDK or later
- Windows operating system
- **FFmpeg** (required for FLAC conversion)
  - Download from: https://ffmpeg.org/download.html
  - Make sure FFmpeg is in your system PATH

## Building the Application

1. Open a terminal in the project directory
2. Restore dependencies:
   ```
   dotnet restore
   ```
3. Build the project:
   ```
   dotnet build
   ```
4. Run the application:
   ```
   dotnet run
   ```

## How to Use

1. Launch the application
2. Click the **Browse...** button to select a folder containing audio files
3. Click the **Scan** button to analyze the audio files
4. The application will:
   - Display all found audio files (MP3, M4A, FLAC)
   - Show count of each file type
   - Display total size of all files
5. If FLAC files are found:
   - Click **"Convert FLAC to MP3 (320kbps)"**
   - Follow progress in the progress bar and FFmpeg log window
   - Converted files are organized using Picard-style album structure
6. Use **Rename** to apply the same organization to already compatible files

## Installation Notes

### Installing FFmpeg (Required)

**Windows:**
- Option 1: Download from https://ffmpeg.org/download.html
- Option 2: Use Chocolatey: `choco install ffmpeg`
- Option 3: Use Windows Package Manager: `winget install FFmpeg`

Make sure FFmpeg is added to your system PATH so the application can find it.

## Project Structure

- `MainWindow.xaml` - UI definition
- `MainWindow.xaml.cs` - Scan, convert, rename, progress, cancellation, and deletion workflow
- `FileNamer.cs` - Picard-style naming and empty-folder cleanup
- `FfmpegConsoleWindow.xaml` / `FfmpegConsoleWindow.xaml.cs` - Real-time FFmpeg log viewer
- `MediaAnalyzer.cs` - Audio file analysis functionality
- `App.xaml` / `App.xaml.cs` - Application entry point
- `MediaFileAnalyzer.csproj` - Project configuration

## Conversion Details

- **Quality**: 320kbps (maximum MP3 quality)
- **Codec**: MP3 (MPEG-1 Layer 3)
- **Audio Processing**: Automatic metadata preservation
- **Parallel Processing**: Not used (sequential conversion to ensure stability)

## Car USB Tips

- Format USB drives as `FAT32` (or `exFAT` if your car supports it)
- Keep folder depth short and filenames simple
- Prefer ID3v2.3-compatible tags when possible for older head units
- Test with a small sample set before copying your full library

## Troubleshooting

### FFmpeg not found
- Make sure FFmpeg is installed
- Check that FFmpeg is in your system PATH
- Restart the application after installing FFmpeg
- A browser window will open to FFmpeg download page if not detected

### Conversion fails
- Verify FFmpeg is installed and accessible from command line: `ffmpeg -version`
- Check that you have write permissions in the target folder
- Ensure sufficient disk space for MP3 files

## License

This project is open source and available for personal and commercial use.
