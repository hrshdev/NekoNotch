using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using OrixNotch.Shell;

namespace OrixNotch.Services;

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

    private const int SwRestore = 9;
    private const int SwShow = 5;

    private static bool BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            else ShowWindow(hWnd, SwShow);
            return SetForegroundWindow(hWnd);
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

    private static IntPtr FindWindowByProcessName(string processName)
    {
        try
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName(processName).Select(p => (uint)p.Id));
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
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
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool TrySwitchToBrowserTab(IntPtr hWnd, string tabTitleSubstring)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null) return false;

            var tabCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, tabCond);
            foreach (AutomationElement tab in tabs)
            {
                try
                {
                    var name = tab.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.Contains(tabTitleSubstring, StringComparison.OrdinalIgnoreCase))
                    {
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern) &&
                            selPattern is SelectionItemPattern sip)
                        {
                            sip.Select();
                            return true;
                        }
                        if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invPattern) &&
                            invPattern is InvokePattern ip)
                        {
                            ip.Invoke();
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

    public static void Launch(string? appId, string? appName = null, string? title = null, string? body = null)
    {
        var textContext = $"{appId} {appName} {title} {body}";

        // 1. Internal NekoNotch alerts
        if (appId?.StartsWith("orixnotch:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var tool = appId["orixnotch:".Length..];
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
                // Use UI Automation to switch directly to the "WhatsApp" tab without blind keystrokes
                var browsers = new[] { "zen", "firefox", "chrome", "msedge", "brave", "opera", "vivaldi" };
                IntPtr fallbackBrowserHwnd = IntPtr.Zero;

                foreach (var bName in browsers)
                {
                    var hWnd = FindWindowByProcessName(bName);
                    if (hWnd != IntPtr.Zero)
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

                // If a browser was found but no WhatsApp tab could be selected, open web.whatsapp.com directly
                // (NEVER inject keystrokes into the address bar which corrupts user navigation)
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
