using System;
using System.Collections.Concurrent;
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

public enum SportsCelebrationType
{
    Goal,
    Wicket,
    Six,
    Four,
    Milestone50,
    Milestone100
}

public sealed class SportsCelebrationEvent
{
    public SportMatch Match { get; init; } = null!;
    public SportsCelebrationType Type { get; init; }
    public string BadgeText { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Emoji { get; init; } = "🏆";
    public string BadgeBackgroundHex { get; init; } = "#35FF8A3D";
    public string BadgeForegroundHex { get; init; } = "#FF8A3D";
}

internal sealed class MatchScoreSnapshot
{
    public string HomeRaw { get; set; } = "";
    public string AwayRaw { get; set; } = "";
    public int HomeRuns { get; set; }
    public int HomeWickets { get; set; }
    public int AwayRuns { get; set; }
    public int AwayWickets { get; set; }
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }
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

    public string CompactHomeScore => GetCompactScore(HomeScore);
    public string CompactAwayScore => GetCompactScore(AwayScore);

    private static string GetCompactScore(string score)
    {
        if (string.IsNullOrWhiteSpace(score) || score == "-") return "-";
        var s = score.Trim();
        if (s.Contains('&'))
        {
            var parts = s.Split('&');
            s = parts[^1].Trim();
        }
        if (s.Contains('('))
        {
            s = s.Split('(')[0].Trim();
        }
        return s.Length > 9 ? s[..9] : s;
    }
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
    public string Category { get; set; } = "";
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
    private readonly ConcurrentDictionary<string, MatchScoreSnapshot> _prevSnapshots = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    [ObservableProperty] private SportMatch? _activeMatch;
    [ObservableProperty] private bool _isMatchLive;
    [ObservableProperty] private string _latestEventBanner = "";

    public event Action<SportMatch, string>? MatchEventOccurred;
    public event Action<SportsCelebrationEvent>? CelebrationOccurred;
    public event Action? MatchesUpdated;

    public IReadOnlyList<SportMatch> Matches => _matches;
    public IReadOnlyList<FollowedTeam> FollowedTeams => _config.FollowedTeams;
    public bool ShowScoreInNotch => _config.ShowScoreInNotch;

