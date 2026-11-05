using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NekoNotch.Services;

/// <summary>
/// Hardware display brightness control and real-time watcher via WMI (WmiMonitorBrightness &amp; WmiMonitorBrightnessEvent).
/// Triggers sleek Dynamic Island brightness HUD in the notch whenever brightness is adjusted.
/// </summary>
public static class BrightnessService
{
    private static int _cachedBrightness = -1;
    private static ManagementEventWatcher? _wmiWatcher;
    private static Timer? _pollTimer;
    private static bool _started;
    private static int _isPolling;

    public static event Action<int>? Changed;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        Task.Run(() =>
        {
            try
            {
                var initial = GetBrightness();
                _cachedBrightness = initial;

                // 1. Event-driven WMI watcher: instantly notified by the display driver on brightness changes
                var query = new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
                var scope = new ManagementScope(@"root\wmi");
                _wmiWatcher = new ManagementEventWatcher(scope, query);
                _wmiWatcher.EventArrived += (s, e) =>
                {
                    try
                    {
                        var val = Convert.ToInt32(e.NewEvent["Brightness"]);
                        if (val >= 0 && val <= 100)
                        {
                            _cachedBrightness = val;
                            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Changed?.Invoke(val)));
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log(ex);
                    }
                };
                _wmiWatcher.Start();
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }

            // 2. Lightweight background fallback polling (every 750ms) to ensure external adjustments are never missed
            _pollTimer = new Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;
                try
                {
                    var current = QueryWmiBrightness();
                    if (current >= 0 && current != _cachedBrightness)
                    {
                        _cachedBrightness = current;
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Changed?.Invoke(current)));
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _isPolling, 0);
                }
            }, null, 1500, 750);
        });
    }

    private static int QueryWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
            {
                var val = Convert.ToInt32(obj["CurrentBrightness"]);
                if (val >= 0 && val <= 100) return val;
            }
        }
        catch { }
        return -1;
    }

    public static int GetBrightness()
    {
        var current = QueryWmiBrightness();
        if (current >= 0)
        {
            _cachedBrightness = current;
            return current;
        }

        if (_cachedBrightness >= 0) return _cachedBrightness;
        return 50;
    }

    public static void SetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        _cachedBrightness = brightness;
        Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Changed?.Invoke(brightness)));

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
