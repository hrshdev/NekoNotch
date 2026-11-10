using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace NekoNotch.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// Checks for newer NekoNotch versions and handles notifications and automated seamless updates.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private const string ReleasesApi = "https://api.github.com/repos/hrshdev/NekoNotch";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    public static UpdateService Instance { get; } = new();

    private readonly DispatcherTimer _timer = new();
    private StoreContext? _store;
    private IReadOnlyList<StorePackageUpdate>? _storeUpdates;
    private string? _releaseUrl;
    private GitHubAsset? _exeAsset;
    private GitHubAsset? _msiAsset;

    [ObservableProperty] private UpdateStatus _status;
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private DateTime? _checkedAt;

    /// <summary>Fired when an update is available (for notch peek notification).</summary>
    public event Action<string>? Available;

    /// <summary>Fired when an update download starts or progresses.</summary>
    public event Action<string>? Downloading;

    /// <summary>Fired when an update is ready and restarting.</summary>
    public event Action<string>? ReadyToRestart;

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(fvi.ProductVersion))
                    {
                        var pv = fvi.ProductVersion.Split('+')[0].Trim();
                        if (Version.TryParse(pv, out var v)) return v.ToString(3);
                    }
                    if (!string.IsNullOrEmpty(fvi.FileVersion))
                    {
                        if (Version.TryParse(fvi.FileVersion, out var v)) return v.ToString(3);
                    }
                }
            }
            catch { }
            return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.9.0";
        }
    }

    private readonly SemaphoreSlim _installLock = new(1, 1);

    private UpdateService()
    {
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            await CheckAsync(userAsked: false);
        };

        try
        {
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == PowerModes.Resume)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        await CheckAsync(userAsked: false);
                    });
                }
            };
            SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        await CheckAsync(userAsked: false);
                    });
                }
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
            {
                if (e.IsAvailable)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        await CheckAsync(userAsked: false);
                    });
                }
            };
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public void Start()
    {
        _timer.Stop();
        if (!App.Settings.CheckForUpdates) return;
        _timer.Interval = int.TryParse(Environment.GetEnvironmentVariable("ORIXNOTCH_UPDATE_DELAY"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : FirstCheck;
        _timer.Start();
    }

    public async Task CheckAsync(bool userAsked)
    {
        if (Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing) return;
        Status = UpdateStatus.Checking;
        try
        {
            var found = StartupService.IsPackaged ? await CheckStoreAsync() : await CheckGitHubAsync();
            CheckedAt = DateTime.Now;
            Status = found ? UpdateStatus.Available : UpdateStatus.UpToDate;

            if (found)
            {
                if (App.Settings.AutoInstallUpdates)
                {
                    // Automatic update: download and apply seamlessly in background
                    _ = InstallAsync(silent: true);
                }
                else
                {
                    // Notify user so they can click to install
                    if (!userAsked) NotifyOnce();
                    else Available?.Invoke(LatestVersion);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status = UpdateStatus.Failed;
        }
    }

    private void NotifyOnce()
    {
        var key = StartupService.IsPackaged ? "store:" + string.Join(",", _storeUpdates!.Select(u => u.Package.Id.Version.ToVersionString())) : LatestVersion;
        if (App.Settings.UpdateNotified == key) return;
        App.Settings.UpdateNotified = key;
        SettingsService.Save(notify: false);
        Available?.Invoke(LatestVersion);
    }

    private async Task<bool> CheckGitHubAsync()
    {
        var api = Environment.GetEnvironmentVariable("ORIXNOTCH_UPDATE_BASE") is { Length: > 0 } b ? b.TrimEnd('/') : ReleasesApi;
        using var req = new HttpRequestMessage(HttpMethod.Get, api + "/releases/latest");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NekoNotch/" + CurrentVersion);
        using var resp = await Http.Client.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return false;

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>();
        if (release?.TagName is not { Length: > 0 } tag) return false;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(CurrentVersion, out var current)) return false;

        _releaseUrl = release.HtmlUrl;
        _exeAsset = release.Assets?.FirstOrDefault(a => a.Name?.Equals("NekoNotch.exe", StringComparison.OrdinalIgnoreCase) == true);
        _msiAsset = release.Assets?.FirstOrDefault(a => a.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);
        LatestVersion = latest.ToString(3);
        return latest > current;
    }

    private async Task<bool> CheckStoreAsync()
    {
        _store ??= CreateStoreContext();
        _storeUpdates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();
        if (_storeUpdates.Count == 0) return false;
        LatestVersion = _storeUpdates[0].Package.Id.Version.ToVersionString();
        try
        {
            if (_store.CanSilentlyDownloadStorePackageUpdates)
                await _store.TrySilentDownloadStorePackageUpdatesAsync(_storeUpdates);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return true;
    }

    private static StoreContext CreateStoreContext()
    {
        var context = StoreContext.GetDefault();
        if (Application.Current?.MainWindow is { } window && new WindowInteropHelper(window).Handle is var hwnd && hwnd != IntPtr.Zero)
            WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
        return context;
    }

    public async Task InstallAsync(bool silent = false)
    {
        if (!await _installLock.WaitAsync(0)) return;
        try
        {
            if (StartupService.IsPackaged)
        {
            try
            {
                if (_store is not null && _storeUpdates is { Count: > 0 } updates)
                {
                    var result = await _store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                    if (result.OverallState is StorePackageUpdateState.Completed or StorePackageUpdateState.Canceled) return;
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
            Open($"ms-windows-store://pdp/?PFN={Package.Current.Id.FamilyName}");
            return;
        }

        var currentExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NekoNotch.exe");

        // 1. MSI update if installed via MSI (must take precedence over portable exe update)
        if (IsMsiInstall && _msiAsset is not null)
        {
            Status = UpdateStatus.Downloading;
            Downloading?.Invoke(LatestVersion);
            var msi = await DownloadMsiAsync(_msiAsset, LatestVersion);
            if (msi is not null)
            {
                Status = UpdateStatus.Installing;
                ReadyToRestart?.Invoke(LatestVersion);
                await Task.Delay(1000);
                ApplyMsiUpdateAndRestart(msi, currentExe);
                return;
            }
            Status = UpdateStatus.Failed;
        }

        // 2. Direct self-update for standalone portable NekoNotch.exe
        if (!IsMsiInstall && _exeAsset is not null && File.Exists(currentExe))
        {
            Status = UpdateStatus.Downloading;
            Downloading?.Invoke(LatestVersion);

            var dir = Path.GetDirectoryName(currentExe)!;
            var newExePath = Path.Combine(dir, "NekoNotch.exe.new");
            var downloaded = await DownloadExeAsync(_exeAsset, newExePath);
            if (downloaded)
            {
                Status = UpdateStatus.Installing;
                ReadyToRestart?.Invoke(LatestVersion);
                await Task.Delay(1000);
                ApplyExeUpdateAndRestart(currentExe, newExePath);
                return;
            }
            Status = UpdateStatus.Failed;
        }

        if (!silent)
        {
            Open(_releaseUrl ?? "https://github.com/hrshdev/NekoNotch/releases/latest");
        }
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static bool IsMsiInstall { get; } = DetectMsiInstall();

    private static bool DetectMsiInstall()
    {
        try
        {
            var dir = Registry.GetValue(@"HKEY_CURRENT_USER\Software\NekoNotch", "InstallDir", null) as string 
                   ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\NekoNotch", "InstallDir", null) as string ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\OrixNotch", "InstallDir", null) as string;
            if (dir == null) return false;
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');
            return string.Equals(Norm(dir), Norm(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static readonly HttpClient Download = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NekoNotch/" + CurrentVersion);
        return client;
    }

    private static async Task<bool> DownloadExeAsync(GitHubAsset asset, string destinationPath)
    {
        try
        {
            var url = asset.Url;
            if (string.IsNullOrWhiteSpace(url)) return false;

            var part = destinationPath + ".part";
            if (File.Exists(part)) File.Delete(part);

            using var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(part))
            {
                await response.Content.CopyToAsync(file);
            }

            var info = new FileInfo(part);
            if (info.Length < 10 * 1024 * 1024)
            {
                File.Delete(part);
                return false;
            }

            File.Move(part, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    private static async Task<string?> DownloadMsiAsync(GitHubAsset asset, string version)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(asset.Url)) return null;

            var dir = Path.Combine(Path.GetTempPath(), "NekoNotch-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"NekoNotch-{version}-setup.msi");
            var part = path + ".part";

            using var response = await Download.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(part))
            {
                await response.Content.CopyToAsync(file);
            }

            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private static void ApplyExeUpdateAndRestart(string currentExe, string newExePath)
    {
        try
        {
            var updateDir = Path.Combine(Path.GetTempPath(), "NekoNotch-update");
            Directory.CreateDirectory(updateDir);
            var scriptPath = Path.Combine(updateDir, "update-exe.cmd");

            var script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "taskkill /F /IM NekoNotch.exe >nul 2>&1\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                ":retry\r\n" +
                "move /y \"%~1\" \"%~2\" >nul 2>&1\r\n" +
                "if not errorlevel 1 goto success\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "goto retry\r\n" +
                ":success\r\n" +
                "start \"\" \"%~2\"\r\n" +
                "exit /b 0\r\n";

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\" \"{newExePath}\" \"{currentExe}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void ApplyMsiUpdateAndRestart(string msi, string exePath)
    {
        try
        {
            var updateDir = Path.Combine(Path.GetTempPath(), "NekoNotch-update");
            Directory.CreateDirectory(updateDir);
            var scriptPath = Path.Combine(updateDir, "update-msi.cmd");

            // Reliable external script that terminates any running instance, runs silent MSI upgrade, and restarts NekoNotch
            var script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "taskkill /F /IM NekoNotch.exe >nul 2>&1\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "msiexec.exe /i \"%~1\" /qn REBOOT=ReallySuppress LAUNCHAPP=0\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "start \"\" \"%~2\"\r\n" +
                "exit /b 0\r\n";

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\" \"{msi}\" \"{exePath}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
}

internal static class PackageVersionExtensions
{
    public static string ToVersionString(this PackageVersion v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
