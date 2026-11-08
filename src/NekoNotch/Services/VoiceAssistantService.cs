using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.Recognition;
using System.Text;
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
    private readonly DispatcherTimer _silenceTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    [ObservableProperty] private VoiceState _state = VoiceState.Idle;
    [ObservableProperty] private string _liveHypothesis = "";
    [ObservableProperty] private string _lastCommand = "";
    [ObservableProperty] private string _feedbackMessage = "";
    [ObservableProperty] private bool _isAvailable = false;

    public event Action? WakeWordTriggered;
    public event Action<string>? SpeechHypothesisUpdated;
    public event Action<string, bool>? CommandCompleted;
    public event Action? ListeningEnded;

    private static readonly HashSet<string> KnownTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "chrome", "edge", "firefox", "word", "excel", "powerpoint",
        "notepad", "calculator", "calc", "settings", "discord", "telegram",
        "whatsapp", "terminal", "code", "vs code", "weather", "notes",
        "quicknotes", "clipboard", "sports", "stocks", "ai", "music"
    };

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
        _engine.LoadGrammar(new Grammar(wakeGb) { Name = "WakeWord" });

        // 2. Apps and Tools choices
        var apps = new Choices(
            "spotify", "chrome", "edge", "firefox", "word", "excel",
            "powerpoint", "notepad", "calculator", "calc", "settings", "discord",
            "telegram", "whatsapp", "terminal", "code", "vs code", "weather", "notes",
            "quicknotes", "clipboard", "sports", "stocks", "ai", "music"
        );

        // Standalone App grammar: just saying "spotify", "chrome", etc. once awake
        _engine.LoadGrammar(new Grammar(new GrammarBuilder(apps)) { Name = "StandaloneApps" });

        // Direct Wake + App in one sentence: "hey neko spotify"
        var wakeAppGb = new GrammarBuilder();
        wakeAppGb.Append(wakeChoices);
        wakeAppGb.Append(apps);
        _engine.LoadGrammar(new Grammar(wakeAppGb) { Name = "DirectWakeApp" });

        // 3. Action verbs
        var actionVerbs = new Choices(
            "open", "launch", "go to", "switch to", "show", "bring up", "focus", "start", "run",
            "play", "pause", "resume", "next", "next song", "next track", "skip",
            "previous", "previous song", "previous track", "back", "mute", "unmute",
            "volume up", "volume down", "louder", "quieter",
            "start timer", "set timer", "stop timer", "start pomodoro", "stop pomodoro", "take a screenshot"
        );

        // App Launch grammar: "hey neko open [app]" or "hey neko switch to [app]"
        var appLaunchGb = new GrammarBuilder();
        appLaunchGb.Append(wakeChoices);
        appLaunchGb.Append(new Choices("open", "launch", "go to", "switch to", "show", "bring up", "focus", "start", "run"));
        appLaunchGb.Append(apps);
        _engine.LoadGrammar(new Grammar(appLaunchGb) { Name = "DirectAppLaunch" });

        // Standalone Command grammar (used once awake): "open spotify", "pause", etc.
        var cmdGb = new GrammarBuilder();
        cmdGb.Append(actionVerbs);
        cmdGb.Append(apps, 0, 1);
        _engine.LoadGrammar(new Grammar(cmdGb) { Name = "StandaloneCommand" });

        // 4. Free-form Dictation grammar
        var dictation = new DictationGrammar { Name = "Dictation" };
        _engine.LoadGrammar(dictation);
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (State != VoiceState.Listening) return;

        var text = e.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _ui.BeginInvoke(() =>
        {
            var cleaned = CleanWakePrefix(text);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                LiveHypothesis = cleaned;
                SpeechHypothesisUpdated?.Invoke(cleaned);
            }
        });
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var rawText = e.Result.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(rawText) || e.Result.Confidence < 0.25f) return;

        _ui.BeginInvoke(() => ProcessSpeech(rawText));
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
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
                    // User spoke wake word + command in one sentence! e.g. "hey neko open spotify" or "hey neko spotify"
                    ExecuteVoiceCommand(commandPart);
                }
            }
        }
        else if (State == VoiceState.Listening)
        {
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

        // Strip common action prefixes: "open spotify", "switch to spotify", "show spotify", "bring up spotify"
        var cleanTarget = Regex.Replace(cmd, @"^(open|launch|go to|switch to|show|bring up|focus|start|run)\s+", "").Trim();

        // ----------------- 1. APP LAUNCHING / SWITCHING
        if (KnownTargets.Contains(cleanTarget) ||
            cmd.StartsWith("open ") || cmd.StartsWith("launch ") || cmd.StartsWith("switch to ") ||
            cmd.StartsWith("show ") || cmd.StartsWith("bring up ") || cmd.StartsWith("go to "))
        {
            var target = string.IsNullOrWhiteSpace(cleanTarget) ? cmd : cleanTarget;
            (success, message) = ExecuteAppCommand(target);
        }
        // ----------------- 2. MEDIA CONTROLS
        else if (cmd.Contains("pause") || cmd.Contains("play") || cmd == "resume")
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
        // ----------------- 5. VOICE DICTATION / TYPE INTO ACTIVE APP
        else if (cmd.StartsWith("type ") || cmd.StartsWith("dictate ") || cmd.StartsWith("write "))
        {
            var textToType = Regex.Replace(phrase, @"^(type|dictate|write)\s+", "", RegexOptions.IgnoreCase).Trim();
            textToType = CleanFillerWords(textToType);
            TypeIntoActiveWindow(textToType);
            message = $"Typed: \"{textToType}\"";
        }
        // ----------------- 6. ASK AI
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
        switch (target.ToLowerInvariant())
        {
            case "spotify":
            case "music":
                BringAppToFrontOrLaunch("spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify:");
                return (true, "Opened Spotify");
            case "chrome":
                BringAppToFrontOrLaunch("chrome", null, "chrome");
                return (true, "Opened Chrome");
            case "edge":
                BringAppToFrontOrLaunch("msedge", "Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge", "msedge");
                return (true, "Opened Edge");
            case "firefox":
                BringAppToFrontOrLaunch("firefox", null, "firefox");
                return (true, "Opened Firefox");
            case "word":
                BringAppToFrontOrLaunch("winword", null, "winword");
                return (true, "Opened MS Word");
            case "excel":
                BringAppToFrontOrLaunch("excel", null, "excel");
                return (true, "Opened MS Excel");
            case "powerpoint":
                BringAppToFrontOrLaunch("powerpnt", null, "powerpnt");
                return (true, "Opened PowerPoint");
            case "notepad":
                BringAppToFrontOrLaunch("notepad", null, "notepad");
                return (true, "Opened Notepad");
            case "calculator":
            case "calc":
                BringAppToFrontOrLaunch("calc", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "calc:");
                return (true, "Opened Calculator");
            case "settings":
                AppLauncher.Launch("ms-settings:");
                return (true, "Opened Settings");
            case "discord":
                BringAppToFrontOrLaunch("discord", null, "discord");
                return (true, "Opened Discord");
            case "telegram":
                BringAppToFrontOrLaunch("telegram", null, "telegram");
                return (true, "Opened Telegram");
            case "whatsapp":
                BringAppToFrontOrLaunch("whatsapp", null, "whatsapp");
                return (true, "Opened WhatsApp");
            case "terminal":
                BringAppToFrontOrLaunch("wt", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "wt");
                return (true, "Opened Terminal");
            case "code":
            case "vs code":
                BringAppToFrontOrLaunch("code", null, "code");
                return (true, "Opened VS Code");
            case "weather":
                OpenNotchTool("weather");
                return (true, "Opened Weather");
            case "sports":
                OpenNotchTool("sports");
                return (true, "Opened Sports");
            case "notes":
            case "quicknotes":
                OpenNotchTool("quicknotes");
                return (true, "Opened Notes");
            case "clipboard":
                OpenNotchTool("clipboard");
                return (true, "Opened Clipboard");
            case "stocks":
                OpenNotchTool("stocks");
                return (true, "Opened Stocks");
            case "ai":
                OpenNotchTool("ask");
                return (true, "Opened Ask AI");
            default:
                AppLauncher.Launch(target);
                return (true, $"Opened {target}");
        }
    }

    private static void BringAppToFrontOrLaunch(string processName, string? aumid, string fallbackCommand)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length > 0)
            {
                var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));
                IntPtr targetHwnd = IntPtr.Zero;

                // 1. Check if any process already has a non-zero MainWindowHandle
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            targetHwnd = p.MainWindowHandle;
                            break;
                        }
                    }
                    catch { }
                }

                // 2. If MainWindowHandle is 0 (typical for CEF / Spotify / Discord when minimized), enumerate all top-level windows
                if (targetHwnd == IntPtr.Zero)
                {
                    EnumWindows((hWnd, _) =>
                    {
                        GetWindowThreadProcessId(hWnd, out var pid);
                        if (pids.Contains(pid))
                        {
                            var cls = new StringBuilder(256);
                            GetClassName(hWnd, cls, 256);
                            var clsName = cls.ToString();
                            var title = new StringBuilder(512);
                            GetWindowText(hWnd, title, 512);

                            // Match main application frame
                            if (clsName.Contains("Chrome_WidgetWin") || clsName.Contains("ApplicationFrameWindow") ||
                                title.ToString().Contains(processName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetHwnd = hWnd;
                                return false; // Found
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                }

                if (targetHwnd != IntPtr.Zero)
                {
                    // Restore from minimized/tray state and bring to front
                    ShowWindow(targetHwnd, 9 /* SW_RESTORE */);
                    ShowWindow(targetHwnd, 5 /* SW_SHOW */);
                    BringWindowToTop(targetHwnd);
                    SetForegroundWindow(targetHwnd);
                    SwitchToThisWindow(targetHwnd, true);
                    return;
                }
            }

            // 3. If process not running or window could not be activated, launch via Store AUMID if present
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"shell:AppsFolder\\{aumid}",
                        UseShellExecute = true
                    });
                    return;
                }
                catch { }
            }

            // 4. Fallback to direct process or shell command
            Process.Start(new ProcessStartInfo(fallbackCommand) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            AppLauncher.Launch(fallbackCommand);
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
                await Task.Delay(150);
                _ui.Invoke(() =>
                {
                    Clipboard.SetText(text + " ");
                });

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
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
