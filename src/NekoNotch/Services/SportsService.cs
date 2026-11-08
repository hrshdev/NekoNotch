using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NekoNotch.Services;

public enum SportType
{
    Football,
    Cricket
}

public sealed class SportMatch
{
    public string Id { get; set; } = "";
    public SportType Sport { get; set; }
    public string LeagueName { get; set; } = "";
    public string LeagueId { get; set; } = "";
    
    public string HomeTeamName { get; set; } = "";
    public string HomeTeamCode { get; set; } = "";
    public string HomeTeamLogo { get; set; } = "";
    public string HomeScore { get; set; } = "0";

    public string AwayTeamName { get; set; } = "";
    public string AwayTeamCode { get; set; } = "";
    public string AwayTeamLogo { get; set; } = "";
    public string AwayScore { get; set; } = "0";

    public string State { get; set; } = "pre"; // pre, in, post
    public string StatusDetail { get; set; } = ""; // 74', FT, 18.2 ov, etc.
    public string Summary { get; set; } = ""; // Context / commentary / outcome
    public DateTime StartTime { get; set; }
    
    public bool IsLive => State.Equals("in", StringComparison.OrdinalIgnoreCase);

    public string ScoreDisplay => Sport == SportType.Football
        ? $"{HomeScore} - {AwayScore}"
        : $"{HomeScore} vs {AwayScore}";
}


public sealed class FootballTimelineEvent
{
    public string Clock { get; set; } = "";
    public string Type { get; set; } = ""; // Goal, Yellow Card, Red Card, Sub
    public string Text { get; set; } = "";
    public bool IsHomeTeam { get; set; }
}

public sealed class FootballMatchStats
{
    public string HomePossession { get; set; } = "50%";
    public string AwayPossession { get; set; } = "50%";
    public string HomeShotsOnTarget { get; set; } = "0";
    public string AwayShotsOnTarget { get; set; } = "0";
    public string HomeTotalShots { get; set; } = "0";
    public string AwayTotalShots { get; set; } = "0";
    public string HomeCorners { get; set; } = "0";
    public string AwayCorners { get; set; } = "0";
    public string HomeFouls { get; set; } = "0";
    public string AwayFouls { get; set; } = "0";
}

public sealed class CricketInningScore
{
    public string TeamName { get; set; } = "";
    public string Runs { get; set; } = "0";
    public string Wickets { get; set; } = "0";
    public string Overs { get; set; } = "0.0";
    public bool IsBatting { get; set; }
    public string Description { get; set; } = "";
}

public sealed class CricketPlayerEntry
{
    public string Name { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class MatchDetail
{
    public string MatchId { get; set; } = "";
    public List<FootballTimelineEvent> FootballEvents { get; set; } = [];
    public FootballMatchStats? FootballStats { get; set; }
    public List<CricketInningScore> CricketInnings { get; set; } = [];
    public List<CricketPlayerEntry> CricketPlayers { get; set; } = [];
}

public sealed class FollowedTeam
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public SportType Sport { get; set; }
    public string LeagueId { get; set; } = "";
    public string LogoUrl { get; set; } = "";
}

public sealed class SportsConfig
{
    public List<FollowedTeam> FollowedTeams { get; set; } = [];
    public string? PinnedMatchId { get; set; }
    public bool ShowScoreInNotch { get; set; } = true;
    public bool CheerOnGoalOrWicket { get; set; } = true;
}

public sealed partial class SportsService : ObservableObject
{
    private static readonly Lazy<SportsService> _lazy = new(() => new SportsService());
    public static SportsService Instance => _lazy.Value;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _pollTimer = new();
    private readonly string _storageFile = "sports.json";

    private SportsConfig _config = new();
    private readonly List<SportMatch> _matches = [];
    private readonly Dictionary<string, (string homeScore, string awayScore)> _prevScores = [];

    [ObservableProperty] private SportMatch? _activeMatch;
    [ObservableProperty] private bool _isMatchLive;
    [ObservableProperty] private string _latestEventBanner = "";

