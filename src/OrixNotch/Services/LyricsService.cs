using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrixNotch.Services;

public sealed record LyricLine(double Time, string Text);

/// <summary>
/// Looks up lyrics for the current track with a two-tier strategy:
/// 1. LRCLIB (https://lrclib.net) for time-synced .lrc lyrics.
/// 2. JioSaavn API for extensive Tamil, Telugu, Hindi and Indian regional songs.
/// Cleans video titles (HD Video Song, Official Video, etc.) automatically.
/// </summary>
public static partial class LyricsService
{
    private static readonly Dictionary<string, IReadOnlyList<LyricLine>?> Cache = new();

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    public static string CleanTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw;
        t = Regex.Replace(t, @"(?i)\b(HD|4K|8K|UHD)\s+(Video|Audio)(\s+Song)?\b", "");
        t = Regex.Replace(t, @"(?i)\b(Official|Full)\s+(Video|Audio|Music\s+Video|Song|Lyrical|Track|Visualizer)(\s+Song)?\b", "");
        t = Regex.Replace(t, @"(?i)\b(Video\s+Song|Lyric\s+Video|Lyrical\s+Video|Audio\s+Song|Remix|Cover|Reprise)\b", "");
        t = Regex.Replace(t, @"(?i)\[.*?\]|\(.*?\)", "");

        var parts = Regex.Split(t, @"\s*[\|\-–—]\s*");
        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            t = parts[0];
        }
        return t.Trim(' ', '-', '|', ':', '.', ',');
    }

    public static string CleanArtist(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var a = raw;
        a = Regex.Replace(a, @"(?i)\b(VEVO|Topic|Official|Channel|Music|Records|Entertainment|Films|Series)\b", "");
        return a.Trim(' ', '-', '|', ':', '.', ',');
    }

    public static async Task<IReadOnlyList<LyricLine>?> GetAsync(string title, string artist, double duration)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) return null;

        var cleanTitle = CleanTitle(title);
        var cleanArtist = CleanArtist(artist);
        var key = $"{cleanArtist}\u0001{cleanTitle}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        IReadOnlyList<LyricLine>? result = null;

        // 1. Try LRCLIB for time-synced LRC lyrics
        try
        {
            var searchTitle = !string.IsNullOrWhiteSpace(cleanTitle) ? cleanTitle : title;
            var searchArtist = !string.IsNullOrWhiteSpace(cleanArtist) ? cleanArtist : artist;
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(searchArtist)}&track_name={Uri.EscapeDataString(searchTitle)}";
            if (duration > 0) url += $"&duration={Math.Round(duration).ToString(CultureInfo.InvariantCulture)}";

            using var response = await Http.Client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
                    result = Parse(synced.GetString()!);
            }
        }
        catch (HttpRequestException)
        {
            // Network issue, will try fallback or retry next time
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        // 2. Fallback to JioSaavn for Tamil, Indian, and regional music
        if (result is null || result.Count == 0)
        {
            var query = !string.IsNullOrWhiteSpace(cleanTitle) ? cleanTitle : title;
            result = await GetFromJioSaavnAsync(query, duration);
        }

        Cache[key] = result;
        return result;
    }

    private static async Task<IReadOnlyList<LyricLine>?> GetFromJioSaavnAsync(string query, double duration)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            var searchUrl = $"https://www.jiosaavn.com/api.php?__call=autocomplete.get&query={Uri.EscapeDataString(query)}&_format=json&ctx=web6dot0";
            using var searchResp = await Http.Client.GetAsync(searchUrl);
            if (!searchResp.IsSuccessStatusCode) return null;

            using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
            if (!searchDoc.RootElement.TryGetProperty("songs", out var songs) ||
                !songs.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var song in data.EnumerateArray().Take(4))
            {
                if (!song.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
                    continue;

                var lyricsUrl = $"https://www.jiosaavn.com/api.php?__call=lyrics.getLyrics&lyrics_id={id}&_format=json&ctx=web6dot0";
                using var lyricsResp = await Http.Client.GetAsync(lyricsUrl);
                if (!lyricsResp.IsSuccessStatusCode) continue;

                using var lyricsDoc = JsonDocument.Parse(await lyricsResp.Content.ReadAsStringAsync());
                if (!lyricsDoc.RootElement.TryGetProperty("lyrics", out var rawLyrics) ||
                    rawLyrics.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = rawLyrics.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var cleanHtml = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                cleanHtml = System.Net.WebUtility.HtmlDecode(cleanHtml);
                var rawLines = cleanHtml.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (rawLines.Count == 0) continue;

                var step = duration > 10 ? (duration * 0.90) / rawLines.Count : 3.5;
                var list = new List<LyricLine>(rawLines.Count);
                for (var i = 0; i < rawLines.Count; i++)
                {
                    list.Add(new LyricLine(Math.Round(i * step, 1), rawLines[i]));
                }
                return list;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return null;
    }

    private static List<LyricLine>? Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LrcLine().Match(raw.Trim());
            if (!m.Success) continue;
            var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add(new LyricLine(t, m.Groups[3].Value.Trim()));
        }
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>Index of the line being sung at <paramref name="position"/> seconds, or -1.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, double position)
    {
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time <= position + 0.15) index = i;
            else break;
        }
        return index;
    }
}
