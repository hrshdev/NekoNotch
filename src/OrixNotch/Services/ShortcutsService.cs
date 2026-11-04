using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Interop;
using Drawing = System.Drawing;

namespace OrixNotch.Services;

/// <summary>A one-click action: launch or restore an app, file, folder, URL or Windows settings page.</summary>
public sealed class ShortcutItem : ObservableObject
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _target = "";
    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                _icon = null;
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(InitialLetter));
            }
        }
    }

    private string _arguments = "";
    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    [JsonIgnore]
    private ImageSource? _icon;

    [JsonIgnore]
    public ImageSource? Icon => _icon ??= ResolveIcon();

    [JsonIgnore]
    public bool HasIcon => Icon != null;

    [JsonIgnore]
    public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    [JsonIgnore]
    private bool _isDragging;

    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    [JsonIgnore]
    private bool _isDropTarget;

    [JsonIgnore]
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }


    private ImageSource? ResolveIcon()
    {
        try
        {
            var target = Target?.Trim() ?? "";
            var name = Name?.Trim() ?? "";

            // 1. Packaged Windows Apps & URI Protocols
            if (target.StartsWith("ms-settings:network-wifi", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase))
            {
                var icon = ExtractFromImageres(175);
                if (icon != null) return icon;
            }
            if (target.StartsWith("ms-settings:bluetooth", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                var icon = ExtractFromImageres(79);
                if (icon != null) return icon;
            }
            if (target.StartsWith("ms-settings:sound", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Sound", StringComparison.OrdinalIgnoreCase))
            {
                var icon = ExtractFromImageres(100);
                if (icon != null) return icon;
            }
            if (target.StartsWith("ms-settings:batterysaver", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Battery", StringComparison.OrdinalIgnoreCase))
            {
                var icon = ExtractFromImageres(141);
                if (icon != null) return icon;
            }
            if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                var uwp = ShellItem.AppIcon("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
                if (uwp != null) return uwp;
                var icon = ExtractFromImageres(110);
                if (icon != null) return icon;
            }
            if (target.StartsWith("ms-screenclip:", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Snip", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Snipping Tool", StringComparison.OrdinalIgnoreCase))
            {
                var uwp = ShellItem.AppIcon("Microsoft.ScreenSketch_8wekyb3d8bbwe!App");
                if (uwp != null) return uwp;
            }
            if (target.Equals("mspaint.exe", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("mspaint", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Paint", StringComparison.OrdinalIgnoreCase))
            {
                var uwp = ShellItem.AppIcon("Microsoft.Paint_8wekyb3d8bbwe!App");
                if (uwp != null) return uwp;
            }
            if (target.Contains("LockWorkStation", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Lock PC", StringComparison.OrdinalIgnoreCase))
            {
                var icon = ExtractFromImageres(44);
                if (icon != null) return icon;
            }
            if (target.Equals("wt.exe", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("wt", StringComparison.OrdinalIgnoreCase))
            {
                var uwp = ShellItem.AppIcon("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
                if (uwp != null) return uwp;
            }
            if (target.Equals("calc.exe", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("calc", StringComparison.OrdinalIgnoreCase))
            {
                var uwp = ShellItem.AppIcon("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
                if (uwp != null) return uwp;
            }

            // 2. Directory / Folder icon via SHGetFileInfo
            var resolved = ResolveFullPath(target);
            if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
            {
                var shfi = new Win32.SHFILEINFO();
                var res = Win32.SHGetFileInfo(resolved, 0, ref shfi, (uint)Marshal.SizeOf(shfi), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        return bmp;
                    }
                    finally
                    {
                        Win32.DestroyIcon(shfi.hIcon);
                    }
                }
            }

            // 3. Regular file / executable icon
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                using var sysIcon = Drawing.Icon.ExtractAssociatedIcon(resolved);
                if (sysIcon != null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
            }
        }
        catch { }
        return null;
    }

    private static ImageSource? ExtractFromImageres(int index)
    {
        try
        {
            var count = Win32.ExtractIconEx("imageres.dll", index, out var large, out var small, 1);
            if (large != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
                finally
                {
                    Win32.DestroyIcon(large);
                    if (small != IntPtr.Zero) Win32.DestroyIcon(small);
                }
            }
        }
        catch { }
        return null;
    }

    public static string ResolveFullPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";
        target = Environment.ExpandEnvironmentVariables(target);
        if (File.Exists(target) || Directory.Exists(target)) return target;

        // Common System32 apps
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var inSys32 = Path.Combine(sys32, target);
        if (File.Exists(inSys32)) return inSys32;
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var withExe = Path.Combine(sys32, target + ".exe");
            if (File.Exists(withExe)) return withExe;
        }

        // Common Windows root apps (e.g. explorer.exe, regedit.exe)
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var inWin = Path.Combine(winDir, target);
        if (File.Exists(inWin)) return inWin;

        if (target.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || target.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            var psPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(psPath)) return psPath;
        }
        // Common installed applications
        if (target.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) || target.Equals("chrome", StringComparison.OrdinalIgnoreCase))
        {
            var chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            if (File.Exists(chromePath)) return chromePath;
            chromePath = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
            if (File.Exists(chromePath)) return chromePath;
        }
        if (target.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || target.Equals("edge", StringComparison.OrdinalIgnoreCase))
        {
            var edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edgePath)) return edgePath;
            edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edgePath)) return edgePath;
        }
        if (target.Equals("code.exe", StringComparison.OrdinalIgnoreCase) || target.Equals("code", StringComparison.OrdinalIgnoreCase))
        {
            var codePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
            if (File.Exists(codePath)) return codePath;
        }
        if (target.Equals("wt.exe", StringComparison.OrdinalIgnoreCase) || target.Equals("wt", StringComparison.OrdinalIgnoreCase))
        {
            var wtPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(wtPath)) return wtPath;
        }

        return target;
    }
}

public sealed class ShortcutsService
{
    private const string FileName = "shortcuts.json";

    public static ShortcutsService Instance { get; } = new();

    public ObservableCollection<ShortcutItem> Items { get; }

    private ShortcutsService()
    {
        var path = Storage.PathOf(FileName);
        var items = File.Exists(path) ? Storage.Load<List<ShortcutItem>>(FileName) : Defaults();
        // Upgrade legacy default shortcuts if only the old 12 pills exist
        if (items.Count == 12 && items.Any(x => x.Name == "Snip screen") && items.Any(x => x.Name == "Lock PC") && !items.Any(x => x.Name == "Chrome" || x.Name == "Explorer"))
        {
            items = Defaults();
        }
        Items = new ObservableCollection<ShortcutItem>(items);
        if (!File.Exists(path) || (items.Count > 0 && items[0].Name == "Chrome")) Save();
    }

    public static List<ShortcutItem> Defaults() =>
    [
        new() { Name = "Chrome", Target = "chrome.exe" },
        new() { Name = "VS Code", Target = "code.exe" },
        new() { Name = "Explorer", Target = "explorer.exe" },
        new() { Name = "Terminal", Target = "wt.exe" },
        new() { Name = "Notepad", Target = "notepad.exe" },
        new() { Name = "Calculator", Target = "calc.exe" },
        new() { Name = "Task Manager", Target = "taskmgr.exe" },
        new() { Name = "Settings", Target = "ms-settings:" },
        new() { Name = "Edge", Target = "msedge.exe" },
        new() { Name = "Paint", Target = "mspaint.exe" },
        new() { Name = "Cmd", Target = "cmd.exe" },
        new() { Name = "Snip", Target = "ms-screenclip:" },
        new() { Name = "Downloads", Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
        new() { Name = "Documents", Target = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
        new() { Name = "Bluetooth", Target = "ms-settings:bluetooth" },
        new() { Name = "Wi-Fi", Target = "ms-settings:network-wifi" },
        new() { Name = "Sound", Target = "ms-settings:sound" },
        new() { Name = "Control Panel", Target = "control.exe" },
        new() { Name = "Lock PC", Target = "rundll32.exe", Arguments = "user32.dll,LockWorkStation" },
        new() { Name = "Battery", Target = "ms-settings:batterysaver" }
    ];

    public static bool Run(ShortcutItem item, out string? error) => ActivateOrLaunch(item, forceNewInstance: false, out error);

    public static bool ActivateOrLaunch(ShortcutItem item, bool forceNewInstance, out string? error)
    {
        error = null;
        try
        {
            if (!forceNewInstance)
            {
                // Attempt to find and restore existing window session first
                if (TryActivateExisting(item))
                {
                    return true;
                }
            }

            // Launch fresh instance if not running or forced
            var resolved = ShortcutItem.ResolveFullPath(item.Target);
            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(resolved) ? item.Target : resolved,
                Arguments = item.Arguments ?? "",
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryActivateExisting(ShortcutItem item)
    {
        try
        {
            var target = item.Target;
            if (string.IsNullOrWhiteSpace(target)) return false;

            if (target.Contains("rundll32", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("ms-screenclip:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var fileName = Path.GetFileNameWithoutExtension(target);
            if (!string.IsNullOrWhiteSpace(fileName)) candidates.Add(fileName);
            if (!string.IsNullOrWhiteSpace(item.Name)) candidates.Add(item.Name);

            // Special application process mappings
            if (fileName.Equals("calc", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("CalculatorApp");
                candidates.Add("Calculator");
            }
            else if (fileName.Equals("wt", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("WindowsTerminal");
            }
            else if (fileName.Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Code");
            }

            var running = Process.GetProcesses();
            foreach (var name in candidates)
            {
                foreach (var proc in running)
                {
                    try
                    {
                        if (proc.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(proc.MainWindowTitle) && proc.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            var hWnd = proc.MainWindowHandle;
                            if (hWnd != IntPtr.Zero && Win32.IsWindowVisible(hWnd))
                            {
                                BringToFront(hWnd);
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private static void BringToFront(IntPtr hWnd)
    {
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            else ShowWindow(hWnd, SwShow);
            SetForegroundWindow(hWnd);
            SwitchToThisWindow(hWnd, true);
        }
        catch { }
    }

    public void Add(ShortcutItem item)
    {
        Items.Add(item);
        Save();
    }

    public void Remove(ShortcutItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Save() => Storage.Save(FileName, Items.ToList());
}
