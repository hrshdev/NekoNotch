using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoNotch.Services;
using NekoNotch.Shell;

namespace NekoNotch.Tools;

public sealed partial class SportsView : UserControl, IToolView
{
    private SportType? _currentFilter = null; // null = All
    private SportMatch? _spotlightMatch;
    private string _searchQuery = "";

    public sealed class MatchItemViewModel
    {
        public string Id { get; set; } = "";
        public string SportEmoji { get; set; } = "";
        public string MatchTitle { get; set; } = "";
        public string LeagueAndSummary { get; set; } = "";
        public string StatusOrScoreText { get; set; } = "";
        public Brush StatusBgBrush { get; set; } = Brushes.Transparent;
        public Brush StatusFgBrush { get; set; } = Brushes.White;
        public Brush PinColor { get; set; } = Brushes.Gray;
        public SportMatch RawMatch { get; set; } = null!;
    }

    public sealed class PresetChipViewModel
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public SportsView()
    {
        InitializeComponent();
        LoadQuickPresets();
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

    private void OnFilterAll(object sender, RoutedEventArgs e)
    {
        _currentFilter = null;
        RefreshUI();
    }

    private void OnFilterFootball(object sender, RoutedEventArgs e)
    {
        _currentFilter = SportType.Football;
        RefreshUI();
    }

    private void OnFilterCricket(object sender, RoutedEventArgs e)
    {
        _currentFilter = SportType.Cricket;
        RefreshUI();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text.Trim();
        RefreshUI();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Updating scores...";
        await SportsService.Instance.RefreshMatchesAsync();
        StatusText.Text = "Live Sports & Scorecards";
    }

    private void LoadQuickPresets()
    {
        var chips = new List<PresetChipViewModel>
        {
            new() { Id = "fb-ars", Label = "⚽ Arsenal" },
            new() { Id = "fb-mci", Label = "⚽ Man City" },
            new() { Id = "fb-rma", Label = "⚽ Real Madrid" },
            new() { Id = "fb-bar", Label = "⚽ Barcelona" },
            new() { Id = "cr-ind", Label = "🏏 India" },
            new() { Id = "cr-csk", Label = "🏏 CSK" },
            new() { Id = "cr-rcb", Label = "🏏 RCB" }
        };
        QuickPresetsList.ItemsSource = chips;
    }

    private void OnQuickFollowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var preset = SportsService.PresetTeams.FirstOrDefault(p => p.Id == tag);
            if (preset != null)
            {
                SportsService.Instance.FollowTeam(preset);
                StatusText.Text = $"Following {preset.Name}!";
                RefreshUI();
            }
        }
    }

    private void OnUnfollowTeamClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string teamId)
        {
            SportsService.Instance.UnfollowTeam(teamId);
            RefreshUI();
        }
    }

    private void RefreshUI()
    {
        var matches = SportsService.Instance.Matches;
        var filtered = matches.AsEnumerable();

        if (_currentFilter.HasValue)
        {
            filtered = filtered.Where(m => m.Sport == _currentFilter.Value);
        }

        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var q = _searchQuery.ToUpperInvariant();
            filtered = filtered.Where(m =>
                m.HomeTeamName.ToUpperInvariant().Contains(q) ||
                m.AwayTeamName.ToUpperInvariant().Contains(q) ||
                m.HomeTeamCode.ToUpperInvariant().Contains(q) ||
                m.AwayTeamCode.ToUpperInvariant().Contains(q) ||
                m.LeagueName.ToUpperInvariant().Contains(q));
        }

        var matchList = filtered.ToList();

        // Spotlight Match:
        // Priority 1: User's explicitly chosen spotlight
        // Priority 2: Pinned match
        // Priority 3: Active live match
        // Priority 4: First match in current filtered list
        var pinnedId = SportsService.Instance.FollowedTeams; // Just checking config
        var activeMatch = SportsService.Instance.ActiveMatch;

        if (_spotlightMatch != null && matchList.All(m => m.Id != _spotlightMatch.Id))
        {
            // If the previously selected match isn't in filtered list, check if it's active
            if (activeMatch != null && matchList.Any(m => m.Id == activeMatch.Id))
                _spotlightMatch = activeMatch;
            else
                _spotlightMatch = matchList.FirstOrDefault();
        }
        else if (_spotlightMatch == null)
        {
            _spotlightMatch = activeMatch ?? matchList.FirstOrDefault();
        }

        // Render Spotlight Card
        if (_spotlightMatch != null)
        {
            SpotlightPopulatedView.Visibility = Visibility.Visible;
            SpotlightEmptyView.Visibility = Visibility.Collapsed;

            SpotlightLeagueText.Text = _spotlightMatch.LeagueName.ToUpperInvariant();
            SpotlightHomeName.Text = _spotlightMatch.HomeTeamCode;
            SpotlightAwayName.Text = _spotlightMatch.AwayTeamCode;
            SpotlightScoreText.Text = _spotlightMatch.Sport == SportType.Football
                ? $"{_spotlightMatch.HomeScore} - {_spotlightMatch.AwayScore}"
                : $"{_spotlightMatch.HomeScore} / {_spotlightMatch.AwayScore}";

            SpotlightTimeText.Text = string.IsNullOrEmpty(_spotlightMatch.StatusDetail)
                ? (_spotlightMatch.IsLive ? "LIVE" : "Upcoming")
                : _spotlightMatch.StatusDetail;

            SpotlightSummaryText.Text = string.IsNullOrEmpty(_spotlightMatch.Summary)
                ? $"{_spotlightMatch.HomeTeamName} vs {_spotlightMatch.AwayTeamName}"
                : _spotlightMatch.Summary;

            if (!string.IsNullOrEmpty(_spotlightMatch.HomeTeamLogo))
            {
                try { SpotlightHomeLogo.Source = new BitmapImage(new Uri(_spotlightMatch.HomeTeamLogo)); } catch { }
            }
            if (!string.IsNullOrEmpty(_spotlightMatch.AwayTeamLogo))
            {
                try { SpotlightAwayLogo.Source = new BitmapImage(new Uri(_spotlightMatch.AwayTeamLogo)); } catch { }
            }

            var isPinned = SportsService.Instance.ActiveMatch?.Id == _spotlightMatch.Id;
            PinToNotchText.Text = isPinned ? "Pinned ✓" : "Pin";

            if (_spotlightMatch.IsLive)
            {
                SpotlightStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
                SpotlightStatusText.Foreground = new SolidColorBrush(Color.FromRgb(50, 215, 75));
                SpotlightStatusText.Text = "● LIVE " + _spotlightMatch.StatusDetail;
                LiveIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                SpotlightStatusBadge.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
                SpotlightStatusText.Foreground = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;
                SpotlightStatusText.Text = _spotlightMatch.StatusDetail;
                LiveIndicator.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            SpotlightPopulatedView.Visibility = Visibility.Collapsed;
            SpotlightEmptyView.Visibility = Visibility.Visible;
            LiveIndicator.Visibility = Visibility.Collapsed;
        }

        // List items
        var viewModels = new List<MatchItemViewModel>();
        var liveGreen = new SolidColorBrush(Color.FromRgb(50, 215, 75));
        var liveBg = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
        var grayText = (Brush)TryFindResource("SubTextBrush") ?? Brushes.Gray;
        var grayBg = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        var accentBrush = (Brush)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;

        foreach (var m in matchList)
        {
            var isPinned = activeMatch?.Id == m.Id;
            viewModels.Add(new MatchItemViewModel
            {
                Id = m.Id,
                SportEmoji = m.Sport == SportType.Football ? "⚽" : "🏏",
                MatchTitle = $"{m.HomeTeamName} vs {m.AwayTeamName}",
                LeagueAndSummary = $"{m.LeagueName} • {(string.IsNullOrEmpty(m.Summary) ? m.StatusDetail : m.Summary)}",
                StatusOrScoreText = m.IsLive ? $"{m.HomeScore} - {m.AwayScore} ({m.StatusDetail})" : m.StatusDetail,
                StatusBgBrush = m.IsLive ? liveBg : grayBg,
                StatusFgBrush = m.IsLive ? liveGreen : grayText,
                PinColor = isPinned ? accentBrush : grayText,
                RawMatch = m
            });
        }

        MatchesList.ItemsSource = viewModels;
        NoMatchesText.Visibility = viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Followed Teams
        var followed = SportsService.Instance.FollowedTeams;
        FollowedTeamsList.ItemsSource = followed.ToList();
        NoFollowedHint.Visibility = followed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPinToNotchClick(object sender, RoutedEventArgs e)
    {
        if (_spotlightMatch != null)
        {
            SportsService.Instance.PinMatch(_spotlightMatch.Id);
            var isPinned = SportsService.Instance.ActiveMatch?.Id == _spotlightMatch.Id;
            StatusText.Text = isPinned
                ? $"Pinned {_spotlightMatch.HomeTeamCode} vs {_spotlightMatch.AwayTeamCode} to Notch!"
                : "Unpinned match from Notch.";
            RefreshUI();
        }
    }

    private void OnPinItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string matchId)
        {
            SportsService.Instance.PinMatch(matchId);
            StatusText.Text = "Toggled pin to Dynamic Island Notch!";
            RefreshUI();
        }
    }

    private void OnMatchRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MatchItemViewModel vm)
        {
            _spotlightMatch = vm.RawMatch;
            RefreshUI();
        }
    }
}
