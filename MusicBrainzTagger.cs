using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaFileAnalyzer
{
    public record TrackMetadata(
        string Title,
        string Artist,
        string AlbumArtist,
        string Album,
        uint TrackNumber,
        uint TrackCount,
        uint DiscNumber,
        uint DiscCount,
        uint Year,
        string RecordingId,
        string ReleaseId,
        string AcoustId);

    public static class MusicBrainzTagger
    {
        private static readonly HttpClient _http;

        public static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "usb_prep", "acoustid_key.txt");

        static MusicBrainzTagger()
        {
            _http = new HttpClient();
            // MusicBrainz requires a descriptive User-Agent
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "MediaFileAnalyzer/1.0 ( https://github.com/usb_prep )");
        }

        public static string? LoadApiKey()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var key = File.ReadAllText(SettingsPath).Trim();
                    return string.IsNullOrWhiteSpace(key) ? null : key;
                }
            }
            catch { }
            return null;
        }

        public static void SaveApiKey(string key)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, key.Trim());
            }
            catch { }
        }

        /// <summary>
        /// Runs fpcalc on the file and returns (duration, fingerprint).
        /// </summary>
        public static async Task<(double Duration, string Fingerprint)?> GetFingerprintAsync(
            string filePath, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "fpcalc",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-json");
                psi.ArgumentList.Add(filePath);

                using var process = Process.Start(psi)!;
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0) return null;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                double duration = root.GetProperty("duration").GetDouble();
                string fingerprint = root.GetProperty("fingerprint").GetString()!;
                return (duration, fingerprint);
            }
            catch { return null; }
        }

        /// <summary>
        /// Full pipeline: fingerprint → AcoustID → MusicBrainz → TrackMetadata.
        /// Returns null if the track could not be identified.
        /// </summary>
        public static async Task<TrackMetadata?> LookupAsync(
            string filePath, string apiKey, CancellationToken ct)
        {
            var fp = await GetFingerprintAsync(filePath, ct);
            if (fp == null) return null;

            var (duration, fingerprint) = fp.Value;

            // AcoustID lookup – returns recording IDs + release-group types
            var url = "https://api.acoustid.org/v2/lookup" +
                $"?client={Uri.EscapeDataString(apiKey)}" +
                $"&meta=recordings+releasegroups" +
                $"&fingerprint={Uri.EscapeDataString(fingerprint)}" +
                $"&duration={Math.Round(duration)}";

            string? recordingId = null;
            string acoustId = string.Empty;

            try
            {
                using var response = await _http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "ok") return null;

                var results = root.GetProperty("results");
                double bestScore = 0;
                JsonElement? bestResult = null;

                foreach (var result in results.EnumerateArray())
                {
                    double score = result.GetProperty("score").GetDouble();
                    if (score > bestScore) { bestScore = score; bestResult = result; }
                }

                if (bestResult == null || bestScore < 0.5) return null;

                acoustId = bestResult.Value.GetProperty("id").GetString() ?? string.Empty;

                if (!bestResult.Value.TryGetProperty("recordings", out var recordings)
                    || recordings.GetArrayLength() == 0)
                    return null;

                // Prefer recording linked to an Album release-group
                JsonElement? bestRec = null;
                int bestPriority = -1;
                foreach (var rec in recordings.EnumerateArray())
                {
                    int priority = 0;
                    if (rec.TryGetProperty("releasegroups", out var rgs))
                        foreach (var rg in rgs.EnumerateArray())
                            if (rg.TryGetProperty("type", out var t) && t.GetString() == "Album")
                                priority += 2;
                    if (priority > bestPriority) { bestPriority = priority; bestRec = rec; }
                }

                bestRec ??= recordings[0];
                recordingId = bestRec.Value.GetProperty("id").GetString();
            }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(recordingId)) return null;

            // MusicBrainz API rate limit: max 1 request/sec
            await Task.Delay(1100, ct);
            return await GetRecordingMetadataAsync(recordingId, acoustId, ct);
        }

        private static async Task<TrackMetadata?> GetRecordingMetadataAsync(
            string recordingId, string acoustId, CancellationToken ct)
        {
            try
            {
                var url = $"https://musicbrainz.org/ws/2/recording/{recordingId}" +
                    "?inc=artists+releases+artist-credits&fmt=json";

                using var response = await _http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string title = root.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                string artist = ExtractArtistCredit(root);

                string album = "", albumArtist = "", releaseId = "";
                uint trackNumber = 0, trackCount = 0, discNumber = 1, discCount = 1, year = 0;

                if (root.TryGetProperty("releases", out var releases))
                {
                    JsonElement? bestRelease = null;
                    int bestPriority = -1;

                    foreach (var rel in releases.EnumerateArray())
                    {
                        int priority = 0;
                        if (rel.TryGetProperty("release-group", out var rg)
                            && rg.TryGetProperty("primary-type", out var pt)
                            && pt.GetString() == "Album")
                            priority += 2;
                        if (rel.TryGetProperty("status", out var st)
                            && st.GetString() == "Official")
                            priority += 1;
                        if (priority > bestPriority) { bestPriority = priority; bestRelease = rel; }
                    }

                    if (bestRelease.HasValue)
                    {
                        var rel = bestRelease.Value;
                        releaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                        album = rel.TryGetProperty("title", out var at) ? at.GetString() ?? "" : "";
                        albumArtist = ExtractArtistCredit(rel);

                        if (rel.TryGetProperty("date", out var dt))
                        {
                            var ds = dt.GetString() ?? "";
                            if (ds.Length >= 4 && uint.TryParse(ds[..4], out var y)) year = y;
                        }

                        if (rel.TryGetProperty("media", out var media))
                        {
                            discCount = (uint)media.GetArrayLength();
                            foreach (var medium in media.EnumerateArray())
                            {
                                if (!medium.TryGetProperty("tracks", out var tracks)
                                    || tracks.GetArrayLength() == 0)
                                    continue;

                                if (medium.TryGetProperty("position", out var dp)
                                    && dp.TryGetUInt32(out var dpNum))
                                    discNumber = dpNum;
                                if (medium.TryGetProperty("track-count", out var tc)
                                    && tc.TryGetUInt32(out var tcNum))
                                    trackCount = tcNum;

                                var track = tracks[0];
                                if (track.TryGetProperty("position", out var tn)
                                    && tn.TryGetUInt32(out var tnNum))
                                    trackNumber = tnNum;
                                break;
                            }
                        }
                    }
                }

                return new TrackMetadata(
                    title, artist, albumArtist, album,
                    trackNumber, trackCount, discNumber, discCount,
                    year, recordingId, releaseId, acoustId);
            }
            catch { return null; }
        }

        private static string ExtractArtistCredit(JsonElement element)
        {
            if (!element.TryGetProperty("artist-credit", out var credits)
                || credits.GetArrayLength() == 0)
                return "";

            var sb = new StringBuilder();
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.TryGetProperty("artist", out var a)
                    && a.TryGetProperty("name", out var n))
                    sb.Append(n.GetString());
                if (credit.TryGetProperty("joinphrase", out var jp))
                    sb.Append(jp.GetString());
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Writes the fetched metadata into the file's tags using TagLibSharp.
        /// Only overwrites fields where we have a non-empty value.
        /// </summary>
        public static void ApplyTags(string filePath, TrackMetadata meta)
        {
            using var audioFile = TagLib.File.Create(filePath);
            var tag = audioFile.Tag;

            if (!string.IsNullOrWhiteSpace(meta.Title)) tag.Title = meta.Title;
            if (!string.IsNullOrWhiteSpace(meta.Artist))
                tag.Performers = new[] { meta.Artist };
            if (!string.IsNullOrWhiteSpace(meta.AlbumArtist))
                tag.AlbumArtists = new[] { meta.AlbumArtist };
            if (!string.IsNullOrWhiteSpace(meta.Album)) tag.Album = meta.Album;
            if (meta.TrackNumber > 0) tag.Track = meta.TrackNumber;
            if (meta.TrackCount > 0) tag.TrackCount = meta.TrackCount;
            if (meta.DiscNumber > 0) tag.Disc = meta.DiscNumber;
            if (meta.DiscCount > 0) tag.DiscCount = meta.DiscCount;
            if (meta.Year > 0) tag.Year = meta.Year;

            audioFile.Save();
        }
    }
}
