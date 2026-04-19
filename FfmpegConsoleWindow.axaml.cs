using System;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MediaFileAnalyzer;

public partial class FfmpegConsoleWindow : Window
{
    private readonly object _logLock = new object();
    private readonly StringBuilder _pendingLogs = new StringBuilder();
    private Timer? _flushTimer;
    private int _pendingLineCount;
    private int _uiFlushQueued;
    private const int FlushIntervalMs = 180;
    private const int LineThresholdForFlush = 80;
    private const int MaxConsoleChars = 200_000;

    public FfmpegConsoleWindow()
    {
        InitializeComponent();
        _flushTimer = new Timer(_ => TryScheduleUiFlush(), null, FlushIntervalMs, FlushIntervalMs);
        this.Closing += (_, _) =>
        {
            TryScheduleUiFlush();
            _flushTimer?.Dispose();
            _flushTimer = null;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void ClearLogs()
    {
        lock (_logLock)
        {
            _pendingLogs.Clear();
            _pendingLineCount = 0;
        }
        
        Dispatcher.UIThread.Post(() =>
        {
            var consoleTextBox = ConsoleTextBox ?? this.FindControl<TextBox>("ConsoleTextBox");
            if (consoleTextBox != null)
            {
                consoleTextBox.Text = string.Empty;
            }
        }, DispatcherPriority.Background);
    }

    public void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        bool flushNow = false;
        lock (_logLock)
        {
            _pendingLogs.Append(text).Append(Environment.NewLine);
            _pendingLineCount++;
            flushNow = _pendingLogs.Length > 10_000 || _pendingLineCount >= LineThresholdForFlush;
        }

        if (flushNow)
        {
            TryScheduleUiFlush();
        }
    }

    private void TryScheduleUiFlush()
    {
        if (Interlocked.Exchange(ref _uiFlushQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                FlushLogsOnUiThread();
            }
            finally
            {
                Interlocked.Exchange(ref _uiFlushQueued, 0);

                lock (_logLock)
                {
                    if (_pendingLogs.Length > 0)
                    {
                        TryScheduleUiFlush();
                    }
                }
            }
        }, DispatcherPriority.Background);
    }

    private void FlushLogsOnUiThread()
    {
        string logsToAdd;
        lock (_logLock)
        {
            if (_pendingLogs.Length == 0)
            {
                return;
            }

            logsToAdd = _pendingLogs.ToString();
            _pendingLogs.Clear();
            _pendingLineCount = 0;
        }

        var consoleTextBox = ConsoleTextBox ?? this.FindControl<TextBox>("ConsoleTextBox");
        if (consoleTextBox != null)
        {
            var existing = consoleTextBox.Text ?? string.Empty;
            var combined = existing + logsToAdd;
            if (combined.Length > MaxConsoleChars)
            {
                combined = combined[^MaxConsoleChars..];
            }

            consoleTextBox.Text = combined;
            consoleTextBox.CaretIndex = consoleTextBox.Text?.Length ?? 0;
        }
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearLogs();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }
}
