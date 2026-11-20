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
    private bool _isBusy;
    private TransferSummary? _lastTransferSummary;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resolvedAlbumArtists = new(StringComparer.OrdinalIgnoreCase);
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
            FolderPathTextBox.PropertyChanged += (_, change) =>
            {
                if (change.Property != TextBox.TextProperty || _isBusy) return;
                _currentScanPath = FolderPathTextBox.Text?.Trim() ?? string.Empty;
                InvalidateAnalysis(clearSource: true);
            };
            _filesDataGrid!.SelectionChanged += (_, _) => UpdateWorkflowSummary();
            UseSelectionCheckBox.IsCheckedChanged += (_, _) => UpdateWorkflowSummary();
            UpdateActionAvailability();
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        // The hand-written loader does not initialize source-generated named fields.
        FolderPathTextBox = this.FindControl<TextBox>("FolderPathTextBox")!;
        TargetFolderPathTextBox = this.FindControl<TextBox>("TargetFolderPathTextBox")!;
        BrowseSourceButton = this.FindControl<Button>("BrowseSourceButton")!;
        BrowseTargetButton = this.FindControl<Button>("BrowseTargetButton")!;
        CompareButton = this.FindControl<Button>("CompareButton")!;
        PrepareUsbButton = this.FindControl<Button>("PrepareUsbButton")!;
        ConvertButton = this.FindControl<Button>("ConvertButton")!;
        SettingsButton = this.FindControl<Button>("SettingsButton")!;
        AdvancedButton = this.FindControl<Button>("AdvancedButton")!;
        CompilationCheckBox = this.FindControl<CheckBox>("CompilationCheckBox")!;
        UseSelectionCheckBox = this.FindControl<CheckBox>("UseSelectionCheckBox")!;
        FilesDataGrid = this.FindControl<Avalonia.Controls.DataGrid>("FilesDataGrid")!;
        WorkflowSummaryText = this.FindControl<TextBlock>("WorkflowSummaryText")!;
        ProfileText = this.FindControl<TextBlock>("ProfileText")!;
        ResultText = this.FindControl<TextBlock>("ResultText")!;
        StatusText = this.FindControl<TextBlock>("StatusText")!;
        TotalFilesText = this.FindControl<TextBlock>("TotalFilesText")!;
        ImageCountText = this.FindControl<TextBlock>("ImageCountText")!;
        VideoCountText = this.FindControl<TextBlock>("VideoCountText")!;
        TotalSizeText = this.FindControl<TextBlock>("TotalSizeText")!;
        ProgressBorder = this.FindControl<Border>("ProgressBorder")!;
        ConversionProgressBar = this.FindControl<ProgressBar>("ConversionProgressBar")!;
        ProgressStatusText = this.FindControl<TextBlock>("ProgressStatusText")!;
        CurrentFileText = this.FindControl<TextBlock>("CurrentFileText")!;
        ProgressCountText = this.FindControl<TextBlock>("ProgressCountText")!;
        StopButton = this.FindControl<Button>("StopButton")!;
    }

    private void EnsureFfmpegConsoleWindow()
    {
        if (_ffmpegConsoleWindow != null)
        {
            return;
        }

        _ffmpegConsoleWindow = new FfmpegConsoleWindow();
        _ffmpegConsoleWindow.Closed += (_, _) => _ffmpegConsoleWindow = null;
    }

    private void ShowLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        EnsureFfmpegConsoleWindow();
        if (!_ffmpegConsoleWindow!.IsVisible) _ffmpegConsoleWindow.Show(this);
        _ffmpegConsoleWindow.Activate();
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
            convertButton.Content = $"Convertir les FLAC dans la source → {GetOutputFormatDisplayName()} ({GetBitrateLabel()})";
        }
        ProfileText.Text = $"Copie sans conversion : {GetKeepFormatsSummary()} · Autres formats : {GetOutputFormatDisplayName()}, {GetCodecDisplayName()}, {GetBitrateLabel()}";
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
        return _conversionSettings.OutputCodec;
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
            stopButton.Content = "Arrêt en cours…";
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
        if (_isBusy) return;
        var dialog = new SettingsWindow(_conversionSettings);
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            _conversionSettings = ConversionSettingsStore.Load();
            RefreshConversionUi();
            InvalidateAnalysis(clearSource: false);
        }
    }

    

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
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
        var noButton = new Button { Content = "Annuler", Width = 100, Margin = new Thickness(6,0) };
        var yesButton = new Button { Content = "Confirmer", Width = 110, Margin = new Thickness(6,0) };

        var tcs = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

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

    private async Task ReorganizeButton_Click(object? sender, RoutedEventArgs e)
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
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
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
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
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

    private async Task RepairTargetButton_Click(object? sender, RoutedEventArgs e)
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

    private async Task CleanTargetDuplicatesButton_Click(object? sender, RoutedEventArgs e)
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
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
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
        if (_isBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not null)
        {
            try
            {
                var folderPathTextBox = FolderPathTextBox ?? this.FindControl<TextBox>("FolderPathTextBox");
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                string? selectedPath;
                if (!BeginOperation()) return;
                try { selectedPath = await PickFolderWithLinuxFallbackAsync(topLevel, "Choisir le dossier de musique source"); }
                finally { EndOperation(); }

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    _currentScanPath = selectedPath;
                    if (folderPathTextBox != null)
                    {
                        folderPathTextBox.Text = selectedPath;
                    }

                    if (statusText != null)
                    {
                        statusText.Text = $"Source choisie : {selectedPath}. Choisissez l’USB puis analysez.";
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the actual exception for debugging
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                if (statusText != null)
                {
                    statusText.Text = $"Erreur de sélection du dossier : {ex.Message}";
                }
                System.Diagnostics.Debug.WriteLine($"BrowseButton_Click exception: {ex}");
            }
        }
    }

    private async void BrowseTargetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not null)
        {
            try
            {
                var targetFolderPathTextBox = TargetFolderPathTextBox ?? this.FindControl<TextBox>("TargetFolderPathTextBox");
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                var compareButton = CompareButton ?? this.FindControl<Button>("CompareButton");

                string? selectedPath;
                if (!BeginOperation()) return;
                try { selectedPath = await PickFolderWithLinuxFallbackAsync(topLevel, "Choisir la destination USB"); }
                finally { EndOperation(); }

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    _targetPath = selectedPath;
                    InvalidateAnalysis(clearSource: false);
                    if (targetFolderPathTextBox != null)
                        targetFolderPathTextBox.Text = selectedPath;
                    if (statusText != null)
                        statusText.Text = $"Destination USB choisie : {selectedPath}. Relancez l’analyse.";
                    UpdateActionAvailability();
                }
            }
            catch (Exception ex)
            {
                var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");
                if (statusText != null)
                    statusText.Text = $"Erreur de sélection du dossier : {ex.Message}";
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
        FolderPathTextBox.IsEnabled = TargetFolderPathTextBox.IsEnabled = !_isBusy;
        BrowseSourceButton.IsEnabled = BrowseTargetButton.IsEnabled = SettingsButton.IsEnabled = !_isBusy;
        AdvancedButton.IsEnabled = !_isBusy;
        CompareButton.IsEnabled = !_isBusy && hasSource && hasTarget;
        PrepareUsbButton.Content = _hasComparisonResults ? "3. Préparer l’USB" : "2. Analyser";
        PrepareUsbButton.IsEnabled = !_isBusy && hasSource && hasTarget && (!_hasComparisonResults || hasFiles);
        ConvertButton.IsEnabled = !_isBusy && _mediaFiles.Any(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase));
        UseSelectionCheckBox.IsEnabled = !_isBusy && _hasComparisonResults;
        CompilationCheckBox.IsEnabled = !_isBusy;
        FilesDataGrid.IsEnabled = !_isBusy;
        UpdateWorkflowSummary();
    }

    private void InvalidateAnalysis(bool clearSource)
    {
        _hasComparisonResults = false;
        _lastTransferSummary = null;
        ResultText.Text = string.Empty;
        if (clearSource)
        {
            _mediaFiles.Clear();
            _detectedCompilationAlbums.Clear();
            TotalFilesText.Text = ImageCountText.Text = VideoCountText.Text = "0";
            TotalSizeText.Text = "0 MB";
            CompilationCheckBox.IsVisible = false;
        }
        foreach (var file in _mediaFiles) file.CompareStatus = "Non analysé";
        RefreshGrid();
        UpdateActionAvailability();
    }

    private void RefreshGrid()
    {
        if (_filesDataGrid == null) return;
        _filesDataGrid.ItemsSource = null;
        _filesDataGrid.ItemsSource = _mediaFiles;
    }

    private List<MediaFileInfo> GetPreparationCandidates()
    {
        var scope = UseSelectionCheckBox.IsChecked == true ? GetSelectedMediaFiles() : _mediaFiles.ToList();
        return scope.Where(f => f.CompareStatus == "À ajouter").ToList();
    }

    private void UpdateWorkflowSummary()
    {
        if (!_hasComparisonResults)
        {
            WorkflowSummaryText.Text = "1. Choisissez la source et la destination USB, puis analysez. Aucun fichier source n’est modifié.";
            return;
        }
        var candidates = GetPreparationCandidates();
        int conversions = candidates.Count(f => !IsKeptExtension(Path.GetExtension(f.FilePath)));
        int incomplete = _mediaFiles.Count(f => f.HasIncompleteTags);
        string scope = UseSelectionCheckBox.IsChecked == true
            ? $"Sélection : {GetSelectedMediaFiles().Count} piste(s), {candidates.Count} admissible(s)"
            : $"Toutes les pistes manquantes : {candidates.Count}";
        WorkflowSummaryText.Text = $"2. Vérifiez : {_mediaFiles.Count(f => f.CompareStatus == "Présent sur l’USB")} présentes · {_mediaFiles.Count(f => f.CompareStatus == "À ajouter")} manquantes · {_mediaFiles.Count(f => f.CompareStatus == "Doublon source")} doublons source · {incomplete} tags incomplets (correspondance parfois basée sur le nom).\n{scope} → {candidates.Count - conversions} à copier, {conversions} à convertir. Les pistes présentes et doublons source sont exclus.";
    }

    private bool BeginOperation()
    {
        if (_isBusy) return false;
        _isBusy = true;
        UpdateActionAvailability();
        return true;
    }

    private void EndOperation()
    {
        _isBusy = false;
        UpdateActionAvailability();
    }

    private async void AdvancedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var menu = new Window
        {
            Title = "Actions avancées",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var convert = new Button { Content = "Convertir les FLAC dans la source (outil local)", IsEnabled = ConvertButton.IsEnabled, Margin = new Thickness(0, 6, 0, 6) };
        var reorganize = new Button { Content = "USB : regrouper par album (noms conservés)", IsEnabled = Directory.Exists(_targetPath), Margin = new Thickness(0, 6, 0, 6) };
        var clean = new Button { Content = "USB : nettoyer les doublons", IsEnabled = Directory.Exists(_targetPath), Margin = new Thickness(0, 6, 0, 6) };
        var repair = new Button { Content = "USB : inspecter et réparer", IsEnabled = Directory.Exists(_targetPath), Margin = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "Annuler", Margin = new Thickness(0, 12, 0, 0) };

        var tcs = new TaskCompletionSource<string?>();
        menu.Closed += (_, _) => tcs.TrySetResult(null);
        convert.Click += (_, _) => { tcs.TrySetResult("convert"); menu.Close(); };
        reorganize.Click += (_, _) => { tcs.TrySetResult("reorganize"); menu.Close(); };
        clean.Click += (_, _) => { tcs.TrySetResult("clean"); menu.Close(); };
        repair.Click += (_, _) => { tcs.TrySetResult("repair"); menu.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); menu.Close(); };

        menu.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { new TextBlock { Text = "La préparation normale convertit directement vers l’USB.\nCes outils sont distincts et peuvent modifier les dossiers.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, convert, reorganize, clean, repair, cancel }
        };

        if (TopLevel.GetTopLevel(this) is Window owner)
            await menu.ShowDialog(owner);
        else
            menu.Show();

        var choice = await tcs.Task;
        if (choice == "convert")
        {
            await ConvertLocallyAsync();
        }
        else if (choice != null && BeginOperation())
        {
            try
            {
                if (choice == "reorganize") await ReorganizeButton_Click(this, new RoutedEventArgs());
                else if (choice == "clean") await CleanTargetDuplicatesButton_Click(this, new RoutedEventArgs());
                else if (choice == "repair") await RepairTargetButton_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex) { StatusText.Text = $"Échec de la maintenance : {ex.Message}"; }
            finally
            {
                _operationCts?.Dispose();
                _operationCts = null;
                InvalidateAnalysis(clearSource: false);
                EndOperation();
            }
        }
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 580,
            SizeToContent = SizeToContent.Height,
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
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
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

    private async Task RunSourceScanAsync(string folderPath, CancellationToken cancellationToken)
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
        if (statusText != null) statusText.Text = "Analyse de la source…";

        var scannedFiles = await Task.Run(() => ScanFolder(folderPath, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

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
        if (statusText != null) statusText.Text = $"Source analysée : {totalFiles} fichier(s), dont {flacCount} FLAC.";
        UpdateActionAvailability();

        var compilationCheckBox = CompilationCheckBox ?? this.FindControl<CheckBox>("CompilationCheckBox");
        if (totalFiles > 0)
        {
            var allPaths = _mediaFiles.Select(f => f.FilePath).ToList();
            _detectedCompilationAlbums = await Task.Run(() => FileNamer.DetectCompilationAlbums(allPaths), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            bool hasCompilations = _detectedCompilationAlbums.Count > 0;
            if (compilationCheckBox != null)
            {
                compilationCheckBox.IsVisible = hasCompilations;
                compilationCheckBox.IsChecked = hasCompilations;
            }
            if (hasCompilations && statusText != null)
                statusText.Text += $" — {_detectedCompilationAlbums.Count} compilation(s) détectée(s).";
        }
        else
        {
            _detectedCompilationAlbums.Clear();
            if (compilationCheckBox != null) compilationCheckBox.IsVisible = false;
        }
    }

    private async Task<CompareResult?> RunCompareWorkflowAsync(CancellationToken cancellationToken)
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
            if (statusText != null) statusText.Text = "Aucun fichier audio trouvé dans la source.";
            return null;
        }

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Arrêter"; }
        if (progressStatusText != null) progressStatusText.Text = "Analyse de l’USB et comparaison des tags locaux…";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (progressCountText != null) progressCountText.Text = string.Empty;
        if (conversionProgressBar != null) { conversionProgressBar.Value = 0; conversionProgressBar.IsIndeterminate = true; }
        try
        {
            var result = await Task.Run(() => CompareSourceAndTarget(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            RefreshGrid();
            _hasComparisonResults = true;
            if (statusText != null)
                statusText.Text = $"Analyse terminée : {result.MissingCount} à ajouter, {result.AlreadyOnTargetCount} présentes, {result.DuplicateInSourceCount} doublons source, {result.DuplicateOnTargetCount} doublons USB. Vérifiez le résumé avant de préparer.";
            UpdateActionAvailability();
            return result;
        }
        catch (OperationCanceledException)
        {
            _hasComparisonResults = false;
            if (statusText != null) statusText.Text = "Analyse arrêtée. Relancez l’analyse avant de préparer.";
            return null;
        }
        catch (Exception ex)
        {
            _hasComparisonResults = false;
            if (statusText != null) statusText.Text = $"Échec de l’analyse : {ex.Message}";
            return null;
        }
        finally
        {
            if (progressBorder != null) progressBorder.IsVisible = false;
            if (stopButton != null) stopButton.IsVisible = false;
        }
    }

    private async Task TransferCandidatesAsync(List<MediaFileInfo> candidates)
    {
        var statusText = StatusText ?? this.FindControl<TextBlock>("StatusText");

        if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
        {
            if (statusText != null) statusText.Text = "Please choose a valid target folder before transferring.";
            return;
        }

        if (candidates.Count == 0)
        {
            if (statusText != null) statusText.Text = "No files are queued for transfer.";
            return;
        }

        bool hasUnsupported = candidates.Any(c => !IsKeptExtension($".{c.Format}"));
        bool convertUnsupported = hasUnsupported;

        candidates = OrderCandidatesForPhysicalWrite(candidates);

        var progressBorder = ProgressBorder ?? this.FindControl<Border>("ProgressBorder");
        var stopButton = StopButton ?? this.FindControl<Button>("StopButton");
        var progressStatusText = ProgressStatusText ?? this.FindControl<TextBlock>("ProgressStatusText");
        var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
        var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");

        if (progressBorder != null) progressBorder.IsVisible = true;
        if (stopButton != null) { stopButton.IsVisible = true; stopButton.IsEnabled = true; stopButton.Content = "Arrêter"; }
        if (progressStatusText != null) progressStatusText.Text = "Préparation de l’USB : copie et conversion…";
        if (currentFileText != null) currentFileText.Text = string.Empty;
        if (conversionProgressBar != null) { conversionProgressBar.Value = 0; conversionProgressBar.IsIndeterminate = false; }
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        if (convertUnsupported)
        {
            EnsureFfmpegConsoleWindow();
            AppendFfmpegLog($"=== Transfer conversion started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }

        try
        {
            var summary = await Task.Run(() => TransferFiles(candidates, convertUnsupported, _operationCts.Token));
            _lastTransferSummary = summary;
            if (statusText != null)
                statusText.Text = $"{(summary.Canceled ? "Préparation arrêtée" : "Préparation terminée")} : {summary.Copied} copiées, {summary.Converted} converties, {summary.Skipped} ignorées, {summary.Failed} échecs.";
            ResultText.Text = statusText?.Text ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = "Préparation arrêtée avant le transfert. Aucun bilan de fichiers disponible.";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Échec de la préparation : {ex.Message}";
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
        // Refresh even after a partial transfer; keep the durable result separate from analysis status.
        _hasComparisonResults = false;
        if (_lastTransferSummary != null)
        {
            _operationCts = new CancellationTokenSource();
            try { await RunCompareWorkflowAsync(_operationCts.Token); }
            finally { _operationCts.Dispose(); _operationCts = null; }
        }
        if (ConversionProgressBar != null) ConversionProgressBar.IsIndeterminate = false;
    }

    private async void PrepareUsbButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_hasComparisonResults) { await AnalyzeWorkflowAsync(); return; }
        if (!BeginOperation()) return;
        try
        {
            var candidates = GetPreparationCandidates();
            if (candidates.Count == 0)
            {
                StatusText.Text = "Aucune piste à ajouter dans cette portée. Sélectionnez des pistes manquantes ou décochez la portée sélection.";
                return;
            }
            if (!await ValidateConversionAsync(candidates)) return;
            var (resolvedCount, unresolved) = await ResolveMissingAlbumArtistsAsync(candidates);
            if (unresolved == null) { StatusText.Text = "Résolution MusicBrainz arrêtée. Aucune écriture effectuée."; return; }
            if (unresolved.Count > 0 && !await ShowConfirmDialogAsync("Album artist introuvable",
                $"{unresolved.Count} album(s) sans album artist n’ont pas été trouvés sur MusicBrainz :\n{string.Join("\n", unresolved.Take(8))}{(unresolved.Count > 8 ? "\n…" : "")}\nContinuer en classant ces pistes selon l’artiste de la piste ?"))
            { StatusText.Text = "Préparation annulée avant toute écriture."; return; }
            int conversions = candidates.Count(f => !IsKeptExtension(Path.GetExtension(f.FilePath)));
            var confirmed = await ShowConfirmDialogAsync("Préparer l’USB ?",
                $"Portée : {(UseSelectionCheckBox.IsChecked == true ? "sélection du tableau" : "toutes les pistes manquantes")}\nDestination : {_targetPath}\n{candidates.Count - conversions} copie(s), {conversions} conversion(s).\nAlbum artist résolu via MusicBrainz : {resolvedCount} album(s).\n{ProfileText.Text}\nLes originaux restent inchangés. Les pistes déjà présentes et doublons source sont exclus.");
            if (!confirmed) { StatusText.Text = "Préparation annulée avant toute écriture."; return; }
            _lastTransferSummary = null;
            ResultText.Text = string.Empty;
            await TransferCandidatesAsync(candidates);
        }
        catch (Exception ex) { ResultText.Text = $"Échec de la préparation : {ex.Message}"; }
        finally { EndOperation(); }
    }

    private async void CompareButton_Click(object? sender, RoutedEventArgs e)
    {
        await AnalyzeWorkflowAsync();
    }

    private static string AlbumGroupKey(string filePath, string album)
        => $"{Path.GetDirectoryName(filePath)}|{NormalizeKeyPart(album)}";

    private static string? ReadAlbumArtist(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            string? value = file.Tag.FirstAlbumArtist?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    private static string StripFeaturing(string artist)
        => System.Text.RegularExpressions.Regex.Replace(artist, @"\s+[\(\[]?(ft\.?|feat\.?|featuring)\s.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    // Looks up the album artist on MusicBrainz for every album lacking one, before any file is written.
    private async Task<(int Resolved, List<string>? Unresolved)> ResolveMissingAlbumArtistsAsync(List<MediaFileInfo> candidates)
    {
        var unresolved = new List<string>();
        int resolved = 0;
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        ProgressBorder.IsVisible = StopButton.IsVisible = true;
        StopButton.IsEnabled = true;
        StopButton.Content = "Arrêter";
        ConversionProgressBar.IsIndeterminate = false;
        ConversionProgressBar.Value = 0;
        try
        {
            var groups = await Task.Run(() => candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Album) && ReadAlbumArtist(c.FilePath) == null)
                .GroupBy(c => AlbumGroupKey(c.FilePath, c.Album))
                .Where(g => !_resolvedAlbumArtists.ContainsKey(g.Key))
                .Select(g => (Key: g.Key, Album: g.First().Album,
                    Artist: StripFeaturing(g.GroupBy(c => c.Artist).OrderByDescending(a => a.Count()).First().Key),
                    Tracks: _mediaFiles.Count(m => AlbumGroupKey(m.FilePath, m.Album) == g.Key)))
                .ToList(), token);

            ProgressStatusText.Text = "Recherche de l’album artist sur MusicBrainz (albums sans album artist)…";
            for (int i = 0; i < groups.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var group = groups[i];
                CurrentFileText.Text = $"{group.Artist} — {group.Album}";
                ProgressCountText.Text = $"{i + 1}/{groups.Count}";
                ConversionProgressBar.Value = (i * 100) / groups.Count;
                try
                {
                    string? credit = await AlbumArtistResolver.ResolveAsync(group.Artist, group.Album, group.Tracks, token);
                    if (credit == null) unresolved.Add($"{group.Artist} — {group.Album}");
                    else _resolvedAlbumArtists[group.Key] = credit;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    unresolved.Add($"{group.Artist} — {group.Album} ({ex.Message})");
                }
            }
            resolved = candidates.Select(c => AlbumGroupKey(c.FilePath, c.Album)).Where(_resolvedAlbumArtists.ContainsKey).Distinct().Count();
            return (resolved, unresolved);
        }
        catch (OperationCanceledException) { return (resolved, null); }
        finally
        {
            ProgressBorder.IsVisible = StopButton.IsVisible = false;
            ProgressCountText.Text = CurrentFileText.Text = string.Empty;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private static void WriteAlbumArtistTag(string path, string albumArtist)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.AlbumArtists = new[] { albumArtist };
        file.Save();
    }

    private async Task<bool> ValidateConversionAsync(List<MediaFileInfo> candidates, bool forceConversion = false)
    {
        if (!forceConversion && candidates.All(f => IsKeptExtension(Path.GetExtension(f.FilePath)))) return true;
        if (!await FfmpegCapabilityDetector.HasEncoderAsync(_conversionSettings.OutputCodec))
        {
            await ShowInfoDialogAsync("Conversion indisponible",
                $"FFmpeg ou l’encodeur {_conversionSettings.OutputCodec} est indisponible. Installez FFmpeg avec cet encodeur ou choisissez un autre codec dans Paramètres. Aucun fichier n’a été écrit.");
            return false;
        }
        return true;
    }

    private async Task AnalyzeWorkflowAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            if (!Directory.Exists(_currentScanPath) || !Directory.Exists(_targetPath))
            {
                StatusText.Text = "Choisissez une source et une destination existantes.";
                return;
            }
            // Nested folders would make the source include its own generated output.
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_currentScanPath));
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_targetPath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (source.Equals(target, comparison) || target.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar, comparison) || source.StartsWith(Path.EndsInDirectorySeparator(target) ? target : target + Path.DirectorySeparatorChar, comparison))
            {
                StatusText.Text = "Source et destination doivent être distinctes, sans être imbriquées.";
                _hasComparisonResults = false;
                return;
            }
            _operationCts = new CancellationTokenSource();
            ProgressBorder.IsVisible = StopButton.IsVisible = true;
            StopButton.IsEnabled = true;
            StopButton.Content = "Arrêter";
            ConversionProgressBar.IsIndeterminate = true;
            ProgressStatusText.Text = "Analyse de la source…";
            CurrentFileText.Text = ProgressCountText.Text = string.Empty;
            await RunSourceScanAsync(_currentScanPath, _operationCts.Token);
            ProgressStatusText.Text = "Analyse de l’USB…";
            await RunCompareWorkflowAsync(_operationCts.Token);
        }
        catch (OperationCanceledException) { _hasComparisonResults = false; StatusText.Text = "Analyse arrêtée. Relancez l’analyse avant de préparer."; }
        catch (Exception ex) { _hasComparisonResults = false; StatusText.Text = $"Échec de l’analyse : {ex.Message}"; }
        finally
        {
            ProgressBorder.IsVisible = StopButton.IsVisible = false;
            ConversionProgressBar.IsIndeterminate = false;
            _operationCts?.Dispose();
            _operationCts = null;
            EndOperation();
        }
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
                status = "Tags inconnus";
            }
            else if (!seenSourceKeys.Add(key))
            {
                duplicateInSource++;
                status = "Doublon source";
            }
            else if (targetKeys.Contains(key))
            {
                alreadyOnTarget++;
                status = "Présent sur l’USB";
            }
            else
            {
                missing++;
                status = "À ajouter";
            }

            int percent = (processed * 100) / Math.Max(1, total);
            int capturedProcessed = processed;
            string capturedFile = mediaFile.FileName;
            // Controls are locked during comparison; publish plain model data before the task completes.
            mediaFile.CompareStatus = status;
            Dispatcher.UIThread.Post(() =>
            {
                var conversionProgressBar = ConversionProgressBar ?? this.FindControl<ProgressBar>("ConversionProgressBar");
                var progressCountText = ProgressCountText ?? this.FindControl<TextBlock>("ProgressCountText");
                var currentFileText = CurrentFileText ?? this.FindControl<TextBlock>("CurrentFileText");
                if (conversionProgressBar != null) conversionProgressBar.Value = percent;
                if (progressCountText != null) progressCountText.Text = $"{capturedProcessed}/{total}";
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
        var files = Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories);

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
        var files = Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories);

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
        HashSet<string> targetKeys;
        try { targetKeys = BuildMetadataKeySet(_targetPath, cancellationToken); }
        catch (OperationCanceledException) { return new TransferSummary(0, 0, 0, 0, Canceled: true); }
        var pendingTransferKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < candidates.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new TransferSummary(copied, converted, skipped, failed, Canceled: true);
            var media = candidates[i];
            string? incompleteDestination = null;

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
                // Album artist (tag, else MusicBrainz result) decides the folder so featured artists don't split an album.
                string? albumArtist = ReadAlbumArtist(media.FilePath);
                bool albumArtistFromMusicBrainz = false;
                if (albumArtist == null && _resolvedAlbumArtists.TryGetValue(AlbumGroupKey(media.FilePath, media.Album), out string? resolvedArtist))
                {
                    albumArtist = resolvedArtist;
                    albumArtistFromMusicBrainz = true;
                }
                bool isCompilation = albumArtist != null
                    ? albumArtist.Contains("various", StringComparison.OrdinalIgnoreCase)
                    : ReadCompilationFlag(media.FilePath);
                if (albumArtist != null) artist = albumArtist;
                
                string sanitizedArtist = FileOrganizer.SanitizeName(string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist);
                string sanitizedAlbum = FileOrganizer.SanitizeName(string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album);
                // Dot-only metadata must never turn a destination component into a parent traversal.
                if (sanitizedArtist is "." or ".." or "") sanitizedArtist = "Unknown Artist";
                if (sanitizedAlbum is "." or ".." or "") sanitizedAlbum = "Unknown Album";
                
                string fileName = Path.GetFileName(media.FilePath);
                if (!directCopy)
                    fileName = Path.ChangeExtension(fileName, _conversionSettings.OutputFormat);
                
                // For compilations, organize by Album only. For single-artist albums, use Artist/Album
                string destinationPath = isCompilation
                    ? Path.Combine(_targetPath, sanitizedAlbum, fileName)
                    : Path.Combine(_targetPath, sanitizedArtist, sanitizedAlbum, fileName);
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_targetPath)) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(destinationPath).StartsWith(targetRoot, pathComparison))
                    throw new IOException("Le chemin de sortie sort du dossier de destination.");
                
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
                    incompleteDestination = destinationPath;
                    ConvertFileToConfiguredOutput(media.FilePath, destinationPath, cancellationToken);
                    incompleteDestination = null;
                    converted++;
                }

                if (albumArtistFromMusicBrainz)
                {
                    try { WriteAlbumArtistTag(destinationPath, albumArtist!); }
                    catch (Exception ex) { AppendFfmpegLog($"Album artist non écrit dans {destinationPath} : {ex.Message}"); }
                }

                if (!string.IsNullOrWhiteSpace(metadataKey))
                {
                    targetKeys.Add(metadataKey);
                    pendingTransferKeys.Add(metadataKey);
                }
            }
            catch (OperationCanceledException)
            {
                DeleteIncompleteOutput(incompleteDestination);
                return new TransferSummary(copied, converted, skipped, failed, Canceled: true);
            }
            catch (Exception ex)
            {
                DeleteIncompleteOutput(incompleteDestination);
                AppendFfmpegLog($"Échec de {media.FileName} : {ex.Message}");
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

        return new TransferSummary(copied, converted, skipped, failed, Canceled: cancellationToken.IsCancellationRequested);
    }

    private void DeleteIncompleteOutput(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppendFfmpegLog($"Fichier partiel à supprimer manuellement : {path} ({ex.Message})"); }
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

        try
        {
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            process.BeginErrorReadLine();
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new Exception($"FFmpeg failed with exit code {process.ExitCode}");
        }
        finally { lock (_ffmpegProcessLock) _currentFfmpegProcess = null; }
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

    private static bool HasIncompleteLocalTags(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return string.IsNullOrWhiteSpace(file.Tag.FirstPerformer)
                || string.IsNullOrWhiteSpace(file.Tag.Album)
                || string.IsNullOrWhiteSpace(file.Tag.Title);
        }
        catch { return true; }
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
        await AnalyzeWorkflowAsync();
    }

    private List<MediaFileInfo> ScanFolder(string folderPath, CancellationToken cancellationToken)
    {
        var results = new List<MediaFileInfo>();

        var directoriesToScan = new Stack<string>();
        directoriesToScan.Push(folderPath);

        while (directoriesToScan.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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
                        HasIncompleteTags = HasIncompleteLocalTags(file),
                        CompareStatus = "Non analysé"
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
        => await ConvertLocallyAsync();

    private async Task ConvertLocallyAsync()
    {
        if (!BeginOperation()) return;
        bool started = false;
        try
        {
            var files = _mediaFiles.Where(f => f.Format.Equals("flac", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count == 0 || !await ValidateConversionAsync(files, forceConversion: true)) return;
            if (!await ShowConfirmDialogAsync("Conversion locale — attention",
                "Cet outil crée et organise les fichiers convertis DANS LA SOURCE. Des sorties existantes peuvent être remplacées. Les FLAC sont conservés. Ce traitement n’est pas nécessaire pour préparer l’USB. Continuer ?")) return;
            started = true;
            await RunLocalConversionAsync();
        }
        finally
        {
            if (started) InvalidateAnalysis(clearSource: true);
            EndOperation();
        }
    }

    private async Task RunLocalConversionAsync()
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
            int converted = await Task.Run(() => ConvertFlacToConfiguredOutput(flacFiles, compilationAlbumsForConvert, progress, _operationCts.Token));

            if (conversionProgressBar != null) conversionProgressBar.Value = 100;
            if (progressStatusText != null) progressStatusText.Text = "Conversion complete!";
            if (statusText != null) statusText.Text = $"Conversion locale terminée : {converted} réussite(s), {flacFiles.Count - converted} échec(s). FLAC conservés. Relancez l’analyse.";
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

    private int ConvertFlacToConfiguredOutput(List<MediaFileInfo> flacFiles, IReadOnlySet<string>? compilationAlbums, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
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
        return filesCompleted;
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
    public bool HasIncompleteTags { get; set; }
    public string CompareStatus { get; set; } = "Not compared";
}

public record CompareResult(int MissingCount, int AlreadyOnTargetCount, int UnknownCount, int DuplicateOnTargetCount, int DuplicateInSourceCount);
public record TransferSummary(int Copied, int Converted, int Skipped, int Failed, bool Canceled = false);

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
