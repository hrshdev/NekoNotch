using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Mobile-style Control Center tab: quick toggles for Wi-Fi, Bluetooth, Airplane mode,
/// Night Light, Battery Saver, DND, plus real-time hardware Brightness and Sound sliders.
/// Supports in-place sub-page navigation (like Windows 11 Action Center flyouts).
/// </summary>
public partial class ControlsView : UserControl
{
    private bool _updatingFromSystem;
    private string _activeSettingsUri = "ms-settings:";

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

            // 4. Battery status subtitle
            try
            {
                var power = System.Windows.Forms.SystemInformation.PowerStatus;
                var pct = (int)Math.Round(power.BatteryLifePercent * 100);
                if (pct >= 0 && pct <= 100)
                {
                    var isPlugged = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                    BatteryStatusText.Text = isPlugged ? $"{pct}% (AC)" : $"{pct}%";
                    BatteryDetailTitle.Text = $"Battery Level: {pct}%" + (isPlugged ? " (Charging)" : " (On Battery)");
                    BatteryDetailSub.Text = isPlugged
                        ? "Device is plugged in. Click to manage power & sleep timeouts."
                        : $"Remaining battery: {pct}%. Click to view energy saver profile.";
                }
            }
            catch { }
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

    // ==================== IN-PLACE NAVIGATION ====================

    private void SwitchToSubPage(string title, UIElement subPage, string defaultSettingsUri)
    {
        _activeSettingsUri = defaultSettingsUri;
        SubPageTitle.Text = title;

        WifiSubPage.Visibility = Visibility.Collapsed;
        BluetoothSubPage.Visibility = Visibility.Collapsed;
        SoundSubPage.Visibility = Visibility.Collapsed;
        DisplaySubPage.Visibility = Visibility.Collapsed;
        BatterySubPage.Visibility = Visibility.Collapsed;
        AirplaneSubPage.Visibility = Visibility.Collapsed;
        DndSubPage.Visibility = Visibility.Collapsed;

        subPage.Visibility = Visibility.Visible;
        MainView.Visibility = Visibility.Collapsed;
        SubPageView.Visibility = Visibility.Visible;
    }

    private void OnBackToMain(object sender, RoutedEventArgs e)
    {
        SubPageView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        RefreshState();
    }

    private void OnOpenFullSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_activeSettingsUri) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); } catch { }
        }
    }

    // ==================== OPEN SUB-PAGES ====================

    private void OnOpenWifiSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Wi-Fi Networks", WifiSubPage, "ms-settings:network-wifi");
    }

    private void OnOpenBluetoothSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Bluetooth Devices", BluetoothSubPage, "ms-settings:bluetooth");
        RefreshBluetoothDevices();
    }

    private void RefreshBluetoothDevices()
    {
        try
        {
            var devices = BluetoothWatcher.Instance.Devices;
            var list = new List<BluetoothDeviceViewModel>();

            if (devices == null || devices.Count == 0)
            {
                list.Add(new BluetoothDeviceViewModel
                {
                    Name = "No paired devices found",
                    StatusText = "Pair new",
                    StatusBrush = new SolidColorBrush(Color.FromArgb(160, 160, 160, 160))
                });
            }
            else
            {
                foreach (var d in devices)
                {
                    list.Add(new BluetoothDeviceViewModel
                    {
                        Name = string.IsNullOrWhiteSpace(d.Name) ? "Bluetooth Device" : d.Name,
                        StatusText = d.Connected ? "Connected" : "Paired",
                        StatusBrush = d.Connected
                            ? new SolidColorBrush(Color.FromRgb(74, 222, 128)) // soft green
                            : new SolidColorBrush(Color.FromArgb(160, 160, 160, 160))
                    });
                }
            }
            BluetoothDeviceList.ItemsSource = list;
        }
        catch { }
    }

    private void OnOpenSoundSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Sound Output", SoundSubPage, "ms-settings:sound");
    }

    private void OnOpenNightLightSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Display & Brightness", DisplaySubPage, "ms-settings:nightlight");
    }

    private void OnOpenBatterySubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Battery & Power", BatterySubPage, "ms-settings:powersleep");
    }

    private void OnOpenAirplaneSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Airplane Mode", AirplaneSubPage, "ms-settings:network-airplanemode");
    }

    private void OnOpenDndSubPage(object sender, MouseButtonEventArgs e)
    {
        SwitchToSubPage("Do Not Disturb", DndSubPage, "ms-settings:quiethours");
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

    // ==================== SUB-PAGE ACTIONS ====================

    private void OnNativeWifiFlyout(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-availablenetworks:") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true }); }
    }

    private void OnWifiSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true }); }
        catch { }
    }

    private void OnBluetoothSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { }
    }

    private void OnSoundSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch { }
    }

    private void OnVolumeMixerClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true }); }
        catch { }
    }

    private void OnNightLightSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true }); }
        catch { }
    }

    private void OnDisplaySettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }
        catch { }
    }

    private void OnPowerSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); }
        catch { }
    }

    private void OnAirplaneSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-airplanemode") { UseShellExecute = true }); }
        catch { }
    }

    private void OnDndSettingsClick(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:quiethours") { UseShellExecute = true }); }
        catch { }
    }
}

public class BluetoothDeviceViewModel
{
    public string Name { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Brush StatusBrush { get; set; } = Brushes.Gray;
}
