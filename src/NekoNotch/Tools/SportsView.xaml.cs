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
    private string _currentNav = "football"; // "football", "cricket", "my_teams"
    private string _currentLeague = "all";
    private bool _liveOnly = false;
    private string _searchQuery = "";
    private string _teamCategoryFilter = "all";
    private string _teamSearchQuery = "";
    private SportMatch? _detailedMatch;
    private List<CricketInningScorecard> _currentScorecards = [];
    private int _selectedInningIndex = 0;

    public sealed class CricketBatterViewModel
    {
        public string DisplayName { get; set; } = "";
        public string Dismissal { get; set; } = "";
        public int Runs { get; set; }
        public int Balls { get; set; }
        public int Fours { get; set; }
        public int Sixes { get; set; }
        public string StrikeRate { get; set; } = "0.0";
        public bool IsNotOut { get; set; }
        public Brush RunsBrush { get; set; } = Brushes.White;
    }

    public sealed class CricketBowlerViewModel
    {
        public string BowlerName { get; set; } = "";
        public string Overs { get; set; } = "0.0";
        public int Maidens { get; set; }
        public int RunsConceded { get; set; }
        public int Wickets { get; set; }
        public string Economy { get; set; } = "0.0";
        public Brush WicketsBrush { get; set; } = Brushes.White;
    }

    public sealed class MatchCardViewModel
    {
        public string Id { get; set; } = "";
        public SportMatch RawMatch { get; set; } = null!;
        public string LeagueName => RawMatch.LeagueName;
        public string HomeTeamName => RawMatch.HomeTeamName;
        public string HomeTeamLogo => RawMatch.HomeTeamLogo;
        public string HomeScore => RawMatch.HomeScore;
        public string AwayTeamName => RawMatch.AwayTeamName;
        public string AwayTeamLogo => RawMatch.AwayTeamLogo;
        public string AwayScore => RawMatch.AwayScore;
        public string StatusDetail => RawMatch.StatusDetail;
        public string Summary => RawMatch.Summary;
        public bool HasSummary => !string.IsNullOrWhiteSpace(RawMatch.Summary);
        public bool IsLive => RawMatch.IsLive;
        public Brush StatusBgBrush { get; set; } = Brushes.Transparent;
        public Brush StatusFgBrush { get; set; } = Brushes.White;
        public Brush PinColor { get; set; } = Brushes.Gray;
        public bool IsPinned { get; set; }
        public string PinToolTip => IsPinned ? "Unpin from Notch" : "Pin to Notch";
    }

    public sealed class TeamCardViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string LogoUrl { get; set; } = "";
        public string FollowButtonText { get; set; } = "+ Follow";
        public Brush FollowButtonColor { get; set; } = Brushes.Gray;
    }

    public sealed class TeamClusterViewModel
    {
        public string Title { get; set; } = "";
        public string CountText { get; set; } = "";
        public List<TeamCardViewModel> Teams { get; set; } = [];
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
        UpdateLeagueChips();
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

    // ------------------------------------ NAVIGATION CLICKS
    private void OnNavFootballClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "football";
        _currentLeague = "all";
        UpdateLeagueChips();
        ShowScheduleMode();
        RefreshUI();
    }

    private void OnNavCricketClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "cricket";
        _currentLeague = "all";
        UpdateLeagueChips();
        ShowScheduleMode();
        RefreshUI();
    }

    private void OnNavMyTeamsClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _currentNav = "my_teams";
        _currentLeague = "all";
        UpdateLeagueChips();
        ShowScheduleMode();
        RefreshUI();
    }

    private void OnLiveFilterClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _liveOnly = LiveFilterCheck.IsChecked == true;
        RefreshUI();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _searchQuery = SearchBox.Text.Trim();
        RefreshUI();
    }

    private void OnToggleFollowModeClick(object sender, RoutedEventArgs e)
    {
        if (StageFollowMode.Visibility == Visibility.Visible)
        {
            ShowScheduleMode();
        }
        else
        {
            ShowFollowMode();
        }
    }

    private void OnBackFromFollowClick(object sender, RoutedEventArgs e)
    {
        ShowScheduleMode();
    }

    private void ShowScheduleMode()
    {
        StageScheduleMode.Visibility = Visibility.Visible;
        StageDetailMode.Visibility = Visibility.Collapsed;
        StageFollowMode.Visibility = Visibility.Collapsed;
        if (LeagueChipsScroll != null) LeagueChipsScroll.Visibility = Visibility.Visible;
        if (SearchBox != null) SearchBox.Visibility = Visibility.Visible;
        if (FollowTeamsBtn != null) FollowTeamsBtn.Visibility = Visibility.Visible;
    }

    private void ShowDetailMode()
    {
        StageScheduleMode.Visibility = Visibility.Collapsed;
        StageDetailMode.Visibility = Visibility.Visible;
        StageFollowMode.Visibility = Visibility.Collapsed;
        if (LeagueChipsScroll != null) LeagueChipsScroll.Visibility = Visibility.Collapsed;
        if (SearchBox != null) SearchBox.Visibility = Visibility.Collapsed;
        if (FollowTeamsBtn != null) FollowTeamsBtn.Visibility = Visibility.Collapsed;
    }

    private void ShowFollowMode()
    {
        StageScheduleMode.Visibility = Visibility.Collapsed;
        StageDetailMode.Visibility = Visibility.Collapsed;
        StageFollowMode.Visibility = Visibility.Visible;
        if (LeagueChipsScroll != null) LeagueChipsScroll.Visibility = Visibility.Collapsed;
        if (SearchBox != null) SearchBox.Visibility = Visibility.Collapsed;
        if (FollowTeamsBtn != null) FollowTeamsBtn.Visibility = Visibility.Collapsed;
        _teamCategoryFilter = "all";
        _teamSearchQuery = "";
        if (TeamSearchBox != null) TeamSearchBox.Text = "";

        if (FollowModeTitle != null)
        {
            FollowModeTitle.Text = _currentNav == "cricket"
                ? "FOLLOW CRICKET TEAMS"
                : (_currentNav == "football" ? "FOLLOW FOOTBALL CLUBS & TEAMS" : "FOLLOW FAVORITE CLUBS & TEAMS");
        }

        UpdateTeamCategoryChips();
        RefreshUI();
    }

    private void UpdateTeamCategoryChips()
    {
        if (TeamCategoryChipsPanel == null) return;
        TeamCategoryChipsPanel.Children.Clear();
        var chipStyle = (Style)TryFindResource("LeagueChipBtn");

        void AddCategoryChip(string label, string categoryTag, bool isDefault = false)
        {
            var rb = new RadioButton
            {
                Content = label,
                Tag = categoryTag,
                Style = chipStyle,
                GroupName = "DynamicTeamCategoryFilter",
                IsChecked = isDefault
            };
            rb.Click += (s, _) =>
            {
                if (s is RadioButton clicked && clicked.Tag is string tag)
                {
                    _teamCategoryFilter = tag;
                    RefreshUI();
                }
            };
            TeamCategoryChipsPanel.Children.Add(rb);
        }

        AddCategoryChip("All", "all", _teamCategoryFilter == "all");

        if (_currentNav == "cricket")
        {
            AddCategoryChip("International", "International Teams");
            AddCategoryChip("IPL", "Indian Premier League (IPL)");
            AddCategoryChip("BBL", "Big Bash League (BBL)");
        }
        else if (_currentNav == "football")
        {
            AddCategoryChip("Premier League", "Premier League");
            AddCategoryChip("La Liga", "La Liga");
            AddCategoryChip("European Giants", "European Giants");
            AddCategoryChip("ISL", "Indian Super League (ISL)");
            AddCategoryChip("National Teams", "National Teams");
        }
        else
        {
            AddCategoryChip("Premier League", "Premier League");
            AddCategoryChip("La Liga", "La Liga");
            AddCategoryChip("ISL", "Indian Super League (ISL)");
            AddCategoryChip("Cricket Int'l", "International Teams");
            AddCategoryChip("IPL", "Indian Premier League (IPL)");
        }
    }

    private void OnTeamSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _teamSearchQuery = TeamSearchBox.Text.Trim();
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

    // ------------------------------------ LEAGUE CHIPS
    private void UpdateLeagueChips()
    {
        LeagueChipsPanel.Children.Clear();

        var chipStyle = (Style)TryFindResource("LeagueChipBtn");

        void AddChip(string label, string leagueTag, bool isDefault = false)
        {
            var rb = new RadioButton
            {
                Content = label,
                Tag = leagueTag,
                Style = chipStyle,
                GroupName = "DynamicLeagueFilter",
                IsChecked = isDefault
            };
            rb.Click += (s, _) =>
            {
                if (s is RadioButton clicked && clicked.Tag is string tag)
                {
                    _currentLeague = tag;
                    RefreshUI();
                }
            };
            LeagueChipsPanel.Children.Add(rb);
        }

        AddChip("All", "all", true);

        if (_currentNav == "football")
        {
            AddChip("Premier League", "eng.1");
            AddChip("UCL", "uefa.champions");
            AddChip("La Liga", "esp.1");
            AddChip("Serie A", "ita.1");
            AddChip("Bundesliga", "ger.1");
            AddChip("ISL", "ind.1");
        }
        else if (_currentNav == "cricket")
        {
            AddChip("International", "international");
            AddChip("T20 Leagues", "t20");
            AddChip("Domestic", "domestic");
        }
    }

    // ------------------------------------ UI REFRESH
    private void RefreshUI()
    {
        if (!_isInitialized) return;

        var matches = SportsService.Instance.Matches;
        var followed = SportsService.Instance.FollowedTeams;
        var activeMatch = SportsService.Instance.ActiveMatch;

        // 1. Badges & Live Indicators
        MyTeamsCountText.Text = followed.Count.ToString();
        MyTeamsBadge.Visibility = followed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var hasFootballLive = matches.Any(m => m.Sport == SportType.Football && m.IsLive);
        var hasCricketLive = matches.Any(m => m.Sport == SportType.Cricket && m.IsLive);

        FootballLiveDot.Visibility = hasFootballLive ? Visibility.Visible : Visibility.Collapsed;
        CricketLiveDot.Visibility = hasCricketLive ? Visibility.Visible : Visibility.Collapsed;

        var accentBrush = (Brush)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var subTextBrush = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;

        // 2. Populate Follow Teams Grid if in Follow mode
        if (StageFollowMode.Visibility == Visibility.Visible)
        {
            var presetTeams = SportsService.PresetTeams.AsEnumerable();

            if (_currentNav == "football")
            {
                presetTeams = presetTeams.Where(t => t.Sport == SportType.Football);
            }
            else if (_currentNav == "cricket")
            {
                presetTeams = presetTeams.Where(t => t.Sport == SportType.Cricket);
            }

            if (_teamCategoryFilter != "all")
            {
                presetTeams = presetTeams.Where(t => string.Equals(t.Category, _teamCategoryFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_teamSearchQuery))
            {
                var q = _teamSearchQuery.ToUpperInvariant();
                presetTeams = presetTeams.Where(t =>
                    t.Name.ToUpperInvariant().Contains(q) ||
                    t.Code.ToUpperInvariant().Contains(q) ||
                    t.Category.ToUpperInvariant().Contains(q));
            }

            var clusters = presetTeams
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other Teams" : t.Category)
                .Select(g =>
                {
                    var teamCards = g.Select(t =>
                    {
                        var isFollowed = followed.Any(f => f.Id == t.Id);
                        return new TeamCardViewModel
                        {
                            Id = t.Id,
                            Name = t.Name,
                            LogoUrl = t.LogoUrl,
                            FollowButtonText = isFollowed ? "✓ Following" : "+ Follow",
                            FollowButtonColor = isFollowed ? accentBrush : subTextBrush
                        };
                    }).ToList();

                    return new TeamClusterViewModel
                    {
                        Title = g.Key.ToUpperInvariant(),
                        CountText = $"{teamCards.Count} teams",
                        Teams = teamCards
                    };
                })
                .ToList();

            TeamClustersControl.ItemsSource = clusters;
        }

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
        }
        else if (_currentNav == "football")
        {
            filteredMatches = filteredMatches.Where(m => m.Sport == SportType.Football);
        }
        else if (_currentNav == "cricket")
        {
            filteredMatches = filteredMatches.Where(m => m.Sport == SportType.Cricket);
        }

        if (_liveOnly)
        {
            filteredMatches = filteredMatches.Where(m => m.IsLive);
        }

        if (_currentLeague != "all")
        {
            if (_currentLeague == "international")
            {
                filteredMatches = filteredMatches.Where(m => m.LeagueName.Contains("tour of", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("Cup", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("ICC", StringComparison.OrdinalIgnoreCase));
            }
            else if (_currentLeague == "t20")
            {
                filteredMatches = filteredMatches.Where(m => m.LeagueName.Contains("T20", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("Premier League", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("T10", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("BBL", StringComparison.OrdinalIgnoreCase));
            }
            else if (_currentLeague == "domestic")
            {
                filteredMatches = filteredMatches.Where(m => m.LeagueName.Contains("Trophy", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("Division", StringComparison.OrdinalIgnoreCase) ||
                                                             m.LeagueName.Contains("Shield", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                filteredMatches = filteredMatches.Where(m => m.LeagueId.Equals(_currentLeague, StringComparison.OrdinalIgnoreCase));
            }
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

        var liveGreen = new SolidColorBrush(Color.FromRgb(50, 215, 75));
        var liveBg = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
        var grayBg = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

        var matchCards = new List<MatchCardViewModel>();
        var pinnedId = SportsService.Instance.PinnedMatchId;
        foreach (var m in matchList)
        {
            var isPinned = !string.IsNullOrEmpty(pinnedId) && m.Id == pinnedId;
            matchCards.Add(new MatchCardViewModel
            {
                Id = m.Id,
                RawMatch = m,
                StatusBgBrush = m.IsLive ? liveBg : grayBg,
                StatusFgBrush = m.IsLive ? liveGreen : subTextBrush,
                PinColor = isPinned ? accentBrush : subTextBrush,
                IsPinned = isPinned
            });
        }

        MatchesList.ItemsSource = matchCards;
        NoMatchesState.Visibility = matchCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // If in detail mode, refresh detail banner
        if (_detailedMatch != null && StageDetailMode.Visibility == Visibility.Visible)
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
            _selectedInningIndex = 0;
            _currentScorecards.Clear();
            ShowDetailMode();
            RenderDetailBanner(_detailedMatch);
            _ = LoadMatchDetailAsync(_detailedMatch);
        }
    }

    private void OnBackToScheduleClick(object sender, RoutedEventArgs e)
    {
        ShowScheduleMode();
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

        var pinnedId = SportsService.Instance.PinnedMatchId;
        var isPinned = !string.IsNullOrEmpty(pinnedId) && pinnedId == m.Id;
        DetailPinText.Text = isPinned ? "Unpin from Notch" : "Pin to Notch";
        DetailPinBtn.ToolTip = isPinned ? "Unpin live match from Notch" : "Pin live match to Notch";
        var pinAccent = (Brush)TryFindResource("AccentBrush") ?? Brushes.DeepSkyBlue;
        var normalText = (Brush)TryFindResource("TextBrush") ?? Brushes.White;
        DetailPinIcon.Foreground = isPinned ? pinAccent : normalText;
        DetailPinText.Foreground = isPinned ? pinAccent : normalText;

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
                _currentScorecards = detail.CricketScorecards;
                _selectedInningIndex = Math.Max(0, _currentScorecards.Count - 1);
                CricketPlayersList.ItemsSource = detail.CricketPlayers;
                RenderCricketScorecards();
            }
        });
    }

    private void RenderCricketScorecards()
    {
        CricketInningsTabsPanel.Children.Clear();

        if (_currentScorecards.Count == 0)
        {
            // If no detailed scorecards yet (e.g. pre-match), show squads expanded
            CricketInningsTabsScroll.Visibility = Visibility.Collapsed;
            CricketScorecardCard.Visibility = Visibility.Collapsed;
            CricketSquadsExpander.IsExpanded = true;
            return;
        }

        CricketScorecardCard.Visibility = Visibility.Visible;
        CricketSquadsExpander.IsExpanded = false;

        // If multiple innings, show tabs selector
        if (_currentScorecards.Count > 1)
        {
            CricketInningsTabsScroll.Visibility = Visibility.Visible;
            var chipStyle = (Style)TryFindResource("LeagueChipBtn");

            for (int i = 0; i < _currentScorecards.Count; i++)
            {
                var sc = _currentScorecards[i];
                var tabIndex = i;
                var shortTitle = string.IsNullOrEmpty(sc.TeamName) ? sc.InningTitle : $"{sc.TeamName} ({sc.ScoreSummary})";
                var rb = new RadioButton
                {
                    Content = shortTitle,
                    Tag = tabIndex,
                    Style = chipStyle,
                    GroupName = "CricketInningTabs",
                    IsChecked = tabIndex == _selectedInningIndex
                };
                rb.Click += (s, _) =>
                {
                    if (s is RadioButton btn && btn.Tag is int idx)
                    {
                        _selectedInningIndex = idx;
                        DisplaySelectedInningScorecard();
                    }
                };
                CricketInningsTabsPanel.Children.Add(rb);
            }
        }
        else
        {
            CricketInningsTabsScroll.Visibility = Visibility.Collapsed;
        }

        if (_selectedInningIndex >= _currentScorecards.Count)
        {
            _selectedInningIndex = _currentScorecards.Count - 1;
        }

        DisplaySelectedInningScorecard();
    }

    private void DisplaySelectedInningScorecard()
    {
        if (_currentScorecards.Count == 0 || _selectedInningIndex < 0 || _selectedInningIndex >= _currentScorecards.Count)
        {
            CricketScorecardCard.Visibility = Visibility.Collapsed;
            return;
        }

        var card = _currentScorecards[_selectedInningIndex];
        CricketScorecardCard.Visibility = Visibility.Visible;

        InningHeaderTitle.Text = card.InningTitle;
        InningHeaderScore.Text = card.ScoreSummary;
        InningHeaderExtras.Text = string.IsNullOrEmpty(card.Extras) ? "" : $"Extras: {card.Extras}";

        var textBrush = (Brush)TryFindResource("TextBrush") ?? Brushes.White;
        var subTextBrush = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;
        var accentBrush = (Brush)TryFindResource("AccentBrush") ?? Brushes.DeepSkyBlue;
        var centuryBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0)); // Gold
        var fiftyBrush = new SolidColorBrush(Color.FromRgb(100, 210, 255)); // Light cyan
        var wicketBrush = new SolidColorBrush(Color.FromRgb(50, 215, 75)); // Green

        // Batters
        if (card.Batters.Count > 0)
        {
            BattingHeading.Visibility = Visibility.Visible;
            BattingTableHeader.Visibility = Visibility.Visible;
            NoBattersText.Visibility = Visibility.Collapsed;

            var batterVMs = card.Batters.Select(b => new CricketBatterViewModel
            {
                DisplayName = b.PlayerName,
                Dismissal = b.Dismissal,
                Runs = b.Runs,
                Balls = b.Balls,
                Fours = b.Fours,
                Sixes = b.Sixes,
                StrikeRate = b.StrikeRate,
                IsNotOut = b.IsNotOut,
                RunsBrush = b.Runs >= 100 ? centuryBrush : (b.Runs >= 50 ? fiftyBrush : textBrush)
            }).ToList();

            CricketBattersList.ItemsSource = batterVMs;
        }
        else
        {
            CricketBattersList.ItemsSource = null;
            NoBattersText.Visibility = Visibility.Visible;
            BattingTableHeader.Visibility = Visibility.Collapsed;
        }

        // Bowlers
        if (card.Bowlers.Count > 0)
        {
            BowlingHeading.Visibility = Visibility.Visible;
            BowlingTableHeader.Visibility = Visibility.Visible;
            CricketBowlersList.Visibility = Visibility.Visible;

            var bowlerVMs = card.Bowlers.Select(bw => new CricketBowlerViewModel
            {
                BowlerName = bw.BowlerName,
                Overs = bw.Overs,
                Maidens = bw.Maidens,
                RunsConceded = bw.RunsConceded,
                Wickets = bw.Wickets,
                Economy = bw.Economy,
                WicketsBrush = bw.Wickets >= 3 ? wicketBrush : (bw.Wickets > 0 ? accentBrush : textBrush)
            }).ToList();

            CricketBowlersList.ItemsSource = bowlerVMs;
        }
        else
        {
            BowlingHeading.Visibility = Visibility.Collapsed;
            BowlingTableHeader.Visibility = Visibility.Collapsed;
            CricketBowlersList.Visibility = Visibility.Collapsed;
        }

        // Fall of Wickets
        if (card.FallOfWickets.Count > 0)
        {
            FowSection.Visibility = Visibility.Visible;
            CricketFowList.ItemsSource = card.FallOfWickets;
        }
        else
        {
            FowSection.Visibility = Visibility.Collapsed;
        }

        // Partnerships
        if (card.Partnerships.Count > 0)
        {
            PartnershipsSection.Visibility = Visibility.Visible;
            CricketPartnershipsList.ItemsSource = card.Partnerships;
        }
        else
        {
            PartnershipsSection.Visibility = Visibility.Collapsed;
        }
    }
}