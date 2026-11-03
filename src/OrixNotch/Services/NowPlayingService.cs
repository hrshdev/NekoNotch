using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrixNotch.Interop;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace OrixNotch.Services;

/// <summary>Wraps the Windows global media session (Spotify, browsers, Media Player, Netflix, …).</summary>
public sealed partial class NowPlayingService : ObservableObject
{
    public static NowPlayingService Instance { get; } = new();

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Session? _session;
    private double _positionBase;
    private DateTimeOffset _positionStamp;

    [ObservableProperty] private string _title = "Nothing playing";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _sourceApp = "";
    [ObservableProperty] private ImageSource? _art;
    [ObservableProperty] private Color? _artTint;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;

    public string PositionText => Format(Position);
    public string DurationText => Format(Duration);

    private NowPlayingService() => _ = InitAsync();

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (m, _) => _ui.BeginInvoke(() => Hook(m.GetCurrentSession()));
            _ui.Invoke(() => Hook(_manager.GetCurrentSession()));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void Hook(Session? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        HasSession = session is not null;
        if (session is null)
        {
            Title = "Nothing playing";
            Artist = "";
            SourceApp = "";
            Art = null;
            ArtTint = null;
            IsPlaying = false;
            Duration = 0;
            Position = 0;
            return;
        }

        session.MediaPropertiesChanged += OnMediaChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        SourceApp = PrettyAppName(session.SourceAppUserModelId);
        _ = RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void OnMediaChanged(Session s, MediaPropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(() => _ = RefreshMediaAsync());

    private void OnPlaybackChanged(Session s, PlaybackInfoChangedEventArgs e) =>
        _ui.BeginInvoke(RefreshPlayback);

    private void OnTimelineChanged(Session s, TimelinePropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(RefreshTimeline);

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session) return;

            var rawTitle = props.Title?.Trim() ?? "";
            var rawArtist = props.Artist?.Trim() ?? "";
            var rawAlbum = props.AlbumTitle?.Trim() ?? "";
            var rawApp = SourceApp;
            var aumid = session.SourceAppUserModelId ?? "";

            // Enhanced media extraction for Netflix, YouTube, Spotify, and browser media
            var (enhancedTitle, enhancedArtist, enhancedApp) = await Task.Run(() =>
                EnhanceMediaMetadata(rawTitle, rawArtist, rawAlbum, rawApp, aumid));

            Title = enhancedTitle;
            Artist = enhancedArtist;
            if (!string.IsNullOrEmpty(enhancedApp)) SourceApp = enhancedApp;

            ImageSource? image = null;
            if (props.Thumbnail is not null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var input = stream.AsStreamForRead();
                var buffer = new MemoryStream();
                await input.CopyToAsync(buffer);
                buffer.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 300;
                bmp.StreamSource = buffer;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            Art = image;
            ArtTint = image is BitmapSource src ? AverageColor(src) : null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static (string Title, string Artist, string App) EnhanceMediaMetadata(
        string rawTitle, string rawArtist, string rawAlbum, string app, string aumid)
    {
        var title = rawTitle;
        var artist = rawArtist;
        var sourceApp = app;

        // 1. Check for Netflix (App or Web)
        var isNetflix = aumid.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                        sourceApp.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Netflix", StringComparison.OrdinalIgnoreCase);

        if (isNetflix)
        {
            sourceApp = "Netflix";
            if (title.Equals("Netflix", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(title))
            {
                // Try UIAutomation from Netflix Windows App
                var nfxAppTitle = TryGetNetflixAppTitle();
                if (!string.IsNullOrWhiteSpace(nfxAppTitle))
                {
                    (title, artist) = ParseNetflixTitle(nfxAppTitle);
                }
                else
                {
                    // Try Browser window titles (Zen, Chrome, Edge, Firefox)
                    var winTitles = GetVisibleWindowTitles();
                    var nfxWin = winTitles.FirstOrDefault(w => w.Contains("Netflix", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(nfxWin))
                    {
                        var cleaned = CleanBrowserWindow(nfxWin, "Netflix");
                        (title, artist) = ParseNetflixTitle(cleaned);
                    }
                }
            }

            if (title.Equals("Netflix", StringComparison.OrdinalIgnoreCase))
            {
                // If the app or video doesn't expose a specific episode title yet, keep artist distinct
                artist = "Playing on Netflix";
            }
            return (title, artist, sourceApp);
        }

        // 2. Check for YouTube
        var isYouTube = title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                        sourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                        aumid.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

        if (isYouTube)
        {
            sourceApp = "YouTube";
            title = Regex.Replace(title, @"\s*[-–—]\s*YouTube$", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(artist))
            {
                var split = title.Split(new[] { " - ", " – ", " — " }, 2, StringSplitOptions.TrimEntries);
                if (split.Length == 2)
                {
                    artist = split[0];
                    title = split[1];
                }
                else
                {
                    artist = "YouTube";
                }
            }
            return (title, artist, sourceApp);
        }

        // 3. Check for Spotify
        if (sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || aumid.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            sourceApp = "Spotify";
            if (string.IsNullOrWhiteSpace(artist) && title.Contains(" - "))
            {
                var split = title.Split(new[] { " - ", " – ", " — " }, 2, StringSplitOptions.TrimEntries);
                if (split.Length == 2)
                {
                    artist = split[0];
                    title = split[1];
                }
            }
            if (string.IsNullOrWhiteSpace(artist)) artist = rawAlbum;
            return (title, artist, sourceApp);
        }

        // 4. General browser or player cleanup
        if (string.IsNullOrWhiteSpace(artist))
        {
            if (title.Contains(" - "))
            {
                var split = title.Split(new[] { " - ", " – ", " — " }, 2, StringSplitOptions.TrimEntries);
                if (split.Length == 2)
                {
                    artist = split[0];
                    title = split[1];
                }
            }
            else if (!string.IsNullOrWhiteSpace(rawAlbum))
            {
                artist = rawAlbum;
            }
            else
            {
                artist = sourceApp;
            }
        }

        // Ensure title is never "Unknown title" if window has info
        if (string.IsNullOrWhiteSpace(title) || title.Equals("Unknown title", StringComparison.OrdinalIgnoreCase))
        {
            var winTitles = GetVisibleWindowTitles();
            var matched = winTitles.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w) && !w.Equals("NekoNotch", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matched))
            {
                title = matched;
            }
        }

        // Avoid duplicating title and artist
        if (title.Equals(artist, StringComparison.OrdinalIgnoreCase))
        {
            artist = !string.IsNullOrWhiteSpace(sourceApp) && !sourceApp.Equals(title, StringComparison.OrdinalIgnoreCase)
                ? sourceApp
                : "";
        }

        return (title, artist, sourceApp);
    }

    private static (string Title, string Subtitle) ParseNetflixTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("Netflix", "");
        text = text.Trim();

        if (text.Contains(':'))
        {
            var parts = text.Split(':', 2);
            return (parts[0].Trim(), parts[1].Trim() + " • Netflix");
        }

        var epMatch = Regex.Match(text, @"(.*?)\s+((?:S\d+[:\s]*)?E\d+.*|Episode\s+\d+.*)$", RegexOptions.IgnoreCase);
        if (epMatch.Success && !string.IsNullOrWhiteSpace(epMatch.Groups[1].Value))
        {
            return (epMatch.Groups[1].Value.Trim(), epMatch.Groups[2].Value.Trim() + " • Netflix");
        }

        if (text.Contains(" - "))
        {
            var parts = text.Split(new[] { " - " }, 2, StringSplitOptions.TrimEntries);
            return (parts[0], parts[1] + " • Netflix");
        }

        return (text, "Netflix");
    }

    private static string CleanBrowserWindow(string winTitle, string serviceName)
    {
        var clean = Regex.Replace(winTitle, @"\s*[-–—]\s*(Zen Browser|Google Chrome|Microsoft Edge|Firefox|Mozilla Firefox|Opera|Brave)$", "", RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s*[-–—]\s*" + Regex.Escape(serviceName) + "$", "", RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"^" + Regex.Escape(serviceName) + @"\s*[-–—]\s*", "", RegexOptions.IgnoreCase).Trim();
        return clean;
    }

    private static string? TryGetNetflixAppTitle()
    {
        try
        {
            var desktop = System.Windows.Automation.AutomationElement.RootElement;
            var winCond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Window);
            var windows = desktop.FindAll(System.Windows.Automation.TreeScope.Children, winCond);
            foreach (System.Windows.Automation.AutomationElement win in windows)
            {
                var winName = win.Current.Name ?? "";
                var className = win.Current.ClassName ?? "";
                if (winName.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                {
                    var textCond = new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Text);
                    var textElems = win.FindAll(System.Windows.Automation.TreeScope.Descendants, textCond);
                    foreach (System.Windows.Automation.AutomationElement t in textElems)
                    {
                        var name = t.Current.Name?.Trim();
                        if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                        if (name.Equals("Netflix", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Contains(':') && name.Length <= 8 && char.IsDigit(name[0])) continue;
                        if (name.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Play", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Pause", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Next Episode", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Audio & Subtitles", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Episodes", StringComparison.OrdinalIgnoreCase)) continue;
                        return name;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static List<string> GetVisibleWindowTitles()
    {
        var list = new List<string>();
        try
        {
            Win32.EnumWindows((hWnd, _) =>
            {
                if (!Win32.IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(512);
                Win32.GetWindowText(hWnd, sb, 512);
                var t = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            IsPlaying = false;
        }
    }

    private void RefreshTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t is null) return;
            Duration = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            _positionBase = t.Position.TotalSeconds;
            _positionStamp = t.LastUpdatedTime;
            Tick();
        }
        catch
        {
            // Some players do not expose a timeline.
        }
    }

    /// <summary>Extrapolates the playhead between timeline updates; called by the view's timer.</summary>
    public void Tick()
    {
        var pos = _positionBase;
        if (IsPlaying && _positionStamp != default)
            pos += (DateTimeOffset.Now - _positionStamp).TotalSeconds;
        Position = Duration > 0 ? Math.Clamp(pos, 0, Duration) : Math.Max(0, pos);
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (_session is not null) await _session.TryTogglePlayPauseAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_session is not null) await _session.TrySkipNextAsync();
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_session is not null) await _session.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        if (_session is null) return;
        await _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
    }

    /// <summary>Average color of the artwork, used to tint the Now Playing card.</summary>
    private static Color? AverageColor(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));
            var w = small.PixelWidth;
            var h = small.PixelHeight;
            var pixels = new byte[w * h * 4];
            small.CopyPixels(pixels, w * 4, 0);
            long r = 0, g = 0, b = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }
            var n = Math.Max(1, pixels.Length / 4);
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            return null;
        }
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string PrettyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid;
        var bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Split('.', '_')[0];
        if (name.Length == 0) return aumid;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9A-Fa-f]{8,}$"))
        {
            if (aumid.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
            if (aumid.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
            return "Web App";
        }
        return name.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "spotify" => "Spotify",
            "netflix" => "Netflix",
            "app" when aumid.Contains("Netflix", StringComparison.OrdinalIgnoreCase) => "Netflix",
            "app" when aumid.Contains("Zune", StringComparison.OrdinalIgnoreCase) => "Media Player",
            "microsoft" when aumid.Contains("Zune", StringComparison.OrdinalIgnoreCase) => "Media Player",
            _ => char.ToUpperInvariant(name[0]) + name[1..],
        };
    }
}
