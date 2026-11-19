using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NekoNotch.Services;

/// <summary>
/// Transforms the Windows taskbar into a macOS-style dock:
/// - Frosted glass acrylic blur or crystal clear glass via SetWindowCompositionAttribute.
/// - Centered dock icons via TaskbarAl registry settings with broadcast.
/// - Window corner preferences via DWM.
/// - Dynamic opacity adaptation when a window is maximized.
/// - Clean restoration of default Windows taskbar on exit.
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
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int WCA_ACCENT_POLICY = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
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
        ApplySettings();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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

        ApplyCompositionOnly(_lastMaximized);
    }

    public void RestoreDefaultTaskbar()
    {
        try
        {
            var taskbars = GetAllTaskbars();
            foreach (var tb in taskbars)
            {
                ApplyAccent(tb, AccentState.ACCENT_DISABLED, 0, 0);
                try
                {
                    int donotround = DWMWCP_DONOTROUND;
                    DwmSetWindowAttribute(tb, DWMWA_WINDOW_CORNER_PREFERENCE, ref donotround, sizeof(int));
                }
                catch { }
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
            ApplyCompositionOnly(isMax);
        }
        else
        {
            // Re-apply periodically in case of Explorer restart or refresh
            ApplyCompositionOnly(_lastMaximized);
        }
    }

    private void ApplyCompositionOnly(bool isMaximized)
    {
        if (!App.Settings.MacTaskbarEnabled) return;

        var taskbars = GetAllTaskbars();
        if (taskbars.Count == 0) return;

        AccentState state;
        int flags = 2;
        uint color;
        int cornerPref = (App.Settings.MacTaskbarStyle == "Dock") ? DWMWCP_ROUND : DWMWCP_DONOTROUND;

        if (isMaximized && App.Settings.MacTaskbarDynamicMaximized)
        {
            state = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
            color = (0xD0u << 24) | (0x12u << 16) | (0x12u << 8) | 0x12u;
        }
        else
        {
            switch (App.Settings.MacTaskbarStyle)
            {
                case "Clear":
                    state = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    color = 0x00000000u;
                    break;
                case "Dock":
                    state = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    color = (0x45u << 24) | (0x1Au << 16) | (0x1Au << 8) | 0x1Au;
                    break;
                case "Acrylic":
                default:
                    state = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    color = (0x30u << 24) | (0x1Eu << 16) | (0x1Eu << 8) | 0x1Eu;
                    break;
            }
        }

        foreach (var tb in taskbars)
        {
            ApplyAccent(tb, state, flags, color);
            try
            {
                DwmSetWindowAttribute(tb, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            }
            catch { }
        }
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
