using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace MediaFileAnalyzer
{
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

        private ObservableCollection<MediaFileInfo> _mediaFiles;
        private string _currentScanPath = string.Empty;
        private string _targetPath = string.Empty;
        private bool _hasComparisonResults;
        private FfmpegConsoleWindow? _ffmpegConsoleWindow;
        private CancellationTokenSource? _operationCts;
        private Process? _currentFfmpegProcess;
        private readonly object _ffmpegProcessLock = new();

        public MainWindow()
        {
            InitializeComponent();
            _mediaFiles = new ObservableCollection<MediaFileInfo>();
            FilesDataGrid.ItemsSource = _mediaFiles;
        }

        private void EnsureFfmpegConsoleWindow()
        {
            if (_ffmpegConsoleWindow != null)
            {
                return;
            }

            _ffmpegConsoleWindow = new FfmpegConsoleWindow
            {
                Owner = this
            };

            _ffmpegConsoleWindow.Closed += (_, _) =>
            {
                _ffmpegConsoleWindow = null;
            };
        }

        private void AppendFfmpegLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                EnsureFfmpegConsoleWindow();
                _ffmpegConsoleWindow?.AppendLog(line);
            });
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopButton.IsEnabled = false;
            StopButton.Content = "Stopping...";
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

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to scan for audio files";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    FolderPathTextBox.Text = dialog.SelectedPath;
                    _currentScanPath = dialog.SelectedPath;
                    _hasComparisonResults = false;
                    StatusText.Text = $"Folder selected: {dialog.SelectedPath}";
                    UpdateActionAvailability();
                }
            }
        }

        private void BrowseTargetButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a target folder (USB drive destination)";
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TargetFolderPathTextBox.Text = dialog.SelectedPath;
                    _targetPath = dialog.SelectedPath;
                    _hasComparisonResults = false;
                    StatusText.Text = $"Target folder selected: {dialog.SelectedPath}";
                    UpdateActionAvailability();
                }
            }
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            string folderPath = FolderPathTextBox.Text;
            
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                MessageBox.Show("Please select a valid folder.", "Invalid Folder", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentScanPath = folderPath;
            _hasComparisonResults = false;

            // Clear previous results
            _mediaFiles.Clear();
            TotalFilesText.Text = "0";
            ImageCountText.Text = "0";
            VideoCountText.Text = "0";
            TotalSizeText.Text = "0 MB";
            StatusText.Text = "Scanning...";

            try
            {
                await Task.Run(() => ScanFolder(folderPath));
                
                // Update statistics
                int totalFiles = _mediaFiles.Count;
                int mp3Count = _mediaFiles.Count(f => f.Format.ToLower() == "mp3");
                int flacCount = _mediaFiles.Count(f => f.Format.ToLower() == "flac");
                long totalSize = _mediaFiles.Sum(f => f.FileSizeBytes);

                Dispatcher.Invoke(() =>
                {
                    TotalFilesText.Text = totalFiles.ToString();
                    ImageCountText.Text = mp3Count.ToString();
                    VideoCountText.Text = flacCount.ToString();
                    TotalSizeText.Text = FormatFileSize(totalSize);
                    StatusText.Text = $"Scan complete. Found {totalFiles} audio file(s). FLAC files: {flacCount}";
                    
                    // Enable convert button if FLAC found
                    if (flacCount > 0)
                    {
                        ConvertButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ConvertButton.Visibility = Visibility.Collapsed;
                    }
                    
                    // Show rename button if there are any audio files
                    if (totalFiles > 0)
                    {
                        RenameButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        RenameButton.Visibility = Visibility.Collapsed;
                    }

                    UpdateActionAvailability();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scanning folder: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error occurred during scan.";
                UpdateActionAvailability();
            }
        }

        private void ScanFolder(string folderPath)
        {
            try
            {
                var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file).ToLower();
                    
                    if (ScannableAudioExtensions.Contains(extension))
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
                            Format = extension.TrimStart('.'),
                            Artist = metadata.Artist,
                            Album = metadata.Album,
                            Title = metadata.Title,
                            CompareStatus = "Not compared"
                        };

                        Dispatcher.Invoke(() => _mediaFiles.Add(mediaInfo));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip folders we don't have access to
            }
        }

        private void UpdateActionAvailability()
        {
            bool hasSource = !string.IsNullOrWhiteSpace(_currentScanPath) && Directory.Exists(_currentScanPath);
            bool hasTarget = !string.IsNullOrWhiteSpace(_targetPath) && Directory.Exists(_targetPath);
            bool hasFiles = _mediaFiles.Count > 0;

            CompareButton.IsEnabled = hasSource && hasTarget && hasFiles;
            TransferButton.Visibility = hasTarget && hasFiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
            {
                MessageBox.Show("Please select a valid target folder.", "Missing Target",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_mediaFiles.Count == 0)
            {
                MessageBox.Show("Scan a source folder first.", "No Source Files",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProgressBorder.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            StopButton.Content = "Stop";
            ProgressStatusText.Text = "Comparing source and target metadata...";
            CurrentFileText.Text = string.Empty;
            ConversionProgressBar.Value = 0;
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();

            try
            {
                var result = await Task.Run(() => CompareSourceAndTarget(_operationCts.Token));

                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    _hasComparisonResults = true;
                    StatusText.Text = $"Compare complete. Missing on target: {result.MissingCount}, already on target: {result.AlreadyOnTargetCount}, unknown metadata: {result.UnknownCount}.";
                    TransferButton.Visibility = Visibility.Visible;
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Comparison stopped by user.";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    MessageBox.Show($"Error during compare: {ex.Message}", "Compare Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Comparison failed.";
                });
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
                Dispatcher.Invoke(() =>
                {
                    mediaFile.CompareStatus = status;
                    ConversionProgressBar.Value = percent;
                    ProgressCountText.Text = $"{processed}/{total}";
                    CurrentFileText.Text = mediaFile.FileName;
                    FilesDataGrid.Items.Refresh();
                });
            }

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
                {
                    continue;
                }

                var metadata = ReadTrackMetadata(file);
                var key = BuildTrackKey(metadata.Artist, metadata.Album, metadata.Title, Path.GetFileName(file));
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private async void TransferButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_targetPath) || !Directory.Exists(_targetPath))
            {
                MessageBox.Show("Please choose a valid target folder before transferring.", "Missing Target",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedFiles = GetSelectedMediaFiles();
            var candidates = selectedFiles.Count > 0
                ? selectedFiles
                : _mediaFiles.Where(m => string.Equals(m.CompareStatus, "Missing", StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 0)
            {
                MessageBox.Show("Select files to transfer, or run compare so missing files can be transferred.", "No Transfer Candidates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (selectedFiles.Count == 0 && !_hasComparisonResults)
            {
                var proceedWithoutCompare = MessageBox.Show(
                    "No files selected and no compare results found. Transfer all scanned files instead?",
                    "Transfer Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (proceedWithoutCompare != MessageBoxResult.Yes)
                {
                    return;
                }

                candidates = _mediaFiles.ToList();
            }

            bool hasUnsupported = candidates.Any(c => !SupportedAudioExtensions.Contains($".{c.Format}"));
            bool convertUnsupported = false;

            if (hasUnsupported)
            {
                var convertPrompt = MessageBox.Show(
                    "Some selected files are not MP3/M4A. Convert unsupported files to MP3 before copying?\n\nChoose No to skip unsupported files.",
                    "Convert Before Copy",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (convertPrompt == MessageBoxResult.Cancel)
                {
                    return;
                }

                convertUnsupported = convertPrompt == MessageBoxResult.Yes;

                if (convertUnsupported && !FFmpegHelper.IsFFmpegInstalled())
                {
                    MessageBox.Show("FFmpeg is required to convert unsupported formats.", "FFmpeg Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var startTransfer = MessageBox.Show(
                $"Transfer {candidates.Count} file(s) to target folder?",
                "Start Transfer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (startTransfer != MessageBoxResult.Yes)
            {
                return;
            }

            ProgressBorder.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            StopButton.Content = "Stop";
            ProgressStatusText.Text = "Transferring files...";
            CurrentFileText.Text = string.Empty;
            ConversionProgressBar.Value = 0;
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();

            if (convertUnsupported)
            {
                EnsureFfmpegConsoleWindow();
                _ffmpegConsoleWindow?.Show();
                _ffmpegConsoleWindow?.Activate();
                AppendFfmpegLog($"=== Transfer conversion started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            }

            try
            {
                var summary = await Task.Run(() => TransferFiles(candidates, convertUnsupported, _operationCts.Token));

                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = $"Transfer complete. Copied: {summary.Copied}, Converted: {summary.Converted}, Skipped: {summary.Skipped}, Failed: {summary.Failed}.";
                    MessageBox.Show(
                        $"Transfer complete.\nCopied: {summary.Copied}\nConverted: {summary.Converted}\nSkipped: {summary.Skipped}\nFailed: {summary.Failed}",
                        "Transfer Summary",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Transfer stopped by user.";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Collapsed;
                    MessageBox.Show($"Transfer failed: {ex.Message}", "Transfer Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Transfer failed.";
                });
            }
            finally
            {
                _operationCts?.Dispose();
                _operationCts = null;
                if (convertUnsupported)
                {
                    AppendFfmpegLog($"=== Transfer conversion ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                }
            }
        }

        private List<MediaFileInfo> GetSelectedMediaFiles()
        {
            var result = new List<MediaFileInfo>();
            if (FilesDataGrid.SelectedItems is IList selected)
            {
                foreach (var item in selected)
                {
                    if (item is MediaFileInfo media)
                    {
                        result.Add(media);
                    }
                }
            }

            return result;
        }

        private TransferSummary TransferFiles(List<MediaFileInfo> candidates, bool convertUnsupported, CancellationToken cancellationToken)
        {
            int copied = 0;
            int converted = 0;
            int skipped = 0;
            int failed = 0;
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
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    if (File.Exists(destinationPath))
                    {
                        var decision = PromptConflictDecision(media.FileName, destinationPath);
                        if (decision == MessageBoxResult.Cancel)
                        {
                            throw new OperationCanceledException("Transfer canceled by user on conflict prompt.");
                        }

                        if (decision == MessageBoxResult.No)
                        {
                            skipped++;
                            continue;
                        }
                    }

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
                    Dispatcher.Invoke(() =>
                    {
                        ConversionProgressBar.Value = percent;
                        ProgressCountText.Text = $"{i + 1}/{total}";
                        CurrentFileText.Text = candidates[i].FileName;
                    });
                }
            }

            return new TransferSummary(copied, converted, skipped, failed);
        }

        private MessageBoxResult PromptConflictDecision(string fileName, string destinationPath)
        {
            return Dispatcher.Invoke(() => MessageBox.Show(
                $"File already exists:\n{fileName}\n\nDestination:\n{destinationPath}\n\nYes = Overwrite, No = Skip, Cancel = Stop transfer.",
                "File Conflict",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question));
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo) ?? throw new Exception("Unable to start ffmpeg process.");

            lock (_ffmpegProcessLock)
            {
                _currentFfmpegProcess = process;
            }

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    AppendFfmpegLog(eventArgs.Data);
                }
            };

            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    AppendFfmpegLog(eventArgs.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();

            lock (_ffmpegProcessLock)
            {
                _currentFfmpegProcess = null;
            }

            if (process.ExitCode != 0)
            {
                throw new Exception($"FFmpeg failed with exit code {process.ExitCode}");
            }
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
            {
                return string.Empty;
            }

            return string.Join(' ', value.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private async void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            var audioFiles = _mediaFiles.ToList();
            
            if (audioFiles.Count == 0)
            {
                MessageBox.Show("No audio files found to rename.", "Info", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Confirm renaming
            var confirmResult = MessageBox.Show(
                $"Rename {audioFiles.Count} file(s) to follow Picard naming convention?\n\n" +
                "Format: Artist/Album/TrackNumber - Title\n\n" +
                "This action cannot be undone.",
                "Confirm Renaming",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
                return;

            // Show progress bar
            ProgressBorder.Visibility = Visibility.Visible;
            RenameButton.Visibility = Visibility.Collapsed;
            ConvertButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            StopButton.Content = "Stop";
            ProgressCountText.Text = "";
            ConversionProgressBar.Value = 0;
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();

            // Create progress reporter
            var progress = new Progress<ConversionProgress>(report =>
            {
                ConversionProgressBar.Value = report.PercentComplete;
                ProgressStatusText.Text = $"Renaming: ({report.FilesCompleted}/{report.TotalFiles})";
                ProgressCountText.Text = $"{report.FilesCompleted}/{report.TotalFiles}";
                CurrentFileText.Text = report.CurrentFile;
            });

            try
            {
                await Task.Run(() => RenameAllFiles(audioFiles, _currentScanPath, progress, _operationCts.Token));
                
                Dispatcher.Invoke(() =>
                {
                    ConversionProgressBar.Value = 100;
                    ProgressStatusText.Text = "Renaming complete!";
                    
                    MessageBox.Show(
                        $"Renaming complete!\n{audioFiles.Count} file(s) renamed to Picard convention.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    
                    StatusText.Text = "Renaming complete. Re-scan folder to see updated file structure.";
                    
                    // Hide progress bar and show buttons again
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    RenameButton.Visibility = Visibility.Visible;
                    StopButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Renaming stopped by user.";
                    ProgressStatusText.Text = "Renaming stopped.";
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    RenameButton.Visibility = Visibility.Visible;
                    ConvertButton.Visibility = Visibility.Visible;
                    StopButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during renaming: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Renaming failed.";
                
                // Hide progress bar and show buttons again
                ProgressBorder.Visibility = Visibility.Collapsed;
                RenameButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
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
                    
                    // Report progress
                    progress?.Report(new ConversionProgress
                    {
                        CurrentFile = audioFile.FileName,
                        FilesCompleted = filesCompleted,
                        TotalFiles = totalFiles,
                        PercentComplete = percentComplete
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Error renaming {audioFile.FileName}: {ex.Message}";
                    });
                }
            }
        }

        private async void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            var flacFiles = _mediaFiles.Where(f => f.Format.ToLower() == "flac").ToList();
            
            if (flacFiles.Count == 0)
            {
                MessageBox.Show("No FLAC files found to convert.", "Info", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Check if FFmpeg is installed
            if (!FFmpegHelper.IsFFmpegInstalled())
            {
                var result = MessageBox.Show(
                    "FFmpeg is not installed. Do you want to download and install it?\n\n" +
                    "This will open a browser to download FFmpeg.",
                    "FFmpeg Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://ffmpeg.org/download.html",
                        UseShellExecute = true
                    });
                }
                return;
            }

            // Confirm conversion
            var confirmResult = MessageBox.Show(
                $"Convert {flacFiles.Count} FLAC file(s) to MP3?\n\nThis will create new MP3 files with the highest quality (320kbps).",
                "Confirm Conversion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
                return;

            // Show progress bar, hide buttons
            ProgressBorder.Visibility = Visibility.Visible;
            ConvertButton.Visibility = Visibility.Collapsed;
            RenameButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            StopButton.Content = "Stop";
            ProgressCountText.Text = "";
            ConversionProgressBar.Value = 0;
            ConvertButton.IsEnabled = false;
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();

            EnsureFfmpegConsoleWindow();
            _ffmpegConsoleWindow?.ClearLogs();
            _ffmpegConsoleWindow?.Show();
            _ffmpegConsoleWindow?.Activate();
            AppendFfmpegLog($"=== Conversion started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            AppendFfmpegLog($"Files to convert: {flacFiles.Count}");

            // Create progress reporter
            var progress = new Progress<ConversionProgress>(report =>
            {
                ConversionProgressBar.Value = report.PercentComplete;
                ProgressStatusText.Text = $"Converting: ({report.FilesCompleted}/{report.TotalFiles})";
                ProgressCountText.Text = $"{report.FilesCompleted}/{report.TotalFiles}";
                CurrentFileText.Text = report.CurrentFile;
            });

            try
            {
                await Task.Run(() => ConvertFlacToMp3(flacFiles, progress, _operationCts.Token));
                
                Dispatcher.Invoke(() =>
                {
                    ConversionProgressBar.Value = 100;
                    ProgressStatusText.Text = "Conversion complete!";
                    
                    MessageBox.Show(
                        $"Conversion complete!\n{flacFiles.Count} FLAC file(s) converted to MP3.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    
                    // Ask if user wants to delete original FLAC files
                    var deleteResult = MessageBox.Show(
                        $"Do you want to delete the original {flacFiles.Count} FLAC file(s)?\n\nThis action cannot be undone.",
                        "Delete Original Files?",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (deleteResult == MessageBoxResult.Yes)
                    {
                        int deletedCount = 0;
                        foreach (var flacFile in flacFiles)
                        {
                            if (TryDeleteWithRetry(flacFile.FilePath, flacFile.FileName))
                            {
                                deletedCount++;
                            }
                        }
                        
                        MessageBox.Show($"Deleted {deletedCount} FLAC file(s).", "Deletion Complete",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        StatusText.Text = $"Conversion complete. Deleted {deletedCount} FLAC file(s). Re-scan folder to see new MP3 files.";
                    }
                    else
                    {
                        StatusText.Text = "Conversion complete. Original FLAC files preserved. Re-scan folder to see new MP3 files.";
                    }
                    
                    // Hide progress bar and show buttons again
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    ConvertButton.Visibility = Visibility.Visible;
                    RenameButton.Visibility = Visibility.Visible;
                    ConvertButton.IsEnabled = true;
                    StopButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendFfmpegLog("Conversion canceled by user.");
                    StatusText.Text = "Conversion stopped by user.";
                    ProgressStatusText.Text = "Conversion stopped.";
                    ProgressBorder.Visibility = Visibility.Collapsed;
                    ConvertButton.Visibility = Visibility.Visible;
                    RenameButton.Visibility = Visibility.Visible;
                    ConvertButton.IsEnabled = true;
                    StopButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during conversion: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Conversion failed.";
                
                // Hide progress bar and show buttons again
                ProgressBorder.Visibility = Visibility.Collapsed;
                ConvertButton.Visibility = Visibility.Visible;
                RenameButton.Visibility = Visibility.Visible;
                ConvertButton.IsEnabled = true;
                StopButton.Visibility = Visibility.Collapsed;
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
                    
                    // Use FFmpeg to convert with best quality (320kbps) and force overwrite existing files
                    var arguments = $"-y -i \"{flacFile.FilePath}\" -b:a 320k -q:v 0 \"{outputPath}\"";
                    
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(processInfo))
                    {
                        if (process == null)
                        {
                            throw new Exception("Unable to start ffmpeg process.");
                        }

                        lock (_ffmpegProcessLock)
                        {
                            _currentFfmpegProcess = process;
                        }

                        process.OutputDataReceived += (_, eventArgs) =>
                        {
                            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                            {
                                AppendFfmpegLog(eventArgs.Data);
                            }
                        };

                        process.ErrorDataReceived += (_, eventArgs) =>
                        {
                            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                            {
                                AppendFfmpegLog(eventArgs.Data);
                            }
                        };

                        process.BeginOutputReadLine();
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
                    }

                    AppendFfmpegLog("ffmpeg finished successfully.");

                    // Rename the converted MP3 file to follow Picard naming convention
                    if (File.Exists(outputPath))
                    {
                        string baseDirectory = string.IsNullOrWhiteSpace(_currentScanPath)
                            ? (Path.GetDirectoryName(flacFile.FilePath) ?? Directory.GetCurrentDirectory())
                            : _currentScanPath;
                        FileNamer.RenameToPickardStyle(outputPath, baseDirectory);
                        AppendFfmpegLog("Renamed/moved converted MP3 with Picard naming.");
                    }

                    filesCompleted++;
                    int percentComplete = (filesCompleted * 100) / totalFiles;
                    
                    // Report progress
                    progress?.Report(new ConversionProgress
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
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Error converting {flacFile.FileName}: {ex.Message}";
                    });
                }
            }

            AppendFfmpegLog($"\n=== Conversion ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }

        private bool TryDeleteWithRetry(string filePath, string fileName)
        {
            while (true)
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
                catch (IOException ioEx) when (IsFileInUse(ioEx))
                {
                    var retryResult = System.Windows.Forms.MessageBox.Show(
                        $"Cannot delete '{fileName}' because it is used by another process.\n\nClose the application using this file, then click Retry.",
                        "File In Use",
                        System.Windows.Forms.MessageBoxButtons.RetryCancel,
                        System.Windows.Forms.MessageBoxIcon.Warning);

                    if (retryResult != System.Windows.Forms.DialogResult.Retry)
                    {
                        return false;
                    }

                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete {fileName}: {ex.Message}",
                        "Delete Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
        }

        private static bool IsFileInUse(IOException exception)
        {
            int code = exception.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
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

                using (var process = Process.Start(processInfo))
                {
                    process?.WaitForExit(3000);
                    return process?.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
