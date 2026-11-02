using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>
/// Launches or focuses the corresponding Windows application from a notification's AppId or AppName.
/// </summary>
public static class AppLauncher
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    public static void Launch(string? appId, string? appName = null)
    {
        if (string.IsNullOrWhiteSpace(appId) && string.IsNullOrWhiteSpace(appName)) return;

        // 1. Internal OrixNotch alerts
        if (appId?.StartsWith("orixnotch:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var tool = appId["orixnotch:".Length..];
            NotchWindow.Instance?.OpenTool(tool);
            return;
        }

        // 2. Direct executable or file path
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

        // 3. Launch or focus via Windows Shell AppsFolder (UWP, Store, MSIX, and registered Win32 AUMIDs)
        if (!string.IsNullOrWhiteSpace(appId))
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

        // 4. Fallback: If shell:AppsFolder was unable to activate, search running processes to restore and focus
        var searchName = !string.IsNullOrWhiteSpace(appName) ? appName : appId;
        if (!string.IsNullOrWhiteSpace(searchName))
        {
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;
                        if (proc.ProcessName.Contains(searchName, StringComparison.OrdinalIgnoreCase) ||
                            proc.MainWindowTitle.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                        {
                            ShowWindow(proc.MainWindowHandle, SwRestore);
                            SetForegroundWindow(proc.MainWindowHandle);
                            return;
                        }
                    }
                    catch
                    {
                        // Some system processes reject process name inspection
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }
}
