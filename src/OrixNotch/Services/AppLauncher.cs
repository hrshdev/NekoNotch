using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        // 2. Web WhatsApp notification detection
        if (textContext.Contains("web.whatsapp.com", StringComparison.OrdinalIgnoreCase) ||
            (appName?.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) == true && (appId == null || !appId.Contains('!'))))
        {
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero &&
                            proc.MainWindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                        {
                            BringToFront(proc.MainWindowHandle);
                            return;
                        }
                    }
                    catch { }
                }

                // If no active window with "WhatsApp" title, open WhatsApp Web in the user's browser
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
