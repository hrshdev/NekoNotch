using System.Management;

namespace OrixNotch.Services;

/// <summary>
/// Hardware display brightness control via Windows Management Instrumentation (WMI).
/// </summary>
public static class BrightnessService
{
    public static int GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToInt32(obj["CurrentBrightness"]);
            }
        }
        catch { }
        return 70;
    }

    public static void SetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
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
