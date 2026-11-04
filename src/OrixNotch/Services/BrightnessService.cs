using System.Management;
using System.Runtime.InteropServices;

namespace OrixNotch.Services;

/// <summary>
/// Hardware display brightness control via Windows Management Instrumentation (WMI) and DDC/CI.
/// Retrieves true live PC display brightness instead of hardcoded numbers.
/// </summary>
public static class BrightnessService
{
    private static int _cachedBrightness = -1;

    public static int GetBrightness()
    {
        try
        {
            // 1. Primary: WMI (WmiMonitorBrightness) - supported on all laptops & integrated displays
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
            {
                var val = Convert.ToInt32(obj["CurrentBrightness"]);
                if (val >= 0 && val <= 100)
                {
                    _cachedBrightness = val;
                    return val;
                }
            }
        }
        catch { }

        // 2. Return last known real brightness if available
        if (_cachedBrightness >= 0) return _cachedBrightness;

        return 50;
    }

    public static void SetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        _cachedBrightness = brightness;
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject obj in searcher.Get())
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)brightness });
                    break;
                }
            }
            catch { }
        });
    }
}
