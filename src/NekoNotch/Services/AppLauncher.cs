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

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private static readonly string[] BrowserNames =
    {
        "zen", "firefox", "chrome", "msedge", "brave", "opera", "vivaldi", "arc", "chromium", "waterfox"
    };

    private static bool BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            else ShowWindow(hWnd, SwShow);
            SetForegroundWindow(hWnd);
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

    private static bool TrySwitchToBrowserTab(IntPtr hWnd, string tabTitleSubstring)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null) return false;

            // Search across all tab-like control types (tabs, lists, custom XUL sidebar items, buttons, panes)
            var tabCond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)
            );

            var elements = root.FindAll(TreeScope.Descendants, tabCond);
            foreach (AutomationElement tab in elements)
            {
                try
                {
                    var name = tab.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.Contains(tabTitleSubstring, StringComparison.OrdinalIgnoreCase))
                    {
                        // 1. SelectionItemPattern
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern) &&
                            selPattern is SelectionItemPattern sip)
                        {
                            sip.Select();
                            return true;
                        }

                        // 2. InvokePattern
                        if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invPattern) &&
                            invPattern is InvokePattern ip)
                        {
                            ip.Invoke();
                            return true;
                        }

                        // 3. Click bounding rectangle on screen
                        var rect = tab.Current.BoundingRectangle;
                        if (rect.Width > 0 && rect.Height > 0)
                        {
                            BringToFront(hWnd);
                            Thread.Sleep(50);
                            var cx = (int)(rect.Left + rect.Width / 2);
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
    public static void LaunchMediaSource(string? sourceAumid, string? sourceApp, string? title, string? artist)
    {
        var searchTerms = BuildMediaSearchTerms(title, artist, sourceApp, sourceAumid);
        var isBrowser = IsBrowserCandidate(sourceAumid, sourceApp);

        if (isBrowser)
        {
            if (TryActivateBrowserMedia(sourceAumid, searchTerms))
                return;
        }

        // Check if it's a UWP / Store App (contains '!')
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

        // Search running desktop processes
        if (TryActivateDesktopApp(sourceAumid, sourceApp))
        {
            return;
        }

        // Fallback: search browser tabs even if not identified as a browser upfront
        if (!isBrowser && TryActivateBrowserMedia(null, searchTerms))
        {
            return;
        }

        // Last resort: general launcher
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

        // Stage 1: Fast check - does any browser window title match?
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

        // Stage 2: Deep tab search via UI Automation
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

        // Stage 3: Fallback - bring the running browser to front
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
