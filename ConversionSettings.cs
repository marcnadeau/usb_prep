using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MediaFileAnalyzer;

public sealed class ConversionSettings
{
    public HashSet<string> KeepExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".m4a"
    };

    public string OutputFormat { get; set; } = ".mp3";

    public string OutputCodec { get; set; } = "libmp3lame";

    public int BitrateKbps { get; set; } = 320;

    public ConversionSettings Clone()
    {
        var clone = new ConversionSettings
        {
            KeepExtensions = new HashSet<string>(KeepExtensions, StringComparer.OrdinalIgnoreCase),
            OutputFormat = OutputFormat,
            OutputCodec = OutputCodec,
            BitrateKbps = BitrateKbps
        };

        clone.Normalize();
        return clone;
    }

    public void Normalize()
    {
        KeepExtensions = new HashSet<string>(KeepExtensions
            .Select(AudioConversionCatalog.NormalizeExtension)
            .Where(AudioConversionCatalog.IsKnownInputFormat), StringComparer.OrdinalIgnoreCase);

        if (KeepExtensions.Count == 0)
        {
            KeepExtensions = new HashSet<string>(AudioConversionCatalog.DefaultKeepExtensions, StringComparer.OrdinalIgnoreCase);
        }

        OutputFormat = AudioConversionCatalog.NormalizeExtension(OutputFormat);
        if (!AudioConversionCatalog.IsKnownOutputFormat(OutputFormat))
        {
            OutputFormat = AudioConversionCatalog.DefaultOutputFormat;
        }

        if (BitrateKbps <= 0)
        {
            BitrateKbps = AudioConversionCatalog.DefaultBitrateKbps;
        }

        if (AudioConversionCatalog.GetCodecOptions(OutputFormat, fraunhoferAvailable: true)
            .All(codec => !codec.Id.Equals(OutputCodec, StringComparison.OrdinalIgnoreCase)))
        {
            OutputCodec = AudioConversionCatalog.GetDefaultCodecId(OutputFormat);
        }
    }
}

public static class ConversionSettingsStore
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "usb_prep",
        "conversion_settings.json");

    public static ConversionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return GetDefaults();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<ConversionSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (settings == null)
            {
                return GetDefaults();
            }

            settings.Normalize();
            return settings;
        }
        catch
        {
            return GetDefaults();
        }
    }

    public static void Save(ConversionSettings settings)
    {
        try
        {
            var normalized = settings.Clone();
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
        }
    }

    public static ConversionSettings GetDefaults()
    {
        var defaults = new ConversionSettings();
        defaults.Normalize();
        return defaults;
    }
}

public sealed record InputFormatOption(string Extension, string Label)
{
    public override string ToString() => Label;
}

public sealed record OutputFormatOption(string Extension, string Label, string DefaultCodecId)
{
    public override string ToString() => Label;
}

public sealed record AudioCodecOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public static class AudioConversionCatalog
{
    public const int DefaultBitrateKbps = 320;
    public const string DefaultOutputFormat = ".mp3";

    public static readonly IReadOnlyList<InputFormatOption> InputFormats = new[]
    {
        new InputFormatOption(".mp3", "MP3 (.mp3)"),
        new InputFormatOption(".m4a", "M4A / AAC (.m4a)"),
        new InputFormatOption(".flac", "FLAC (.flac)"),
        new InputFormatOption(".wav", "WAV (.wav)"),
        new InputFormatOption(".aac", "AAC (.aac)"),
        new InputFormatOption(".ogg", "OGG (.ogg)"),
        new InputFormatOption(".wma", "WMA (.wma)"),
        new InputFormatOption(".aiff", "AIFF (.aiff)"),
        new InputFormatOption(".alac", "ALAC (.alac)")
    };

    public static readonly IReadOnlyList<OutputFormatOption> OutputFormats = new[]
    {
        new OutputFormatOption(".mp3", "MP3 (.mp3)", "libmp3lame"),
        new OutputFormatOption(".m4a", "M4A / AAC (.m4a)", "libfdk_aac")
    };

    public static readonly IReadOnlyList<string> DefaultKeepExtensions = new[] { ".mp3", ".m4a" };

    public static bool IsKnownInputFormat(string extension)
        => InputFormats.Any(format => format.Extension.Equals(NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownOutputFormat(string extension)
        => OutputFormats.Any(format => format.Extension.Equals(NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        extension = extension.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return extension.ToLowerInvariant();
    }

    public static OutputFormatOption GetOutputFormat(string extension)
    {
        extension = NormalizeExtension(extension);
        return OutputFormats.FirstOrDefault(format => format.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            ?? OutputFormats[0];
    }

    public static string GetDefaultCodecId(string extension)
        => GetOutputFormat(extension).DefaultCodecId;

    public static IReadOnlyList<AudioCodecOption> GetCodecOptions(string outputFormat, bool fraunhoferAvailable)
    {
        outputFormat = NormalizeExtension(outputFormat);

        if (outputFormat.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            var options = new List<AudioCodecOption>();
            if (fraunhoferAvailable)
            {
                options.Add(new AudioCodecOption("libfdk_aac", "Fraunhofer AAC (libfdk_aac)"));
            }

            options.Add(new AudioCodecOption("aac", "AAC (native)"));
            return options;
        }

        return new[]
        {
            new AudioCodecOption("libmp3lame", "MP3 (libmp3lame)")
        };
    }

    public static string GetFormatLabel(string extension)
    {
        extension = NormalizeExtension(extension);
        return InputFormats.FirstOrDefault(format => format.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))?.Label
            ?? extension.ToUpperInvariant();
    }
}

public static class FfmpegCapabilityDetector
{
    public static async Task<bool> HasFraunhoferAacAsync()
        => await HasEncoderAsync("libfdk_aac");

    public static async Task<bool> HasEncoderAsync(string encoderId)
    {
        if (!FFmpegHelper.IsFFmpegInstalled())
        {
            return false;
        }

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
            return output.Contains(encoderId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
