using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Mobile-style Control Center tab: quick toggles for Wi-Fi, Bluetooth, Airplane mode,
/// Night Light, Battery Saver, DND, plus real-time hardware Brightness and Sound sliders.
/// </summary>
public partial class ControlsView : UserControl
{
    private bool _updatingFromSystem;

    public ControlsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshState();
        VolumeService.Instance.Changed += OnVolumeServiceChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VolumeService.Instance.Changed -= OnVolumeServiceChanged;
    }

    private void RefreshState()
    {
        _updatingFromSystem = true;
        try
        {
            // 1. Initialise brightness slider
            var brightness = BrightnessService.GetBrightness();
            BrightnessSlider.Value = brightness;
            BrightnessValText.Text = $"{brightness}%";

            // 2. Initialise volume slider & mute icon
            var vol = (int)Math.Round(VolumeService.Instance.GetLevel() * 100);
            var muted = VolumeService.Instance.IsMuted();
            VolumeSlider.Value = vol;
            VolumeValText.Text = muted ? "Muted" : $"{vol}%";
            VolumeMuteIcon.Kind = (muted || vol == 0) ? AppIcon.SpeakerMuted : AppIcon.Speaker;

            // 3. Wi-Fi status subtitle
            var netAvailable = NetworkInterface.GetIsNetworkAvailable();
            WifiStatusText.Text = netAvailable ? "Connected" : "Disconnected";
        }
        catch { }
        finally
        {
            _updatingFromSystem = false;
        }
    }

    private void OnVolumeServiceChanged(float level, bool muted)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _updatingFromSystem = true;
            try
            {
                var pct = (int)Math.Round(level * 100);
                VolumeSlider.Value = pct;
                VolumeValText.Text = muted ? "Muted" : $"{pct}%";
                VolumeMuteIcon.Kind = (muted || pct == 0) ? AppIcon.SpeakerMuted : AppIcon.Speaker;
            }
            finally
            {
                _updatingFromSystem = false;
            }
        });
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromSystem) return;
        var val = (int)Math.Round(e.NewValue);
        BrightnessValText.Text = $"{val}%";
        BrightnessService.SetBrightness(val);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromSystem) return;
        var val = (int)Math.Round(e.NewValue);
        VolumeValText.Text = $"{val}%";
        VolumeMuteIcon.Kind = val == 0 ? AppIcon.SpeakerMuted : AppIcon.Speaker;
        VolumeService.Instance.SetLevel(val / 100f);
    }

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        VolumeService.Instance.ToggleMute();
        var muted = VolumeService.Instance.IsMuted();
        var vol = (int)Math.Round(VolumeSlider.Value);
        VolumeValText.Text = muted ? "Muted" : $"{vol}%";
        VolumeMuteIcon.Kind = (muted || vol == 0) ? AppIcon.SpeakerMuted : AppIcon.Speaker;
    }

    private void OnWifiClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-availablenetworks:") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true }); }
    }

    private void OnBluetoothClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { }
    }

    private void OnAirplaneClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-airplanemode") { UseShellExecute = true }); }
        catch { }
    }

    private void OnNightLightClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true }); }
        catch { }
    }

    private void OnBatterySaverClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true }); }
        catch { }
    }

    private void OnDndClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:quiethours") { UseShellExecute = true }); }
        catch { }
    }

    private void OnSnipClick(object sender, MouseButtonEventArgs e)
    {
        NotchWindow.Instance?.Collapse();
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch { }
    }

    private void OnLockClick(object sender, MouseButtonEventArgs e)
    {
        NotchWindow.Instance?.Collapse();
        try { Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true }); }
        catch { }
    }
}
