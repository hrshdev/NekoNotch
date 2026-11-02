using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Player page: thumbnail on the left, details + neat timeline bar + playback controls on the right.</summary>
public partial class NowPlayingView : UserControl, IToolView
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<LyricLine>? _lines;
    private string _trackKey = "";
    private int _current = -2;
    private bool _lyricsActive;
    private static NowPlayingService Service => NowPlayingService.Instance;

    public NowPlayingView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Service.PropertyChanged += OnServiceChanged;
        UpdatePlayGlyph();
    }

    public void OnShown()
    {
        Service.Tick();
        _ = LoadLyricsAsync();
        UpdateTint();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingService.IsPlaying):
                UpdatePlayGlyph();
                break;
            case nameof(NowPlayingService.Title) or nameof(NowPlayingService.Artist) or nameof(NowPlayingService.Duration):
                if (IsVisible) _ = LoadLyricsAsync();
                break;
            case nameof(NowPlayingService.Art) or nameof(NowPlayingService.ArtTint):
                UpdateTint();
                break;
        }
    }

    private void Tick()
    {
        Service.Tick();
        if (_lines is null || !_lyricsActive) return;
        var index = LyricsService.IndexAt(_lines, Service.Position);
        if (index != _current) Highlight(index);
    }

    private void OnToggleLyrics(object sender, RoutedEventArgs e)
    {
        _lyricsActive = !_lyricsActive;
        InfoPanel.Visibility = _lyricsActive ? Visibility.Collapsed : Visibility.Visible;
        LyricsPanel.Visibility = _lyricsActive ? Visibility.Visible : Visibility.Collapsed;
        if (_lyricsActive) Tick();
    }

    private async Task LoadLyricsAsync()
    {
        var key = $"{Service.Artist}|{Service.Title}";
        if (key == _trackKey && _lines is not null) return;
        _trackKey = key;
        _lines = null;
        _current = -2;
        LyricsLines.Children.Clear();
        LyricsToggle.Visibility = Visibility.Collapsed;
        _lyricsActive = false;
        InfoPanel.Visibility = Visibility.Visible;
        LyricsPanel.Visibility = Visibility.Collapsed;

        if (!App.Settings.LyricsEnabled || !Service.HasSession)
        {
            LyricsStatus.Text = "";
            return;
        }

        var lines = await LyricsService.GetAsync(Service.Title, Service.Artist, Service.Duration);
        if (key != _trackKey) return;
        if (lines is null || lines.Count == 0)
        {
            LyricsStatus.Text = "";
            return;
        }

        _lines = lines;
        foreach (var line in lines)
        {
            LyricsLines.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(line.Text) ? "♪" : line.Text,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
                Opacity = 0.35,
            });
        }
        LyricsToggle.Visibility = Visibility.Visible;
        Tick();
    }

    /// <summary>Brightens the current line and glides it to the vertical center.</summary>
    private void Highlight(int index)
    {
        _current = index;
        for (var i = 0; i < LyricsLines.Children.Count; i++)
        {
            var tb = (TextBlock)LyricsLines.Children[i];
            var target = i == index ? 1.0 : Math.Abs(i - index) == 1 ? 0.5 : 0.3;
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(250)));
        }
        if (index < 0) return;

        LyricsLines.UpdateLayout();
        var line = (FrameworkElement)LyricsLines.Children[index];
        var y = line.TranslatePoint(new Point(0, 0), LyricsLines).Y + LyricsLines.Margin.Top;
        var offset = Math.Max(0, y - (LyricsScroll.ViewportHeight - line.ActualHeight) / 2);
        AnimateScroll(offset);
    }

    private void AnimateScroll(double to)
    {
        var from = LyricsScroll.VerticalOffset;
        var start = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 320);
            var eased = 1 - Math.Pow(1 - t, 3);
            LyricsScroll.ScrollToVerticalOffset(from + (to - from) * eased);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void UpdatePlayGlyph() =>
        PlayIcon.Kind = Service.IsPlaying ? AppIcon.Pause : AppIcon.Play;

    private void UpdateTint()
    {
        var surface = ((SolidColorBrush)ThemeService.Get("SurfaceBrush")).Color;
        var hasArt = Service.Art is not null;
        Backdrop.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;

        if (hasArt)
        {
            var scrim = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x40, surface.R, surface.G, surface.B), 0),
                    new GradientStop(Color.FromArgb(0xA8, surface.R, surface.G, surface.B), 0.55),
                    new GradientStop(Color.FromArgb(0xE0, surface.R, surface.G, surface.B), 1),
                },
            };
            scrim.Freeze();
            Scrim.Background = scrim;
            Card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        }
        else if (Service.ArtTint is { } tint)
        {
            Scrim.Background = null;
            var mixed = Color.FromRgb((byte)((tint.R + surface.R * 2) / 3), (byte)((tint.G + surface.G * 2) / 3), (byte)((tint.B + surface.B * 2) / 3));
            var brush = new LinearGradientBrush(mixed, surface, 0);
            brush.Freeze();
            Card.Background = brush;
        }
        else
        {
            Scrim.Background = null;
            Card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        }
    }

    private async void OnSeek(object sender, MouseButtonEventArgs e)
    {
        if (Service.Duration <= 0 || Progress.ActualWidth <= 0) return;
        var fraction = e.GetPosition(Progress).X / Progress.ActualWidth;
        await Service.SeekAsync(Math.Clamp(fraction, 0, 1) * Service.Duration);
    }
}
