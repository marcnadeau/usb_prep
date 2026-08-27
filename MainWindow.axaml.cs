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
    private static readonly HashSet<string> ScannableAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".wma", ".aiff", ".alac"
    };

    private readonly ObservableCollection<MediaFileInfo> _mediaFiles = new();
    private string _currentScanPath = string.Empty;
    private string _targetPath = string.Empty;
    private bool _hasComparisonResults;
    private Avalonia.Controls.DataGrid? _filesDataGrid;
    private ConversionSettings _conversionSettings = ConversionSettingsStore.Load();
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

            RefreshConversionUi();
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

    private void RefreshConversionUi()
    {
        var convertButton = ConvertButton ?? this.FindControl<Button>("ConvertButton");
        if (convertButton != null)
        {
            convertButton.Content = $"Convert FLAC to {GetOutputFormatDisplayName()} ({GetBitrateLabel()})";
        }
    }

    private string GetOutputFormatDisplayName()
        => AudioConversionCatalog.GetOutputFormat(_conversionSettings.OutputFormat).Label.Split(' ')[0];

    private string GetCodecDisplayName()
        => AudioConversionCatalog.GetCodecOptions(_conversionSettings.OutputFormat, fraunhoferAvailable: true)
            .FirstOrDefault(codec => codec.Id.Equals(_conversionSettings.OutputCodec, StringComparison.OrdinalIgnoreCase))?.Label
            ?? AudioConversionCatalog.GetCodecOptions(_conversionSettings.OutputFormat, fraunhoferAvailable: true).First().Label;

    private string GetBitrateLabel() => $"{Math.Max(1, _conversionSettings.BitrateKbps)}kbps";

    private string GetKeepFormatsSummary()
        => string.Join(", ", _conversionSettings.KeepExtensions
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .Select(AudioConversionCatalog.GetFormatLabel));

    private bool IsKeptExtension(string extension)
        => _conversionSettings.KeepExtensions.Contains(AudioConversionCatalog.NormalizeExtension(extension));

    private string ResolveOutputCodecId()
    {
        var availableCodecs = AudioConversionCatalog.GetCodecOptions(_conversionSettings.OutputFormat, fraunhoferAvailable: FFmpegHelper.IsFFmpegInstalled());
        return availableCodecs.FirstOrDefault(codec => codec.Id.Equals(_conversionSettings.OutputCodec, StringComparison.OrdinalIgnoreCase))?.Id
            ?? availableCodecs.First().Id;
    }

    private string BuildTranscodeArguments(string sourcePath, string destinationPath)
    {
        string codecId = ResolveOutputCodecId();
        int bitrate = Math.Max(1, _conversionSettings.BitrateKbps);
        string outputFormat = AudioConversionCatalog.NormalizeExtension(_conversionSettings.OutputFormat);
        string extraArgs = outputFormat.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            ? " -movflags +faststart"
            : string.Empty;

        return $"-hide_banner -nostats -loglevel warning -y -i \"{sourcePath}\" -vn -map_metadata 0 -c:a {codecId} -b:a {bitrate}k{extraArgs} \"{destinationPath}\"";
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

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_conversionSettings);
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            _conversionSettings = ConversionSettingsStore.Load();
            RefreshConversionUi();
        }
    }

    

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var messageText = new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var noButton = new Button { Content = "No", Width = 100, Margin = new Thickness(6,0) };
        var yesButton = new Button { Content = "Yes", Width = 100, Margin = new Thickness(6,0) };

        var tcs = new TaskCompletionSource<bool>();

        noButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        yesButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

        buttonPanel.Children.Add(noButton);
        buttonPanel.Children.Add(yesButton);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                messageText,
                buttonPanel
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

        return await tcs.Task;
    }

    private async Task ReorganizeTargetAsync(string targetPath)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Organizing target drive by Album...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int Current, int Total, string FileName)>(report =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (conversionProgressBar != null && report.Total > 0)
                    {
                        conversionProgressBar.Value = (report.Current * 100) / report.Total;
                    }
                    if (progressCountText != null) progressCountText.Text = $"{report.Current}/{report.Total}";
                    if (currentFileText != null) currentFileText.Text = report.FileName;
                });
            });

            var result = await Task.Run(() => FileOrganizer.OrganizeFilesAsync(targetPath, dryRun: false, progress), _operationCts.Token);

            // Update UI with results
            Dispatcher.UIThread.Post(() =>
            {
                if (conversionProgressBar != null) conversionProgressBar.Value = 100;
                if (progressCountText != null) progressCountText.Text = $"Moved: {result.Moved}, Skipped: {result.Skipped}, Errors: {result.Errors}";
                if (progressStatusText != null) progressStatusText.Text = result.Errors > 0 ? "Reorganize complete with some errors." : "Target reorganize complete.";
            });

            if (statusText != null) statusText.Text = $"Reorganize complete: {result.Moved} files moved, {result.Skipped} skipped, {result.Errors} errors.";
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "Target reorganize canceled by user.";
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error reorganizing target: {ex.Message}";
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            UpdateActionAvailability();
        }
    }

    private void RewritePhysicalOrderOnTarget(
        string targetPath,
        ref int processed,
        int total,
        TextBlock? progressCountText,
        ProgressBar? conversionProgressBar,
        TextBlock? currentFileText,
        CancellationToken cancellationToken)
    {
        string tempRoot = Path.Combine(targetPath, $".usb_prep_rewrite_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var files = Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories)
                .Where(f => ScannableAudioExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            var byDirectory = files
                .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in byDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var orderedFiles = group
                    .Select(path =>
                    {
                        var (disc, track) = ReadTrackPositionForOrdering(path, Path.GetFileName(path));
                        return new
                        {
                            Path = path,
                            Disc = disc,
                            Track = track,
                            Name = Path.GetFileName(path)
                        };
                    })
                    .OrderBy(x => x.Disc)
                    .ThenBy(x => x.Track)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var moved = new List<(string TempPath, string DestinationPath, string DisplayName)>();
                foreach (var item in orderedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string tempPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{Path.GetExtension(item.Path)}");
                    File.Move(item.Path, tempPath);
                    moved.Add((tempPath, item.Path, item.Name));
                }

                foreach (var item in moved)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(item.TempPath, item.DestinationPath, overwrite: true);

                    processed++;
                    int capturedProcessed = processed;
                    string display = item.DisplayName;
                    int percent = (capturedProcessed * 100) / Math.Max(1, total);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                        if (progressCountText != null) progressCountText.Text = $"{capturedProcessed}/{total}";
                        if (currentFileText != null) currentFileText.Text = display;
                    });
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private async void ReorganizeButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please select a valid target folder before reorganizing.";
            return;
        }

        var confirm = await ShowConfirmDialogAsync("Reorganize target drive?", "This will rename and reorganize audio files on the selected target into Album/Track layout. Continue?");
        if (confirm)
        {
            await ReorganizeTargetAsync(_targetPath);
        }
    }

    private sealed record TargetRepairPlan(
        int TotalAudioFiles,
        int FilesNeedingMove,
        int FoldersNeedingRewrite,
        int FilesNeedingRewrite,
        int FilesWithUnknownTags,
        List<string> PreviewLines);

    private enum RepairTargetMode
    {
        FolderOrderOnly,
        SongsInFolders,
        FullRepair
    }

    private TargetRepairPlan BuildTargetRepairPlan(string targetPath, RepairTargetMode mode, CancellationToken cancellationToken)
    {
        var audioFiles = Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories)
            .Where(f => ScannableAudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previewLines = new List<string>();
        int filesNeedingMove = 0;
        int foldersNeedingRewrite = 0;
        int filesNeedingRewrite = 0;
        int filesWithUnknownTags = 0;

        var compilationAlbums = FileNamer.DetectCompilationAlbums(audioFiles);
        var inspectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mode is RepairTargetMode.FolderOrderOnly or RepairTargetMode.FullRepair)
        {
            var rootDirectories = Directory.GetDirectories(targetPath, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var desiredRootDirectories = rootDirectories
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!rootDirectories.SequenceEqual(desiredRootDirectories, StringComparer.OrdinalIgnoreCase))
            {
                foldersNeedingRewrite += rootDirectories.Count;
                if (previewLines.Count < 40)
                {
                    previewLines.Add("REWRITE top-level folder order on target");
                }
            }
        }

        foreach (var file in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mode == RepairTargetMode.FullRepair)
            {
                string expectedPath = FileNamer.GetPicardPath(file, targetPath, compilationAlbums);
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                {
                    filesNeedingMove++;
                    if (previewLines.Count < 40)
                    {
                        previewLines.Add($"MOVE  {Path.GetRelativePath(targetPath, file)} -> {Path.GetRelativePath(targetPath, expectedPath)}");
                    }
                }
            }

            var metadata = ReadTrackMetadata(file);
            if (string.IsNullOrWhiteSpace(metadata.Artist) && string.IsNullOrWhiteSpace(metadata.Album) && string.IsNullOrWhiteSpace(metadata.Title))
            {
                filesWithUnknownTags++;
            }

            string currentDirectory = Path.GetDirectoryName(file) ?? targetPath;
            if (!inspectedDirectories.Add(currentDirectory))
            {
                continue;
            }

            var siblingAudioFiles = Directory.GetFiles(currentDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => ScannableAudioExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (siblingAudioFiles.Count <= 1)
            {
                continue;
            }

            var physicalOrder = siblingAudioFiles.ToList();
            var desiredOrder = siblingAudioFiles
                .Select(path =>
                {
                    var (disc, track) = ReadTrackPositionForOrdering(path, Path.GetFileName(path));
                    return new { Path = path, Disc = disc, Track = track, Name = Path.GetFileName(path) };
                })
                .OrderBy(x => x.Disc)
                .ThenBy(x => x.Track)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Path)
                .ToList();

            if ((mode is RepairTargetMode.SongsInFolders or RepairTargetMode.FullRepair) &&
                !desiredOrder.SequenceEqual(physicalOrder, StringComparer.OrdinalIgnoreCase))
            {
                filesNeedingRewrite += siblingAudioFiles.Count;
                if (previewLines.Count < 40)
                {
                    previewLines.Add($"REWRITE order in {Path.GetRelativePath(targetPath, currentDirectory)}");
                }
            }
        }

        return new TargetRepairPlan(audioFiles.Count, filesNeedingMove, foldersNeedingRewrite, filesNeedingRewrite, filesWithUnknownTags, previewLines);
    }

    private async Task<bool> ShowTargetRepairPreviewDialogAsync(TargetRepairPlan plan, RepairTargetMode mode)
    {
        string previewText = string.Join(Environment.NewLine, plan.PreviewLines);
        if (plan.PreviewLines.Count == 0)
        {
            previewText = "No preview lines available.";
        }
        else if (plan.TotalAudioFiles > plan.PreviewLines.Count)
        {
            previewText += Environment.NewLine + Environment.NewLine + "Additional files may also be adjusted.";
        }

        var dialog = new Window
        {
            Title = "Repair USB preview",
            Width = 860,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var intro = new TextBlock
        {
            Text = $"Mode: {GetRepairModeLabel(mode)}. Found {plan.TotalAudioFiles} audio file(s). Proposed fixes: {plan.FilesNeedingMove} relocate, {plan.FoldersNeedingRewrite} folder-order rewrites, {plan.FilesNeedingRewrite} song-order rewrites, {plan.FilesWithUnknownTags} with weak or missing tags.",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var details = new TextBox
        {
            Text = previewText,
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 380,
            FontFamily = new Avalonia.Media.FontFamily("monospace")
        };

        var note = new TextBlock
        {
            Text = "This repairs the selected USB target in place. It reorganizes files and rewrites their physical order without emptying the drive first.",
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.DimGray
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancelButton = new Button { Content = "Cancel", Width = 120, Margin = new Thickness(6, 0) };
        var repairButton = new Button { Content = "Repair USB", Width = 140, Margin = new Thickness(6, 0) };

        var tcs = new TaskCompletionSource<bool>();
        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        repairButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(repairButton);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                intro,
                details,
                note,
                buttonPanel
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

        return await tcs.Task;
    }

    private static string GetRepairModeLabel(RepairTargetMode mode)
        => mode switch
        {
            RepairTargetMode.FolderOrderOnly => "Fix folder order only",
            RepairTargetMode.SongsInFolders => "Fix song order inside folders",
            RepairTargetMode.FullRepair => "Full repair",
            _ => "Repair"
        };

    private async Task<RepairTargetMode?> ShowRepairModeDialogAsync()
    {
        var dialog = new Window
        {
            Title = "Repair USB mode",
            Width = 620,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var intro = new TextBlock
        {
            Text = "Choose how the USB drive should be repaired.",
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        var folderButton = new Button { Content = "Fix folder order only", HorizontalAlignment = HorizontalAlignment.Stretch };
        var songsButton = new Button { Content = "Fix song order inside folders", HorizontalAlignment = HorizontalAlignment.Stretch };
        var fullButton = new Button { Content = "Full repair", HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancelButton = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Width = 120, Margin = new Thickness(0, 12, 0, 0) };

        var tcs = new TaskCompletionSource<RepairTargetMode?>();
        folderButton.Click += (_, _) => { tcs.TrySetResult(RepairTargetMode.FolderOrderOnly); dialog.Close(); };
        songsButton.Click += (_, _) => { tcs.TrySetResult(RepairTargetMode.SongsInFolders); dialog.Close(); };
        fullButton.Click += (_, _) => { tcs.TrySetResult(RepairTargetMode.FullRepair); dialog.Close(); };
        cancelButton.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

        buttonPanel.Children.Add(folderButton);
        buttonPanel.Children.Add(songsButton);
        buttonPanel.Children.Add(fullButton);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                intro,
                buttonPanel,
                cancelButton
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

        return await tcs.Task;
    }

    private void RewriteFolderOrderOnTarget(
        string targetPath,
        ref int processed,
        int total,
        TextBlock? progressCountText,
        ProgressBar? conversionProgressBar,
        TextBlock? currentFileText,
        CancellationToken cancellationToken)
    {
        string tempRoot = Path.Combine(targetPath, $".usb_prep_folder_rewrite_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var directories = Directory.GetDirectories(targetPath, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var moved = new List<(string TempPath, string DestinationPath, string DisplayName)>();
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
                Directory.Move(directory, tempPath);
                moved.Add((tempPath, directory, Path.GetFileName(directory)));
            }

            foreach (var item in moved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(item.TempPath, item.DestinationPath);
                processed++;
                int capturedProcessed = processed;
                int percent = (capturedProcessed * 100) / Math.Max(1, total);
                Dispatcher.UIThread.Post(() =>
                {
                    if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                    if (progressCountText != null) progressCountText.Text = $"{capturedProcessed}/{total}";
                    if (currentFileText != null) currentFileText.Text = item.DisplayName;
                });
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    private async Task RepairTargetAsync(string targetPath, RepairTargetMode mode)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = $"{GetRepairModeLabel(mode)}...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        if (progressCountText != null) progressCountText.Text = string.Empty;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        try
        {
            int processed = 0;
            int total = 1;

            if (mode is RepairTargetMode.FolderOrderOnly or RepairTargetMode.FullRepair)
            {
                total = Math.Max(total, Directory.GetDirectories(targetPath, "*", SearchOption.TopDirectoryOnly).Length);
            }

            if (mode is RepairTargetMode.SongsInFolders or RepairTargetMode.FullRepair)
            {
                total = Math.Max(total, Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories)
                    .Count(f => ScannableAudioExtensions.Contains(Path.GetExtension(f))));
            }

            if (mode == RepairTargetMode.FolderOrderOnly)
            {
                await Task.Run(() => RewriteFolderOrderOnTarget(targetPath, ref processed, total, progressCountText, conversionProgressBar, currentFileText, _operationCts.Token), _operationCts.Token);
            }
            else if (mode == RepairTargetMode.SongsInFolders)
            {
                await Task.Run(() => RewritePhysicalOrderOnTarget(targetPath, ref processed, total, progressCountText, conversionProgressBar, currentFileText, _operationCts.Token), _operationCts.Token);
            }
            else
            {
                var result = await Task.Run(() => FileOrganizer.OrganizeFilesAsync(targetPath, dryRun: false, null), _operationCts.Token);
                processed += result.Moved + result.Skipped + result.Errors;
                await Task.Run(() => RewriteFolderOrderOnTarget(targetPath, ref processed, total, progressCountText, conversionProgressBar, currentFileText, _operationCts.Token), _operationCts.Token);
                await Task.Run(() => RewritePhysicalOrderOnTarget(targetPath, ref processed, total, progressCountText, conversionProgressBar, currentFileText, _operationCts.Token), _operationCts.Token);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (conversionProgressBar != null) conversionProgressBar.Value = 100;
                if (progressStatusText != null) progressStatusText.Text = $"{GetRepairModeLabel(mode)} complete.";
            });

            if (statusText != null) statusText.Text = $"{GetRepairModeLabel(mode)} complete.";
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "USB repair canceled by user.";
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error repairing USB: {ex.Message}";
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            UpdateActionAvailability();
        }
    }

    private async void RepairTargetButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please select a valid target folder before repairing the USB drive.";
            return;
        }

        var mode = await ShowRepairModeDialogAsync();
        if (mode == null)
        {
            if (statusText != null) statusText.Text = "USB repair canceled.";
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        TargetRepairPlan plan;
        try
        {
            plan = await Task.Run(() => BuildTargetRepairPlan(_targetPath, mode.Value, _operationCts.Token), _operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "USB inspection canceled.";
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error inspecting USB drive: {ex.Message}";
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }

        _operationCts?.Dispose();
        _operationCts = null;

        if (plan.FilesNeedingMove == 0 && plan.FoldersNeedingRewrite == 0 && plan.FilesNeedingRewrite == 0)
        {
            await ShowInfoDialogAsync("USB looks OK", "No obvious folder-layout or song-order issues were detected on the selected target.");
            return;
        }

        bool confirm = await ShowTargetRepairPreviewDialogAsync(plan, mode.Value);
        if (!confirm)
        {
            if (statusText != null) statusText.Text = "USB repair canceled.";
            return;
        }

        await RepairTargetAsync(_targetPath, mode.Value);
    }

    private async void CleanTargetDuplicatesButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please select a valid target folder before cleaning duplicates.";
            return;
        }

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Scanning target for duplicates...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        if (progressCountText != null) progressCountText.Text = string.Empty;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        List<string> filesToDelete;
        try
        {
            filesToDelete = await Task.Run(() => BuildTargetDuplicateDeleteList(_targetPath, _operationCts.Token), _operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "Duplicate scan canceled by user.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error scanning duplicates: {ex.Message}";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }

        if (progressBorder != null) progressBorder.IsVisible = false;
        if (stopButton != null) stopButton.IsVisible = false;

        if (filesToDelete.Count == 0)
        {
            if (statusText != null) statusText.Text = "No duplicates found on target.";
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }

        bool confirmed = await ShowDuplicateCleanupPreviewDialogAsync(filesToDelete);
        if (!confirmed)
        {
            if (statusText != null) statusText.Text = "Duplicate cleanup canceled.";
            _operationCts?.Dispose();
            _operationCts = null;
            return;
        }

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Deleting duplicate files from target...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;

        int deleted = 0;
        int failed = 0;

        try
        {
            await Task.Run(() =>
            {
                int total = filesToDelete.Count;
                for (int i = 0; i < total; i++)
                {
                    _operationCts?.Token.ThrowIfCancellationRequested();
                    string file = filesToDelete[i];

                    try
                    {
                        if (File.Exists(file))
                        {
                            string? sourceDirectory = Path.GetDirectoryName(file);
                            File.Delete(file);
                            deleted++;

                            if (!string.IsNullOrWhiteSpace(sourceDirectory))
                            {
                                // Keep target tidy by removing empty folders where possible.
                                CleanupDirectoryIfEmptyRecursive(sourceDirectory, _targetPath);
                            }
                        }
                    }
                    catch
                    {
                        failed++;
                    }

                    int index = i;
                    int percent = ((index + 1) * 100) / Math.Max(1, total);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                        if (progressCountText != null) progressCountText.Text = $"{index + 1}/{total}";
                        if (currentFileText != null) currentFileText.Text = Path.GetFileName(file);
                    });
                }
            }, _operationCts!.Token);

            if (statusText != null) statusText.Text = $"Duplicate cleanup complete. Deleted: {deleted}, Failed: {failed}.";
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = $"Duplicate cleanup stopped. Deleted: {deleted}, Failed: {failed}.";
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            UpdateActionAvailability();
        }
    }

    private List<string> BuildTargetDuplicateDeleteList(string basePath, CancellationToken cancellationToken)
    {
        var duplicateGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories)
            .Where(f => ScannableAudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = files[i];
            var metadata = ReadTrackMetadata(file);
            var key = BuildTrackKey(metadata.Artist, metadata.Album, metadata.Title, Path.GetFileName(file));
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!duplicateGroups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                duplicateGroups[key] = list;
            }

            list.Add(file);
        }

        var filesToDelete = new List<string>();
        foreach (var group in duplicateGroups.Values)
        {
            if (group.Count <= 1)
                continue;

            // Keep the shortest path (usually the cleanest/organized location), delete the others.
            var ordered = group
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            filesToDelete.AddRange(ordered.Skip(1));
        }

        return filesToDelete;
    }

    private async Task<bool> ShowDuplicateCleanupPreviewDialogAsync(List<string> filesToDelete)
    {
        const int previewLimit = 40;
        var previewEntries = filesToDelete.Take(previewLimit).Select(p => p).ToList();
        string previewText = string.Join(Environment.NewLine, previewEntries);
        if (filesToDelete.Count > previewLimit)
        {
            previewText += Environment.NewLine + Environment.NewLine + $"... and {filesToDelete.Count - previewLimit} more file(s).";
        }

        var dialog = new Window
        {
            Title = "Duplicate cleanup preview",
            Width = 840,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var intro = new TextBlock
        {
            Text = $"{filesToDelete.Count} duplicate file(s) found on target. The files listed below will be deleted. Continue?",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var previewBox = new TextBox
        {
            Text = previewText,
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 360,
            FontFamily = new Avalonia.Media.FontFamily("monospace")
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancelButton = new Button { Content = "Cancel", Width = 120, Margin = new Thickness(6, 0) };
        var deleteButton = new Button { Content = "Delete duplicates", Width = 160, Margin = new Thickness(6, 0) };

        var tcs = new TaskCompletionSource<bool>();
        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        deleteButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(deleteButton);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                intro,
                previewBox,
                buttonPanel
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

        return await tcs.Task;
    }

    private static void CleanupDirectoryIfEmptyRecursive(string startDirectory, string stopAtDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(stopAtDirectory))
            return;

        string current = Path.GetFullPath(startDirectory);
        string stopAt = Path.GetFullPath(stopAtDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (current.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current))
                break;

            bool hasFiles = Directory.EnumerateFiles(current).Any();
            bool hasDirectories = Directory.EnumerateDirectories(current).Any();
            if (hasFiles || hasDirectories)
                break;

            Directory.Delete(current, recursive: false);
            var parent = Directory.GetParent(current);
            if (parent == null)
                break;
            current = parent.FullName;
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
        var prepareUsbButton = PrepareUsbButton ?? this.FindControl<Button>("PrepareUsbButton");

        if (compareButton != null)
            compareButton.IsEnabled = hasSource && hasTarget && hasFiles;
        if (transferButton != null)
            transferButton.IsEnabled = hasTarget && hasFiles;
        if (prepareUsbButton != null)
            prepareUsbButton.IsEnabled = hasSource && hasTarget && hasFiles;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 580,
            Height = 220,
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

        var tcs = new TaskCompletionSource<bool>();
        okButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

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

        await tcs.Task;
    }

    private static bool TryLaunchPicard(string folderPath)
    {
        var launchCandidates = new[]
        {
            (FileName: "picard", Args: Array.Empty<string>()),
            (FileName: "musicbrainz-picard", Args: Array.Empty<string>()),
            (FileName: "flatpak", Args: new[] { "run", "org.musicbrainz.Picard" })
        };

        foreach (var candidate in launchCandidates)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = candidate.FileName,
                    UseShellExecute = false
                };

                foreach (var arg in candidate.Args)
                {
                    processInfo.ArgumentList.Add(arg);
                }

                processInfo.ArgumentList.Add(folderPath);

                var process = Process.Start(processInfo);
                if (process != null)
                {
                    return true;
                }
            }
            catch
            {
                // Try next candidate.
            }
        }

        return false;
    }

    private async Task<bool> PromptForPicardAndRescanAsync()
    {
        var wantsPicard = await ShowConfirmDialogAsync(
            "Review tags in Picard?",
            "Do you want to open MusicBrainz Picard now to review or correct tags before the transfer starts?");

        if (!wantsPicard)
        {
            return true;
        }

        if (!TryLaunchPicard(_currentScanPath))
        {
            await ShowInfoDialogAsync(
                "Picard not available",
                "MusicBrainz Picard could not be launched automatically. Install Picard or continue without it.");

            return await ShowConfirmDialogAsync(
                "Continue without Picard?",
                "Picard did not launch. Continue with compare and transfer anyway?");
        }

        await ShowInfoDialogAsync(
            "Picard launched",
            "Picard has been opened with your source folder. Make any tag fixes there, save them, then click OK here to rescan and continue.");

        await RescanCurrentSourceAsync();
        return true;
    }

    private async Task RescanCurrentSourceAsync()
    {
        var folderPathTextBox = FolderPathTextBox ?? this.FindControl<TextBox>("FolderPathTextBox");
        if (folderPathTextBox != null)
        {
            folderPathTextBox.Text = _currentScanPath;
        }

        await RunSourceScanAsync(_currentScanPath);
    }

    private async Task RunSourceScanAsync(string folderPath)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var totalFilesText = TotalFilesText ?? this.FindControl<TextBlock>("TotalFilesText");
        var imageCountText = ImageCountText ?? this.FindControl<TextBlock>("ImageCountText");
        var videoCountText = VideoCountText ?? this.FindControl<TextBlock>("VideoCountText");
        var totalSizeText = TotalSizeText ?? this.FindControl<TextBlock>("TotalSizeText");
        var convertButton = ConvertButton ?? this.FindControl<Button>("ConvertButton");

        _currentScanPath = folderPath;
        _hasComparisonResults = false;
        _mediaFiles.Clear();
        if (totalFilesText != null) totalFilesText.Text = "0";
        if (imageCountText != null) imageCountText.Text = "0";
        if (videoCountText != null) videoCountText.Text = "0";
        if (totalSizeText != null) totalSizeText.Text = "0 MB";
        if (statusText != null) statusText.Text = "Scanning...";

        var scannedFiles = await Task.Run(() => ScanFolder(folderPath));

        foreach (var mediaInfo in scannedFiles)
        {
            _mediaFiles.Add(mediaInfo);
        }

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
        if (convertButton != null) convertButton.IsEnabled = flacCount > 0;
        UpdateActionAvailability();

        var compilationCheckBox = CompilationCheckBox ?? this.FindControl<CheckBox>("CompilationCheckBox");
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
            if (hasCompilations && statusText != null)
                statusText.Text += $" — {_detectedCompilationAlbums.Count} multi-artist album(s) detected.";
        }
        else
        {
            _detectedCompilationAlbums.Clear();
            if (compilationCheckBox != null) compilationCheckBox.IsVisible = false;
        }
    }

    private async Task<CompareResult?> RunCompareWorkflowAsync()
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
        var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please select a valid target folder.";
            return null;
        }

        if (_mediaFiles.Count == 0)
        {
            if (statusText != null) statusText.Text = "Scan a source folder first.";
            return null;
        }

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Comparing source and target metadata...";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (progressCountText != null) progressCountText.Text = string.Empty;
        if (conversionProgressBar != null) conversionProgressBar.Value = 0;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        try
        {
            var result = await Task.Run(() => CompareSourceAndTarget(_operationCts.Token));
            _hasComparisonResults = true;
            if (statusText != null)
                statusText.Text = $"Compare complete. Missing on target: {result.MissingCount}, already on target: {result.AlreadyOnTargetCount}, unknown metadata: {result.UnknownCount}, duplicates on target: {result.DuplicateOnTargetCount}, duplicates in source: {result.DuplicateInSourceCount}.";
            var transferButton = TransferButton ?? this.FindControl<Button>("TransferButton");
            if (transferButton != null) transferButton.IsEnabled = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "Comparison stopped by user.";
            return null;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Comparison failed: {ex.Message}";
            return null;
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task TransferCandidatesAsync(List<MediaFileInfo> candidates, bool requireCompareConfirmation)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please choose a valid target folder before transferring.";
            return;
        }

        if (requireCompareConfirmation && !_hasComparisonResults)
        {
            var proceed = await ShowConfirmDialogAsync(
                "No compare results",
                "You haven't compared the source and target. Proceeding may copy duplicates or unnecessary files. Continue?");

            if (!proceed)
            {
                if (statusText != null) statusText.Text = "Transfer canceled — run Compare first.";
                return;
            }
        }

        if (candidates.Count == 0)
        {
            if (statusText != null) statusText.Text = "No files are queued for transfer.";
            return;
        }

        bool hasUnsupported = candidates.Any(c => !IsKeptExtension($".{c.Format}"));
        bool convertUnsupported = hasUnsupported && FFmpegHelper.IsFFmpegInstalled();

        candidates = OrderCandidatesForPhysicalWrite(candidates);

        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Stop"; }
        if (progressStatusText != null) progressStatusText.Text = "Transferring and organizing files...";
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

            if (statusText != null)
                statusText.Text = $"Transfer complete. Copied: {summary.Copied}, Converted: {summary.Converted}, Skipped: {summary.Skipped}, Failed: {summary.Failed}.";
        }
        catch (OperationCanceledException)
        {
            if (statusText != null) statusText.Text = "Transfer stopped by user.";
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Transfer failed: {ex.Message}";
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
            _operationCts?.Dispose();
            _operationCts = null;
            if (convertUnsupported)
                AppendFfmpegLog($"=== Transfer conversion ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
    }

    private async void PrepareUsbButton_Click(object? sender, RoutedEventArgs e)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");

        if (string.IsNullOrWhiteSpace(_currentScanPath) || !Directory.Exists(_currentScanPath))
        {
            if (statusText != null) statusText.Text = "Select and scan a source folder first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Select a target folder before starting the one-step prep.";
            return;
        }

        if (_mediaFiles.Count == 0)
        {
            if (statusText != null) statusText.Text = "Scan a source folder first.";
            return;
        }

        if (!await PromptForPicardAndRescanAsync())
        {
            return;
        }

        var compareResult = await RunCompareWorkflowAsync();
        if (compareResult == null)
        {
            return;
        }

        var transferCandidates = _mediaFiles
            .Where(m => string.Equals(m.CompareStatus, "Missing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (transferCandidates.Count == 0)
        {
            await ShowInfoDialogAsync(
                "Nothing to transfer",
                "Everything already appears to be on the target, so there is nothing new to copy.");
            return;
        }

        var proceed = await ShowConfirmDialogAsync(
            "Start transfer now?",
            $"{transferCandidates.Count} file(s) are missing on the target. Start the transfer and conversion step now?");

        if (!proceed)
        {
            if (statusText != null) statusText.Text = "One-step prep stopped before transfer.";
            return;
        }

        await TransferCandidatesAsync(transferCandidates, requireCompareConfirmation: false);
    }

    private async void CompareButton_Click(object? sender, RoutedEventArgs e)
    {
        await RunCompareWorkflowAsync();
    }

    private CompareResult CompareSourceAndTarget(CancellationToken cancellationToken)
    {
        var (targetKeys, duplicateOnTargetCount) = BuildMetadataKeySetWithDuplicateCount(_targetPath, cancellationToken);
        int total = _mediaFiles.Count;
        int processed = 0;
        int missing = 0;
        int alreadyOnTarget = 0;
        int unknown = 0;
        int duplicateInSource = 0;
        var seenSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            else if (!seenSourceKeys.Add(key))
            {
                duplicateInSource++;
                status = "Duplicate in source";
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

        return new CompareResult(missing, alreadyOnTarget, unknown, duplicateOnTargetCount, duplicateInSource);
    }

    private (HashSet<string> Keys, int DuplicateCount) BuildMetadataKeySetWithDuplicateCount(string basePath, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicateCount = 0;
        var files = Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file);
            if (!ScannableAudioExtensions.Contains(extension))
                continue;

            var metadata = ReadTrackMetadata(file);
            var key = BuildTrackKey(metadata.Artist, metadata.Album, metadata.Title, Path.GetFileName(file));
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!keys.Add(key))
                duplicateCount++;
        }

        return (keys, duplicateCount);
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
        var selectedFiles = GetSelectedMediaFiles();
        var candidates = selectedFiles.Count > 0
            ? selectedFiles
            : _mediaFiles.Where(m => string.Equals(m.CompareStatus, "Missing", StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidates.Count == 0)
        {
            if (selectedFiles.Count == 0 && !_hasComparisonResults)
                candidates = _mediaFiles.ToList();
        }
        if (candidates.Count == 0)
        {
            var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
            if (statusText != null) statusText.Text = "Select files to transfer, or run compare so missing files can be transferred.";
            return;
        }

        await TransferCandidatesAsync(candidates, requireCompareConfirmation: true);
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

    private List<MediaFileInfo> OrderCandidatesForPhysicalWrite(List<MediaFileInfo> candidates)
    {
        var prepared = new List<(MediaFileInfo Media, string DestinationRelative, string DestinationDirectory, int Disc, int Track, int OriginalIndex)>();

        for (int i = 0; i < candidates.Count; i++)
        {
            var media = candidates[i];
            string extension = Path.GetExtension(media.FilePath).ToLowerInvariant();
            string relativePath = Path.GetRelativePath(_currentScanPath, media.FilePath);
            string destinationRelative = IsKeptExtension(extension)
                ? relativePath
                : Path.ChangeExtension(relativePath, _conversionSettings.OutputFormat);
            string destinationDirectory = Path.GetDirectoryName(destinationRelative) ?? string.Empty;

            var (disc, track) = ReadTrackPositionForOrdering(media.FilePath, media.FileName);
            prepared.Add((media, destinationRelative, destinationDirectory, disc, track, i));
        }

        return prepared
            .OrderBy(p => p.DestinationDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Disc)
            .ThenBy(p => p.Track)
            .ThenBy(p => p.DestinationRelative, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.OriginalIndex)
            .Select(p => p.Media)
            .ToList();
    }

    private (int Disc, int Track) ReadTrackPositionForOrdering(string filePath, string fileName)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            uint disc = tagFile.Tag.Disc;
            uint track = tagFile.Tag.Track;

            if (disc > 0 || track > 0)
            {
                int discValue = disc > 0 ? (int)disc : int.MaxValue;
                int trackValue = track > 0 ? (int)track : int.MaxValue;
                return (discValue, trackValue);
            }
        }
        catch
        {
            // Fall back to filename-based ordering if tags are unreadable.
        }

        return ParseTrackPositionFromFileName(fileName);
    }

    private static (int Disc, int Track) ParseTrackPositionFromFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return (int.MaxValue, int.MaxValue);

        int index = 0;
        while (index < name.Length && char.IsDigit(name[index]))
            index++;

        if (index == 0)
            return (int.MaxValue, int.MaxValue);

        if (!int.TryParse(name[..index], out int first) || first <= 0)
            return (int.MaxValue, int.MaxValue);

        int tail = index;
        while (tail < name.Length && (name[tail] == ' ' || name[tail] == '-' || name[tail] == '_' || name[tail] == '.'))
            tail++;

        if (index < name.Length && name[index] == '-' && tail < name.Length)
        {
            int secondStart = tail;
            int secondEnd = secondStart;
            while (secondEnd < name.Length && char.IsDigit(name[secondEnd]))
                secondEnd++;

            if (secondEnd > secondStart && int.TryParse(name[secondStart..secondEnd], out int second) && second > 0)
                return (first, second);
        }

        return (int.MaxValue, first);
    }

    private TransferSummary TransferFiles(List<MediaFileInfo> candidates, bool convertUnsupported, CancellationToken cancellationToken)
    {
        int copied = 0, converted = 0, skipped = 0, failed = 0;
        int total = candidates.Count;
        var targetKeys = BuildMetadataKeySet(_targetPath, cancellationToken);
        var pendingTransferKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var media = candidates[i];

            try
            {
                string extension = Path.GetExtension(media.FilePath).ToLowerInvariant();
                bool directCopy = IsKeptExtension(extension);

                var metadata = ReadTrackMetadata(media.FilePath);
                var metadataKey = BuildTrackKey(metadata.Artist, metadata.Album, metadata.Title, media.FileName);
                if (!string.IsNullOrWhiteSpace(metadataKey))
                {
                    if (targetKeys.Contains(metadataKey) || pendingTransferKeys.Contains(metadataKey))
                    {
                        skipped++;
                        continue;
                    }
                }

                if (!directCopy && !convertUnsupported)
                {
                    skipped++;
                    continue;
                }

                // Organize into Artist/Album directories (or Album only for compilations)
                var (artist, album, title) = ReadTrackMetadata(media.FilePath);
                bool isCompilation = ReadCompilationFlag(media.FilePath);
                
                string sanitizedArtist = FileOrganizer.SanitizeName(string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist);
                string sanitizedAlbum = FileOrganizer.SanitizeName(string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album);
                
                string fileName = Path.GetFileName(media.FilePath);
                if (!directCopy)
                    fileName = Path.ChangeExtension(fileName, _conversionSettings.OutputFormat);
                
                // For compilations, organize by Album only. For single-artist albums, use Artist/Album
                string destinationPath = isCompilation
                    ? Path.Combine(_targetPath, sanitizedAlbum, fileName)
                    : Path.Combine(_targetPath, sanitizedArtist, sanitizedAlbum, fileName);
                
                // Handle filename collisions
                int counter = 1;
                string baseDestinationPath = destinationPath;
                while (File.Exists(destinationPath))
                {
                    string? directory = Path.GetDirectoryName(baseDestinationPath);
                    string name = Path.GetFileNameWithoutExtension(baseDestinationPath);
                    string ext = Path.GetExtension(baseDestinationPath);
                    destinationPath = Path.Combine(directory ?? _targetPath, $"{name} ({counter}){ext}");
                    counter++;
                }

                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                // Recreate files in transfer order so FAT entry order follows track numbering.
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);

                if (directCopy)
                {
                    File.Copy(media.FilePath, destinationPath, overwrite: false);
                    copied++;
                }
                else
                {
                    ConvertFileToConfiguredOutput(media.FilePath, destinationPath, cancellationToken);
                    converted++;
                }

                if (!string.IsNullOrWhiteSpace(metadataKey))
                {
                    targetKeys.Add(metadataKey);
                    pendingTransferKeys.Add(metadataKey);
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

    private void ConvertFileToConfiguredOutput(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        AppendFfmpegLog($"Converting for transfer: {sourcePath} -> {destinationPath}");
        var arguments = BuildTranscodeArguments(sourcePath, destinationPath);
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

    private bool ReadCompilationFlag(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            // Check if track has multiple performers or artist is "Various Artists"
            bool hasMultiplePerformers = tagFile.Tag.Performers.Length > 1;
            bool isVariousArtists = tagFile.Tag.FirstPerformer?.IndexOf("various", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasMultiplePerformers || isVariousArtists;
        }
        catch
        {
            return false;
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

        string folderPath = folderPathTextBox?.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            if (statusText != null)
            {
                statusText.Text = "Please enter a valid folder path.";
            }
            return;
        }

        try
        {
            await RunSourceScanAsync(folderPath);
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

    // Rename (Picard) feature removed by user request.

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
        if (convertButton != null) convertButton.IsEnabled = false;
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
            await Task.Run(() => ConvertFlacToConfiguredOutput(flacFiles, compilationAlbumsForConvert, progress, _operationCts.Token));

            if (conversionProgressBar != null) conversionProgressBar.Value = 100;
            if (progressStatusText != null) progressStatusText.Text = "Conversion complete!";
            if (statusText != null) statusText.Text = $"Conversion complete. Original FLAC files preserved. Re-scan folder to see new {GetOutputFormatDisplayName()} files.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsEnabled = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            AppendFfmpegLog("Conversion canceled by user.");
            if (statusText != null) statusText.Text = "Conversion stopped by user.";
            if (progressStatusText != null) progressStatusText.Text = "Conversion stopped.";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsEnabled = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (statusText != null) statusText.Text = $"Error during conversion: {ex.Message}";
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (convertButton != null) convertButton.IsEnabled = true;
            if (stopButton != null) stopButton.IsVisible = false;
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void ConvertFlacToConfiguredOutput(List<MediaFileInfo> flacFiles, IReadOnlySet<string>? compilationAlbums, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        int filesCompleted = 0;
        int totalFiles = flacFiles.Count;

        foreach (var flacFile in flacFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outputPath = Path.ChangeExtension(flacFile.FilePath, _conversionSettings.OutputFormat);
                AppendFfmpegLog($"\n--- [{filesCompleted + 1}/{totalFiles}] {flacFile.FileName} ---");
                AppendFfmpegLog($"Input : {flacFile.FilePath}");
                AppendFfmpegLog($"Output: {outputPath}");

                var arguments = BuildTranscodeArguments(flacFile.FilePath, outputPath);
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
                    AppendFfmpegLog($"Renamed/moved converted {GetOutputFormatDisplayName()} file with Picard naming.");
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

public record CompareResult(int MissingCount, int AlreadyOnTargetCount, int UnknownCount, int DuplicateOnTargetCount, int DuplicateInSourceCount);
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
