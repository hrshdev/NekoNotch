using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NekoNotch.Services;

/// <summary>
/// Transforms the Windows taskbar into a modern floating macOS-style dock:
/// - Smooth floating dock pill with rounded margins and elevation.
/// - Full Auto-hide & Hover-to-Show support without broken hit-testing.
/// - Translucent frosted glass blur and layered opacity.
/// - Centered dock icons via TaskbarAl registry settings with broadcast.
/// - Clean restoration of default Windows taskbar on exit or when disabled.
/// </summary>
public sealed class TaskbarService
{
    public static TaskbarService Instance { get; } = new();

    private DispatcherTimer? _timer;
    private int? _originalTaskbarAl;
    private bool? _originalAutoHide;
    private bool _hasCapturedOriginals;
    private bool _lastMaximized;
    private bool _isRunning;

    // Win32 Interop
    private const int ABM_GETSTATE = 0x00000004;
    private const int ABM_SETSTATE = 0x0000000A;
    private const int ABS_AUTOHIDE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int LWA_ALPHA = 2;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    private const int WCA_ACCENT_POLICY = 19;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        CaptureOriginals();

        if (App.Settings.MacTaskbarEnabled)
        {
            ApplySettings();
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => CheckPeriodicState();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer?.Stop();
        _timer = null;

        SetTrayVisibility(true);

        if (App.Settings.MacTaskbarRestoreOnExit)
        {
            RestoreDefaultTaskbar();
        }
    }