    public event Action<SportMatch, string>? MatchEventOccurred;
    public event Action? MatchesUpdated;

    public IReadOnlyList<SportMatch> Matches => _matches;
    public IReadOnlyList<FollowedTeam> FollowedTeams => _config.FollowedTeams;
    public bool ShowScoreInNotch => _config.ShowScoreInNotch;

    public static readonly IReadOnlyList<FollowedTeam> PresetTeams =
    [
        // Football - Premier League
        new() { Id = "fb-ars", Name = "Arsenal", Code = "ARS", Sport = SportType.Football, LeagueId = "eng.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/359.png" },
        new() { Id = "fb-mci", Name = "Manchester City", Code = "MCI", Sport = SportType.Football, LeagueId = "eng.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/382.png" },
        new() { Id = "fb-liv", Name = "Liverpool", Code = "LIV", Sport = SportType.Football, LeagueId = "eng.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/364.png" },
        new() { Id = "fb-mun", Name = "Manchester United", Code = "MUN", Sport = SportType.Football, LeagueId = "eng.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/360.png" },
        new() { Id = "fb-che", Name = "Chelsea", Code = "CHE", Sport = SportType.Football, LeagueId = "eng.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/363.png" },
        // Football - La Liga, Bundesliga, Serie A, ISL
        new() { Id = "fb-rma", Name = "Real Madrid", Code = "RMA", Sport = SportType.Football, LeagueId = "esp.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/86.png" },
        new() { Id = "fb-bar", Name = "Barcelona", Code = "BAR", Sport = SportType.Football, LeagueId = "esp.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/83.png" },
        new() { Id = "fb-bay", Name = "Bayern Munich", Code = "BAY", Sport = SportType.Football, LeagueId = "ger.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/132.png" },
        new() { Id = "fb-int", Name = "Inter Milan", Code = "INT", Sport = SportType.Football, LeagueId = "ita.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/110.png" },
        new() { Id = "fb-juv", Name = "Juventus", Code = "JUV", Sport = SportType.Football, LeagueId = "ita.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/111.png" },
        new() { Id = "fb-kbf", Name = "Kerala Blasters", Code = "KBFC", Sport = SportType.Football, LeagueId = "ind.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/17708.png" },
        new() { Id = "fb-mbg", Name = "Mohun Bagan SG", Code = "MBSG", Sport = SportType.Football, LeagueId = "ind.1", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/19088.png" },
        // Cricket - International
        new() { Id = "cr-ind", Name = "India", Code = "IND", Sport = SportType.Cricket, LeagueId = "8039", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/6.png" },
        new() { Id = "cr-aus", Name = "Australia", Code = "AUS", Sport = SportType.Cricket, LeagueId = "8039", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/2.png" },
        new() { Id = "cr-eng", Name = "England", Code = "ENG", Sport = SportType.Cricket, LeagueId = "8039", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/1.png" },
        // Cricket - IPL
        new() { Id = "cr-csk", Name = "Chennai Super Kings", Code = "CSK", Sport = SportType.Cricket, LeagueId = "8048", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4342.png" },
        new() { Id = "cr-rcb", Name = "Royal Challengers Bengaluru", Code = "RCB", Sport = SportType.Cricket, LeagueId = "8048", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4340.png" },
        new() { Id = "cr-mi",  Name = "Mumbai Indians", Code = "MI", Sport = SportType.Cricket, LeagueId = "8048", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4346.png" },
        new() { Id = "cr-kkr", Name = "Kolkata Knight Riders", Code = "KKR", Sport = SportType.Cricket, LeagueId = "8048", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4341.png" }
    ];

    private SportsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NekoNotch/1.0");
        LoadConfig();



        _pollTimer.Interval = TimeSpan.FromSeconds(20);
        _pollTimer.Tick += async (_, _) => await RefreshMatchesAsync();
        _pollTimer.Start();

        Task.Run(RefreshMatchesAsync);
    }

    private void LoadConfig()
    {
        try
        {
            _config = Storage.Load<SportsConfig>(_storageFile);
        }
        catch
        {
            _config = new SportsConfig();
        }
    }

    public void SaveConfig()
    {
        Storage.Save(_storageFile, _config);
    }

    public void FollowTeam(FollowedTeam team)
    {
        if (!_config.FollowedTeams.Any(t => t.Id == team.Id))
        {
            _config.FollowedTeams.Add(team);
            SaveConfig();
            Task.Run(RefreshMatchesAsync);
        }
    }

    public void UnfollowTeam(string teamId)
    {
        _config.FollowedTeams.RemoveAll(t => t.Id == teamId);
        SaveConfig();
        Task.Run(RefreshMatchesAsync);
    }

    public void PinMatch(string? matchId)
    {
        if (_config.PinnedMatchId == matchId)
        {
            _config.PinnedMatchId = null;
        }
        else
        {
            _config.PinnedMatchId = matchId;
        }
        SaveConfig();
        UpdateActiveMatch();
        MatchesUpdated?.Invoke();
    }

    public async Task RefreshMatchesAsync()
    {
        var leaguesToQuery = new HashSet<(SportType sport, string leagueId)>();
        foreach (var team in _config.FollowedTeams)
        {
            if (!string.IsNullOrEmpty(team.LeagueId))
                leaguesToQuery.Add((team.Sport, team.LeagueId));
        }

        // Always query top football and cricket leagues
        leaguesToQuery.Add((SportType.Football, "eng.1"));
        leaguesToQuery.Add((SportType.Football, "uefa.champions"));
        leaguesToQuery.Add((SportType.Football, "esp.1"));
        leaguesToQuery.Add((SportType.Football, "ita.1"));
        leaguesToQuery.Add((SportType.Football, "ger.1"));
        leaguesToQuery.Add((SportType.Football, "ind.1"));
        leaguesToQuery.Add((SportType.Cricket, "8039"));
        leaguesToQuery.Add((SportType.Cricket, "8048"));
        leaguesToQuery.Add((SportType.Cricket, "8044"));

        var fetched = new List<SportMatch>();

        foreach (var (sport, leagueId) in leaguesToQuery)
        {
            try
            {
                var matches = await FetchLeagueMatchesAsync(sport, leagueId);
                fetched.AddRange(matches);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        // Deduplicate matches by ID
        var distinctMatches = fetched
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .OrderByDescending(m => m.IsLive)
            .ThenBy(m => m.StartTime)
            .ToList();

        // Check for score changes / events on live matches
        foreach (var m in distinctMatches.Where(x => x.IsLive))
        {
            if (_prevScores.TryGetValue(m.Id, out var prev))
            {
                if (prev.homeScore != m.HomeScore || prev.awayScore != m.AwayScore)
                {
                    // Event detected!
                    string bannerText = m.Sport == SportType.Football
                        ? $"⚽ GOAL! {m.HomeTeamCode} {m.HomeScore} - {m.AwayScore} {m.AwayTeamCode}"
                        : $"🏏 SCORE UPDATE: {m.HomeTeamCode} {m.HomeScore} vs {m.AwayTeamCode} {m.AwayScore}";

                    LatestEventBanner = bannerText;
                    MatchEventOccurred?.Invoke(m, bannerText);
                }
            }
            _prevScores[m.Id] = (m.HomeScore, m.AwayScore);
        }

        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            _matches.Clear();
            _matches.AddRange(distinctMatches);
            UpdateActiveMatch();
            MatchesUpdated?.Invoke();
        });
    }

    private void UpdateActiveMatch()
    {
        SportMatch? selected = null;

        // 1. Pinned match if exists
        if (!string.IsNullOrEmpty(_config.PinnedMatchId))
        {
            selected = _matches.FirstOrDefault(m => m.Id == _config.PinnedMatchId);
        }

        // 2. Live match involving a followed team ONLY
        if (selected == null && _config.FollowedTeams.Count > 0)
        {
            var followedCodes = _config.FollowedTeams.Select(t => t.Code.ToUpperInvariant()).ToHashSet();
            var followedNames = _config.FollowedTeams.Select(t => t.Name.ToUpperInvariant()).ToHashSet();

            selected = _matches.FirstOrDefault(m => m.IsLive &&
                (followedCodes.Contains(m.HomeTeamCode.ToUpperInvariant()) ||
                 followedCodes.Contains(m.AwayTeamCode.ToUpperInvariant()) ||
                 followedNames.Any(n => m.HomeTeamName.ToUpperInvariant().Contains(n)) ||
                 followedNames.Any(n => m.AwayTeamName.ToUpperInvariant().Contains(n))));
        }

        // NO fallback to random matches! Only show if pinned or if followed team is live.

        ActiveMatch = selected;
        IsMatchLive = selected?.IsLive == true;
    }

    private async Task<List<SportMatch>> FetchLeagueMatchesAsync(SportType sport, string leagueId)
    {
        var result = new List<SportMatch>();
        string url = sport == SportType.Football
            ? $"https://site.api.espn.com/apis/site/v2/sports/soccer/{leagueId}/scoreboard"
            : $"https://site.api.espn.com/apis/site/v2/sports/cricket/{leagueId}/scoreboard";

        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string leagueName = "";
        if (root.TryGetProperty("leagues", out var leagues) && leagues.GetArrayLength() > 0)
        {
            leagueName = leagues[0].GetProperty("name").GetString() ?? "";
        }

        if (!root.TryGetProperty("events", out var events)) return result;

        foreach (var ev in events.EnumerateArray())
        {
            if (!ev.TryGetProperty("id", out var idProp)) continue;
            var id = idProp.GetString() ?? Guid.NewGuid().ToString();

            if (!ev.TryGetProperty("status", out var statusObj)) continue;
            var state = "pre";
            var detail = "";
            if (statusObj.TryGetProperty("type", out var typeObj))
            {
                if (typeObj.TryGetProperty("state", out var stProp))
                    state = stProp.GetString() ?? "pre";
                if (typeObj.TryGetProperty("detail", out var dProp))
                    detail = dProp.GetString() ?? "";
            }

            var displayClock = statusObj.TryGetProperty("displayClock", out var dc) ? dc.GetString() ?? "" : "";
            var summary = statusObj.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";

            DateTime startTime = DateTime.UtcNow;
            if (ev.TryGetProperty("date", out var dtProp) && DateTime.TryParse(dtProp.GetString(), out var parsedDate))
            {
                startTime = parsedDate;
            }

            if (!ev.TryGetProperty("competitions", out var competitions) || competitions.GetArrayLength() == 0) continue;
            var comp = competitions[0];
            if (!comp.TryGetProperty("competitors", out var competitors) || competitors.GetArrayLength() < 2) continue;

            var comp1 = competitors[0];
            var comp2 = competitors[1];

            var isComp1Home = comp1.TryGetProperty("homeAway", out var ha) && ha.GetString() == "home";
            var home = isComp1Home ? comp1 : comp2;
            var away = isComp1Home ? comp2 : comp1;

            if (!home.TryGetProperty("team", out var homeTeam) || !away.TryGetProperty("team", out var awayTeam)) continue;

            var homeName = homeTeam.TryGetProperty("displayName", out var hdn) ? hdn.GetString() ?? "" : (homeTeam.TryGetProperty("name", out var hn) ? hn.GetString() ?? "" : "");
            var awayName = awayTeam.TryGetProperty("displayName", out var adn) ? adn.GetString() ?? "" : (awayTeam.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "");

            var homeCode = homeTeam.TryGetProperty("abbreviation", out var hab) ? hab.GetString() ?? "" : (homeName.Length > 0 ? homeName[..Math.Min(3, homeName.Length)].ToUpperInvariant() : "HOM");
            var awayCode = awayTeam.TryGetProperty("abbreviation", out var aab) ? aab.GetString() ?? "" : (awayName.Length > 0 ? awayName[..Math.Min(3, awayName.Length)].ToUpperInvariant() : "AWY");

            var homeLogo = homeTeam.TryGetProperty("logo", out var hl) ? hl.GetString() ?? "" : "";
            var awayLogo = awayTeam.TryGetProperty("logo", out var al) ? al.GetString() ?? "" : "";

            var homeScore = home.TryGetProperty("score", out var hs) ? hs.GetString() ?? "0" : "0";
            var awayScore = away.TryGetProperty("score", out var ascore) ? ascore.GetString() ?? "0" : "0";

            if (string.IsNullOrWhiteSpace(homeScore)) homeScore = "-";
            if (string.IsNullOrWhiteSpace(awayScore)) awayScore = "-";

            string statusText = state == "in"
                ? (sport == SportType.Football ? (string.IsNullOrEmpty(displayClock) ? "LIVE" : displayClock) : (string.IsNullOrEmpty(detail) ? "LIVE" : detail))
                : (string.IsNullOrEmpty(detail) ? (startTime > DateTime.UtcNow ? startTime.ToLocalTime().ToString("HH:mm") : "FT") : detail);

            result.Add(new SportMatch
            {
                Id = id,
                Sport = sport,
                LeagueName = string.IsNullOrEmpty(leagueName) ? (sport == SportType.Football ? "Football" : "Cricket") : leagueName,
                LeagueId = leagueId,
                HomeTeamName = homeName,
                HomeTeamCode = homeCode,
                HomeTeamLogo = homeLogo,
                HomeScore = homeScore,
                AwayTeamName = awayName,
                AwayTeamCode = awayCode,
                AwayTeamLogo = awayLogo,
                AwayScore = awayScore,
                State = state,
                StatusDetail = statusText,
                Summary = summary,
                StartTime = startTime
            });
        }

        return result;
    }

    public async Task<MatchDetail?> FetchMatchDetailAsync(SportMatch match)
    {
        try
        {
            var detail = new MatchDetail { MatchId = match.Id };
            string url = match.Sport == SportType.Football
                ? $"https://site.api.espn.com/apis/site/v2/sports/soccer/{match.LeagueId}/summary?event={match.Id}"
                : $"https://site.api.espn.com/apis/site/v2/sports/cricket/{match.LeagueId}/summary?event={match.Id}";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (match.Sport == SportType.Football)
            {
                if (root.TryGetProperty("keyEvents", out var keList))
                {
                    foreach (var k in keList.EnumerateArray())
                    {
                        var text = k.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                        var typeObj = k.TryGetProperty("type", out var to) ? to : default;
                        var typeText = typeObj.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        var clock = k.TryGetProperty("clock", out var clk) && clk.TryGetProperty("displayValue", out var dv)
                            ? dv.GetString() ?? "" : "";

                        bool isHome = true;
                        if (k.TryGetProperty("team", out var tObj) && tObj.TryGetProperty("displayName", out var dn))
                        {
                            var teamName = dn.GetString() ?? "";
                            isHome = !string.IsNullOrEmpty(teamName) && match.HomeTeamName.Contains(teamName, StringComparison.OrdinalIgnoreCase);
                        }

                        if (typeText.Contains("Goal", StringComparison.OrdinalIgnoreCase) ||
                            typeText.Contains("Card", StringComparison.OrdinalIgnoreCase) ||
                            typeText.Contains("Substitution", StringComparison.OrdinalIgnoreCase) ||
                            typeText.Contains("Penalty", StringComparison.OrdinalIgnoreCase))
                        {
                            detail.FootballEvents.Add(new FootballTimelineEvent
                            {
                                Clock = clock,
                                Type = typeText,
                                Text = text,
                                IsHomeTeam = isHome
                            });
                        }
                    }
                }

                if (root.TryGetProperty("boxscore", out var box) && box.TryGetProperty("teams", out var teams) && teams.GetArrayLength() >= 2)
                {
                    var stats = new FootballMatchStats();
                    for (int i = 0; i < 2; i++)
                    {
                        var t = teams[i];
                        bool isHome = i == 0;
                        if (t.TryGetProperty("team", out var tm) && tm.TryGetProperty("homeAway", out var ha))
                        {
                            isHome = ha.GetString() == "home";
                        }
                        if (t.TryGetProperty("statistics", out var statsList))
                        {
                            foreach (var s in statsList.EnumerateArray())
                            {
                                var name = s.TryGetProperty("name", out var np) ? np.GetString() : "";
                                var val = s.TryGetProperty("displayValue", out var vp) ? vp.GetString() ?? "0" : "0";
                                switch (name)
                                {
                                    case "possessionPct":
                                        if (isHome) stats.HomePossession = val + "%"; else stats.AwayPossession = val + "%";
                                        break;
                                    case "shotsOnTarget":
                                        if (isHome) stats.HomeShotsOnTarget = val; else stats.AwayShotsOnTarget = val;
                                        break;
                                    case "totalShots":
                                        if (isHome) stats.HomeTotalShots = val; else stats.AwayTotalShots = val;
                                        break;
                                    case "wonCorners":
                                        if (isHome) stats.HomeCorners = val; else stats.AwayCorners = val;
                                        break;
                                    case "foulsCommitted":
                                        if (isHome) stats.HomeFouls = val; else stats.AwayFouls = val;
                                        break;
                                }
                            }
                        }
                    }
                    detail.FootballStats = stats;
                }
            }
            else // Cricket
            {
                if (root.TryGetProperty("header", out var hdr) && hdr.TryGetProperty("competitions", out var comps) && comps.GetArrayLength() > 0)
                {
                    var comp = comps[0];
                    if (comp.TryGetProperty("competitors", out var competitors))
                    {
                        foreach (var competitor in competitors.EnumerateArray())
                        {
                            var tName = competitor.TryGetProperty("team", out var tm) && tm.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                            if (competitor.TryGetProperty("linescores", out var linescores))
                            {
                                foreach (var ls in linescores.EnumerateArray())
                                {
                                    var r = ls.TryGetProperty("runs", out var rp) ? rp.GetInt32().ToString() : "0";
                                    var w = ls.TryGetProperty("wickets", out var wp) ? wp.GetInt32().ToString() : "0";
                                    var o = ls.TryGetProperty("overs", out var op) ? op.GetDouble().ToString("0.0") : "0.0";
                                    var isBatting = ls.TryGetProperty("isBatting", out var bp) && bp.GetBoolean();
                                    var desc = ls.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "";
                                    detail.CricketInnings.Add(new CricketInningScore
                                    {
                                        TeamName = tName,
                                        Runs = r,
                                        Wickets = w,
                                        Overs = o,
                                        IsBatting = isBatting,
                                        Description = desc
                                    });
                                }
                            }
                        }
                    }
                }

                if (root.TryGetProperty("rosters", out var rosters))
                {
                    foreach (var r in rosters.EnumerateArray())
                    {
                        var tName = r.TryGetProperty("team", out var tm) && tm.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                        if (r.TryGetProperty("roster", out var rosterList))
                        {
                            foreach (var p in rosterList.EnumerateArray())
                            {
                                var pName = p.TryGetProperty("athlete", out var ath) && ath.TryGetProperty("displayName", out var pdn) ? pdn.GetString() ?? "" : "";
                                var role = p.TryGetProperty("position", out var pos) && pos.TryGetProperty("displayName", out var rdn) ? rdn.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(pName))
                                {
                                    detail.CricketPlayers.Add(new CricketPlayerEntry
                                    {
                                        Name = pName,
                                        TeamName = tName,
                                        Role = role
                                    });
                                }
                            }
                        }
                    }
                }
            }

            return detail;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

}
