using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MediaFileAnalyzer
{
    public static class MediaAnalyzer
    {
        public static string GetAudioDuration(string filePath)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of \"default=noprint_wrappers=1:nokey=1:noval=0\" \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    var output = process?.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrEmpty(output) && double.TryParse(output, out var seconds))
                    {
                        var timespan = TimeSpan.FromSeconds(seconds);
                        return timespan.ToString(@"hh\:mm\:ss");
                    }
                }
            }
            catch { }

            return "N/A";
        }
    }
}
