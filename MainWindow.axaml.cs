using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MediaFileAnalyzer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MediaFileInfo> _mediaFiles = new();
        private string _currentScanPath = string.Empty;
        private Avalonia.Controls.DataGrid? _filesDataGrid;
    private FfmpegConsoleWindow? _ffmpegConsoleWindow;
    private CancellationTokenSource? _operationCts;
    private Process? _currentFfmpegProcess;
    private readonly object _ffmpegProcessLock = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _filesDataGrid = this.FindControl<Avalonia.Controls.DataGrid>("FilesDataGrid");
            if (_filesDataGrid != null)
                _filesDataGrid.ItemsSource = _mediaFiles;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void EnsureFfmpegConsoleWindow()
    {
        if (_ffmpegConsoleWindow != null)
        {
            return;
        }

        _ffmpegConsoleWindow = new FfmpegConsoleWindow();
        _ffmpegConsoleWindow.Closed += (_, _) => _ffmpegConsoleWindow = null;
    }

    private void AppendFfmpegLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Ignore known harmless warning caused by embedded artwork pixel formats.
        if (line.Contains("deprecated pixel format used, make sure you did set range correctly", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            EnsureFfmpegConsoleWindow();
            _ffmpegConsoleWindow?.AppendLog(line);
        });
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        if (stopButton != null)
        {
            stopButton.IsEnabled = false;
            stopButton.Content = "Stopping...";
        }

        _operationCts?.Cancel();

        lock (_ffmpegProcessLock)
        {
            try
            {
                if (_currentFfmpegProcess != null && !_currentFfmpegProcess.HasExited)
                {
                    _currentFfmpegProcess.Kill(entireProcessTree: true);
                    AppendFfmpegLog("FFmpeg process killed by user.");
                }
            }
            catch (Exception ex)
            {
                AppendFfmpegLog($"Stop warning: {ex.Message}");
            }
        }
    }

    private void QuitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        Close();
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not null)
        {
            try
            {
                var folderPathTextBox = FolderPathTextBox ?? this.FindControl<TextBox>("FolderPathTextBox");
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Select a folder to scan for audio files"
                });

                var selectedFolder = folders.FirstOrDefault();
                if (selectedFolder != null)
                {
                    var selectedPath = selectedFolder.TryGetLocalPath();
                    
                    // Fallback: if TryGetLocalPath() returns null/empty, try other methods
                    if (string.IsNullOrWhiteSpace(selectedPath) && selectedFolder.Path != null)
                    {
                        selectedPath = selectedFolder.Path.LocalPath;
                    }
                    
                    // Second fallback: try FileSystemInfo.FullPath via reflection
                    if (string.IsNullOrWhiteSpace(selectedPath))
                    {
                        try
                        {
                            var fileSystemInfoProp = selectedFolder.GetType().GetProperty("FileSystemInfo");
                            if (fileSystemInfoProp != null)
                            {
                                var fileSystemInfo = fileSystemInfoProp.GetValue(selectedFolder) as System.IO.FileSystemInfo;
                                if (fileSystemInfo != null)
                                {
                                    selectedPath = fileSystemInfo.FullName;
                                }
                            }
                        }
                        catch
                        {
                            // FileSystemInfo not available
                        }
                    }
                    
                    if (!string.IsNullOrWhiteSpace(selectedPath))
                    {
                        _currentScanPath = selectedPath;
                        if (folderPathTextBox != null)
                        {
                            folderPathTextBox.Text = selectedPath;
                        }
                        
                        if (statusText != null)
                        {
                            statusText.Text = $"Folder selected: {selectedPath}";
                        }
                    }
                    else
                    {
                        if (statusText != null)
                        {
                            statusText.Text = "Could not resolve folder path. Please try typing the path manually.";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the actual exception for debugging
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                if (statusText != null)
                {
                    statusText.Text = $"File browser error: {ex.Message}";
                }
                System.Diagnostics.Debug.WriteLine($"BrowseButton_Click exception: {ex}");
            }
        }
    }

    private async void ScanButton_Click(object? sender, RoutedEventArgs e)
    {
        var folderPathTextBox = FolderPathTextBox ?? this.FindControl<TextBox>("FolderPathTextBox");
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var totalFilesText = TotalFilesText ?? this.FindControl<TextBlock>("TotalFilesText");
        var imageCountText = ImageCountText ?? this.FindControl<TextBlock>("ImageCountText");
        var videoCountText = VideoCountText ?? this.FindControl<TextBlock>("VideoCountText");
        var totalSizeText = TotalSizeText ?? this.FindControl<TextBlock>("TotalSizeText");
        var convertButton = ConvertButton ?? this.FindControl<Button>("ConvertButton");
        var renameButton = RenameButton ?? this.FindControl<Button>("RenameButton");

        string folderPath = folderPathTextBox?.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            if (statusText != null)
            {
                statusText.Text = "Please enter a valid folder path.";
            }
            return;
        }

        _currentScanPath = folderPath;
        _mediaFiles.Clear();
        if (totalFilesText != null) totalFilesText.Text = "0";
        if (imageCountText != null) imageCountText.Text = "0";
        if (videoCountText != null) videoCountText.Text = "0";
        if (totalSizeText != null) totalSizeText.Text = "0 MB";
        if (statusText != null) statusText.Text = "Scanning...";

        try
        {
            var scannedFiles = await Task.Run(() => ScanFolder(folderPath));

            foreach (var mediaInfo in scannedFiles)
            {
                _mediaFiles.Add(mediaInfo);
            }

                // Force the DataGrid to refresh its rows
                if (_filesDataGrid != null)
                {
                    _filesDataGrid.ItemsSource = null;
                    _filesDataGrid.ItemsSource = _mediaFiles;
                }

            int totalFiles = _mediaFiles.Count;
            int mp3Count = _mediaFiles.Count(f => f.Format.Equals("mp3", StringComparison.OrdinalIgnoreCase));
            int flacCount = _mediaFiles.Count(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase));
            long totalSize = _mediaFiles.Sum(f => f.FileSizeBytes);

            if (totalFilesText != null) totalFilesText.Text = totalFiles.ToString();
            if (imageCountText != null) imageCountText.Text = mp3Count.ToString();
            if (videoCountText != null) videoCountText.Text = flacCount.ToString();
            if (totalSizeText != null) totalSizeText.Text = FormatFileSize(totalSize);
            if (statusText != null) statusText.Text = $"Scan complete. Found {totalFiles} audio file(s). FLAC files: {flacCount}";
            if (convertButton != null) convertButton.IsVisible = flacCount > 0;
            if (renameButton != null) renameButton.IsVisible = totalFiles > 0;
        }
        catch (Exception ex)
        {
            if (statusText != null)
            {
                statusText.Text = $"Error scanning folder: {ex.Message}";
            }
        }
    }

    private List<MediaFileInfo> ScanFolder(string folderPath)
    {
        var results = new List<MediaFileInfo>();

        var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".flac"
        };

        var directoriesToScan = new Stack<string>();
        directoriesToScan.Push(folderPath);

        while (directoriesToScan.Count > 0)
        {
            var currentDirectory = directoriesToScan.Pop();

            string[] filesInDirectory;
            try
            {
                filesInDirectory = Directory.GetFiles(currentDirectory, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in filesInDirectory)
            {
                var extension = Path.GetExtension(file);
                if (!audioExtensions.Contains(extension))
                {
                    continue;
                }

                try
                {
                    var fileInfo = new FileInfo(file);
                    var mediaInfo = new MediaFileInfo
                    {
                        FileName = fileInfo.Name,
                        FilePath = file,
                        FileType = "Audio",
                        FileSizeBytes = fileInfo.Length,
                        FileSize = FormatFileSize(fileInfo.Length),
                        Format = extension.TrimStart('.').ToLowerInvariant()
                    };

                    results.Add(mediaInfo);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible files.
                }
                catch (IOException)
                {
                    // Skip files that disappear or are temporarily locked.
                }
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                directoriesToScan.Push(subDirectory);
            }
        }

        return results;
    }

    private async void RenameButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var renameButton = RenameButton ?? this.FindControl<Button>("RenameButton");
        var convertButton = ConvertButton ?? this.FindControl<Button>("ConvertButton");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");

        var audioFiles = _mediaFiles.ToList();
        if (audioFiles.Count == 0)
        {
            if (statusText != null) statusText.Text = "No audio files found to rename.";
            return;
        }

        if (statusText != null) statusText.Text = $"Ready to rename {audioFiles.Count} file(s). Starting rename operation...";

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (renameButton != null) renameButton.IsVisible = false;
        if (convertButton != null) convertButton.IsVisible = false;
        if (stopButton != null)
        {
            stopButton.IsVisible = true;
            stopButton.IsEnabled = true;
            stopButton.Content = "Stop";
        }
        if (progressCountText != null) progressCountText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        var progress = new Progress<ConversionProgress>(report =>
        {
            if (conversionProgressBar != null) conversionProgressBar.Value = report.PercentComplete;
            if (progressStatusText != null) progressStatusText.Text = $"Renaming: ({report.FilesCompleted}/{report.TotalFiles})";
            if (progressCountText != null) progressCountText.Text = $"{report.FilesCompleted}/{report.TotalFiles}";
            if (currentFileText != null) currentFileText.Text = report.CurrentFile;
        });

        try
        {
            await Task.Run(() => RenameAllFiles(audioFiles, _currentScanPath, progress, _operationCts.Token));

            if (conversionProgressBar != null) conversionProgressBar.Value = 100;
            if (progressStatusText != null) progressStatusText.Text = "Renaming complete!";
            if (statusText != null) statusText.Text = "Renaming complete. Re-scan folder to see updated file structure.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (renameButton != null) renameButton.IsVisible = true;
            if (convertButton != null) convertButton.IsVisible = _mediaFiles.Any(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase));
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "Renaming stopped by user.";
            if (progressStatusText != null) progressStatusText.Text = "Renaming stopped.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (renameButton != null) renameButton.IsVisible = true;
            if (convertButton != null) convertButton.IsVisible = _mediaFiles.Any(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase));
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error during renaming: {ex.Message}";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (renameButton != null) renameButton.IsVisible = true;
            if (convertButton != null) convertButton.IsVisible = _mediaFiles.Any(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase));
            if (stopButton != null) stopButton.IsVisible = false;
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void RenameAllFiles(List<MediaFileInfo> audioFiles, string basePath, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        int filesCompleted = 0;
        int totalFiles = audioFiles.Count;

        foreach (var audioFile in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileNamer.RenameToPickardStyle(audioFile.FilePath, basePath);
                filesCompleted++;
                int percentComplete = (filesCompleted * 100) / totalFiles;

                progress.Report(new ConversionProgress
                {
                    CurrentFile = audioFile.FileName,
                    FilesCompleted = filesCompleted,
                    TotalFiles = totalFiles,
                    PercentComplete = percentComplete
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                    if (statusText != null)
                    {
                        statusText.Text = $"Error renaming {audioFile.FileName}: {ex.Message}";
                    }
                });
            }
        }
    }

    private async void ConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var convertButton = ConvertButton ?? this.FindControl<Button>("ConvertButton");
        var renameButton = RenameButton ?? this.FindControl<Button>("RenameButton");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");

        var flacFiles = _mediaFiles.Where(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase)).ToList();
        if (flacFiles.Count == 0)
        {
            if (statusText != null) statusText.Text = "No FLAC files found to convert.";
            return;
        }

        if (!FFmpegHelper.IsFFmpegInstalled())
        {
            if (statusText != null) statusText.Text = "FFmpeg is not installed or not in PATH.";
            return;
        }

        if (statusText != null) statusText.Text = $"Starting conversion of {flacFiles.Count} FLAC file(s)...";

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (convertButton != null) convertButton.IsVisible = false;
        if (renameButton != null) renameButton.IsVisible = false;
        if (stopButton != null)
        {
            stopButton.IsVisible = true;
            stopButton.IsEnabled = true;
            stopButton.Content = "Stop";
        }
        if (progressCountText != null) progressCountText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        EnsureFfmpegConsoleWindow();
        _ffmpegConsoleWindow?.ClearLogs();
        if (_ffmpegConsoleWindow != null && !_ffmpegConsoleWindow.IsVisible)
        {
            _ffmpegConsoleWindow.Show(this);
        }

        _ffmpegConsoleWindow?.Activate();
        AppendFfmpegLog($"=== Conversion started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        AppendFfmpegLog($"Files to convert: {flacFiles.Count}");

        var progress = new Progress<ConversionProgress>(report =>
        {
            if (conversionProgressBar != null) conversionProgressBar.Value = report.PercentComplete;
            if (progressStatusText != null) progressStatusText.Text = $"Converting: ({report.FilesCompleted}/{report.TotalFiles})";
            if (progressCountText != null) progressCountText.Text = $"{report.FilesCompleted}/{report.TotalFiles}";
            if (currentFileText != null) currentFileText.Text = report.CurrentFile;
        });

        try
        {
            await Task.Run(() => ConvertFlacToMp3(flacFiles, progress, _operationCts.Token));

            if (conversionProgressBar != null) conversionProgressBar.Value = 100;
            if (progressStatusText != null) progressStatusText.Text = "Conversion complete!";
            if (statusText != null) statusText.Text = "Conversion complete. Original FLAC files preserved. Re-scan folder to see new MP3 files.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsVisible = true;
            if (renameButton != null) renameButton.IsVisible = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            AppendFfmpegLog("Conversion canceled by user.");
            if (statusText != null) statusText.Text = "Conversion stopped by user.";
            if (progressStatusText != null) progressStatusText.Text = "Conversion stopped.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsVisible = true;
            if (renameButton != null) renameButton.IsVisible = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error during conversion: {ex.Message}";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsVisible = true;
            if (renameButton != null) renameButton.IsVisible = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void ConvertFlacToMp3(List<MediaFileInfo> flacFiles, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        int filesCompleted = 0;
        int totalFiles = flacFiles.Count;

        foreach (var flacFile in flacFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outputPath = Path.ChangeExtension(flacFile.FilePath, ".mp3");
                AppendFfmpegLog($"\n--- [{filesCompleted + 1}/{totalFiles}] {flacFile.FileName} ---");
                AppendFfmpegLog($"Input : {flacFile.FilePath}");
                AppendFfmpegLog($"Output: {outputPath}");

                var arguments = $"-hide_banner -nostats -loglevel warning -y -i \"{flacFile.FilePath}\" -vn -b:a 320k \"{outputPath}\"";
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo) ?? throw new Exception("Unable to start ffmpeg process.");

                lock (_ffmpegProcessLock)
                {
                    _currentFfmpegProcess = process;
                }

                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    {
                        AppendFfmpegLog(eventArgs.Data);
                    }
                };

                process.BeginErrorReadLine();
                process.WaitForExit();

                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    throw new Exception($"FFmpeg failed with exit code {process.ExitCode}");
                }

                lock (_ffmpegProcessLock)
                {
                    _currentFfmpegProcess = null;
                }

                AppendFfmpegLog("ffmpeg finished successfully.");
                if (File.Exists(outputPath))
                {
                    string baseDirectory = string.IsNullOrWhiteSpace(_currentScanPath)
                        ? Path.GetDirectoryName(flacFile.FilePath) ?? Directory.GetCurrentDirectory()
                        : _currentScanPath;
                    FileNamer.RenameToPickardStyle(outputPath, baseDirectory);
                    AppendFfmpegLog("Renamed/moved converted MP3 with Picard naming.");
                }

                filesCompleted++;
                int percentComplete = (filesCompleted * 100) / totalFiles;
                progress.Report(new ConversionProgress
                {
                    CurrentFile = flacFile.FileName,
                    FilesCompleted = filesCompleted,
                    TotalFiles = totalFiles,
                    PercentComplete = percentComplete
                });
            }
            catch (OperationCanceledException)
            {
                lock (_ffmpegProcessLock)
                {
                    _currentFfmpegProcess = null;
                }

                throw;
            }
            catch (Exception ex)
            {
                lock (_ffmpegProcessLock)
                {
                    _currentFfmpegProcess = null;
                }

                AppendFfmpegLog($"ERROR: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                    if (statusText != null)
                    {
                        statusText.Text = $"Error converting {flacFile.FileName}: {ex.Message}";
                    }
                });
            }
        }

        AppendFfmpegLog($"\n=== Conversion ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    private static bool TryDeleteWithRetry(string filePath)
    {
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return true;
                }

                File.Delete(filePath);
                return true;
            }
            catch
            {
                Thread.Sleep(400);
            }
        }

        return false;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

public class MediaFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string FileSize { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Dimensions { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

public class ConversionProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public int FilesCompleted { get; set; }
    public int TotalFiles { get; set; }
    public int PercentComplete { get; set; }
}

public static class FFmpegHelper
{
    public static bool IsFFmpegInstalled()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
