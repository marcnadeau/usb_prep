using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MediaFileAnalyzer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a"
    };

    private static readonly HashSet<string> ScannableAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".wma", ".aiff", ".alac"
    };

    private readonly ObservableCollection<MediaFileInfo> _mediaFiles = new();
        private string _currentScanPath = string.Empty;
        private string _targetPath = string.Empty;
        private bool _hasComparisonResults;
        private Avalonia.Controls.DataGrid? _filesDataGrid;
    private FfmpegConsoleWindow? _ffmpegConsoleWindow;
    private CancellationTokenSource? _operationCts;
    private Process? _currentFfmpegProcess;
    private readonly object _ffmpegProcessLock = new();
    private HashSet<string> _detectedCompilationAlbums = new(StringComparer.OrdinalIgnoreCase);

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

    private async void FormatTargetAsFat32Button_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            var statusTextMissing = StatusText ?? this.FindControl<TextBlock>("StatusText");
            if (statusTextMissing != null)
                statusTextMissing.Text = "Select a valid target path before showing formatting instructions.";
            return;
        }

        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        string driveHint = Path.GetPathRoot(_targetPath) ?? _targetPath;
        string instructions = BuildFat32Instructions(driveHint);

        await ShowInformationDialogAsync("FAT32 formatting instructions", instructions);

        if (statusText != null)
            statusText.Text = "Formatting instructions displayed. Verify the disk before running commands.";
    }

    private static string BuildFat32Instructions(string driveHint)
    {
        if (OperatingSystem.IsWindows())
        {
            return
                "Windows (DiskPart)\n\n" +
                "Warning: these commands erase the selected disk.\n\n" +
                "1. Open Command Prompt as Administrator.\n" +
                "2. Type: diskpart\n" +
                "3. Type: list disk\n" +
                "4. Identify your USB disk number carefully.\n" +
                "5. Type: select disk N\n" +
                "6. Type: clean\n" +
                "7. Type: create partition primary\n" +
                "8. Type: format fs=fat32 quick\n" +
                "9. Type: assign\n" +
                "10. Type: exit\n\n" +
                $"Selected target in app: {driveHint}";
        }

        if (OperatingSystem.IsLinux())
        {
            return
                "Linux\n\n" +
                "Warning: these commands erase the selected disk.\n\n" +
                "1. Open a terminal.\n" +
                "2. Identify your USB disk: lsblk\n" +
                "3. Unmount mounted partitions: sudo umount /dev/sdX*\n" +
                "4. Create a new partition table and partition:\n" +
                "   sudo parted /dev/sdX --script mklabel msdos mkpart primary fat32 1MiB 100%\n" +
                "5. Format the new partition in FAT32:\n" +
                "   sudo mkfs.vfat -F 32 -n USB /dev/sdX1\n\n" +
                "Replace sdX with the correct disk (example: sdb).\n" +
                $"Selected target in app: {driveHint}";
        }

        return
            "FAT32 formatting instructions\n\n" +
            "This app shows manual steps only.\n" +
            "Use DiskPart on Windows or parted + mkfs.vfat on Linux.\n\n" +
            $"Selected target in app: {driveHint}";
    }

    private async Task ShowInformationDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 660,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var messageText = new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                messageText,
                okButton
            }
        };

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
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

                var selectedPath = await PickFolderWithLinuxFallbackAsync(
                    topLevel,
                    "Select a folder to scan for audio files");

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

    private async void BrowseTargetButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not null)
        {
            try
            {
                var targetFolderPathTextBox = TargetFolderPathTextBox ?? this.FindControl<TextBox>("TargetFolderPathTextBox");
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                var compareButton = CompareButton ?? this.FindControl<Button>("CompareButton");

                var selectedPath = await PickFolderWithLinuxFallbackAsync(
                    topLevel,
                    "Select a target folder (USB drive destination)");

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    _targetPath = selectedPath;
                    _hasComparisonResults = false;
                    if (targetFolderPathTextBox != null)
                        targetFolderPathTextBox.Text = selectedPath;
                    if (statusText != null)
                        statusText.Text = $"Target folder selected: {selectedPath}";
                    UpdateActionAvailability();
                }
            }
            catch (Exception ex)
            {
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                if (statusText != null)
                    statusText.Text = $"File browser error: {ex.Message}";
            }
        }
    }

    private async Task<string?> PickFolderWithLinuxFallbackAsync(TopLevel topLevel, string title)
    {
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = title
            });

            var selectedFolder = folders.FirstOrDefault();
            return selectedFolder is null ? null : ResolveFolderPath(selectedFolder);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenFolderPickerAsync failed: {ex}");

            // On Linux, D-Bus/portal failures can happen on some desktop setups.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var externalPath = await PickFolderWithExternalLinuxDialogAsync(title);
                if (!string.IsNullOrWhiteSpace(externalPath))
                {
                    return externalPath;
                }
            }

            throw;
        }
    }

    private static string? ResolveFolderPath(IStorageFolder selectedFolder)
    {
        var selectedPath = selectedFolder.TryGetLocalPath();

        if (string.IsNullOrWhiteSpace(selectedPath) && selectedFolder.Path != null)
        {
            selectedPath = selectedFolder.Path.LocalPath;
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            try
            {
                var fileSystemInfoProp = selectedFolder.GetType().GetProperty("FileSystemInfo");
                if (fileSystemInfoProp != null)
                {
                    var fileSystemInfo = fileSystemInfoProp.GetValue(selectedFolder) as FileSystemInfo;
                    if (fileSystemInfo != null)
                    {
                        selectedPath = fileSystemInfo.FullName;
                    }
                }
            }
            catch
            {
                // FileSystemInfo not available for this storage provider.
            }
        }

        return selectedPath;
    }

    private static async Task<string?> PickFolderWithExternalLinuxDialogAsync(string title)
    {
        var dialogs = new List<(string FileName, string[] Args)>
        {
            ("zenity", new[] { "--file-selection", "--directory", "--title", title }),
            ("qarma", new[] { "--file-selection", "--directory", "--title", title }),
            ("kdialog", new[] { "--getexistingdirectory", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), title })
        };

        foreach (var dialog in dialogs)
        {
            var selectedPath = await TryRunFolderDialogAsync(dialog.FileName, dialog.Args);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                return selectedPath;
            }
        }

        return null;
    }

    private static async Task<string?> TryRunFolderDialogAsync(string fileName, string[] args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return null;
            }

            var selectedPath = output.Trim();
            return Directory.Exists(selectedPath) ? selectedPath : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateActionAvailability()
    {
        bool hasSource = !string.IsNullOrWhiteSpace(_currentScanPath) && Directory.Exists(_currentScanPath);
        bool hasTarget = !string.IsNullOrWhiteSpace(_targetPath) && Directory.Exists(_targetPath);
        bool hasFiles = _mediaFiles.Count > 0;

        var compareButton = CompareButton ?? this.FindControl<Button>("CompareButton");
        var transferButton = TransferButton ?? this.FindControl<Button>("TransferButton");

        if (compareButton != null)
            compareButton.IsEnabled = hasSource && hasTarget && hasFiles;
        if (transferButton != null)
            transferButton.IsVisible = hasTarget && hasFiles;
    }

    private async void CompareButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please select a valid target folder.";
            return;
        }

        if (_mediaFiles.Count == 0)
        {
            if (statusText != null) statusText.Text = "Scan a source folder first.";
            return;
        }

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Comparing source and target metadata...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        try
        {
            var result = await Task.Run(() => CompareSourceAndTarget(_operationCts.Token));

            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _hasComparisonResults = true;
            if (statusText != null)
                statusText.Text = $"Compare complete. Missing on target: {result.MissingCount}, already on target: {result.AlreadyOnTargetCount}, unknown metadata: {result.UnknownCount}.";
            var transferButton = TransferButton ?? this.FindControl<Button>("TransferButton");
            if (transferButton != null) transferButton.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            if (statusText != null) statusText.Text = "Comparison stopped by user.";
        }
        catch (Exception ex)
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            if (statusText != null) statusText.Text = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private CompareResult CompareSourceAndTarget(CancellationToken cancellationToken)
    {
        var targetKeys = BuildMetadataKeySet(_targetPath, cancellationToken);
        int total = _mediaFiles.Count;
        int processed = 0;
        int missing = 0;
        int alreadyOnTarget = 0;
        int unknown = 0;

        foreach (var mediaFile in _mediaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            var key = BuildTrackKey(mediaFile.Artist, mediaFile.Album, mediaFile.Title, mediaFile.FileName);
            string status;

            if (string.IsNullOrWhiteSpace(key))
            {
                unknown++;
                status = "Unknown tags";
            }
            else if (targetKeys.Contains(key))
            {
                alreadyOnTarget++;
                status = "On target";
            }
            else
            {
                missing++;
                status = "Missing";
            }

            int percent = (processed * 100) / Math.Max(1, total);
            string capturedFile = mediaFile.FileName;
            string capturedStatus = status;
            Dispatcher.UIThread.Post(() =>
            {
                mediaFile.CompareStatus = capturedStatus;
                var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
                var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
                var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
                if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                if (progressCountText != null) progressCountText.Text = $"{processed}/{total}";
                if (currentFileText != null) currentFileText.Text = capturedFile;
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_filesDataGrid != null)
            {
                _filesDataGrid.ItemsSource = null;
                _filesDataGrid.ItemsSource = _mediaFiles;
            }
        });

        return new CompareResult(missing, alreadyOnTarget, unknown);
    }

    private HashSet<string> BuildMetadataKeySet(string basePath, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file);
            if (!ScannableAudioExtensions.Contains(extension))
                continue;

            var metadata = ReadTrackMetadata(file);
            var key = BuildTrackKey(metadata.Artist, metadata.Album, metadata.Title, Path.GetFileName(file));
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        return keys;
    }

    private async void TransferButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please choose a valid target folder before transferring.";
            return;
        }

        var selectedFiles = GetSelectedMediaFiles();
        var candidates = selectedFiles.Count > 0
            ? selectedFiles
            : _mediaFiles.Where(m => string.Equals(m.CompareStatus, "Missing", StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidates.Count == 0)
        {
            if (selectedFiles.Count == 0 && !_hasComparisonResults)
                candidates = _mediaFiles.ToList();

            if (candidates.Count == 0)
            {
                if (statusText != null) statusText.Text = "Select files to transfer, or run compare so missing files can be transferred.";
                return;
            }
        }

        bool hasUnsupported = candidates.Any(c => !SupportedAudioExtensions.Contains($".{c.Format}"));
        bool convertUnsupported = false;

        if (hasUnsupported && FFmpegHelper.IsFFmpegInstalled())
            convertUnsupported = true;

        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Transferring files...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        if (convertUnsupported)
        {
            EnsureFfmpegConsoleWindow();
            if (_ffmpegConsoleWindow != null && !_ffmpegConsoleWindow.IsVisible)
                _ffmpegConsoleWindow.Show(this);
            _ffmpegConsoleWindow?.Activate();
            AppendFfmpegLog($"=== Transfer conversion started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }

        try
        {
            var summary = await Task.Run(() => TransferFiles(candidates, convertUnsupported, _operationCts.Token));

            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            if (statusText != null)
                statusText.Text = $"Transfer complete. Copied: {summary.Copied}, Converted: {summary.Converted}, Skipped: {summary.Skipped}, Failed: {summary.Failed}.";
        }
        catch (OperationCanceledException)
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            if (statusText != null) statusText.Text = "Transfer stopped by user.";
        }
        catch (Exception ex)
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            if (statusText != null) statusText.Text = $"Transfer failed: {ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            if (convertUnsupported)
                AppendFfmpegLog($"=== Transfer conversion ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
    }

    private List<MediaFileInfo> GetSelectedMediaFiles()
    {
        var result = new List<MediaFileInfo>();
        if (_filesDataGrid?.SelectedItems is System.Collections.IList selected)
        {
            foreach (var item in selected)
            {
                if (item is MediaFileInfo media)
                    result.Add(media);
            }
        }
        return result;
    }

    private TransferSummary TransferFiles(List<MediaFileInfo> candidates, bool convertUnsupported, CancellationToken cancellationToken)
    {
        int copied = 0, converted = 0, skipped = 0, failed = 0;
        int total = candidates.Count;

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var media = candidates[i];

            try
            {
                string extension = Path.GetExtension(media.FilePath).ToLowerInvariant();
                bool directCopy = SupportedAudioExtensions.Contains(extension);

                if (!directCopy && !convertUnsupported)
                {
                    skipped++;
                    continue;
                }

                string relativePath = Path.GetRelativePath(_currentScanPath, media.FilePath);
                string destinationRelative = directCopy
                    ? relativePath
                    : Path.ChangeExtension(relativePath, ".mp3");
                string destinationPath = Path.Combine(_targetPath, destinationRelative);

                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                if (directCopy)
                {
                    File.Copy(media.FilePath, destinationPath, overwrite: true);
                    copied++;
                }
                else
                {
                    ConvertFileToMp3(media.FilePath, destinationPath, cancellationToken);
                    converted++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failed++;
            }
            finally
            {
                int percent = ((i + 1) * 100) / Math.Max(1, total);
                int idx = i;
                Dispatcher.UIThread.Post(() =>
                {
                    var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
                    var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
                    var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
                    if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                    if (progressCountText != null) progressCountText.Text = $"{idx + 1}/{total}";
                    if (currentFileText != null) currentFileText.Text = candidates[idx].FileName;
                });
            }
        }

        return new TransferSummary(copied, converted, skipped, failed);
    }

    private void ConvertFileToMp3(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        AppendFfmpegLog($"Converting for transfer: {sourcePath} -> {destinationPath}");
        var arguments = $"-y -i \"{sourcePath}\" -b:a 320k -q:v 0 \"{destinationPath}\"";
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
            _currentFfmpegProcess = process;

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                AppendFfmpegLog(eventArgs.Data);
        };

        process.BeginErrorReadLine();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_ffmpegProcessLock)
            _currentFfmpegProcess = null;

        if (process.ExitCode != 0)
            throw new Exception($"FFmpeg failed with exit code {process.ExitCode}");
    }

    private (string Artist, string Album, string Title) ReadTrackMetadata(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            string artist = tagFile.Tag.FirstPerformer ?? string.Empty;
            string album = tagFile.Tag.Album ?? string.Empty;
            string title = tagFile.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath);
            return (artist.Trim(), album.Trim(), title.Trim());
        }
        catch
        {
            return (string.Empty, string.Empty, Path.GetFileNameWithoutExtension(filePath));
        }
    }

    private string BuildTrackKey(string artist, string album, string title, string fileName)
    {
        string normalizedArtist = NormalizeKeyPart(artist);
        string normalizedAlbum = NormalizeKeyPart(album);
        string normalizedTitle = NormalizeKeyPart(title);

        if (!string.IsNullOrWhiteSpace(normalizedArtist) &&
            !string.IsNullOrWhiteSpace(normalizedAlbum) &&
            !string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return $"{normalizedArtist}|{normalizedAlbum}|{normalizedTitle}";
        }

        string fallback = NormalizeKeyPart(Path.GetFileNameWithoutExtension(fileName));
        return string.IsNullOrWhiteSpace(fallback) ? string.Empty : $"fallback|{fallback}";
    }

    private static string NormalizeKeyPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', value.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
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
        _hasComparisonResults = false;
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
            UpdateActionAvailability();

            // Detect compilation albums in the background (reads tags for grouping).
            var compilationCheckBox = CompilationCheckBox ?? this.FindControl<CheckBox>("CompilationCheckBox");
            var namingHintText = NamingHintText ?? this.FindControl<TextBlock>("NamingHintText");
            if (totalFiles > 0)
            {
                var allPaths = _mediaFiles.Select(f => f.FilePath).ToList();
                _detectedCompilationAlbums = await Task.Run(() => FileNamer.DetectCompilationAlbums(allPaths));
                bool hasCompilations = _detectedCompilationAlbums.Count > 0;
                if (compilationCheckBox != null)
                {
                    compilationCheckBox.IsVisible = hasCompilations;
                    compilationCheckBox.IsChecked = hasCompilations;
                }
                if (namingHintText != null)
                    namingHintText.IsVisible = !hasCompilations;
                if (hasCompilations && statusText != null)
                    statusText.Text += $" — {_detectedCompilationAlbums.Count} multi-artist album(s) detected.";
            }
            else
            {
                _detectedCompilationAlbums.Clear();
                if (compilationCheckBox != null) compilationCheckBox.IsVisible = false;
                if (namingHintText != null) namingHintText.IsVisible = true;
            }
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
                if (!ScannableAudioExtensions.Contains(extension))
                {
                    continue;
                }

                try
                {
                    var fileInfo = new FileInfo(file);
                    var metadata = ReadTrackMetadata(file);
                    var mediaInfo = new MediaFileInfo
                    {
                        FileName = fileInfo.Name,
                        FilePath = file,
                        FileType = "Audio",
                        FileSizeBytes = fileInfo.Length,
                        FileSize = FormatFileSize(fileInfo.Length),
                        Format = extension.TrimStart('.').ToLowerInvariant(),
                        Artist = metadata.Artist,
                        Album = metadata.Album,
                        Title = metadata.Title,
                        CompareStatus = "Not compared"
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

        // Respect the compilation checkbox: pass the detected set only when the box is checked.
        var compilationCheckBox = CompilationCheckBox ?? this.FindControl<CheckBox>("CompilationCheckBox");
        IReadOnlySet<string>? compilationAlbums = (compilationCheckBox?.IsChecked == true)
            ? _detectedCompilationAlbums
            : null;

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
            await Task.Run(() => RenameAllFiles(audioFiles, _currentScanPath, compilationAlbums, progress, _operationCts.Token));

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

    private void RenameAllFiles(List<MediaFileInfo> audioFiles, string basePath, IReadOnlySet<string>? compilationAlbums, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        int filesCompleted = 0;
        int totalFiles = audioFiles.Count;

        foreach (var audioFile in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileNamer.RenameToPicardStyle(audioFile.FilePath, basePath, compilationAlbums);
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

        // Capture compilation preference on the UI thread before handing off to background.
        var compilationCheckBoxForConvert = CompilationCheckBox ?? this.FindControl<CheckBox>("CompilationCheckBox");
        IReadOnlySet<string>? compilationAlbumsForConvert = (compilationCheckBoxForConvert?.IsChecked == true)
            ? _detectedCompilationAlbums
            : null;

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
            await Task.Run(() => ConvertFlacToMp3(flacFiles, compilationAlbumsForConvert, progress, _operationCts.Token));

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

    private void ConvertFlacToMp3(List<MediaFileInfo> flacFiles, IReadOnlySet<string>? compilationAlbums, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
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
                    FileNamer.RenameToPicardStyle(outputPath, baseDirectory, compilationAlbums);
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
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CompareStatus { get; set; } = "Not compared";
}

public record CompareResult(int MissingCount, int AlreadyOnTargetCount, int UnknownCount);
public record TransferSummary(int Copied, int Converted, int Skipped, int Failed);

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
