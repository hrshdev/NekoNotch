using System;
using System.IO;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace NekoNotch.Services;

/// <summary>
/// "Open at login". Manages instant startup upon user logon:
/// - Windows Startup folder shortcut (executes immediately on desktop load, avoiding sequential Run delays)
/// - Per-user Run registry key (standard fallback)
/// - StartupApproved registration (marks explicitly enabled in Task Manager / Windows Explorer)
/// - Disable Explorer startup delay (StartupDelayInMSec = 0)
/// - Packaged MSIX StartupTask when running as a packaged app
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NekoNotch";
    private const string TaskId = "NekoNotchAutostart";
    private const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApprovedFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string SerializeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";

    /// <summary>True when running from an MSIX package (Store or sideloaded).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>The user switched startup off in Task Manager; the app may not override that.</summary>
    public static bool DisabledByUser { get; private set; }

    private static bool DetectPackage()
    {
        try
        {
            return Package.Current is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// At launch: turn startup on for new installs (once), then make Windows match the setting.
    /// The packaged state can change behind our back (Task Manager), so it is read back into the setting.
    /// </summary>
    public static async Task SyncAsync()
    {
        var settings = App.Settings;
        if (!settings.StartupDefaultApplied)
        {
            settings.LaunchAtStartup = true;
            settings.StartupDefaultApplied = true;
            SettingsService.Save(notify: false);
        }

        if (IsPackaged)
        {
            var state = await GetTaskStateAsync();
            DisabledByUser = state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
            var enabled = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            if (settings.LaunchAtStartup && !enabled && !DisabledByUser) enabled = await ApplyAsync(true);
            else if (!settings.LaunchAtStartup && enabled) await ApplyAsync(false);
            else if (DisabledByUser && settings.LaunchAtStartup)
            {
                settings.LaunchAtStartup = false;
                SettingsService.Save(notify: false);
            }
            return;
        }

        await ApplyAsync(settings.LaunchAtStartup);
    }

    /// <summary>Turns open-at-login on or off. Returns whether it is on afterwards.</summary>
    public static async Task<bool> ApplyAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    task.Disable();
                    return false;
                }
                var state = await task.RequestEnableAsync();
                DisabledByUser = state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NekoNotch.exe");
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var shortcutPath = Path.Combine(startupFolder, "NekoNotch.lnk");

            // 1. Manage Startup folder shortcut (instant launch on login, no sequential queue delays)
            if (enabled && File.Exists(exePath))
            {
                CreateShortcut(shortcutPath, exePath, Path.GetDirectoryName(exePath) ?? "");
                SetStartupApproved(StartupApprovedFolderKey, "NekoNotch.lnk", true);
            }
            else
            {
                try { if (File.Exists(shortcutPath)) File.Delete(shortcutPath); } catch { }
                SetStartupApproved(StartupApprovedFolderKey, "NekoNotch.lnk", false);
            }

            // 2. Manage HKCU Run key (fallback)
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null)
            {
                try { key.DeleteValue("OrixNotch", throwOnMissingValue: false); } catch { }

                if (enabled)
                {
                    var command = $"\"{exePath}\" --startup";
                    key.SetValue(ValueName, command);
                    SetStartupApproved(StartupApprovedRunKey, ValueName, true);
                    DisableWindowsStartupDelay();
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    SetStartupApproved(StartupApprovedRunKey, ValueName, false);
                }
            }

            return enabled;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = "--startup";
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = "NekoNotch Dynamic Island";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void SetStartupApproved(string keyPath, string valueName, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key == null) return;
            // 0x02 indicates explicitly enabled in Windows Explorer / Task Manager, 0x03 indicates disabled
            byte[] bytes = enabled
                ? [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
                : [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
            key.SetValue(valueName, bytes, RegistryValueKind.Binary);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void DisableWindowsStartupDelay()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SerializeKey, writable: true);
            key?.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static async Task<StartupTaskState?> GetTaskStateAsync()
    {
        try
        {
            return (await StartupTask.GetAsync(TaskId)).State;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }
}
