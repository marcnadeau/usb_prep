using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaFileAnalyzer;

/// <summary>Resolves an album artist from the MusicBrainz web API (release search by album + artist).</summary>
public static class AlbumArtistResolver
{
    private const int MinimumScore = 90;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(1100);
    private static readonly SemaphoreSlim RateLimit = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/"), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaFileAnalyzer/1.0 ( https://github.com/usb_prep )");
        return client;
    }

    /// <summary>Returns the release artist credit, or null when no confident match exists. Throws on network failure.</summary>
    public static async Task<string?> ResolveAsync(string artist, string album, int trackCount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;

        string query = string.IsNullOrWhiteSpace(artist)
            ? $"release:\"{Escape(album)}\""
            : $"release:\"{Escape(album)}\" AND artist:\"{Escape(artist)}\"";
        using JsonDocument document = await GetJsonAsync($"release?query={Uri.EscapeDataString(query)}&limit=10&fmt=json", cancellationToken);
        if (!document.RootElement.TryGetProperty("releases", out JsonElement releases)) return null;

        string wantedTitle = Normalize(album);
        var best = releases.EnumerateArray()
            .Select(release => new
            {
                Title = Str(release, "title") ?? string.Empty,
                Credit = ReadCredit(release),
                Score = release.TryGetProperty("score", out JsonElement s) && s.TryGetInt32(out int v) ? v : 0,
                Tracks = release.TryGetProperty("media", out JsonElement media)
                    ? media.EnumerateArray().Sum(m => m.TryGetProperty("track-count", out JsonElement c) && c.TryGetInt32(out int n) ? n : 0)
                    : 0
            })
            .Where(r => r.Score >= MinimumScore && r.Credit.Length > 0 && Normalize(r.Title) == wantedTitle)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => Math.Abs(r.Tracks - trackCount))
            .FirstOrDefault();

        return best?.Credit;
    }

    private static async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        await RateLimit.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                TimeSpan remaining = MinimumDelay - (DateTimeOffset.UtcNow - _lastRequest);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);

                using HttpResponseMessage response = await Http.GetAsync(path, cancellationToken);
                _lastRequest = DateTimeOffset.UtcNow;
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }

                bool retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= MaxAttempts) response.EnsureSuccessStatusCode();
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), cancellationToken);
            }
        }
        finally { RateLimit.Release(); }
    }

    private static string ReadCredit(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out JsonElement credits) || credits.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Concat(credits.EnumerateArray().Select(c =>
            (Str(c, "name") ?? string.Empty) + (Str(c, "joinphrase") ?? string.Empty))).Trim();
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Normalize(string value)
        => string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
