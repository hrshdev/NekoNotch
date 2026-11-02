using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrixNotch.Services;

public enum NotchActivityKind
{
    None,
    Music,
    Timer,
    Stopwatch,
    Focus,
    Break,
    Reminder,
}

/// <summary>
/// Combines the various things that can occupy the notch when it is closed (music, timers,
/// Pomodoro, event reminders). Higher-priority activities take over from lower ones.
/// </summary>
public sealed partial class NotchActivityService : ObservableObject
{
    public static NotchActivityService Instance { get; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private NotchActivityKind _kind = NotchActivityKind.None;
    [ObservableProperty] private double _progress;

    public bool HasActivity => Kind != NotchActivityKind.None;

    private readonly DispatcherTimer _musicGraceTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _inMusicGrace;

    private NotchActivityService()
    {
        NowPlayingService.Instance.PropertyChanged += OnChanged;
        PomodoroService.Instance.PropertyChanged += OnChanged;
        CountdownService.Instance.PropertyChanged += OnChanged;
        StopwatchService.Instance.PropertyChanged += OnChanged;
        EventReminderService.Instance.PropertyChanged += OnChanged;

        _musicGraceTimer.Tick += (_, _) =>
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            Refresh();
        };

        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    partial void OnKindChanged(NotchActivityKind value) => OnPropertyChanged(nameof(HasActivity));

    public void Refresh()
    {
        var pom = PomodoroService.Instance;
        var ctd = CountdownService.Instance;
        var sw = StopwatchService.Instance;
        var np = NowPlayingService.Instance;

        NotchActivityKind kind;
        string text;
        double progress = 0;
        var reminder = EventReminderService.Instance;
        if (reminder.IsActive)
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = NotchActivityKind.Reminder;
            text = reminder.Text;
            progress = reminder.Progress;
        }
        else if (pom.IsRunning)
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = pom.IsBreak ? NotchActivityKind.Break : NotchActivityKind.Focus;
            text = pom.RemainingText; // mode shows as glyph + colour
            progress = pom.Progress;
        }
        else if (ctd.IsRunning)
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = NotchActivityKind.Timer;
            text = ctd.RemainingText;
            progress = ctd.Progress;
        }
        else if (sw.IsRunning)
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = NotchActivityKind.Stopwatch;
            text = sw.ElapsedText;
            progress = 0; // no total: track ring only
        }
        else if (np.HasSession && np.IsPlaying)
        {
            // Active playback resumes or continues: cancel any grace timer
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = NotchActivityKind.Music;
            text = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} – {np.Artist}";
        }
        else if (np.HasSession && (Kind == NotchActivityKind.Music || _inMusicGrace))
        {
            // Buffering, seeking, or temporary pause: hold the music notch in place for a 4s grace period
            if (!_inMusicGrace)
            {
                _inMusicGrace = true;
                _musicGraceTimer.Start();
            }
            kind = NotchActivityKind.Music;
            text = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} – {np.Artist}";
        }
        else
        {
            _musicGraceTimer.Stop();
            _inMusicGrace = false;
            kind = NotchActivityKind.None;
            text = "";
        }

        Kind = kind;
        Text = text;
        Progress = progress;
    }
}
