using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
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
    private readonly DispatcherTimer _silenceTimer = new() { Interval = TimeSpan.FromSeconds(5) };

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
    }

    public void Start()
    {
        if (!App.Settings.EnableWakeWord) return;

        Task.Run(() =>
        {
            try
            {
                InitializeRecognizer();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _ui.Invoke(() => IsAvailable = false);
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
                _engine.RecognizeAsyncCancel();
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
        var wakeGrammar = new Grammar(wakeGb) { Name = "WakeWord" };
        _engine.LoadGrammar(wakeGrammar);

        // 2. Direct "Wake + Action" combined phrases (e.g. "hey neko open spotify")
        var actionVerbs = new Choices(
            "open", "launch", "go to", "play", "pause", "next", "next song",
            "next track", "previous", "previous song", "mute", "unmute",
            "volume up", "volume down", "start timer", "set timer", "stop timer",
            "start pomodoro", "stop pomodoro", "take a screenshot"
        );

        var apps = new Choices(
            "spotify", "chrome", "edge", "firefox", "word", "excel",
            "powerpoint", "notepad", "calculator", "settings", "discord",
            "telegram", "whatsapp", "terminal", "code", "weather", "notes",
            "clipboard", "sports", "stocks", "ai"
        );

        // App Launch grammar: "hey neko open [app]"
        var appLaunchGb = new GrammarBuilder();
        appLaunchGb.Append(wakeChoices);
        appLaunchGb.Append(new Choices("open", "launch", "go to"));
        appLaunchGb.Append(apps);
        _engine.LoadGrammar(new Grammar(appLaunchGb) { Name = "DirectAppLaunch" });

        // Standalone Command grammar (used once awake)
        var cmdGb = new GrammarBuilder();
        cmdGb.Append(actionVerbs);
        cmdGb.Append(apps, 0, 1);
        _engine.LoadGrammar(new Grammar(cmdGb) { Name = "StandaloneCommand" });

        // Dictation grammar for free-form speech once awake
        var dictation = new DictationGrammar { Name = "Dictation" };
        dictation.Enabled = false; // Enabled only when awake
        _engine.LoadGrammar(dictation);
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (State != VoiceState.Listening) return;

        var text = e.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _ui.BeginInvoke(() =>
        {
            LiveHypothesis = CleanWakePrefix(text);
            SpeechHypothesisUpdated?.Invoke(LiveHypothesis);
        });
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var rawText = e.Result.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(rawText) || e.Result.Confidence < 0.35f) return;

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

        // ----------------- 1. APP LAUNCHING / SWITCHING
        if (cmd.StartsWith("open ") || cmd.StartsWith("launch ") || cmd.StartsWith("go to "))
        {
            var target = Regex.Replace(cmd, @"^(open|launch|go to)\s+", "").Trim();
            (success, message) = ExecuteAppCommand(target);
        }
        // ----------------- 2. MEDIA CONTROLS
        else if (cmd.Contains("pause") || cmd.Contains("play"))
        {
            NowPlayingService.Instance.PlayPauseCommand.Execute(null);
            message = "Playback toggled";
        }
        else if (cmd.Contains("next") || cmd.Contains("skip"))
        {
            NowPlayingService.Instance.NextCommand.Execute(null);
            message = "Skipped to next";
        }
        else if (cmd.Contains("previous") || cmd.Contains("back"))
        {
            NowPlayingService.Instance.PreviousCommand.Execute(null);
            message = "Previous track";
        }
        // ----------------- 3. VOLUME
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
        // ----------------- 4. TIMERS & POMODORO
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
        // ----------------- 5. NOTCH TOOLS
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
        // ----------------- 6. VOICE DICTATION / TYPE INTO ACTIVE APP
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
                AppLauncher.Launch("spotify");
                return (true, "Opened Spotify");
            case "chrome":
                AppLauncher.Launch("chrome");
                return (true, "Opened Chrome");
            case "edge":
                AppLauncher.Launch("msedge");
                return (true, "Opened Edge");
            case "word":
                AppLauncher.Launch("winword");
                return (true, "Opened MS Word");
            case "excel":
                AppLauncher.Launch("excel");
                return (true, "Opened MS Excel");
            case "powerpoint":
                AppLauncher.Launch("powerpnt");
                return (true, "Opened PowerPoint");
            case "notepad":
                AppLauncher.Launch("notepad");
                return (true, "Opened Notepad");
            case "calculator":
                AppLauncher.Launch("calc");
                return (true, "Opened Calculator");
            case "settings":
                AppLauncher.Launch("ms-settings:");
                return (true, "Opened Windows Settings");
            case "discord":
                AppLauncher.Launch("discord");
                return (true, "Opened Discord");
            case "telegram":
                AppLauncher.Launch("telegram");
                return (true, "Opened Telegram");
            case "whatsapp":
                AppLauncher.Launch("whatsapp");
                return (true, "Opened WhatsApp");
            case "terminal":
                AppLauncher.Launch("wt");
                return (true, "Opened Windows Terminal");
            case "code":
                AppLauncher.Launch("code");
                return (true, "Opened VS Code");
            default:
                AppLauncher.Launch(target);
                return (true, $"Opened {target}");
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
