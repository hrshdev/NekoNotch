using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NekoNotch.Services;

/// <summary>
/// Transforms the Windows taskbar into a modern floating macOS-style dock:
/// - Smooth floating dock pill with rounded margins and elevation.
/// - Translucent frosted glass blur and layered opacity.
/// - Centered dock icons via TaskbarAl registry settings with broadcast.
/// - Safe and non-destructive: never clips through active application icons.
/// - Clean restoration of default Windows taskbar on exit or when disabled.
/// </summary>
public sealed class TaskbarService
{
    public static TaskbarService Instance { get; } = new();

    private DispatcherTimer? _timer;
    private int? _originalTaskbarAl;
    private bool _hasCapturedOriginalAl;
    private bool _lastMaximized;
    private bool _isRunning;

    // Win32 Interop
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

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
        CaptureOriginalTaskbarAlignment();

        if (App.Settings.MacTaskbarEnabled)
        {
            ApplySettings();
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => CheckPeriodicState();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer?.Stop();
        _timer = null;

        if (App.Settings.MacTaskbarRestoreOnExit)
        {
            RestoreDefaultTaskbar();
        }
    }

    private void CaptureOriginalTaskbarAlignment()
    {
        if (_hasCapturedOriginalAl) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (key?.GetValue("TaskbarAl") is int val)
            {
                _originalTaskbarAl = val;
                _hasCapturedOriginalAl = true;
            }
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

        ApplyDockStyling(_lastMaximized);
    }

    public void RestoreDefaultTaskbar()
    {
        try
        {
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
        }
        catch { }
    }

    private void CheckPeriodicState()
    {
        if (!App.Settings.MacTaskbarEnabled) return;

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
            ApplyDockStyling(isMax);
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

            // Determine opacity & accent color
            byte alpha;
            AccentState accentState;
            uint accentColor;

            if (isMaximized && App.Settings.MacTaskbarDynamicMaximized)
            {
                // Deeper frosted tint when a foreground app is maximized
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
                        alpha = 225;
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

        if (style == "Clear")
        {
            // Clear glass mode without clipping
            SetWindowRgn(tb, IntPtr.Zero, true);
            return;
        }

        // Floating rounded dock margins: safely encompasses all apps, weather, and tray
        const int marginX = 8;
        const int marginY = 4;
        const int radius = 18;

        IntPtr rgn = CreateRoundRectRgn(marginX, marginY, tbW - marginX, tbH - marginY, radius, radius);
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

    private static List<IntPtr> GetAllTaskbars()
    {
        var list = new List<IntPtr>();
        try
        {
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary == IntPtr.Zero)
            {
                try
                {
                    IntPtr hDesk = OpenDesktop("default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                    {
                        SetThreadDesktop(hDesk);
                        primary = FindWindow("Shell_TrayWnd", null);
                    }
                }
                catch { }
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
