using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using NekoNotch.Shell;

namespace NekoNotch.Services;

/// <summary>
/// Launches or focuses the corresponding Windows application from a notification.
/// </summary>
public static class AppLauncher
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static void ClickScreenPoint(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(25);
        mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, 0);
        Thread.Sleep(25);
        mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, 0);
    }

        [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private static readonly string[] BrowserNames =
    {
        "zen", "firefox", "chrome", "msedge", "brave", "opera", "vivaldi", "arc", "chromium", "waterfox"
    };

    public static bool BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            else ShowWindow(hWnd, SwShow);

            var foreWnd = GetForegroundWindow();
            uint foreThread = 0;
            if (foreWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreWnd, out foreThread);
            }
            var curThread = GetCurrentThreadId();

            if (foreThread != 0 && foreThread != curThread)
            {
                AttachThreadInput(curThread, foreThread, true);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                AttachThreadInput(curThread, foreThread, false);
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }

            SwitchToThisWindow(hWnd, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindWindowByTitle(string substring)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            var title = sb.ToString();
            if (title.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static List<IntPtr> FindWindowsByProcessName(string processName)
    {
        var list = new List<IntPtr>();
        try
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName(processName).Select(p => (uint)p.Id));
            if (pids.Count == 0) return list;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pids.Contains(pid))
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(hWnd, sb, 512);
                    if (sb.Length > 0)
                    {
                        list.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    private static IntPtr FindExistingAppWindow(string? appName, string? aumid, string? title)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(appName)) terms.Add(appName.Trim());
        if (!string.IsNullOrWhiteSpace(aumid))
        {
            var clean = aumid;
            if (clean.Contains('.')) clean = clean.Split('.').Last();
            if (clean.Contains('!')) clean = clean.Split('!').First();
            if (clean.Contains('\\')) clean = Path.GetFileNameWithoutExtension(clean);
            if (!string.IsNullOrWhiteSpace(clean) && !terms.Any(t => t.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                terms.Add(clean.Trim());
        }

        // Add special package prefixes or common aliases
        if (terms.Any(t => t.Contains("Netflix", StringComparison.OrdinalIgnoreCase)))
        {
            if (!terms.Any(t => t.Equals("Netflix", StringComparison.OrdinalIgnoreCase))) terms.Add("Netflix");
        }
        if (terms.Any(t => t.Contains("Spotify", StringComparison.OrdinalIgnoreCase)))
        {
            if (!terms.Any(t => t.Equals("Spotify", StringComparison.OrdinalIgnoreCase))) terms.Add("Spotify");
        }

        IntPtr found = IntPtr.Zero;
        uint myPid = 0;
        try { myPid = (uint)Process.GetCurrentProcess().Id; } catch { }

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == myPid || pid == 0) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            var winTitle = sb.ToString().Trim();

            var cbClass = new StringBuilder(256);
            GetClassName(hWnd, cbClass, 256);
            var cls = cbClass.ToString();

            // Ignore system shells / desktop
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return true;

            // 1. Direct title matching (e.g. "Netflix", "Spotify Free", "VLC media player")
            if (!string.IsNullOrEmpty(winTitle))
            {
                foreach (var term in terms)
                {
                    if (winTitle.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                        winTitle.StartsWith(term + " -", StringComparison.OrdinalIgnoreCase) ||
                        winTitle.EndsWith("- " + term, StringComparison.OrdinalIgnoreCase) ||
                        winTitle.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
            }

            // 2. Process name matching (e.g. netflix.exe, spotify.exe, vlc.exe)
            try
            {
                var proc = Process.GetProcessById((int)pid);
                var pName = proc.ProcessName;
                foreach (var term in terms)
                {
                    if (pName.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                        pName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        return found;
    }


    private static bool TrySwitchToBrowserTab(IntPtr hWnd, string tabTitleSubstring)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null) return false;

            // Search ONLY tab items and tree/list items (for vertical tabs like Zen/Edge/TreeTabs)
            // DO NOT search Pane, Custom, Button which traverses entire DOM and freezes
            var tabCond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
            );

            var elements = root.FindAll(TreeScope.Descendants, tabCond);
            foreach (AutomationElement tab in elements)
            {
                try
                {
                    var name = tab.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.Contains(tabTitleSubstring, StringComparison.OrdinalIgnoreCase))
                    {
                        // 1. SelectionItemPattern (Firefox, XUL, standard UI)
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern) &&
                            selPattern is SelectionItemPattern sip)
                        {
                            sip.Select();
                            return true;
                        }


                        // 3. InvokePattern
                        if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invPattern) &&
                            invPattern is InvokePattern ip)
                        {
                            ip.Invoke();
                            return true;
                        }

                        // 4. Physical click: click at 30% from the left to avoid close button ('X') on the right
                        var rect = tab.Current.BoundingRectangle;
                        if (rect.Width > 0 && rect.Height > 0)
                        {
                            BringToFront(hWnd);
                            Thread.Sleep(40);
                            var cx = (int)(rect.Left + Math.Min(30, rect.Width * 0.3));
                            var cy = (int)(rect.Top + rect.Height / 2);
                            ClickScreenPoint(cx, cy);
                            return true;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Activates the application or browser tab responsible for currently playing audio.
    /// If media is playing in a web browser (YouTube, Netflix, Spotify Web, SoundCloud, etc.),
    /// it searches across open browser tabs, switches directly to that tab, and brings the window to front.
    /// If media is playing in a native app (Spotify, Netflix App, Apple Music, VLC), it activates that app.
    /// </summary>
        public static bool OpenSpotify()
    {
        // 1. If Spotify has a window, unminimize and bring it to front
        var hWnd = FindSpotifyMainWindow();
        if (hWnd != IntPtr.Zero)
        {
            return BringToFront(hWnd);
        }

        // 2. If Spotify is running without a window (e.g. minimized to tray) or not running yet:
        // Try WindowsApps execution alias (Microsoft Store package)
        try
        {
            var winApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\Spotify.exe");
            if (File.Exists(winApps))
            {
                Process.Start(new ProcessStartInfo(winApps) { UseShellExecute = true });
                Thread.Sleep(250);
                hWnd = FindSpotifyMainWindow();
                if (hWnd != IntPtr.Zero) BringToFront(hWnd);
                return true;
            }
        }
        catch { }

        // 3. Try standard AppData desktop installation
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Spotify\Spotify.exe");
            if (File.Exists(appData))
            {
                Process.Start(new ProcessStartInfo(appData) { UseShellExecute = true });
                Thread.Sleep(250);
                hWnd = FindSpotifyMainWindow();
                if (hWnd != IntPtr.Zero) BringToFront(hWnd);
                return true;
            }
        }
        catch { }

        // 4. Try Store AppUserModelID directly via explorer shell:AppsFolder
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                UseShellExecute = true
            });
            Thread.Sleep(250);
            hWnd = FindSpotifyMainWindow();
            if (hWnd != IntPtr.Zero) BringToFront(hWnd);
            return true;
        }
        catch { }

        // 5. Fallback: spotify: protocol
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            Thread.Sleep(250);
            hWnd = FindSpotifyMainWindow();
            if (hWnd != IntPtr.Zero) BringToFront(hWnd);
            return true;
        }
        catch { }

        return false;
    }

    public static IntPtr FindSpotifyMainWindow()
    {
        var spotPids = new HashSet<uint>(
            Process.GetProcesses()
                   .Where(p => p.ProcessName.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0)
                   .Select(p => (uint)p.Id)
        );

        if (spotPids.Count == 0) return IntPtr.Zero;

        IntPtr bestWnd = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (spotPids.Contains(pid))
            {
                var cb = new StringBuilder(256);
                GetClassName(hWnd, cb, 256);
                var cls = cb.ToString();

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, 512);
                var title = sb.ToString();

                RECT r;
                GetWindowRect(hWnd, out r);
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;

                if (cls.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (IsIconic(hWnd) || w > 150 || h > 150 || !string.IsNullOrWhiteSpace(title))
                    {
                        bestWnd = hWnd;
                        return false; // Found real GUI window
                    }
                }
                else if (cls == "SpotifyMainWindow" || (!string.IsNullOrWhiteSpace(title) && title.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    bestWnd = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return bestWnd;
    }

    public static void LaunchMediaSource(string? sourceAumid, string? sourceApp, string? title, string? artist)
    {
        var searchTerms = BuildMediaSearchTerms(title, artist, sourceApp, sourceAumid);
        var isBrowser = IsBrowserCandidate(sourceAumid, sourceApp);

        // 1. If it's identified as browser media, search open browser tabs and windows first
        if (isBrowser)
        {
            if (TryActivateBrowserMedia(sourceAumid, searchTerms))
                return;
        }

        // 2. Search already open windows (crucial for UWP apps like Netflix App, Spotify, VLC, etc.)
        // This MUST run BEFORE any shell:AppsFolder or process launch to prevent opening duplicate windows!
        var existingWnd = FindExistingAppWindow(sourceApp, sourceAumid, title);
        if (existingWnd != IntPtr.Zero)
        {
            BringToFront(existingWnd);
            return;
        }

        // 3. Search running desktop processes by process name
        if (TryActivateDesktopApp(sourceAumid, sourceApp))
        {
            return;
        }

        // 4. Fallback browser search if media might have been from a browser but not marked upfront
        if (!isBrowser && TryActivateBrowserMedia(null, searchTerms))
        {
            return;
        }

        // 5. Only if absolutely NO window or process is already running, launch UWP app as fallback
        if (!string.IsNullOrWhiteSpace(sourceAumid) && sourceAumid.Contains('!'))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{sourceAumid}",
                    UseShellExecute = true
                });
                return;
            }
            catch { }
        }

        // 6. Last resort: general launcher
        Launch(sourceAumid, sourceApp, title, artist);
    }

    private static bool IsBrowserCandidate(string? aumid, string? appName)
    {
        var combined = $"{aumid} {appName}".ToLowerInvariant();
        if (BrowserNames.Any(b => combined.Contains(b))) return true;
        if (aumid == null || !aumid.Contains('!'))
        {
            if (combined.Contains("youtube") || combined.Contains("netflix") || combined.Contains("web app") || combined.Contains("soundcloud"))
                return true;
        }
        return false;
    }

    private static List<string> BuildMediaSearchTerms(string? title, string? artist, string? sourceApp, string? aumid)
    {
        var terms = new List<string>();

        void AddTerm(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            var s = t.Trim();
            if (s.Length >= 2 && !terms.Any(existing => existing.Equals(s, StringComparison.OrdinalIgnoreCase)))
            {
                terms.Add(s);
            }
        }

        if (!string.IsNullOrWhiteSpace(title) && !title.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase))
        {
            AddTerm(title);

            // Clean common suffix tags
            var cleaned = Regex.Replace(title, @"\s*[-|]\s*(YouTube|Netflix|Spotify|SoundCloud|Twitch).*$", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*[\(\[](official\s*(music\s*)?video|lyrics?(\s*video)?|audio|hd|4k|remastered)[\)\]]", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Trim(' ', '-', '|');
            AddTerm(cleaned);

            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
            {
                AddTerm(string.Join(" ", words.Take(3)));
            }
        }

        if (!string.IsNullOrWhiteSpace(artist) &&
            !artist.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase) &&
            !artist.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            AddTerm(artist);
        }

        var services = new[] { "Netflix", "YouTube", "Spotify", "SoundCloud", "Twitch", "Prime Video", "Disney+", "Hotstar", "Apple Music", "Bandcamp", "Deezer", "Tidal" };
        foreach (var svc in services)
        {
            if (sourceApp?.Contains(svc, StringComparison.OrdinalIgnoreCase) == true ||
                title?.Contains(svc, StringComparison.OrdinalIgnoreCase) == true ||
                aumid?.Contains(svc, StringComparison.OrdinalIgnoreCase) == true)
            {
                AddTerm(svc);
            }
        }

        return terms;
    }

    private static bool TryActivateBrowserMedia(string? preferredAumid, IReadOnlyList<string> searchTerms)
    {
        var browserOrder = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredAumid))
        {
            var lower = preferredAumid.ToLowerInvariant();
            var matched = BrowserNames.FirstOrDefault(b => lower.Contains(b));
            if (matched != null) browserOrder.Add(matched);
        }

        foreach (var b in BrowserNames)
        {
            if (!browserOrder.Contains(b)) browserOrder.Add(b);
        }

        IntPtr firstFallback = IntPtr.Zero;

        // Stage 1: Fast check - does any open browser window title match directly?
        foreach (var bName in browserOrder)
        {
            var hwnds = FindWindowsByProcessName(bName);
            foreach (var hWnd in hwnds)
            {
                if (firstFallback == IntPtr.Zero) firstFallback = hWnd;

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, 512);
                var winTitle = sb.ToString();

                foreach (var term in searchTerms)
                {
                    if (winTitle.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        BringToFront(hWnd);
                        return true;
                    }
                }
            }
        }

        // Stage 2: Deep tab search via UI Automation (fast TabItem lookup)
        foreach (var bName in browserOrder)
        {
            var hwnds = FindWindowsByProcessName(bName);
            foreach (var hWnd in hwnds)
            {
                foreach (var term in searchTerms)
                {
                    if (TrySwitchToBrowserTab(hWnd, term))
                    {
                        BringToFront(hWnd);
                        return true;
                    }
                }
            }
        }

        // Stage 3: If preferred browser is known (e.g. Chrome was playing), bring its window to front
        if (!string.IsNullOrWhiteSpace(preferredAumid))
        {
            var lower = preferredAumid.ToLowerInvariant();
            var matched = BrowserNames.FirstOrDefault(b => lower.Contains(b));
            if (matched != null)
            {
                var prefHwnds = FindWindowsByProcessName(matched);
                if (prefHwnds.Count > 0)
                {
                    BringToFront(prefHwnds[0]);
                    return true;
                }
            }
        }

        // Stage 4: Fallback - bring the running browser to front
        if (firstFallback != IntPtr.Zero)
        {
            BringToFront(firstFallback);
            return true;
        }

        return false;
    }

    private static bool TryActivateDesktopApp(string? aumid, string? appName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(appName)) candidates.Add(appName.Trim());
        if (!string.IsNullOrWhiteSpace(aumid))
        {
            var clean = aumid;
            if (clean.Contains('.')) clean = clean.Split('.').Last();
            if (clean.Contains('\\')) clean = Path.GetFileNameWithoutExtension(clean);
            if (!string.IsNullOrWhiteSpace(clean)) candidates.Add(clean.Trim());
        }

        try
        {
            var running = Process.GetProcesses();
            foreach (var name in candidates)
            {
                var match = running.FirstOrDefault(p =>
                {
                    try
                    {
                        return p.MainWindowHandle != IntPtr.Zero &&
                               (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                p.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));
                    }
                    catch { return false; }
                });

                if (match != null && match.MainWindowHandle != IntPtr.Zero)
                {
                    BringToFront(match.MainWindowHandle);
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static void Launch(string? appId, string? appName = null, string? title = null, string? body = null)
    {
        var textContext = $"{appId} {appName} {title} {body}";

        // 1. Internal NekoNotch alerts
        if (appId?.StartsWith("nekonotch:", StringComparison.OrdinalIgnoreCase) == true || appId?.StartsWith("orixnotch:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var tool = (appId.StartsWith("nekonotch:", StringComparison.OrdinalIgnoreCase) ? appId["nekonotch:".Length..] : appId["orixnotch:".Length..]);
            NotchWindow.Instance?.OpenTool(tool);
            return;
        }

        // 2. Web WhatsApp notification detection: switch to existing tab safely without typing into address bar
        if (textContext.Contains("web.whatsapp.com", StringComparison.OrdinalIgnoreCase) ||
            (appName?.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) == true && (appId == null || !appId.Contains('!'))))
        {
            try
            {
                // A. If ANY window currently has "WhatsApp" in its title, bring it to front directly
                var whatsAppWnd = FindWindowByTitle("WhatsApp");
                if (whatsAppWnd != IntPtr.Zero)
                {
                    BringToFront(whatsAppWnd);
                    return;
                }

                // B. Check running browsers (Zen Browser, Firefox, Chrome, Edge, Brave, etc.)
                var browsers = new[] { "zen", "firefox", "chrome", "msedge", "brave", "opera", "vivaldi" };
                IntPtr fallbackBrowserHwnd = IntPtr.Zero;

                foreach (var bName in browsers)
                {
                    var hwnds = FindWindowsByProcessName(bName);
                    foreach (var hWnd in hwnds)
                    {
                        if (fallbackBrowserHwnd == IntPtr.Zero) fallbackBrowserHwnd = hWnd;

                        // Try to find and switch to WhatsApp tab cleanly via UI Automation
                        if (TrySwitchToBrowserTab(hWnd, "WhatsApp"))
                        {
                            BringToFront(hWnd);
                            return;
                        }
                    }
                }

                // If a browser is already running, simply bring it to front without opening duplicate tabs
                if (fallbackBrowserHwnd != IntPtr.Zero)
                {
                    BringToFront(fallbackBrowserHwnd);
                    return;
                }

                // Only if NO browser is running at all, launch WhatsApp Web
                Process.Start(new ProcessStartInfo("https://web.whatsapp.com") { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        // 2.5 Check for an already-open window (handles UWP / ApplicationFrameHost apps like Netflix as well as normal windows)
        var existingWnd = FindExistingAppWindow(appName, appId, title);
        if (existingWnd != IntPtr.Zero)
        {
            BringToFront(existingWnd);
            return;
        }

        // 3. Search already-running processes by name or window title (e.g. Antigravity, Chrome, Edge, Slack, Spotify)
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(appName)) candidates.Add(appName.Trim());
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var cleanId = appId;
            if (cleanId.Contains('.')) cleanId = cleanId.Split('.').Last();
            if (cleanId.Contains('\\')) cleanId = Path.GetFileNameWithoutExtension(cleanId);
            if (!string.IsNullOrWhiteSpace(cleanId)) candidates.Add(cleanId.Trim());
        }

        try
        {
            var running = Process.GetProcesses();
            foreach (var name in candidates)
            {
                // Exact process name match first
                var match = running.FirstOrDefault(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero && p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });

                // Partial process name or window title match second
                match ??= running.FirstOrDefault(p =>
                {
                    try
                    {
                        return p.MainWindowHandle != IntPtr.Zero &&
                               (p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                p.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));
                    }
                    catch { return false; }
                });

                if (match != null)
                {
                    BringToFront(match.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        // 4. Direct executable or file path on disk
        if (!string.IsNullOrWhiteSpace(appId) && File.Exists(appId))
        {
            try
            {
                Process.Start(new ProcessStartInfo(appId) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        // 5. Real UWP / Store AppUserModelID (MUST contain '!' to avoid opening Explorer Documents)
        if (!string.IsNullOrWhiteSpace(appId) && appId.Contains('!'))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{appId}",
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        // 6. Generic web URL in body / title
        if (!string.IsNullOrWhiteSpace(body) && (body.Contains("http://") || body.Contains("https://")))
        {
            try
            {
                var words = body.Split(' ', '\n', '\r');
                var url = words.FirstOrDefault(w => w.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                    w.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                if (url != null)
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return;
                }
            }
            catch { }
        }

        // 7. Last resort: Try launching by app name command if registered in PATH
        if (!string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                Process.Start(new ProcessStartInfo(appName) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