    public static readonly IReadOnlyList<FollowedTeam> PresetTeams =
    [
        // Football - Premier League
        new() { Id = "fb-ars", Name = "Arsenal", Code = "ARS", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/359.png" },
        new() { Id = "fb-mci", Name = "Manchester City", Code = "MCI", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/382.png" },
        new() { Id = "fb-liv", Name = "Liverpool", Code = "LIV", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/364.png" },
        new() { Id = "fb-mun", Name = "Manchester United", Code = "MUN", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/360.png" },
        new() { Id = "fb-che", Name = "Chelsea", Code = "CHE", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/363.png" },
        new() { Id = "fb-tot", Name = "Tottenham Hotspur", Code = "TOT", Sport = SportType.Football, LeagueId = "eng.1", Category = "Premier League", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/367.png" },

        // Football - La Liga
        new() { Id = "fb-rma", Name = "Real Madrid", Code = "RMA", Sport = SportType.Football, LeagueId = "esp.1", Category = "La Liga", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/86.png" },
        new() { Id = "fb-bar", Name = "Barcelona", Code = "BAR", Sport = SportType.Football, LeagueId = "esp.1", Category = "La Liga", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/83.png" },
        new() { Id = "fb-atm", Name = "Atletico Madrid", Code = "ATM", Sport = SportType.Football, LeagueId = "esp.1", Category = "La Liga", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/1068.png" },

        // Football - European Giants
        new() { Id = "fb-bay", Name = "Bayern Munich", Code = "BAY", Sport = SportType.Football, LeagueId = "ger.1", Category = "European Giants", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/132.png" },
        new() { Id = "fb-bvb", Name = "Borussia Dortmund", Code = "BVB", Sport = SportType.Football, LeagueId = "ger.1", Category = "European Giants", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/124.png" },
        new() { Id = "fb-psg", Name = "Paris Saint-Germain", Code = "PSG", Sport = SportType.Football, LeagueId = "fra.1", Category = "European Giants", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/160.png" },
        new() { Id = "fb-int", Name = "Inter Milan", Code = "INT", Sport = SportType.Football, LeagueId = "ita.1", Category = "European Giants", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/110.png" },
        new() { Id = "fb-juv", Name = "Juventus", Code = "JUV", Sport = SportType.Football, LeagueId = "ita.1", Category = "European Giants", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/111.png" },

        // Football - Indian Super League
        new() { Id = "fb-kbf", Name = "Kerala Blasters", Code = "KBFC", Sport = SportType.Football, LeagueId = "ind.1", Category = "Indian Super League (ISL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/17708.png" },
        new() { Id = "fb-mbg", Name = "Mohun Bagan SG", Code = "MBSG", Sport = SportType.Football, LeagueId = "ind.1", Category = "Indian Super League (ISL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/19088.png" },

        // Football - National Teams
        new() { Id = "fb-arg", Name = "Argentina", Code = "ARG", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/202.png" },
        new() { Id = "fb-bra", Name = "Brazil", Code = "BRA", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/205.png" },
        new() { Id = "fb-fra", Name = "France", Code = "FRA", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/476.png" },
        new() { Id = "fb-esp", Name = "Spain", Code = "ESP", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/164.png" },
        new() { Id = "fb-eng", Name = "England", Code = "ENG", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/448.png" },
        new() { Id = "fb-por", Name = "Portugal", Code = "POR", Sport = SportType.Football, LeagueId = "fifa.world", Category = "National Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/soccer/500/468.png" },

        // Cricket - International Teams
        new() { Id = "cr-ind", Name = "India", Code = "IND", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/6.png" },
        new() { Id = "cr-aus", Name = "Australia", Code = "AUS", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/2.png" },
        new() { Id = "cr-eng", Name = "England", Code = "ENG", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/1.png" },
        new() { Id = "cr-pak", Name = "Pakistan", Code = "PAK", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/7.png" },
        new() { Id = "cr-sa",  Name = "South Africa", Code = "SA", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/3.png" },
        new() { Id = "cr-nz",  Name = "New Zealand", Code = "NZ", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/5.png" },
        new() { Id = "cr-wi",  Name = "West Indies", Code = "WI", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4.png" },
        new() { Id = "cr-ban", Name = "Bangladesh", Code = "BAN", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/25.png" },
        new() { Id = "cr-sl",  Name = "Sri Lanka", Code = "SL", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/8.png" },
        new() { Id = "cr-afg", Name = "Afghanistan", Code = "AFG", Sport = SportType.Cricket, LeagueId = "cricket", Category = "International Teams", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/40.png" },

        // Cricket - Indian Premier League (IPL)
        new() { Id = "cr-csk", Name = "Chennai Super Kings", Code = "CSK", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4342.png" },
        new() { Id = "cr-rcb", Name = "Royal Challengers Bengaluru", Code = "RCB", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4340.png" },
        new() { Id = "cr-mi",  Name = "Mumbai Indians", Code = "MI", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4346.png" },
        new() { Id = "cr-kkr", Name = "Kolkata Knight Riders", Code = "KKR", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4341.png" },
        new() { Id = "cr-srh", Name = "Sunrisers Hyderabad", Code = "SRH", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/5143.png" },
        new() { Id = "cr-gt",  Name = "Gujarat Titans", Code = "GT", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/1298423.png" },
        new() { Id = "cr-rr",  Name = "Rajasthan Royals", Code = "RR", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4345.png" },
        new() { Id = "cr-dc",  Name = "Delhi Capitals", Code = "DC", Sport = SportType.Cricket, LeagueId = "8048", Category = "Indian Premier League (IPL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/4344.png" },

        // Cricket - Big Bash League (BBL)
        new() { Id = "cr-per", Name = "Perth Scorchers", Code = "PER", Sport = SportType.Cricket, LeagueId = "8044", Category = "Big Bash League (BBL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/5239.png" },
        new() { Id = "cr-syd", Name = "Sydney Sixers", Code = "SYS", Sport = SportType.Cricket, LeagueId = "8044", Category = "Big Bash League (BBL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/5241.png" },
        new() { Id = "cr-mel", Name = "Melbourne Renegades", Code = "MLR", Sport = SportType.Cricket, LeagueId = "8044", Category = "Big Bash League (BBL)", LogoUrl = "https://a.espncdn.com/i/teamlogos/cricket/500/5237.png" }
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
        if (!await _refreshLock.WaitAsync(0)) return;
        try
        {
            var fetched = new List<SportMatch>();

        // 1. Fetch Global Cricket Matches (Live, series, tours, leagues)
        try
        {
            var cricketMatches = await FetchGlobalCricketMatchesAsync();
            fetched.AddRange(cricketMatches);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        // 2. Fetch Football Matches (Top global leagues + followed leagues)
        var footballLeagues = new HashSet<string>
        {
            "eng.1",           // Premier League
            "uefa.champions",  // Champions League
            "esp.1",           // La Liga
            "ita.1",           // Serie A
            "ger.1",           // Bundesliga
            "ind.1",           // Indian Super League
            "fra.1",           // Ligue 1
            "usa.1"            // MLS
        };

        foreach (var team in _config.FollowedTeams)
        {
            if (team.Sport == SportType.Football && !string.IsNullOrEmpty(team.LeagueId))
                footballLeagues.Add(team.LeagueId);
        }

        foreach (var leagueId in footballLeagues)
        {
            try
            {
                var matches = await FetchFootballLeagueMatchesAsync(leagueId);
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

        // Check for celebrations / events on live matches
        foreach (var m in distinctMatches.Where(x => x.IsLive))
        {
            if (_prevSnapshots.TryGetValue(m.Id, out var prev))
            {
                if (m.Sport == SportType.Football)
                {
                    int.TryParse(m.HomeScore, out var currHome);
                    int.TryParse(m.AwayScore, out var currAway);

                    bool homeGoal = currHome > prev.HomeGoals;
                    bool awayGoal = currAway > prev.AwayGoals;

                    if (homeGoal || awayGoal)
                    {
                        var scoringTeam = homeGoal ? m.HomeTeamName : m.AwayTeamName;
                        var time = string.IsNullOrWhiteSpace(m.StatusDetail) ? "" : $" ({m.StatusDetail})";
                        var ev = new SportsCelebrationEvent
                        {
                            Match = m,
                            Type = SportsCelebrationType.Goal,
                            BadgeText = "⚽ GOAL!",
                            Title = $"⚽ GOAL! - {scoringTeam}",
                            Detail = $"{m.HomeTeamCode} {m.HomeScore} - {m.AwayScore} {m.AwayTeamCode}{time}",
                            Emoji = "⚽",
                            BadgeBackgroundHex = "#3530D158",
                            BadgeForegroundHex = "#30D158"
                        };
                        LatestEventBanner = ev.BadgeText;
                        CelebrationOccurred?.Invoke(ev);
                        MatchEventOccurred?.Invoke(m, ev.BadgeText);
                    }
                }
                else if (m.Sport == SportType.Cricket)
                {
                    var (hRuns, hWickets, hValid) = ParseCricketScore(m.HomeScore);
                    var (aRuns, aWickets, aValid) = ParseCricketScore(m.AwayScore);

                    CheckCricketCelebration(m, isHome: true, prev.HomeRuns, prev.HomeWickets, hRuns, hWickets, hValid, prev.HomeRaw != m.HomeScore);
                    CheckCricketCelebration(m, isHome: false, prev.AwayRuns, prev.AwayWickets, aRuns, aWickets, aValid, prev.AwayRaw != m.AwayScore);
                }
            }

            var snap = new MatchScoreSnapshot
            {
                HomeRaw = m.HomeScore,
                AwayRaw = m.AwayScore
            };
            if (m.Sport == SportType.Football)
            {
                if (int.TryParse(m.HomeScore, out var hg)) snap.HomeGoals = hg;
                if (int.TryParse(m.AwayScore, out var ag)) snap.AwayGoals = ag;
            }
            else
            {
                var (hRuns, hWickets, _) = ParseCricketScore(m.HomeScore);
                var (aRuns, aWickets, _) = ParseCricketScore(m.AwayScore);
                snap.HomeRuns = hRuns;
                snap.HomeWickets = hWickets;
                snap.AwayRuns = aRuns;
                snap.AwayWickets = aWickets;
            }
            _prevSnapshots[m.Id] = snap;
        }

        var app = System.Windows.Application.Current;
        if (app != null && app.Dispatcher != null)
        {
            app.Dispatcher.Invoke(() =>
            {
                _matches.Clear();
                _matches.AddRange(distinctMatches);
                UpdateActiveMatch();
                MatchesUpdated?.Invoke();
            });
        }
        else
        {
            lock (_matches)
            {
                _matches.Clear();
                _matches.AddRange(distinctMatches);
                UpdateActiveMatch();
                MatchesUpdated?.Invoke();
            }
        }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _refreshLock.Release();
        }
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

    private async Task<List<SportMatch>> FetchFootballLeagueMatchesAsync(string leagueId)
    {
        var result = new List<SportMatch>();
        string url = $"https://site.api.espn.com/apis/site/v2/sports/soccer/{leagueId}/scoreboard";

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
                ? (string.IsNullOrEmpty(displayClock) ? "LIVE" : displayClock)
                : (string.IsNullOrEmpty(detail) ? (startTime > DateTime.UtcNow ? startTime.ToLocalTime().ToString("HH:mm") : "FT") : detail);

            result.Add(new SportMatch
            {
                Id = id,
                Sport = SportType.Football,
                LeagueName = string.IsNullOrEmpty(leagueName) ? "Football" : leagueName,
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

    private async Task<List<SportMatch>> FetchGlobalCricketMatchesAsync()
    {
        var result = new List<SportMatch>();
        try
        {
            var url = "https://site.api.espn.com/apis/personalized/v2/scoreboard/header?sport=cricket&region=in&lang=en";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("sports", out var sports)) return result;

            foreach (var sport in sports.EnumerateArray())
            {
                if (!sport.TryGetProperty("leagues", out var leagues)) continue;
                foreach (var league in leagues.EnumerateArray())
                {
                    var leagueName = league.TryGetProperty("name", out var ln) ? ln.GetString() ?? "Cricket" : "Cricket";
                    var leagueId = league.TryGetProperty("id", out var lid) ? lid.GetString() ?? "cricket" : "cricket";

                    if (!league.TryGetProperty("events", out var events)) continue;
                    foreach (var ev in events.EnumerateArray())
                    {
                        if (!ev.TryGetProperty("id", out var idProp)) continue;
                        var id = idProp.GetString() ?? Guid.NewGuid().ToString();

                        var state = "pre";
                        if (ev.TryGetProperty("status", out var stProp))
                            state = stProp.GetString() ?? "pre";

                        var summary = ev.TryGetProperty("summary", out var smProp) ? smProp.GetString() ?? "" : "";
                        var detail = "";

                        if (ev.TryGetProperty("fullStatus", out var fs))
                        {
                            if (fs.TryGetProperty("type", out var tProp) && tProp.TryGetProperty("detail", out var dProp))
                                detail = dProp.GetString() ?? "";
                            if (string.IsNullOrEmpty(summary) && fs.TryGetProperty("summary", out var fsm))
                                summary = fsm.GetString() ?? "";
                        }

                        DateTime startTime = DateTime.UtcNow;
                        if (ev.TryGetProperty("date", out var dtProp) && DateTime.TryParse(dtProp.GetString(), out var parsedDate))
                            startTime = parsedDate;

                        if (!ev.TryGetProperty("competitors", out var comps) || comps.GetArrayLength() < 2) continue;
                        var c1 = comps[0];
                        var c2 = comps[1];

                        var isC1Home = c1.TryGetProperty("homeAway", out var ha) && ha.GetString() == "home";
                        var home = isC1Home ? c1 : c2;
                        var away = isC1Home ? c2 : c1;

                        var homeName = home.TryGetProperty("displayName", out var hdn) ? hdn.GetString() ?? "" : (home.TryGetProperty("name", out var hn) ? hn.GetString() ?? "" : "");
                        var awayName = away.TryGetProperty("displayName", out var adn) ? adn.GetString() ?? "" : (away.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "");

                        var homeCode = home.TryGetProperty("abbreviation", out var hab) ? hab.GetString() ?? "" : (homeName.Length > 0 ? homeName[..Math.Min(3, homeName.Length)].ToUpperInvariant() : "HOM");
                        var awayCode = away.TryGetProperty("abbreviation", out var aab) ? aab.GetString() ?? "" : (awayName.Length > 0 ? awayName[..Math.Min(3, awayName.Length)].ToUpperInvariant() : "AWY");

                        var homeLogo = home.TryGetProperty("logo", out var hl) ? hl.GetString() ?? "" : "";
                        var awayLogo = away.TryGetProperty("logo", out var al) ? al.GetString() ?? "" : "";

                        var homeScore = home.TryGetProperty("score", out var hs) ? hs.GetString() ?? "" : "";
                        var awayScore = away.TryGetProperty("score", out var ascore) ? ascore.GetString() ?? "" : "";

                        if (string.IsNullOrWhiteSpace(homeScore)) homeScore = "-";
                        if (string.IsNullOrWhiteSpace(awayScore)) awayScore = "-";

                        var statusText = state == "in"
                            ? (string.IsNullOrEmpty(detail) ? (string.IsNullOrEmpty(summary) ? "LIVE" : summary) : detail)
                            : (string.IsNullOrEmpty(detail) ? (startTime > DateTime.UtcNow ? startTime.ToLocalTime().ToString("HH:mm") : "FT") : detail);

                        result.Add(new SportMatch
                        {
                            Id = id,
                            Sport = SportType.Cricket,
                            LeagueName = leagueName,
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
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
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

    public static (int runs, int wickets, bool valid) ParseCricketScore(string? score)
    {
        if (string.IsNullOrWhiteSpace(score) || score == "-") return (0, 0, false);
        var s = score.Trim();
        if (s.Contains('&'))
        {
            var parts = s.Split('&');
            s = parts[^1].Trim();
        }
        if (s.Contains('('))
        {
            s = s.Split('(')[0].Trim();
        }
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\d/]", "").Trim();
        if (string.IsNullOrEmpty(s)) return (0, 0, false);

        if (s.Contains('/'))
        {
            var slashParts = s.Split('/');
            if (int.TryParse(slashParts[0], out var r))
            {
                int w = 0;
                if (slashParts.Length > 1) int.TryParse(slashParts[1], out w);
                return (r, w, true);
            }
        }
        else
        {
            if (int.TryParse(s, out var r))
            {
                return (r, 0, true);
            }
        }
        return (0, 0, false);
    }

    private void CheckCricketCelebration(SportMatch m, bool isHome, int prevRuns, int prevWickets, int currRuns, int currWickets, bool valid, bool rawChanged)
    {
        if (!valid || !rawChanged) return;
        if (currRuns < prevRuns) return; // Innings rollover

        var teamName = isHome ? m.HomeTeamName : m.AwayTeamName;
        var teamCode = isHome ? m.HomeTeamCode : m.AwayTeamCode;
        var currScoreDisplay = isHome ? m.HomeScore : m.AwayScore;
        var matchSummary = $"{m.HomeTeamCode} {m.CompactHomeScore} vs {m.AwayTeamCode} {m.CompactAwayScore}";

        // 1. Wicket!
        if (currWickets > prevWickets)
        {
            var ev = new SportsCelebrationEvent
            {
                Match = m,
                Type = SportsCelebrationType.Wicket,
                BadgeText = "🔴 WICKET!",
                Title = $"🔴 WICKET! - {teamName}",
                Detail = $"{teamCode} {currScoreDisplay} • {matchSummary}",
                Emoji = "🔴",
                BadgeBackgroundHex = "#35FF453A",
                BadgeForegroundHex = "#FF453A"
            };
            LatestEventBanner = ev.BadgeText;
            CelebrationOccurred?.Invoke(ev);
            MatchEventOccurred?.Invoke(m, ev.BadgeText);
            return;
        }

        var runDelta = currRuns - prevRuns;

        // 2. Six!
        if (runDelta == 6)
        {
            var ev = new SportsCelebrationEvent
            {
                Match = m,
                Type = SportsCelebrationType.Six,
                BadgeText = "💥 SIX!",
                Title = $"💥 SIX! - {teamName}",
                Detail = $"{teamCode} hits a MAXIMUM! • {matchSummary}",
                Emoji = "💥",
                BadgeBackgroundHex = "#35FF9F0A",
                BadgeForegroundHex = "#FF9F0A"
            };
            LatestEventBanner = ev.BadgeText;
            CelebrationOccurred?.Invoke(ev);
            MatchEventOccurred?.Invoke(m, ev.BadgeText);
            return;
        }

        // 3. Four!
        if (runDelta == 4)
        {
            var ev = new SportsCelebrationEvent
            {
                Match = m,
                Type = SportsCelebrationType.Four,
                BadgeText = "🔥 FOUR!",
                Title = $"🔥 FOUR! - {teamName}",
                Detail = $"{teamCode} finds the boundary! • {matchSummary}",
                Emoji = "🔥",
                BadgeBackgroundHex = "#35FFD60A",
                BadgeForegroundHex = "#FFD60A"
            };
            LatestEventBanner = ev.BadgeText;
            CelebrationOccurred?.Invoke(ev);
            MatchEventOccurred?.Invoke(m, ev.BadgeText);
            return;
        }

        // 4. Milestone 100 runs
        if (prevRuns > 0 && prevRuns < 100 && currRuns >= 100)
        {
            var ev = new SportsCelebrationEvent
            {
                Match = m,
                Type = SportsCelebrationType.Milestone100,
                BadgeText = "💯 CENTURY!",
                Title = $"💯 100 UP! - {teamName}",
                Detail = $"{teamCode} crosses 100 runs! • {matchSummary}",
                Emoji = "💯",
                BadgeBackgroundHex = "#35BF5AF2",
                BadgeForegroundHex = "#BF5AF2"
            };
            LatestEventBanner = ev.BadgeText;
            CelebrationOccurred?.Invoke(ev);
            MatchEventOccurred?.Invoke(m, ev.BadgeText);
            return;
        }

        // 5. Milestone 50 runs
        if (prevRuns > 0 && prevRuns < 50 && currRuns >= 50)
        {
            var ev = new SportsCelebrationEvent
            {
                Match = m,
                Type = SportsCelebrationType.Milestone50,
                BadgeText = "⭐ 50 UP!",
                Title = $"⭐ 50 UP! - {teamName}",
                Detail = $"{teamCode} crosses 50 runs! • {matchSummary}",
                Emoji = "⭐",
                BadgeBackgroundHex = "#3564D2FF",
                BadgeForegroundHex = "#64D2FF"
            };
            LatestEventBanner = ev.BadgeText;
            CelebrationOccurred?.Invoke(ev);
            MatchEventOccurred?.Invoke(m, ev.BadgeText);
            return;
        }
    }

}
