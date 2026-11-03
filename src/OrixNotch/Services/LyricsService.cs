using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrixNotch.Services;

public sealed record LyricLine(double Time, string Text);
public sealed record TrackLyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced);

/// <summary>
/// Looks up lyrics for the current track with strict title/movie validation:
/// 1. LRCLIB (https://lrclib.net) for precision time-synced .lrc lyrics.
/// 2. JioSaavn API for extensive Tamil, Telugu, Hindi and Indian regional songs.
/// Distinguishes between time-synced lyrics and plain text reader lyrics.
/// </summary>
public static partial class LyricsService
{
    private static readonly Dictionary<string, TrackLyrics?> Cache = new();

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    public static string CleanTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw;

        // If title contains pipe separators (e.g. YouTube format "Song | Movie | Artists"), take first part
        var pipeIndex = t.IndexOf('|');
        if (pipeIndex > 0)
        {
            t = t[..pipeIndex];
        }

        t = Regex.Replace(t, @"(?i)\s*-\s*(?:2k|4k|8k|hd|uhd)?\s*(?:video|audio|lyric|lyrical)?\s*song.*", "");
        t = Regex.Replace(t, @"(?i)\b(?:HD|2K|4K|8K|UHD)\s+(?:Video|Audio)(?:\s+Song)?\b", "");
        t = Regex.Replace(t, @"(?i)\b(?:Official|Full)\s+(?:Video|Audio|Music\s+Video|Song|Lyrical|Track|Visualizer)(?:\s+Song)?\b", "");
        t = Regex.Replace(t, @"(?i)\b(?:Video\s+Song|Lyric\s+Video|Lyrical\s+Video|Audio\s+Song|Remix|Cover|Reprise)\b", "");
        t = Regex.Replace(t, @"\[.*?\]|\(.*?\)", "");

        return t.Trim(' ', '-', '|', ':', '.', ',');
    }

    public static string? ExtractMovie(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains('|')) return null;

        var parts = raw.Split('|');
        for (var i = 1; i < parts.Length; i++)
        {
            var seg = parts[i].Trim();
            if (string.IsNullOrWhiteSpace(seg)) continue;

            // Skip non-latin segments (such as pure Tamil script titles)
            if (Regex.IsMatch(seg, @"[\u0B80-\u0BFF]")) continue;

            // Skip common label, channel, or video metadata words
            if (Regex.IsMatch(seg, @"(?i)\b(?:channel|music|records|audio|video|official|hd|4k|2k|t-series|sony|saregama|ayngaran|tips|aditya|star\s*music|think\s*music|sun\s*pictures)\b"))
                continue;

            // First clean alphanumeric phrase of reasonable length is the movie/album name
            if (seg.Length >= 2 && seg.Length <= 40)
            {
                return seg;
            }
        }
        return null;
    }

    public static string CleanArtist(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var a = raw;
        a = Regex.Replace(a, @"(?i)\b(?:VEVO|Topic|Official|Channel|Music|Records|Entertainment|Films|Series)\b", "");
        return a.Trim(' ', '-', '|', ':', '.', ',');
    }

    public static async Task<TrackLyrics?> GetAsync(string title, string artist, double duration)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) return null;

        var cleanTitle = CleanTitle(title);
        var movie = ExtractMovie(title);
        var cleanArtist = CleanArtist(artist);
        var key = $"{movie}\u0001{cleanArtist}\u0001{cleanTitle}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        TrackLyrics? result = null;

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
                {
                    var parsed = Parse(synced.GetString()!);
                    if (parsed is { Count: > 0 })
                    {
                        result = new TrackLyrics(parsed, true);
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Network issue, will try fallback
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        // 2. Fallback to JioSaavn with strict movie and title matching
        if (result is null || result.Lines.Count == 0)
        {
            result = await GetFromJioSaavnAsync(cleanTitle, movie, cleanArtist, duration);
        }

        Cache[key] = result;
        return result;
    }

    private static async Task<TrackLyrics?> GetFromJioSaavnAsync(string title, string? movie, string? artist, double duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Try queries in order of precision: Title + Movie -> Title + Artist -> Title
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(movie))
        {
            queries.Add($"{title} {movie}");
        }
        if (!string.IsNullOrWhiteSpace(artist) && artist.Length > 2)
        {
            queries.Add($"{title} {artist}");
        }
        queries.Add(title);

        foreach (var query in queries)
        {
            try
            {
                var searchUrl = $"https://www.jiosaavn.com/api.php?__call=autocomplete.get&query={Uri.EscapeDataString(query)}&_format=json&ctx=web6dot0";
                using var searchResp = await Http.Client.GetAsync(searchUrl);
                if (!searchResp.IsSuccessStatusCode) continue;

                using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
                if (!searchDoc.RootElement.TryGetProperty("songs", out var songs) ||
                    !songs.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var song in data.EnumerateArray().Take(6))
                {
                    if (!song.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
                        continue;

                    var candTitle = song.TryGetProperty("title", out var tp) ? tp.GetString() : null;
                    var candDesc = song.TryGetProperty("description", out var dp) ? dp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(candTitle)) continue;

                    // Strict Movie Validation: If movie was extracted, the candidate description MUST contain it
                    if (!string.IsNullOrWhiteSpace(movie))
                    {
                        var normMovie = Regex.Replace(movie.ToLowerInvariant(), @"[^a-z0-9]", "");
                        var normDesc = Regex.Replace((candDesc ?? "").ToLowerInvariant(), @"[^a-z0-9]", "");
                        if (!normDesc.Contains(normMovie))
                        {
                            // Belongs to another movie/album (e.g. Kannupada Pokuthaiya vs Poove Unakkaga). Reject!
                            continue;
                        }
                    }

                    // Strict Title Validation: Verify candidate title shares root stem with requested title
                    var normReqTitle = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]", "");
                    var normCandTitle = Regex.Replace(candTitle.ToLowerInvariant(), @"[^a-z0-9]", "");
                    var minLen = Math.Min(normReqTitle.Length, normCandTitle.Length);
                    if (minLen < 3) continue;

                    var prefixLen = Math.Min(4, minLen);
                    var stemReq = normReqTitle[..prefixLen];
                    var stemCand = normCandTitle[..prefixLen];
                    if (!normCandTitle.Contains(stemReq) && !normReqTitle.Contains(stemCand))
                    {
                        // Completely different song title (e.g. Sollamale vs Anantham). Reject!
                        continue;
                    }

                    // Candidate is verified! Now check if lyrics exist for THIS verified song
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

                    var list = new List<LyricLine>(rawLines.Count);
                    for (var i = 0; i < rawLines.Count; i++)
                    {
                        list.Add(new LyricLine(0, rawLines[i]));
                    }
                    return new TrackLyrics(list, false);
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
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