    private void CaptureOriginals()
    {
        if (_hasCapturedOriginals) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (key?.GetValue("TaskbarAl") is int val)
            {
                _originalTaskbarAl = val;
            }

            _originalAutoHide = GetTaskbarAutoHide();
            _hasCapturedOriginals = true;
        }
        catch { }
    }

    public static bool GetTaskbarAutoHide()
    {
        try
        {
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary == IntPtr.Zero)
            {
                primary = EnsureTaskbarHandle();
            }
            if (primary == IntPtr.Zero) return false;

            var abd = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = primary
            };
            var res = SHAppBarMessage(ABM_GETSTATE, ref abd);
            return (res.ToInt32() & ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetTaskbarAutoHide(bool enable)
    {
        try
        {
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary == IntPtr.Zero)
            {
                primary = EnsureTaskbarHandle();
            }
            if (primary == IntPtr.Zero) return;

            var abd = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = primary
            };
            var cur = SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt32();
            int newState = enable ? (cur | ABS_AUTOHIDE) : (cur & ~ABS_AUTOHIDE);
            abd.lParam = new IntPtr(newState);
            SHAppBarMessage(ABM_SETSTATE, ref abd);
        }
        catch { }
    }

    private static void SetTaskbarAlignment(int align)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: true);
            if (key != null)
            {
                var cur = key.GetValue("TaskbarAl") as int? ?? -1;
                if (cur != align)
                {
                    key.SetValue("TaskbarAl", align, RegistryValueKind.DWord);
                    SendNotifyMessage(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "TraySettings");
                }
            }
        }
        catch { }
    }

    public void ApplySettings()
    {
        if (!App.Settings.MacTaskbarEnabled)
        {
            RestoreDefaultTaskbar();
            return;
        }

        if (App.Settings.MacTaskbarCenterIcons)
        {
            SetTaskbarAlignment(1);
        }
        else if (_originalTaskbarAl.HasValue)
        {
            SetTaskbarAlignment(_originalTaskbarAl.Value);
        }

        SetTrayVisibility(false);
        SetTaskbarAutoHide(App.Settings.MacTaskbarAutoHide);
        ApplyDockStyling(_lastMaximized);
    }

    public static void SetTrayVisibility(bool visible)
    {
        try
        {
            var taskbars = GetAllTaskbars();
            foreach (var tb in taskbars)
            {
                var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero)
                {
                    ShowWindow(tray, visible ? SW_SHOW : SW_HIDE);
                }
            }
        }
        catch { }
    }

    public void RestoreDefaultTaskbar()
    {
        try
        {
            SetTrayVisibility(true);

            var taskbars = GetAllTaskbars();
            foreach (var tb in taskbars)
            {
                // Clear clipping region
                SetWindowRgn(tb, IntPtr.Zero, true);

                // Restore alpha / remove WS_EX_LAYERED
                try
                {
                    var ex = GetWindowLongPtr(tb, GWL_EXSTYLE);
                    SetLayeredWindowAttributes(tb, 0, 255, LWA_ALPHA);
                    SetWindowLongPtr(tb, GWL_EXSTYLE, new IntPtr(ex.ToInt64() & ~WS_EX_LAYERED));
                }
                catch { }

                // Clear composition accent
                ApplyAccent(tb, AccentState.ACCENT_DISABLED, 0, 0);
            }

            if (_originalTaskbarAl.HasValue)
            {
                SetTaskbarAlignment(_originalTaskbarAl.Value);
            }

            if (_originalAutoHide.HasValue)
            {
                SetTaskbarAutoHide(_originalAutoHide.Value);
            }
        }
        catch { }
    }

    private void CheckPeriodicState()
    {
        if (!App.Settings.MacTaskbarEnabled) return;

        // Ensure tray remains hidden when dock feature is active
        SetTrayVisibility(false);

        bool isMax = false;
        if (App.Settings.MacTaskbarDynamicMaximized)
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && IsZoomed(fg))
                {
                    isMax = true;
                }
            }
            catch { }
        }

        if (isMax != _lastMaximized)
        {
            _lastMaximized = isMax;
        }

        ApplyDockStyling(_lastMaximized);
    }

    /// <summary>
    /// Configures Windhawk windows-11-taskbar-styler mod to match the One UI 8.5 Dark Dock style:
    /// - Theme: One_UI_8_5_variant_Dock
    /// - Squircle icons: dark theme (darkbg.png)
    /// - System tray: collapsed completely
    /// - Dock capsule: fully rounded symmetric pill (CornerRadius=25, BorderThickness=1)
    /// </summary>
    public static bool ApplyWindhawkOneUIDarkDock(out string? error)
    {
        error = null;
        try
        {
            const string regKey = @"SOFTWARE\Windhawk\Engine\Mods\windows-11-taskbar-styler\Settings";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(regKey, writable: true);
                if (key != null)
                {
                    key.SetValue("theme", "One_UI_8_5_variant_Dock", RegistryValueKind.String);
                    key.SetValue("styleConstants[0]", @"IconBackground=<ImageBrush Stretch=""Uniform""> <ImageBrush.ImageSource> <BitmapImage UriSource=""https://raw.githubusercontent.com/ramensoftware/windows-11-taskbar-styling-guide/refs/heads/main/Themes/One%20UI%208.5/Assets/darkbg.png"" DecodePixelType=""Logical"" DecodePixelWidth=""64"" DecodePixelHeight=""64"" /> </ImageBrush.ImageSource> </ImageBrush>", RegistryValueKind.String);
                    key.SetValue("controlStyles[0].target", "Taskbar.TaskbarExtensionElement", RegistryValueKind.String);
                    key.SetValue("controlStyles[0].styles[0]", "Visibility=Collapsed", RegistryValueKind.String);
                    key.SetValue("controlStyles[1].target", "Grid#RootGrid > Taskbar.TaskbarBackground > Grid", RegistryValueKind.String);
                    key.SetValue("controlStyles[1].styles[0]", "CornerRadius=25", RegistryValueKind.String);
                    key.SetValue("controlStyles[1].styles[1]", "BorderThickness=1", RegistryValueKind.String);
                    return true;
                }
            }
            catch { }

            var psScript = @"$path = 'HKLM:\SOFTWARE\Windhawk\Engine\Mods\windows-11-taskbar-styler\Settings'
if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
Set-ItemProperty -Path $path -Name 'theme' -Value 'One_UI_8_5_variant_Dock'
Set-ItemProperty -Path $path -Name 'styleConstants[0]' -Value 'IconBackground=<ImageBrush Stretch=""Uniform""> <ImageBrush.ImageSource> <BitmapImage UriSource=""https://raw.githubusercontent.com/ramensoftware/windows-11-taskbar-styling-guide/refs/heads/main/Themes/One%20UI%208.5/Assets/darkbg.png"" DecodePixelType=""Logical"" DecodePixelWidth=""64"" DecodePixelHeight=""64"" /> </ImageBrush.ImageSource> </ImageBrush>'
Set-ItemProperty -Path $path -Name 'controlStyles[0].target' -Value 'Taskbar.TaskbarExtensionElement'
Set-ItemProperty -Path $path -Name 'controlStyles[0].styles[0]' -Value 'Visibility=Collapsed'
Set-ItemProperty -Path $path -Name 'controlStyles[1].target' -Value 'Grid#RootGrid > Taskbar.TaskbarBackground > Grid'
Set-ItemProperty -Path $path -Name 'controlStyles[1].styles[0]' -Value 'CornerRadius=25'
Set-ItemProperty -Path $path -Name 'controlStyles[1].styles[1]' -Value 'BorderThickness=1'
Restart-Service Windhawk -ErrorAction SilentlyContinue
";
            var tempPs1 = Path.Combine(Path.GetTempPath(), "apply_nekonotch_dark_dock.ps1");
            File.WriteAllText(tempPs1, psScript);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                UseShellExecute = true,
                Verb = "runas"
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ApplyDockStyling(bool isMaximized)
    {
        if (!App.Settings.MacTaskbarEnabled) return;

        var taskbars = GetAllTaskbars();
        if (taskbars.Count == 0) return;

        var style = App.Settings.MacTaskbarStyle;

        foreach (var tb in taskbars)
        {
            if (!GetWindowRect(tb, out var tbRect)) continue;
            int tbW = tbRect.Right - tbRect.Left;
            int tbH = tbRect.Bottom - tbRect.Top;
            if (tbW <= 0 || tbH <= 0) continue;

            // When autohidden (height < 35), NEVER clip so bottom-edge hover works immediately
            if (tbH < 35)
            {
                SetWindowRgn(tb, IntPtr.Zero, false);
                continue;
            }

            // Determine opacity & accent color
            byte alpha;
            AccentState accentState;
            uint accentColor;

            if (isMaximized && App.Settings.MacTaskbarDynamicMaximized)
            {
                alpha = 245;
                accentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                accentColor = 0xDF181818u;
            }
            else
            {
                switch (style)
                {
                    case "Clear":
                        alpha = 140;
                        accentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                        accentColor = 0x00000000u;
                        break;
                    case "Dock":
                    case "Floating":
                        alpha = 220;
                        accentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                        accentColor = 0x351E1E1Eu;
                        break;
                    case "Acrylic":
                    default:
                        alpha = 215;
                        accentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                        accentColor = 0x301E1E1Eu;
                        break;
                }
            }

            // Apply opacity and acrylic blur
            SetTaskbarOpacity(tb, alpha);
            ApplyAccent(tb, accentState, 2, accentColor);

            // Apply safe floating dock region
            ApplyDockRegion(tb, tbRect, style);
        }
    }

    private static void ApplyDockRegion(IntPtr tb, RECT tbRect, string style)
    {
        int tbW = tbRect.Right - tbRect.Left;
        int tbH = tbRect.Bottom - tbRect.Top;

        if (tbH < 35 || style == "Clear")
        {
            SetWindowRgn(tb, IntPtr.Zero, true);
            return;
        }

        // Floating rounded dock margins:
        // By using nBottomRect = tbH, the top and lateral edges are rounded and floating (inset),
        // while the bottom edge stays flush with the screen so mouse hover is never lost.
        const int marginX = 10;
        const int marginY = 5;
        const int radius = 20;

        IntPtr rgn = CreateRoundRectRgn(marginX, marginY, tbW - marginX, tbH, radius, radius);
        if (rgn != IntPtr.Zero)
        {
            SetWindowRgn(tb, rgn, true);
        }
    }

    private static void SetTaskbarOpacity(IntPtr tb, byte alpha)
    {
        try
        {
            var ex = GetWindowLongPtr(tb, GWL_EXSTYLE);
            if ((ex.ToInt64() & WS_EX_LAYERED) == 0)
            {
                SetWindowLongPtr(tb, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_LAYERED));
            }
            SetLayeredWindowAttributes(tb, 0, alpha, LWA_ALPHA);
        }
        catch { }
    }

    private static void ApplyAccent(IntPtr hwnd, AccentState state, int flags, uint color)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = flags,
                GradientColor = color,
                AnimationId = 0
            };

            int policySize = Marshal.SizeOf(policy);
            IntPtr policyPtr = Marshal.AllocHGlobal(policySize);
            try
            {
                Marshal.StructureToPtr(policy, policyPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = policyPtr,
                    SizeOfData = policySize
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(policyPtr);
            }
        }
        catch { }
    }

    private static IntPtr EnsureTaskbarHandle()
    {
        try
        {
            IntPtr hDesk = OpenDesktop("default", 0, false, 0x01FF);
            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
                return FindWindow("Shell_TrayWnd", null);
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private static List<IntPtr> GetAllTaskbars()
    {
        var list = new List<IntPtr>();
        try
        {
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary == IntPtr.Zero)
            {
                primary = EnsureTaskbarHandle();
            }
            if (primary != IntPtr.Zero) list.Add(primary);

            EnumWindows((hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                if (GetClassName(hwnd, sb, sb.Capacity) > 0)
                {
                    if (sb.ToString() == "Shell_SecondaryTrayWnd" && !list.Contains(hwnd))
                    {
                        list.Add(hwnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }
}
