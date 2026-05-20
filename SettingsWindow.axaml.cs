using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MediaFileAnalyzer;

public partial class SettingsWindow : Window
{
    private readonly ConversionSettings _settings;
    private readonly List<CheckBox> _keepFormatCheckBoxes = new();
    private bool _fraunhoferAvailable;

    public SettingsWindow() : this(new ConversionSettings()) { }

    public SettingsWindow(ConversionSettings settings)
    {
        _settings = settings.Clone();
        InitializeComponent();

        Loaded += SettingsWindow_Loaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void SettingsWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        PopulateKeepFormatChecks();
        PopulateOutputFormats();
        await RefreshCodecOptionsAsync();
    }

    private void PopulateKeepFormatChecks()
    {
        var panel = this.FindControl<StackPanel>("KeepFormatsPanel");
        if (panel == null)
        {
            return;
        }

        panel.Children.Clear();
        _keepFormatCheckBoxes.Clear();

        foreach (var format in AudioConversionCatalog.InputFormats)
        {
            var checkBox = new CheckBox
            {
                Content = format.Label,
                IsChecked = _settings.KeepExtensions.Contains(format.Extension),
                Tag = format.Extension
            };

            panel.Children.Add(checkBox);
            _keepFormatCheckBoxes.Add(checkBox);
        }
    }

    private void PopulateOutputFormats()
    {
        var comboBox = this.FindControl<ComboBox>("OutputFormatComboBox");
        var bitrateTextBox = this.FindControl<TextBox>("BitrateTextBox");

        if (comboBox != null)
        {
            comboBox.ItemsSource = AudioConversionCatalog.OutputFormats;
            comboBox.SelectedItem = AudioConversionCatalog.GetOutputFormat(_settings.OutputFormat);
        }

        if (bitrateTextBox != null)
        {
            bitrateTextBox.Text = Math.Max(1, _settings.BitrateKbps).ToString();
        }
    }

    private async System.Threading.Tasks.Task RefreshCodecOptionsAsync()
    {
        _fraunhoferAvailable = await FfmpegCapabilityDetector.HasFraunhoferAacAsync();

        var availabilityText = this.FindControl<TextBlock>("CodecAvailabilityText");
        if (availabilityText != null)
        {
            availabilityText.Text = _fraunhoferAvailable
                ? "Fraunhofer AAC (libfdk_aac) is available on this machine."
                : "Fraunhofer AAC is not available. AAC (native) will be offered instead.";
        }

        RefreshCodecOptions();
    }

    private void RefreshCodecOptions()
    {
        var formatComboBox = this.FindControl<ComboBox>("OutputFormatComboBox");
        var codecComboBox = this.FindControl<ComboBox>("OutputCodecComboBox");
        if (formatComboBox == null || codecComboBox == null)
        {
            return;
        }

        var selectedFormat = formatComboBox.SelectedItem as OutputFormatOption
            ?? AudioConversionCatalog.GetOutputFormat(_settings.OutputFormat);
        var codecOptions = AudioConversionCatalog.GetCodecOptions(selectedFormat.Extension, _fraunhoferAvailable);
        codecComboBox.ItemsSource = codecOptions;

        var selectedCodec = codecOptions.FirstOrDefault(codec => codec.Id.Equals(_settings.OutputCodec, StringComparison.OrdinalIgnoreCase))
            ?? codecOptions.First();

        codecComboBox.SelectedItem = selectedCodec;
        codecComboBox.IsEnabled = codecOptions.Count > 1;

        _settings.OutputFormat = selectedFormat.Extension;
        _settings.OutputCodec = selectedCodec.Id;
    }

    private void OutputFormatComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is OutputFormatOption selectedFormat)
        {
            _settings.OutputFormat = selectedFormat.Extension;
            _settings.OutputCodec = selectedFormat.DefaultCodecId;
            RefreshCodecOptions();
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var keepExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkBox in _keepFormatCheckBoxes)
        {
            if (checkBox.IsChecked == true && checkBox.Tag is string extension)
            {
                keepExtensions.Add(extension);
            }
        }

        if (keepExtensions.Count == 0)
        {
            keepExtensions = new HashSet<string>(AudioConversionCatalog.DefaultKeepExtensions, StringComparer.OrdinalIgnoreCase);
        }

        _settings.KeepExtensions = keepExtensions;

        if (this.FindControl<ComboBox>("OutputFormatComboBox")?.SelectedItem is OutputFormatOption selectedFormat)
        {
            _settings.OutputFormat = selectedFormat.Extension;
        }

        if (this.FindControl<ComboBox>("OutputCodecComboBox")?.SelectedItem is AudioCodecOption selectedCodec)
        {
            _settings.OutputCodec = selectedCodec.Id;
        }

        if (int.TryParse(this.FindControl<TextBox>("BitrateTextBox")?.Text, out int bitrate))
        {
            _settings.BitrateKbps = bitrate;
        }

        _settings.Normalize();
        ConversionSettingsStore.Save(_settings);
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
