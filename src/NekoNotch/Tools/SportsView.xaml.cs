using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoNotch.Services;
using NekoNotch.Shell;

namespace NekoNotch.Tools;

public sealed partial class SportsView : UserControl, IToolView
{
    private bool _isInitialized = false;
    private string _currentNav = "football"; // "my_teams", "football", "cricket"
    private string _currentLeague = "all";
    private string _searchQuery = "";
    private SportMatch? _detailedMatch;

    public sealed class MatchCardViewModel
    {
        public string Id { get; set; } = "";
        public SportMatch RawMatch { get; set; } = null!;
        public Brush StatusBgBrush { get; set; } = Brushes.Transparent;
        public Brush StatusFgBrush { get; set; } = Brushes.White;
        public Brush PinColor { get; set; } = Brushes.Gray;
    }

    public sealed class TeamCardViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string LogoUrl { get; set; } = "";
        public string FollowButtonText { get; set; } = "+ Follow";
        public Brush FollowButtonColor { get; set; } = Brushes.Gray;
    }

    public sealed class CricketInningViewModel
    {
        public string TeamName { get; set; } = "";
        public string Description { get; set; } = "";
        public string DisplayScore { get; set; } = "";
    }

    public SportsView()
    {
        InitializeComponent();
        _isInitialized = true;
        SportsService.Instance.MatchesUpdated += OnServiceMatchesUpdated;
        RefreshUI();
    }

    public void OnShown()
    {
        RefreshUI();
        _ = SportsService.Instance.RefreshMatchesAsync();
    }

    public void OnHidden() { }

    private void OnServiceMatchesUpdated()
    {
        Dispatcher.Invoke(RefreshUI);
    }

    // ------------------------------------ NAVIGATION RAIL CLICKS
    private void OnNavMyTeamsClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "my_teams";
        RefreshUI();
    }

    private void OnNavFootballClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "football";
        RefreshUI();
    }

    private void OnNavCricketClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "cricket";
        RefreshUI();
    }

    // ------------------------------------ LEAGUE SELECTOR CLICKS
    private void OnLeagueAllClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "all";
        RefreshUI();
    }

    private void OnLeaguePLClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "eng.1";
        RefreshUI();
    }

    private void OnLeagueUCLClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "uefa.champions";
        RefreshUI();
    }

    private void OnLeagueLaLigaClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "esp.1";
        RefreshUI();
    }

    private void OnLeagueSerieAClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "ita.1";
        RefreshUI();
    }

    private void OnLeagueIPLClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentLeague = "8048";
        RefreshUI();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _searchQuery = SearchBox.Text.Trim();
        RefreshUI();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await SportsService.Instance.RefreshMatchesAsync();
        RefreshUI();
    }

    private void OnToggleFollowTeamClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string teamId)
        {
            var isFollowed = SportsService.Instance.FollowedTeams.Any(t => t.Id == teamId);
            if (isFollowed)
            {
                SportsService.Instance.UnfollowTeam(teamId);
            }
            else
            {
                var preset = SportsService.PresetTeams.FirstOrDefault(p => p.Id == teamId);
                if (preset != null)
                {
                    SportsService.Instance.FollowTeam(preset);
                }
            }
            RefreshUI();
        }
    }

    // ------------------------------------ UI REFRESH
    private void RefreshUI()
    {
        if (!_isInitialized) return;

        var matches = SportsService.Instance.Matches;
        var followed = SportsService.Instance.FollowedTeams;
        var activeMatch = SportsService.Instance.ActiveMatch;

        // 1. Update Rail Badges & Live Indicators
        MyTeamsCountText.Text = followed.Count.ToString();
        MyTeamsBadge.Visibility = followed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var hasFootballLive = matches.Any(m => m.Sport == SportType.Football && m.IsLive);
        var hasCricketLive = matches.Any(m => m.Sport == SportType.Cricket && m.IsLive);

        FootballLiveDot.Visibility = hasFootballLive ? Visibility.Visible : Visibility.Collapsed;
        CricketLiveDot.Visibility = hasCricketLive ? Visibility.Visible : Visibility.Collapsed;

        // 2. Populate Teams Explorer
        var accentBrush = (Brush)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var subTextBrush = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;

        var presetTeams = SportsService.PresetTeams.AsEnumerable();
        if (_currentNav == "football")
            presetTeams = presetTeams.Where(t => t.Sport == SportType.Football);
        else if (_currentNav == "cricket")
            presetTeams = presetTeams.Where(t => t.Sport == SportType.Cricket);

        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var q = _searchQuery.ToUpperInvariant();
            presetTeams = presetTeams.Where(t => t.Name.ToUpperInvariant().Contains(q) || t.Code.ToUpperInvariant().Contains(q));
        }

        var teamVMs = new List<TeamCardViewModel>();
        foreach (var t in presetTeams)
        {
            var isFollowed = followed.Any(f => f.Id == t.Id);
            teamVMs.Add(new TeamCardViewModel
            {
                Id = t.Id,
                Name = t.Name,
                LogoUrl = t.LogoUrl,
                FollowButtonText = isFollowed ? "✓ Following" : "+ Follow",
                FollowButtonColor = isFollowed ? accentBrush : subTextBrush
            });
        }
        TeamsExplorerList.ItemsSource = teamVMs;

        // 3. Filter Match Fixtures
        var filteredMatches = matches.AsEnumerable();

        if (_currentNav == "my_teams")
        {
            var followedCodes = followed.Select(f => f.Code.ToUpperInvariant()).ToHashSet();
            var followedNames = followed.Select(f => f.Name.ToUpperInvariant()).ToHashSet();

            filteredMatches = filteredMatches.Where(m =>
                followedCodes.Contains(m.HomeTeamCode.ToUpperInvariant()) ||
                followedCodes.Contains(m.AwayTeamCode.ToUpperInvariant()) ||
                followedNames.Any(n => m.HomeTeamName.ToUpperInvariant().Contains(n)) ||
                followedNames.Any(n => m.AwayTeamName.ToUpperInvariant().Contains(n)));
            ScheduleHeaderText.Text = "MY TEAMS FIXTURES";
        }
        else if (_currentNav == "football")
        {
            filteredMatches = filteredMatches.Where(m => m.Sport == SportType.Football);
            ScheduleHeaderText.Text = "FOOTBALL FIXTURES";
        }
        else if (_currentNav == "cricket")
        {
            filteredMatches = filteredMatches.Where(m => m.Sport == SportType.Cricket);
            ScheduleHeaderText.Text = "CRICKET FIXTURES";
        }

        if (_currentLeague != "all")
        {
            filteredMatches = filteredMatches.Where(m => m.LeagueId.Equals(_currentLeague, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var q = _searchQuery.ToUpperInvariant();
            filteredMatches = filteredMatches.Where(m =>
                m.HomeTeamName.ToUpperInvariant().Contains(q) ||
                m.AwayTeamName.ToUpperInvariant().Contains(q) ||
                m.HomeTeamCode.ToUpperInvariant().Contains(q) ||
                m.AwayTeamCode.ToUpperInvariant().Contains(q) ||
                m.LeagueName.ToUpperInvariant().Contains(q));
        }

        var matchList = filteredMatches.ToList();

        var liveMatches = matchList.Count(m => m.IsLive);
        if (liveMatches > 0)
        {
            LiveCountBanner.Text = $"● {liveMatches} LIVE";
            LiveCountBanner.Visibility = Visibility.Visible;
        }
        else
        {
            LiveCountBanner.Visibility = Visibility.Collapsed;
        }

        var liveGreen = new SolidColorBrush(Color.FromRgb(50, 215, 75));
        var liveBg = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
        var grayBg = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

        var matchCards = new List<MatchCardViewModel>();
        foreach (var m in matchList)
        {
            var isPinned = activeMatch?.Id == m.Id;
            matchCards.Add(new MatchCardViewModel
            {
                Id = m.Id,
                RawMatch = m,
                StatusBgBrush = m.IsLive ? liveBg : grayBg,
                StatusFgBrush = m.IsLive ? liveGreen : subTextBrush,
                PinColor = isPinned ? accentBrush : subTextBrush
            });
        }

        MatchesList.ItemsSource = matchCards;
        NoMatchesText.Visibility = matchCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // If currently in detail view, refresh detail banner
        if (_detailedMatch != null)
        {
            var updated = matches.FirstOrDefault(m => m.Id == _detailedMatch.Id);
            if (updated != null) _detailedMatch = updated;
            RenderDetailBanner(_detailedMatch);
        }
    }

    private void OnPinItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string matchId)
        {
            SportsService.Instance.PinMatch(matchId);
            RefreshUI();
        }
    }

    private void OnMatchCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MatchCardViewModel vm)
        {
            _detailedMatch = vm.RawMatch;
            StageScheduleMode.Visibility = Visibility.Collapsed;
            StageDetailMode.Visibility = Visibility.Visible;
            RenderDetailBanner(_detailedMatch);
            _ = LoadMatchDetailAsync(_detailedMatch);
        }
    }

    private void OnBackToScheduleClick(object sender, RoutedEventArgs e)
    {
        StageDetailMode.Visibility = Visibility.Collapsed;
        StageScheduleMode.Visibility = Visibility.Visible;
        RefreshUI();
    }

    private void OnDetailPinClick(object sender, RoutedEventArgs e)
    {
        if (_detailedMatch != null)
        {
            SportsService.Instance.PinMatch(_detailedMatch.Id);
            RenderDetailBanner(_detailedMatch);
            RefreshUI();
        }
    }

    private void RenderDetailBanner(SportMatch m)
    {
        DetailHomeName.Text = m.HomeTeamName;
        DetailAwayName.Text = m.AwayTeamName;
        DetailScoreText.Text = m.Sport == SportType.Football
            ? $"{m.HomeScore} - {m.AwayScore}"
            : $"{m.HomeScore} vs {m.AwayScore}";

        DetailStatusText.Text = string.IsNullOrEmpty(m.StatusDetail) ? (m.IsLive ? "LIVE" : "Scheduled") : m.StatusDetail;

        if (m.IsLive)
        {
            DetailStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
            DetailStatusText.Foreground = new SolidColorBrush(Color.FromRgb(50, 215, 75));
        }
        else
        {
            DetailStatusBadge.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            DetailStatusText.Foreground = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;
        }

        if (!string.IsNullOrEmpty(m.HomeTeamLogo))
        {
            try { DetailHomeLogo.Source = new BitmapImage(new Uri(m.HomeTeamLogo)); } catch { }
        }
        if (!string.IsNullOrEmpty(m.AwayTeamLogo))
        {
            try { DetailAwayLogo.Source = new BitmapImage(new Uri(m.AwayTeamLogo)); } catch { }
        }

        var isPinned = SportsService.Instance.ActiveMatch?.Id == m.Id;
        DetailPinText.Text = isPinned ? "Pinned ✓" : "Pin to Notch";

        if (m.Sport == SportType.Football)
        {
            FootballDetailPanel.Visibility = Visibility.Visible;
            CricketDetailPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            FootballDetailPanel.Visibility = Visibility.Collapsed;
            CricketDetailPanel.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadMatchDetailAsync(SportMatch match)
    {
        var detail = await SportsService.Instance.FetchMatchDetailAsync(match);
        if (detail == null || _detailedMatch?.Id != match.Id) return;

        Dispatcher.Invoke(() =>
        {
            if (match.Sport == SportType.Football)
            {
                if (detail.FootballStats != null)
                {
                    HomePossessionText.Text = detail.FootballStats.HomePossession;
                    AwayPossessionText.Text = detail.FootballStats.AwayPossession;
                    HomeShotsText.Text = detail.FootballStats.HomeShotsOnTarget;
                    AwayShotsText.Text = detail.FootballStats.AwayShotsOnTarget;

                    if (double.TryParse(detail.FootballStats.HomePossession.TrimEnd('%'), out var hp) &&
                        double.TryParse(detail.FootballStats.AwayPossession.TrimEnd('%'), out var ap))
                    {
                        HomePossessionCol.Width = new GridLength(Math.Max(10, hp), GridUnitType.Star);
                        AwayPossessionCol.Width = new GridLength(Math.Max(10, ap), GridUnitType.Star);
                    }
                }

                FootballEventsList.ItemsSource = detail.FootballEvents;
            }
            else // Cricket
            {
                var inningVMs = detail.CricketInnings.Select(i => new CricketInningViewModel
                {
                    TeamName = i.TeamName,
                    Description = i.Description,
                    DisplayScore = $"{i.Runs}/{i.Wickets} ({i.Overs} ov)"
                }).ToList();

                CricketInningsList.ItemsSource = inningVMs;
                CricketPlayersList.ItemsSource = detail.CricketPlayers;
            }
        });
    }
}
