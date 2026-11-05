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

    public sealed class MatchItemViewModel
    {
        public string Id { get; set; } = "";
        public string SportEmoji { get; set; } = "";
        public string MatchTitle { get; set; } = "";
        public string LeagueAndSummary { get; set; } = "";
        public string StatusOrScoreText { get; set; } = "";
        public Brush StatusBgBrush { get; set; } = Brushes.Transparent;
        public Brush StatusFgBrush { get; set; } = Brushes.White;
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
        UpdateFilterButtons();
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

    private void UpdateFilterButtons()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var subBrush = (Brush)FindResource("SubTextBrush");

        FilterAllBtn.Foreground = _currentFilter == null ? accentBrush : subBrush;
        FilterFootballBtn.Foreground = _currentFilter == SportType.Football ? accentBrush : subBrush;
        FilterCricketBtn.Foreground = _currentFilter == SportType.Cricket ? accentBrush : subBrush;
    }

    private void OnFilterAll(object sender, RoutedEventArgs e)
    {
        _currentFilter = null;
        UpdateFilterButtons();
        RefreshUI();
    }

    private void OnFilterFootball(object sender, RoutedEventArgs e)
    {
        _currentFilter = SportType.Football;
        UpdateFilterButtons();
        RefreshUI();
    }

    private void OnFilterCricket(object sender, RoutedEventArgs e)
    {
        _currentFilter = SportType.Cricket;
        UpdateFilterButtons();
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
            new() { Id = "fb-rma", Label = "⚽ Real Madrid" },
            new() { Id = "cr-ind", Label = "🏏 India" },
            new() { Id = "cr-csk", Label = "🏏 CSK" },
            new() { Id = "cr-rcb", Label = "🏏 RCB" },
            new() { Id = "fb-mci", Label = "⚽ Man City" }
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
            }
        }
    }

    private void RefreshUI()
    {
        var matches = SportsService.Instance.Matches;
        var filtered = _currentFilter == null
            ? matches
            : matches.Where(m => m.Sport == _currentFilter.Value).ToList();

        // Active / Spotlight Match
        _spotlightMatch = SportsService.Instance.ActiveMatch ?? filtered.FirstOrDefault();

        if (_spotlightMatch != null)
        {
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
                SpotlightStatusText.Foreground = (Brush)FindResource("SubTextBrush");
                SpotlightStatusText.Text = _spotlightMatch.StatusDetail;
                LiveIndicator.Visibility = Visibility.Collapsed;
            }
        }

        // List items
        var viewModels = new List<MatchItemViewModel>();
        var liveGreen = new SolidColorBrush(Color.FromRgb(50, 215, 75));
        var liveBg = new SolidColorBrush(Color.FromArgb(40, 50, 215, 75));
        var grayText = (Brush)FindResource("SubTextBrush");
        var grayBg = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

        foreach (var m in filtered)
        {
            viewModels.Add(new MatchItemViewModel
            {
                Id = m.Id,
                SportEmoji = m.Sport == SportType.Football ? "⚽" : "🏏",
                MatchTitle = $"{m.HomeTeamName} vs {m.AwayTeamName}",
                LeagueAndSummary = $"{m.LeagueName} • {(string.IsNullOrEmpty(m.Summary) ? m.StatusDetail : m.Summary)}",
                StatusOrScoreText = m.IsLive ? $"{m.HomeScore} - {m.AwayScore} ({m.StatusDetail})" : m.StatusDetail,
                StatusBgBrush = m.IsLive ? liveBg : grayBg,
                StatusFgBrush = m.IsLive ? liveGreen : grayText,
                RawMatch = m
            });
        }

        MatchesList.ItemsSource = viewModels;
    }

    private void OnPinToNotchClick(object sender, RoutedEventArgs e)
    {
        if (_spotlightMatch != null)
        {
            SportsService.Instance.PinMatch(_spotlightMatch.Id);
            StatusText.Text = $"Pinned {_spotlightMatch.HomeTeamCode} vs {_spotlightMatch.AwayTeamCode} to Notch!";
        }
    }

    private void OnPinItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string matchId)
        {
            SportsService.Instance.PinMatch(matchId);
            StatusText.Text = "Pinned match to Dynamic Island Notch!";
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
