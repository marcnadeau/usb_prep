using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MediaFileAnalyzer;

public partial class FfmpegConsoleWindow : Window
{
    private readonly object _logLock = new object();
    private StringBuilder _pendingLogs = new StringBuilder();
    private Timer? _flushTimer;
    private const int FlushIntervalMs = 100;
    private const int LineThresholdForFlush = 50;

    public FfmpegConsoleWindow()
    {
        InitializeComponent();
        this.Closing += (_, _) =>
        {
            _flushTimer?.Dispose();
            FlushLogs(); // Flush any remaining logs
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void ClearLogs()
    {
        lock (_logLock)
        {
            _pendingLogs.Clear();
        }
        
        Dispatcher.UIThread.Post(() =>
        {
            var consoleTextBox = ConsoleTextBox ?? this.FindControl<TextBox>("ConsoleTextBox");
            if (consoleTextBox != null)
            {
                consoleTextBox.Text = string.Empty;
            }
        });
    }

    public void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_logLock)
        {
            _pendingLogs.Append(text).Append(Environment.NewLine);
            
            // Flush if we've accumulated many lines
            if (_pendingLogs.Length > 5000 || _pendingLogs.ToString().Split('\n').Length > LineThresholdForFlush)
            {
                FlushLogs();
            }
        }

        // Start or restart the flush timer
        _flushTimer?.Dispose();
        _flushTimer = new Timer(_ => FlushLogs(), null, FlushIntervalMs, Timeout.Infinite);
    }

    private void FlushLogs()
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
        }

        Dispatcher.UIThread.Post(() =>
        {
            var consoleTextBox = ConsoleTextBox ?? this.FindControl<TextBox>("ConsoleTextBox");
            if (consoleTextBox != null)
            {
                consoleTextBox.Text = (consoleTextBox.Text ?? string.Empty) + logsToAdd;
                // Only set caret once per batch
                consoleTextBox.CaretIndex = consoleTextBox.Text?.Length ?? 0;
            }
        });
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
