using System.IO;
using System.Windows;
using System.Windows.Media;
using NekoNotch.Services;
using NekoNotch.Shell;

namespace NekoNotch;

public partial class App : Application
{
    private static readonly string MutexName = "NekoNotch.SingleInstance";
    private static readonly string WakeupEventName = "NekoNotch.WakeupEvent";
    private Mutex? _mutex;
    private EventWaitHandle? _wakeupEvent;
    private TrayService? _tray;

    public static AppSettings Settings => SettingsService.Current;

    /// <summary>
    /// The UI font: Latin in SF Pro when installed, otherwise the bundled Inter; Bengali always in the
    /// bundled Noto Sans Bengali, even inside mixed strings. Both variants are composite fonts shipped
    /// in Fonts/*.CompositeFont, whose targets are relative to the file. (A composite built in code
    /// can't point at pack:// resources: those targets silently fall back to Windows' Bengali font.)
    /// Nothing is installed, and the SF Pro check is one lookup instead of a scan of every system font.
    /// </summary>
    private static FontFamily BuildUiFont()
    {
        var sf = new Typeface("SF Pro Text").TryGetGlyphTypeface(out _);
        return new FontFamily(new Uri("pack://application:,,,/Fonts/"), sf ? "./#NekoUI SF" : "./#NekoUI");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try
            {
                using var wakeup = EventWaitHandle.OpenExisting(WakeupEventName);
                wakeup.Set();
            }
            catch
            {
            }
            _mutex = null;
            Shutdown();
            return;
        }

        try
        {
            _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeupEventName);
            ThreadPool.RegisterWaitForSingleObject(_wakeupEvent, (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is NotchWindow win)
                    {
                        win.Show();
                        win.Activate();
                        win.Expand(activate: true);
                    }
                });
            }, null, -1, false);
        }
        catch
        {
        }

        base.OnStartup(e);
        Field.Register(); // class handlers for the themed text fields
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        // Before any UI measures text.
        Resources["UiFont"] = BuildUiFont();
        ThemeService.Apply();
        SettingsService.ApplyFirstRunDefaults(); // weather city, units, a shorter tab bar (first launch only)
        var window = new NotchWindow();
        MainWindow = window;
        window.Show();
        _tray = new TrayService(window);
        _ = StartupService.SyncAsync(); // open-at-login: on by default, kept in step with Windows
        _ = PhoneTransferService.Instance.SyncAsync(); // LAN server, only when the user turned it on
        UpdateService.Instance.Start(); // first look a couple of minutes in, then daily
        CalendarFeedService.Instance.Start(); // subscribed calendars, every 30 minutes
        PetSoundService.Initialize();
        VoiceAssistantService.Instance.Start(); // offline voice assistant & wake word ("Hey Neko")

        // `NekoNotch.exe --open weather` starts with the panel open on a tool.
        var openAt = Array.IndexOf(e.Args, "--open");
        if (openAt >= 0)
        {
            window.Expand(activate: true);
            if (openAt + 1 < e.Args.Length) window.ShowTool(e.Args[openAt + 1]);
        }
        if (e.Args.Contains("--settings")) window.OpenTool(NotchWindow.SettingsId);
        else if (SettingsService.FirstRun && openAt < 0) window.OpenSettings("tabs"); // welcome: pick your tools
        if (NotchWindow.ShotsDir is not null) window.RunShots(); // Store screenshots (dev only)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        ShelfService.Instance.Save();
        _ = PhoneTransferService.Instance.StopAsync(); // sockets close with the process anyway
        VoiceAssistantService.Instance.Stop();
        _wakeupEvent?.Dispose();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Storage.PathOf("error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
