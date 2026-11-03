using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
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
    private static string? _lastTrackKey;
    private static string? _lastTrackTitle;
    private static string? _lastTrackArtist;
    private static string? _lastSourceApp;
    private static byte[]? _lastThumbnailBytes;


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

            byte[]? currentThumbBytes = null;
            if (props.Thumbnail is not null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var input = stream.AsStreamForRead();
                    var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer);
                    if (buffer.Length > 0)
                    {
                        currentThumbBytes = buffer.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
            }

            // Check if Chromium/browser passed a stale thumbnail from a previous video/tab
            // (e.g. user was playing a YouTube video, then switched to Miruro or anime site with no artwork)
            var currentTrackKey = $"{SourceApp}|{enhancedTitle}|{enhancedArtist}";
            var isTrackChanged = _lastTrackKey != null && _lastTrackKey != currentTrackKey;

            bool isStaleThumbnail = false;
            if (currentThumbBytes is not null && _lastThumbnailBytes is not null && isTrackChanged)
            {
                if (System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(currentThumbBytes, _lastThumbnailBytes))
                {
                    if (_lastSourceApp != SourceApp || 
                        (!enhancedTitle.Contains(_lastTrackTitle ?? "___", StringComparison.OrdinalIgnoreCase) && 
                         !(_lastTrackTitle ?? "___").Contains(enhancedTitle, StringComparison.OrdinalIgnoreCase)))
                    {
                        isStaleThumbnail = true;
                    }
                }
            }

            if (SourceApp != "YouTube" && _lastSourceApp == "YouTube" && isTrackChanged && currentThumbBytes is not null)
            {
                if (_lastThumbnailBytes is not null && 
                    System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(currentThumbBytes, _lastThumbnailBytes))
                {
                    isStaleThumbnail = true;
                }
            }

            if (isStaleThumbnail)
            {
                currentThumbBytes = null;
            }

            ImageSource? image = null;
            if (currentThumbBytes is not null && currentThumbBytes.Length > 0)
            {
                try
                {
                    var ms = new MemoryStream(currentThumbBytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;

                    _lastThumbnailBytes = currentThumbBytes;
                    _lastSourceApp = SourceApp;
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
            }
            else
            {
                _lastThumbnailBytes = null;
                _lastSourceApp = SourceApp;
            }

            _lastTrackKey = currentTrackKey;
            _lastTrackTitle = enhancedTitle;
            _lastTrackArtist = enhancedArtist;

            // Fallback: If playing app or website didn't supply thumbnail, use platform's 1st letter (e.g. M for Miruro, Y for YouTube, N for Netflix)
            if (image is null && (!string.IsNullOrWhiteSpace(SourceApp) || !string.IsNullOrWhiteSpace(Title)))
            {
                image = GeneratePlatformLetterThumbnail(SourceApp);
            }

            Art = image;
            ArtTint = image is BitmapSource src ? AverageColor(src) : null;

            // Background fetch: try retrieving artwork from iTunes / JioSaavn if app didn't supply one
            if (props.Thumbnail is null && !string.IsNullOrWhiteSpace(Title) && Title != "Nothing playing")
            {
                var curTitle = Title;
                var curArtist = Artist;
                _ = Task.Run(async () =>
                {
                    var fetched = await FetchOnlineArtworkAsync(curTitle, curArtist);
                    if (fetched is not null && Title == curTitle)
                    {
                        _ui.Invoke(() =>
                        {
                            Art = fetched;
                            ArtTint = fetched is BitmapSource bs ? AverageColor(bs) : null;
                        });
                    }
                });
            }
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

        // 1.5 Check for web video streaming platforms (Miruro, Crunchyroll, Animepahe, HiAnime, etc.)
        var isAnimeStream = rawTitle.Contains("Miruro", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Crunchyroll", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("HiAnime", StringComparison.OrdinalIgnoreCase) ||
                            rawTitle.Contains("Animepahe", StringComparison.OrdinalIgnoreCase);

        if (isAnimeStream)
        {
            if (rawTitle.Contains("Miruro", StringComparison.OrdinalIgnoreCase)) sourceApp = "Miruro";
            else if (rawTitle.Contains("Crunchyroll", StringComparison.OrdinalIgnoreCase)) sourceApp = "Crunchyroll";
            else if (rawTitle.Contains("HiAnime", StringComparison.OrdinalIgnoreCase)) sourceApp = "HiAnime";
            else if (rawTitle.Contains("Animepahe", StringComparison.OrdinalIgnoreCase)) sourceApp = "Animepahe";

            if (!string.IsNullOrWhiteSpace(rawArtist))
            {
                title = rawArtist;
                artist = sourceApp;
            }
            return (title, artist, sourceApp);
        }

        // 2. Check for YouTube
        var isYouTube = title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                        sourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                        aumid.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

        if (!isYouTube && (sourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                           sourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                           sourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
                           sourceApp.Contains("Web App", StringComparison.OrdinalIgnoreCase) ||
                           aumid.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                           aumid.Contains("Edge", StringComparison.OrdinalIgnoreCase)))
        {
            var winTitles = GetVisibleWindowTitles();
            // MUST match the specific window playing this media, not any random background YouTube tab!
            if (winTitles.Any(w => w.Contains("YouTube", StringComparison.OrdinalIgnoreCase) && 
                                   !string.IsNullOrWhiteSpace(title) && 
                                   title.Length >= 4 &&
                                   w.Contains(title, StringComparison.OrdinalIgnoreCase)))
            {
                isYouTube = true;
            }
        }

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

    private static readonly ConcurrentDictionary<string, BitmapSource> LetterTileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource> ArtworkCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Generates a branded, high-resolution tile showing the platform's first letter (e.g. N for Netflix, Y for YouTube, S for Spotify).</summary>
    public static BitmapSource GeneratePlatformLetterThumbnail(string platform)
    {
        var key = string.IsNullOrWhiteSpace(platform) ? "Music" : platform.Trim();
        if (LetterTileCache.TryGetValue(key, out var cached)) return cached;

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => GeneratePlatformLetterThumbnail(platform));
        }

        var letter = GetPlatformLetter(key);
        var (c1, c2) = GetPlatformGradient(key);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, 256, 256);
            var bg = new LinearGradientBrush(c1, c2, new Point(0.5, 0), new Point(0.5, 1));
            dc.DrawRectangle(bg, null, rect);

            // Subtle top radial gloss
            var gloss = new RadialGradientBrush(Color.FromArgb(45, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            {
                Center = new Point(0.5, 0.1),
                GradientOrigin = new Point(0.5, 0.1),
                RadiusX = 0.65,
                RadiusY = 0.55
            };
            dc.DrawRectangle(gloss, null, rect);

            // Modern typography for the platform letter
            var typeface = new Typeface(
                new FontFamily("Segoe UI Variable Display, Segoe UI, Arial"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Normal);

            var shadowText = new FormattedText(
                letter,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                136,
                new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                96.0);

            var x = (256 - shadowText.Width) / 2;
            var y = (256 - shadowText.Height) / 2;
            dc.DrawText(shadowText, new Point(x, y + 3));

            var mainText = new FormattedText(
                letter,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                136,
                Brushes.White,
                96.0);

            dc.DrawText(mainText, new Point(x, y));
        }

        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();

        LetterTileCache[key] = bmp;
        return bmp;
    }

    private static string GetPlatformLetter(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return "♪";
        var p = platform.Trim();
        if (p.Contains("miruro", StringComparison.OrdinalIgnoreCase)) return "M";
        if (p.Contains("crunchyroll", StringComparison.OrdinalIgnoreCase)) return "C";
        if (p.Contains("youtube", StringComparison.OrdinalIgnoreCase)) return "Y";
        if (p.Contains("netflix", StringComparison.OrdinalIgnoreCase)) return "N";
        if (p.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "S";
        if (p.Contains("apple", StringComparison.OrdinalIgnoreCase)) return "A";
        if (p.Contains("prime", StringComparison.OrdinalIgnoreCase) || p.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return "P";
        if (p.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)) return "S";
        if (p.Contains("twitch", StringComparison.OrdinalIgnoreCase)) return "T";
        if (p.Contains("deezer", StringComparison.OrdinalIgnoreCase)) return "D";
        if (p.Contains("tidal", StringComparison.OrdinalIgnoreCase)) return "T";
        if (p.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "V";

        for (var i = 0; i < p.Length; i++)
        {
            if (char.IsLetter(p[i])) return char.ToUpperInvariant(p[i]).ToString();
        }
        return "♪";
    }

    private static (Color Top, Color Bottom) GetPlatformGradient(string platform)
    {
        var p = (platform ?? "").ToLowerInvariant();
        if (p.Contains("miruro"))
            return (Color.FromRgb(108, 92, 231), Color.FromRgb(45, 52, 54));
        if (p.Contains("crunchyroll"))
            return (Color.FromRgb(244, 117, 33), Color.FromRgb(180, 70, 10));
        if (p.Contains("youtube"))
            return (Color.FromRgb(230, 33, 23), Color.FromRgb(155, 0, 0));
        if (p.Contains("netflix"))
            return (Color.FromRgb(229, 9, 20), Color.FromRgb(128, 0, 0));
        if (p.Contains("spotify"))
            return (Color.FromRgb(29, 185, 84), Color.FromRgb(15, 102, 42));
        if (p.Contains("apple") || p.Contains("itunes"))
            return (Color.FromRgb(252, 60, 68), Color.FromRgb(180, 16, 25));
        if (p.Contains("prime") || p.Contains("amazon"))
            return (Color.FromRgb(0, 168, 225), Color.FromRgb(0, 100, 150));
        if (p.Contains("soundcloud"))
            return (Color.FromRgb(255, 85, 0), Color.FromRgb(190, 50, 0));
        if (p.Contains("twitch"))
            return (Color.FromRgb(145, 70, 255), Color.FromRgb(95, 30, 190));
        if (p.Contains("deezer"))
            return (Color.FromRgb(162, 56, 255), Color.FromRgb(100, 20, 180));
        if (p.Contains("tidal"))
            return (Color.FromRgb(0, 220, 220), Color.FromRgb(0, 130, 140));
        if (p.Contains("vlc"))
            return (Color.FromRgb(255, 120, 0), Color.FromRgb(180, 70, 0));
        if (p.Contains("edge"))
            return (Color.FromRgb(0, 120, 215), Color.FromRgb(0, 70, 150));
        if (p.Contains("chrome"))
            return (Color.FromRgb(66, 133, 244), Color.FromRgb(30, 80, 180));
        if (p.Contains("firefox"))
            return (Color.FromRgb(255, 113, 57), Color.FromRgb(190, 40, 20));

        var hash = Math.Abs(platform?.GetHashCode() ?? 0);
        var palettes = new (Color, Color)[]
        {
            (Color.FromRgb(99, 102, 241), Color.FromRgb(67, 56, 202)),
            (Color.FromRgb(236, 72, 153), Color.FromRgb(190, 24, 93)),
            (Color.FromRgb(20, 184, 166), Color.FromRgb(13, 148, 136)),
            (Color.FromRgb(249, 115, 22), Color.FromRgb(194, 65, 12)),
            (Color.FromRgb(168, 85, 247), Color.FromRgb(126, 34, 206)),
            (Color.FromRgb(59, 130, 246), Color.FromRgb(29, 78, 216)),
        };
        return palettes[hash % palettes.Length];
    }

    private static async Task<ImageSource?> FetchOnlineArtworkAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title) || title == "Nothing playing") return null;
        var cacheKey = $"{title}|{artist}";
        if (ArtworkCache.TryGetValue(cacheKey, out var cached)) return cached;

        // 1. Try iTunes Search API (fast, high-res 600x600 artwork)
        try
        {
            var cleanTitle = LyricsService.CleanTitle(title);
            var cleanArtist = LyricsService.CleanArtist(artist);
            var movie = LyricsService.ExtractMovie(title);
            var searchQ = !string.IsNullOrWhiteSpace(movie) ? $"{cleanTitle} {movie}" : $"{cleanTitle} {cleanArtist}";
            searchQ = searchQ.Trim();
            if (searchQ.Length >= 2)
            {
                var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(searchQ)}&entity=song&limit=1";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var resp = await Http.Client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("results", out var resArr) && resArr.GetArrayLength() > 0)
                    {
                        var item = resArr[0];
                        if (item.TryGetProperty("artworkUrl100", out var artProp))
                        {
                            var artUrl = artProp.GetString()?.Replace("100x100bb", "600x600bb");
                            if (!string.IsNullOrWhiteSpace(artUrl))
                            {
                                var img = await DownloadBitmapAsync(artUrl);
                                if (img != null)
                                {
                                    ArtworkCache[cacheKey] = img;
                                    return img;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Try JioSaavn image (covers Indian/Tamil/Telugu songs)
        try
        {
            var cleanTitle = LyricsService.CleanTitle(title);
            var movie = LyricsService.ExtractMovie(title);
            var query = !string.IsNullOrWhiteSpace(movie) ? $"{cleanTitle} {movie}" : cleanTitle;
            if (query.Length >= 2)
            {
                var url = $"https://www.jiosaavn.com/api.php?__call=autocomplete.get&query={Uri.EscapeDataString(query)}&_format=json&ctx=web6dot0";
                using var resp = await Http.Client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("songs", out var songs) &&
                        songs.TryGetProperty("data", out var data) &&
                        data.GetArrayLength() > 0)
                    {
                        var first = data[0];
                        if (first.TryGetProperty("image", out var imgProp))
                        {
                            var imgUrl = imgProp.GetString()?.Replace("50x50.jpg", "500x500.jpg");
                            if (!string.IsNullOrWhiteSpace(imgUrl))
                            {
                                var img = await DownloadBitmapAsync(imgUrl);
                                if (img != null)
                                {
                                    ArtworkCache[cacheKey] = img;
                                    return img;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static async Task<BitmapImage?> DownloadBitmapAsync(string url)
    {
        try
        {
            using var resp = await Http.Client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
