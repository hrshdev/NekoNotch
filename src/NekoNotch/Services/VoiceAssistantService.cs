using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NekoNotch.Interop;
using NekoNotch.Shell;

namespace NekoNotch.Services;

public enum VoiceState
{
    Idle,
    Listening,
    Processing,
    Executed
}

/// <summary>
/// Native offline voice assistant engine supporting Wake Word ("Hey Neko")
/// and "Voice to Done" PC control actions (Hey Nixi style).
/// Runs 100% locally and offline via System.Speech.Recognition.
/// </summary>
public sealed partial class VoiceAssistantService : ObservableObject
{
    public static VoiceAssistantService Instance { get; } = new();

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private SpeechRecognitionEngine? _engine;
    private readonly DispatcherTimer _silenceTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    [ObservableProperty] private VoiceState _state = VoiceState.Idle;
    [ObservableProperty] private string _liveHypothesis = "";
    [ObservableProperty] private string _lastCommand = "";
    [ObservableProperty] private string _feedbackMessage = "";
    [ObservableProperty] private bool _isAvailable = false;

    public event Action? WakeWordTriggered;
    public event Action<string>? SpeechHypothesisUpdated;
    public event Action<string, bool>? CommandCompleted;
    public event Action? ListeningEnded;

    private VoiceAssistantService()
    {
        _silenceTimer.Tick += (_, _) => OnSilenceTimeout();

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume && App.Settings.EnableWakeWord)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(2500); // Allow Windows audio stack to recover
                        Restart();
                    });
                }
            };

            Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock && App.Settings.EnableWakeWord)
                {
                    if (!IsAvailable) Start();
                }
            };
        }
        catch { }
    }

    public void Start()
    {
        if (!App.Settings.EnableWakeWord) return;

        Task.Run(async () =>
        {
            const int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    InitializeRecognizer();
                    if (IsAvailable) return;
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }

                await Task.Delay(attempt * 1500);
            }
        });
    }

    public void Stop()
    {
        try
        {
            _silenceTimer.Stop();
            if (_engine != null)
            {
                _engine.SpeechHypothesized -= OnSpeechHypothesized;
                _engine.SpeechRecognized -= OnSpeechRecognized;
                _engine.RecognizeCompleted -= OnRecognizeCompleted;
                try { _engine.RecognizeAsyncCancel(); } catch { }
                _engine.Dispose();
                _engine = null;
            }
            _ui.Invoke(() =>
            {
                State = VoiceState.Idle;
                IsAvailable = false;
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public void Restart()
    {
        Stop();
        Thread.Sleep(300); // give OS audio subsystem a moment to release capture endpoint
        Start();
    }

    private void InitializeRecognizer()
    {
        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            if (recognizers.Count == 0)
            {
                _ui.Invoke(() => IsAvailable = false);
                return;
            }

            // Prefer English recognizer (e.g. en-US)
            var recInfo = recognizers.FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                          ?? recognizers[0];

            _engine = new SpeechRecognitionEngine(recInfo.Culture);
            _engine.SetInputToDefaultAudioDevice();

            // Build Wake & Action Grammars
            BuildGrammars();

            _engine.SpeechHypothesized += OnSpeechHypothesized;
            _engine.SpeechRecognized += OnSpeechRecognized;
            _engine.RecognizeCompleted += OnRecognizeCompleted;

            _engine.RecognizeAsync(RecognizeMode.Multiple);

            _ui.Invoke(() =>
            {
                IsAvailable = true;
                State = VoiceState.Idle;
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _ui.Invoke(() => IsAvailable = false);
        }
    }

    private void BuildGrammars()
    {
        if (_engine == null) return;

        // 1. Wake phrases: "Hey Neko", "Neko", "Hey Notch", "Ok Neko"
        var wakeChoices = new Choices("hey neko", "neko", "hey notch", "ok neko");
        var wakeGb = new GrammarBuilder(wakeChoices);
        var wakeGrammar = new Grammar(wakeGb) { Name = "WakeWord", Weight = 1.0f };
        _engine.LoadGrammar(wakeGrammar);

        // 2. Action verbs & apps for direct or standalone commands
        var actionVerbs = new Choices(
            "open", "launch", "go to", "show", "start",
            "play", "pause", "resume", "stop", "next", "next song", "next track", "skip",
            "previous", "previous song", "back", "mute", "unmute",
            "volume up", "volume down", "louder", "quieter"
        );

        var apps = new Choices(
            "spotify", "chrome", "edge", "firefox", "word", "excel",
            "powerpoint", "notepad", "calculator", "settings", "discord",
            "telegram", "whatsapp", "terminal", "code", "weather", "notes",
            "clipboard", "sports", "stocks", "ai"
        );

        // Combined: "Hey Neko open spotify"
        var combinedGb = new GrammarBuilder();
        combinedGb.Append(wakeChoices);
        combinedGb.Append(actionVerbs);
        combinedGb.Append(apps, 0, 1);
        _engine.LoadGrammar(new Grammar(combinedGb) { Name = "CombinedAction", Weight = 1.0f });

        // Standalone action: "open spotify", "pause", "volume up"
        var cmdGb = new GrammarBuilder();
        cmdGb.Append(actionVerbs);
        cmdGb.Append(apps, 0, 1);
        _engine.LoadGrammar(new Grammar(cmdGb) { Name = "StandaloneCommand", Weight = 1.0f });

        // Standalone app names: "spotify", "chrome", "calculator"
        var appOnlyGb = new GrammarBuilder(apps);
        _engine.LoadGrammar(new Grammar(appOnlyGb) { Name = "AppOnly", Weight = 0.9f });

        // Free-form continuous dictation grammar for natural speech & unlisted commands
        var dictation = new DictationGrammar { Name = "Dictation", Weight = 0.6f };
        dictation.Enabled = true;
        _engine.LoadGrammar(dictation);
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (State != VoiceState.Listening) return;

        var text = e.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _ui.BeginInvoke(() =>
        {
            // Reset silence timer while user is actively speaking
            _silenceTimer.Stop();
            _silenceTimer.Start();

            LiveHypothesis = CleanWakePrefix(text);
            SpeechHypothesisUpdated?.Invoke(LiveHypothesis);
        });
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var rawText = e.Result.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(rawText)) return;

        // In idle state, require higher confidence for wake word to avoid false triggers
        if (State == VoiceState.Idle && e.Result.Confidence < 0.25f) return;

        _ui.BeginInvoke(() => ProcessSpeech(rawText));
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        // Keep listening in background
        if (_engine != null && IsAvailable)
        {
            try { _engine.RecognizeAsync(RecognizeMode.Multiple); } catch { }
        }
    }

    private void ProcessSpeech(string rawText)
    {
        var text = rawText.ToLowerInvariant().Trim();

        // 1. Check if Wake Word is present in speech
        bool hasWakeWord = text.StartsWith("hey neko") || text.StartsWith("ok neko") ||
                           text.StartsWith("hey notch") || text.StartsWith("neko");

        if (State == VoiceState.Idle)
        {
            if (hasWakeWord)
            {
                var commandPart = CleanWakePrefix(text);
                TriggerWake();

                if (!string.IsNullOrWhiteSpace(commandPart))
                {
                    // User spoke wake word + command in one sentence! e.g. "hey neko open spotify"
                    ExecuteVoiceCommand(commandPart);
                }
            }
        }
        else if (State == VoiceState.Listening)
        {
            // Already awake: process command
            var command = CleanWakePrefix(text);
            if (!string.IsNullOrWhiteSpace(command))
            {
                ExecuteVoiceCommand(command);
            }
        }
    }

    public void TriggerWake()
    {
        State = VoiceState.Listening;
        LiveHypothesis = "Listening…";
        _silenceTimer.Stop();
        _silenceTimer.Interval = TimeSpan.FromSeconds(7);
        _silenceTimer.Start();

        // Play subtle wake chime if enabled
        if (App.Settings.VoiceChime)
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }

        WakeWordTriggered?.Invoke();
    }

    private string CleanWakePrefix(string text)
    {
        var cleaned = text.Trim();
        foreach (var prefix in new[] { "hey neko", "ok neko", "hey notch", "neko" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(prefix.Length).Trim(' ', ',', '.', '-');
                break;
            }
        }
        return cleaned;
    }

    public void ExecuteVoiceCommand(string phrase)
    {
        _silenceTimer.Stop();
        State = VoiceState.Processing;
        LastCommand = phrase;

        var cmd = phrase.ToLowerInvariant().Trim();
        bool success = true;
        string message = "";

        // ----------------- 1. MEDIA CONTROLS
        if (cmd.Contains("pause") || cmd == "stop music" || cmd == "stop song")
        {
            NowPlayingService.Instance.PlayPauseCommand.Execute(null);
            message = "Paused playback";
        }
        else if (cmd.Contains("play") || cmd.Contains("resume"))
        {
            if (cmd.Contains("spotify"))
            {
                (success, message) = ExecuteAppCommand("spotify");
            }
            else
            {
                NowPlayingService.Instance.PlayPauseCommand.Execute(null);
                message = "Resumed playback";
            }
        }
        else if (cmd.Contains("next") || cmd.Contains("skip"))
        {
            NowPlayingService.Instance.NextCommand.Execute(null);
            message = "Skipped to next";
        }
        else if (cmd.Contains("previous") || cmd.Contains("back track") || cmd.Contains("last song"))
        {
            NowPlayingService.Instance.PreviousCommand.Execute(null);
            message = "Previous track";
        }
        // ----------------- 2. VOLUME
        else if (cmd == "mute" || cmd == "unmute" || cmd.Contains("mute"))
        {
            VolumeService.Instance.ToggleMute();
            message = VolumeService.Instance.IsMuted() ? "Muted" : "Unmuted";
        }
        else if (cmd.Contains("volume up") || cmd.Contains("louder"))
        {
            var cur = VolumeService.Instance.GetLevel();
            var next = Math.Clamp(cur + 0.1f, 0f, 1f);
            VolumeService.Instance.SetLevel(next);
            message = $"Volume {Math.Round(next * 100)}%";
        }
        else if (cmd.Contains("volume down") || cmd.Contains("quieter"))
        {
            var cur = VolumeService.Instance.GetLevel();
            var next = Math.Clamp(cur - 0.1f, 0f, 1f);
            VolumeService.Instance.SetLevel(next);
            message = $"Volume {Math.Round(next * 100)}%";
        }
        else if (Regex.IsMatch(cmd, @"volume\s+(\d+)"))
        {
            var match = Regex.Match(cmd, @"volume\s+(\d+)");
            if (int.TryParse(match.Groups[1].Value, out var val))
            {
                var lvl = Math.Clamp(val / 100f, 0f, 1f);
                VolumeService.Instance.SetLevel(lvl);
                message = $"Volume set to {Math.Round(lvl * 100)}%";
            }
        }
        // ----------------- 3. TIMERS & POMODORO
        else if (cmd.Contains("timer"))
        {
            if (cmd.Contains("stop") || cmd.Contains("cancel"))
            {
                CountdownService.Instance.ResetCommand.Execute(null);
                message = "Timer stopped";
            }
            else
            {
                var numMatch = Regex.Match(cmd, @"(\d+)\s*(min|minute|sec|second)?");
                int mins = 5;
                if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out var parsed))
                {
                    mins = parsed;
                }
                CountdownService.Instance.SetMinutes(mins);
                if (!CountdownService.Instance.IsRunning)
                {
                    CountdownService.Instance.ToggleCommand.Execute(null);
                }
                message = $"Timer set for {mins}m";
            }
        }
        else if (cmd.Contains("pomodoro"))
        {
            if (cmd.Contains("stop") || cmd.Contains("cancel"))
            {
                PomodoroService.Instance.ResetCommand.Execute(null);
                message = "Pomodoro stopped";
            }
            else
            {
                if (!PomodoroService.Instance.IsRunning)
                {
                    PomodoroService.Instance.ToggleCommand.Execute(null);
                }
                message = "Pomodoro started";
            }
        }
        // ----------------- 4. NOTCH TOOLS
        else if (cmd.Contains("weather"))
        {
            OpenNotchTool("weather");
            message = "Opened Weather";
        }
        else if (cmd.Contains("sport"))
        {
            OpenNotchTool("sports");
            message = "Opened Sports Hub";
        }
        else if (cmd.Contains("note"))
        {
            OpenNotchTool("quicknotes");
            message = "Opened Notes";
        }
        else if (cmd.Contains("clipboard"))
        {
            OpenNotchTool("clipboard");
            message = "Opened Clipboard";
        }
        else if (cmd.Contains("stock"))
        {
            OpenNotchTool("stocks");
            message = "Opened Stocks";
        }
        else if (cmd.Contains("setting"))
        {
            OpenNotchTool("settings");
            message = "Opened Settings";
        }
        // ----------------- 5. APPS
        else if (cmd.Contains("spotify"))
        {
            (success, message) = ExecuteAppCommand("spotify");
        }
        else if (cmd.Contains("chrome"))
        {
            (success, message) = ExecuteAppCommand("chrome");
        }
        else if (cmd.Contains("edge"))
        {
            (success, message) = ExecuteAppCommand("edge");
        }
        else if (cmd.Contains("notepad"))
        {
            (success, message) = ExecuteAppCommand("notepad");
        }
        else if (cmd.Contains("calculator") || cmd.Contains("calc"))
        {
            (success, message) = ExecuteAppCommand("calculator");
        }
        else if (cmd.Contains("word"))
        {
            (success, message) = ExecuteAppCommand("word");
        }
        else if (cmd.Contains("excel"))
        {
            (success, message) = ExecuteAppCommand("excel");
        }
        else if (cmd.Contains("powerpoint"))
        {
            (success, message) = ExecuteAppCommand("powerpoint");
        }
        else if (cmd.Contains("terminal") || cmd.Contains("command prompt"))
        {
            (success, message) = ExecuteAppCommand("terminal");
        }
        else if (cmd.Contains("discord"))
        {
            (success, message) = ExecuteAppCommand("discord");
        }
        else if (cmd.Contains("telegram"))
        {
            (success, message) = ExecuteAppCommand("telegram");
        }
        else if (cmd.Contains("whatsapp"))
        {
            (success, message) = ExecuteAppCommand("whatsapp");
        }
        else if (cmd.Contains("code") || cmd.Contains("vs code"))
        {
            (success, message) = ExecuteAppCommand("code");
        }
        else if (cmd.StartsWith("open ") || cmd.StartsWith("launch ") || cmd.StartsWith("go to "))
        {
            var target = Regex.Replace(cmd, @"^(open|launch|go to)\s+", "").Trim();
            (success, message) = ExecuteAppCommand(target);
        }
        // ----------------- 6. VOICE DICTATION
        else if (cmd.StartsWith("type ") || cmd.StartsWith("dictate ") || cmd.StartsWith("write "))
        {
            var textToType = Regex.Replace(phrase, @"^(type|dictate|write)\s+", "", RegexOptions.IgnoreCase).Trim();
            textToType = CleanFillerWords(textToType);
            TypeIntoActiveWindow(textToType);
            message = $"Typed: \"{textToType}\"";
        }
        // ----------------- 7. ASK AI
        else if (cmd.StartsWith("ask ai ") || cmd.StartsWith("ask ") || cmd.StartsWith("what is ") || cmd.StartsWith("who is ") || cmd.StartsWith("tell me "))
        {
            OpenNotchTool("ask");
            message = "Opened Ask AI";
        }
        else
        {
            success = false;
            message = $"Unrecognized: \"{phrase}\"";
        }

        State = VoiceState.Executed;
        FeedbackMessage = message;
        CommandCompleted?.Invoke(message, success);

        // Reset to Idle after brief confirmation
        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        resetTimer.Tick += (_, _) =>
        {
            resetTimer.Stop();
            State = VoiceState.Idle;
            ListeningEnded?.Invoke();
        };
        resetTimer.Start();
    }

    private (bool, string) ExecuteAppCommand(string target)
    {
        switch (target)
        {
            case "spotify":
                try
                {
                    Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
                }
                catch
                {
                    AppLauncher.Launch("spotify");
                }
                RestoreExistingApp("Spotify");
                return (true, "Opened Spotify");

            case "chrome":
                if (!RestoreExistingApp("chrome")) AppLauncher.Launch("chrome");
                return (true, "Opened Chrome");

            case "edge":
                if (!RestoreExistingApp("msedge")) AppLauncher.Launch("msedge");
                return (true, "Opened Edge");

            case "word":
                if (!RestoreExistingApp("WINWORD")) AppLauncher.Launch("winword");
                return (true, "Opened MS Word");

            case "excel":
                if (!RestoreExistingApp("EXCEL")) AppLauncher.Launch("excel");
                return (true, "Opened MS Excel");

            case "powerpoint":
                if (!RestoreExistingApp("POWERPNT")) AppLauncher.Launch("powerpnt");
                return (true, "Opened PowerPoint");

            case "notepad":
                if (!RestoreExistingApp("Notepad")) AppLauncher.Launch("notepad");
                return (true, "Opened Notepad");

            case "calculator":
                try
                {
                    Process.Start(new ProcessStartInfo("calculator:") { UseShellExecute = true });
                }
                catch
                {
                    AppLauncher.Launch("calc");
                }
                RestoreExistingApp("CalculatorApp");
                RestoreExistingApp("Calculator");
                return (true, "Opened Calculator");

            case "settings":
                Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                return (true, "Opened Windows Settings");

            case "discord":
                if (!RestoreExistingApp("Discord")) AppLauncher.Launch("discord");
                return (true, "Opened Discord");

            case "telegram":
                if (!RestoreExistingApp("Telegram")) AppLauncher.Launch("telegram");
                return (true, "Opened Telegram");

            case "whatsapp":
                if (!RestoreExistingApp("WhatsApp")) AppLauncher.Launch("whatsapp");
                return (true, "Opened WhatsApp");

            case "terminal":
                if (!RestoreExistingApp("WindowsTerminal")) AppLauncher.Launch("wt");
                return (true, "Opened Windows Terminal");

            case "code":
                if (!RestoreExistingApp("Code")) AppLauncher.Launch("code");
                return (true, "Opened VS Code");

            default:
                if (!RestoreExistingApp(target)) AppLauncher.Launch(target);
                return (true, $"Opened {target}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static bool RestoreExistingApp(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0) return false;

            var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pids.Contains(pid))
                {
                    found = hWnd;
                    ShowWindow(hWnd, 9 /* SW_RESTORE */);
                    SetForegroundWindow(hWnd);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private void OpenNotchTool(string toolId)
    {
        _ui.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is NotchWindow win)
            {
                win.Show();
                win.Activate();
                win.Expand(activate: true);
                win.ShowTool(toolId);
            }
        });
    }

    private void TypeIntoActiveWindow(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150); // Small pause for focus
                _ui.Invoke(() =>
                {
                    Clipboard.SetText(text + " ");
                });

                // Send Ctrl+V using Win32 keybd_event
                keybd_event(0x11 /* VK_CONTROL */, 0, 0, UIntPtr.Zero);
                keybd_event(0x56 /* V */, 0, 0, UIntPtr.Zero);
                keybd_event(0x56, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
                keybd_event(0x11, 0, 2, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        });
    }

    private static string CleanFillerWords(string input)
    {
        var cleaned = Regex.Replace(input, @"\b(um|uh|er|ah|like|you know)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private void OnSilenceTimeout()
    {
        _silenceTimer.Stop();
        if (State == VoiceState.Listening)
        {
            State = VoiceState.Idle;
            ListeningEnded?.Invoke();
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
