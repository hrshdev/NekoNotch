using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace OrixNotch.Services;

/// <summary>
/// Hardware and OS control manager for Wi-Fi, Bluetooth, Airplane Mode,
/// Night Light (display warmth), Battery Saver (power efficiency), and Do Not Disturb.
/// </summary>
public static class SystemControlsService
{
    // ==================== 1. WI-FI & BLUETOOTH RADIOS ====================

    private static bool _radioAccessRequested;

    private static async Task<bool> EnsureRadioAccessAsync()
    {
        if (_radioAccessRequested) return true;
        try
        {
            var status = await Radio.RequestAccessAsync();
            _radioAccessRequested = status == RadioAccessStatus.Allowed;
            return _radioAccessRequested;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Radio?> GetRadioAsync(RadioKind kind)
    {
        try
        {
            if (!await EnsureRadioAccessAsync()) return null;
            var radios = await Radio.GetRadiosAsync();
            foreach (var r in radios)
            {
                if (r.Kind == kind) return r;
            }
        }
        catch { }
        return null;
    }

    public static async Task<bool> IsWifiOnAsync()
    {
        var radio = await GetRadioAsync(RadioKind.WiFi);
        if (radio != null) return radio.State == RadioState.On;
        return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
    }

    public static async Task<bool> SetWifiAsync(bool enable)
    {
        var radio = await GetRadioAsync(RadioKind.WiFi);
        if (radio != null)
        {
            var res = await radio.SetStateAsync(enable ? RadioState.On : RadioState.Off);
            return res == RadioAccessStatus.Allowed;
        }
        return false;
    }

    public static async Task<bool> ToggleWifiAsync()
    {
        var current = await IsWifiOnAsync();
        var next = !current;
        await SetWifiAsync(next);
        return next;
    }

    public static async Task<bool> IsBluetoothOnAsync()
    {
        var radio = await GetRadioAsync(RadioKind.Bluetooth);
        if (radio != null) return radio.State == RadioState.On;
        return false;
    }

    public static async Task<bool> SetBluetoothAsync(bool enable)
    {
        var radio = await GetRadioAsync(RadioKind.Bluetooth);
        if (radio != null)
        {
            var res = await radio.SetStateAsync(enable ? RadioState.On : RadioState.Off);
            return res == RadioAccessStatus.Allowed;
        }
        return false;
    }

    public static async Task<bool> ToggleBluetoothAsync()
    {
        var current = await IsBluetoothOnAsync();
        var next = !current;
        await SetBluetoothAsync(next);
        return next;
    }

    // ==================== 2. AIRPLANE MODE (COM IRadioManager) ====================

    private static readonly Guid CLSID_RadioManagementAPI = new("581333F6-28DB-41BE-BC7A-FF201F12F3F6");
    private static readonly Guid IID_IRadioManager = new("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSystemRadioStateDelegate(IntPtr instance, out int pbEnabled, out int unknown1, out int unknown2);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSystemRadioStateDelegate(IntPtr instance, int bEnabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr instance);

    public static bool IsAirplaneModeOn()
    {
        IntPtr ppv = IntPtr.Zero;
        Guid clsid = CLSID_RadioManagementAPI;
        Guid iid = IID_IRadioManager;
        try
        {
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out ppv);
            if (hr != 0 || ppv == IntPtr.Zero) return false;

            IntPtr vtable = Marshal.ReadIntPtr(ppv);
            IntPtr getMethodPtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var getSystemRadioState = Marshal.GetDelegateForFunctionPointer<GetSystemRadioStateDelegate>(getMethodPtr);

            int state = 0, d1 = 0, d2 = 0;
            int r = getSystemRadioState(ppv, out state, out d1, out d2);
            // In Windows Radio Management: state 0 = Radios OFF (Airplane mode ON); 1 = Radios ON (Airplane mode OFF)
            return r == 0 && state == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ppv != IntPtr.Zero)
            {
                IntPtr vtable = Marshal.ReadIntPtr(ppv);
                IntPtr relPtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(relPtr);
                rel(ppv);
            }
        }
    }

    public static bool SetAirplaneMode(bool enable)
    {
        IntPtr ppv = IntPtr.Zero;
        Guid clsid = CLSID_RadioManagementAPI;
        Guid iid = IID_IRadioManager;
        try
        {
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out ppv);
            if (hr != 0 || ppv == IntPtr.Zero) return false;

            IntPtr vtable = Marshal.ReadIntPtr(ppv);
            IntPtr setMethodPtr = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size);
            var setSystemRadioState = Marshal.GetDelegateForFunctionPointer<SetSystemRadioStateDelegate>(setMethodPtr);

            // 0 = cut radios (Airplane ON), 1 = restore radios (Airplane OFF)
            int target = enable ? 0 : 1;
            int r = setSystemRadioState(ppv, target);
            return r == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ppv != IntPtr.Zero)
            {
                IntPtr vtable = Marshal.ReadIntPtr(ppv);
                IntPtr relPtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(relPtr);
                rel(ppv);
            }
        }
    }

    public static bool ToggleAirplaneMode()
    {
        var current = IsAirplaneModeOn();
        var next = !current;
        SetAirplaneMode(next);
        return next;
    }

    // ==================== 3. NIGHT LIGHT / SCREEN WARMTH (Gamma Ramp) ====================

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref GammaRamp lpRamp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref GammaRamp lpRamp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct GammaRamp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    private static bool _nightLightActive;

    public static bool IsNightLightOn() => _nightLightActive;

    public static bool SetNightLight(bool enable)
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;

        try
        {
            GammaRamp ramp = new()
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };

            for (int i = 0; i < 256; i++)
            {
                int val = i * 257;
                ramp.Red[i] = (ushort)val;
                if (enable)
                {
                    // Warm amber tone: preserve red, slight reduction in green, significant reduction in blue
                    ramp.Green[i] = (ushort)(val * 0.88);
                    ramp.Blue[i] = (ushort)(val * 0.68);
                }
                else
                {
                    ramp.Green[i] = (ushort)val;
                    ramp.Blue[i] = (ushort)val;
                }
            }

            bool ok = SetDeviceGammaRamp(hdc, ref ramp);
            if (ok) _nightLightActive = enable;
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static bool ToggleNightLight()
    {
        var next = !_nightLightActive;
        SetNightLight(next);
        return next;
    }

    // ==================== 4. BATTERY SAVER / POWER EFFICIENCY ====================

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(ref Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    private static readonly Guid EfficiencyModeGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedModeGuid = new("00000000-0000-0000-0000-000000000000");

    public static bool IsBatterySaverOn()
    {
        try
        {
            Guid actual;
            if (PowerGetActualOverlayScheme(out actual) == 0)
            {
                if (actual == EfficiencyModeGuid) return true;
            }
            return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetBatterySaver(bool enable)
    {
        try
        {
            Guid target = enable ? EfficiencyModeGuid : BalancedModeGuid;
            uint r = PowerSetActiveOverlayScheme(ref target);
            return r == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ToggleBatterySaver()
    {
        var next = !IsBatterySaverOn();
        SetBatterySaver(next);
        return next;
    }

    // ==================== 5. DO NOT DISTURB (Focus Assist) ====================

    private const string NotificationRegPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ToastsValueName = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int ZwUpdateWnfStateData(
        ref ulong StateName,
        IntPtr Buffer,
        int Length,
        Guid TypeId,
        IntPtr ExplicitScope,
        uint MatchingChangeStamp,
        bool CheckStamp);

    private const ulong WNF_SHEL_QUIET_MOMENT_SHELL_MODE_CHANGED = 0xd83063ea3bf5075UL;

    public static bool IsDndOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NotificationRegPath);
            if (key != null)
            {
                var val = key.GetValue(ToastsValueName);
                if (val is int intVal)
                {
                    // 0 = Toasts disabled (DND ON); 1 = Toasts enabled (DND OFF)
                    return intVal == 0;
                }
            }
        }
        catch { }
        return false;
    }

    public static bool SetDnd(bool enable)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(NotificationRegPath))
            {
                key?.SetValue(ToastsValueName, enable ? 0 : 1, RegistryValueKind.DWord);
            }

            // Also signal shell quiet moment via WNF
            try
            {
                var stateName = WNF_SHEL_QUIET_MOMENT_SHELL_MODE_CHANGED;
                byte[] payload = enable ? [0x02, 0x00, 0x00, 0x00] : [0x00, 0x00, 0x00, 0x00];
                IntPtr bufferPtr = Marshal.AllocHGlobal(payload.Length);
                try
                {
                    Marshal.Copy(payload, 0, bufferPtr, payload.Length);
                    ZwUpdateWnfStateData(ref stateName, bufferPtr, payload.Length, Guid.Empty, IntPtr.Zero, 0, false);
                }
                finally
                {
                    Marshal.FreeHGlobal(bufferPtr);
                }
            }
            catch { }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ToggleDnd()
    {
        var next = !IsDndOn();
        SetDnd(next);
        return next;
    }
}
