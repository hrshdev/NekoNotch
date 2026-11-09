using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NekoNotch.Services;
using NekoNotch.Shell;

namespace NekoNotch.Tools;

/// <summary>
/// Mobile-style Control Center tab: quick toggles for Wi-Fi, Bluetooth, Airplane mode,
/// Night Light, Battery Saver, DND, plus real-time hardware Brightness and Sound sliders.
/// Supports direct ON/OFF toggling and in-place sub-page navigation with full Wi-Fi
/// (scan, connect, disconnect, forget) and Bluetooth (add, connect, remove) functionality.
/// </summary>
public partial class ControlsView : UserControl, IToolView, IEscapeHandler
{
    private bool _updatingFromSystem = true;
    private string _activeSettingsUri = "ms-settings:";

    public ControlsView()
    {
        _updatingFromSystem = true;
        InitializeComponent();
        RefreshVolumeNow();
        RefreshBrightnessNow();
        _updatingFromSystem = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void OnShown()
    {
        RefreshState();
    }

    public void OnHidden()
    {
    }

    public bool OnEscape()
    {
        if (SubPageView.Visibility == Visibility.Visible)
        {
            OnBackToMain(null!, null!);
            return true;
        }
        return false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshState();
        VolumeService.Instance.Changed += OnVolumeServiceChanged;
        BluetoothWatcher.Instance.Changed += OnBluetoothWatcherChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VolumeService.Instance.Changed -= OnVolumeServiceChanged;
        BluetoothWatcher.Instance.Changed -= OnBluetoothWatcherChanged;
    }

    private void OnBluetoothWatcherChanged(string name, bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (SubPageView.Visibility == Visibility.Visible && BluetoothSubPage.Visibility == Visibility.Visible)
            {
                RefreshBluetoothDevices();
            }
        });
    }

    private async void RefreshState()
    {
        _updatingFromSystem = true;
        try
        {
            // 1. Initialise real-time PC volume & mute icon
            RefreshVolumeNow();

            // 2. Initialise real-time PC hardware brightness
            RefreshBrightnessNow();

            // 3. Wi-Fi state
            var wifiOn = await SystemControlsService.IsWifiOnAsync();
            var connectedSsid = NativeWifiService.GetConnectedSsid();
            var netAvailable = NetworkInterface.GetIsNetworkAvailable();
            var wifiSubtitle = !string.IsNullOrEmpty(connectedSsid) ? connectedSsid : (netAvailable ? "Connected" : "On");

            UpdateToggleVisuals(
                wifiOn,
                WifiTileBadge, WifiTileIcon, WifiStatusText,
                wifiSubtitle, "Off",
                WifiSubBadge, WifiSubIcon, WifiSubStatusText,
                WifiPill, WifiKnob);

            // 4. Bluetooth state
            var btOn = await SystemControlsService.IsBluetoothOnAsync();
            UpdateToggleVisuals(
                btOn,
                BluetoothTileBadge, BluetoothTileIcon, BluetoothStatusText,
                "On", "Off",
                BluetoothSubBadge, BluetoothSubIcon, BluetoothSubStatusText,
                BluetoothPill, BluetoothKnob);

            // 5. Airplane Mode
            var airplaneOn = SystemControlsService.IsAirplaneModeOn();
            UpdateToggleVisuals(
                airplaneOn,
                AirplaneTileBadge, AirplaneTileIcon, AirplaneStatusText,
                "On", "Off",
                AirplaneSubBadge, AirplaneSubIcon, AirplaneSubStatusText,
                AirplanePill, AirplaneKnob);

            // 6. Night Light (Screen Warmth)
            var nightLightOn = SystemControlsService.IsNightLightOn();
            UpdateToggleVisuals(
                nightLightOn,
                NightLightTileBadge, NightLightTileIcon, NightLightStatusText,
                "Warm", "Off",
                NightLightSubBadge, NightLightSubIcon, NightLightSubStatusText,
                NightLightPill, NightLightKnob);

            // 7. Battery Saver
            var batterySaverOn = SystemControlsService.IsBatterySaverOn();
            UpdateToggleVisuals(
                batterySaverOn,
                BatteryTileBadge, BatteryTileIcon, BatteryStatusText,
                "Saver On", "Normal",
                BatterySubBadge, BatterySubIcon, BatterySubStatusText,
                BatteryPill, BatteryKnob);

            try
            {
                var power = System.Windows.Forms.SystemInformation.PowerStatus;
                var pct = (int)Math.Round(power.BatteryLifePercent * 100);
                if (pct >= 0 && pct <= 100)
                {
                    var isPlugged = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                    if (!batterySaverOn)
                    {
                        BatteryStatusText.Text = isPlugged ? $"{pct}% (AC)" : $"{pct}%";
                    }
                    BatteryDetailTitle.Text = $"Battery Level: {pct}%" + (isPlugged ? " (Charging)" : " (On Battery)");
                    BatteryDetailSub.Text = isPlugged
                        ? "Device is plugged in. Click to manage power & sleep timeouts."
                        : $"Remaining battery: {pct}%. Click to view energy saver profile.";
                }
            }
            catch { }

            // 8. Do Not Disturb
            var dndOn = SystemControlsService.IsDndOn();
            UpdateToggleVisuals(
                dndOn,
                DndTileBadge, DndTileIcon, DndStatusText,
                "Silent", "Off",
                DndSubBadge, DndSubIcon, DndSubStatusText,
                DndPill, DndKnob);
        }
        catch { }
        finally
        {
            _updatingFromSystem = false;
        }
    }

    private void UpdateToggleVisuals(
        bool isOn,
        Border tileBadge,
        HeroIcon tileIcon,
        TextBlock tileText,
        string onText,
        string offText,
        Border? subBadge,
        HeroIcon? subIcon,
        TextBlock? subText,
        Border? pill,
        Ellipse? knob)
    {
        var accentSoft = (Brush)FindResource("AccentSoftBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var surface2 = (Brush)FindResource("Surface2Brush");
        var subTextBrush = (Brush)FindResource("SubTextBrush");

        tileBadge.Background = isOn ? accentSoft : surface2;
        tileIcon.Foreground = isOn ? accent : subTextBrush;
        tileText.Text = isOn ? onText : offText;

        if (subBadge != null) subBadge.Background = isOn ? accentSoft : surface2;
        if (subIcon != null) subIcon.Foreground = isOn ? accent : subTextBrush;
        if (subText != null) subText.Text = isOn ? onText : offText;
        if (pill != null) pill.Background = isOn ? accent : surface2;
        if (knob != null) knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private void RefreshVolumeNow()
    {
        _updatingFromSystem = true;
        try
        {
            var vol = (int)Math.Round(VolumeService.Instance.GetLevel() * 100);
            var muted = VolumeService.Instance.IsMuted();
            VolumeSlider.Value = vol;
            VolumeValText.Text = muted ? "Muted" : $"{vol}%";
            VolumeMuteIcon.Kind = (muted || vol == 0) ? AppIcon.SpeakerMuted : AppIcon.Speaker;
        }
        catch { }
        finally
        {
            _updatingFromSystem = false;
        }
    }

    private void RefreshBrightnessNow()
    {
        Task.Run(() =>
        {
            var b = BrightnessService.GetBrightness();
            Dispatcher.BeginInvoke(() =>
            {
                _updatingFromSystem = true;
                try
                {
                    BrightnessSlider.Value = b;
                    BrightnessValText.Text = $"{b}%";
                }
                finally
                {
                    _updatingFromSystem = false;
                }
            });
        });
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

    private void OnBrightnessCardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var step = e.Delta > 0 ? 5 : -5;
        var next = Math.Clamp(BrightnessSlider.Value + step, 0, 100);
        BrightnessSlider.Value = next;
    }

    private void OnVolumeCardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var step = e.Delta > 0 ? 5 : -5;
        var next = Math.Clamp(VolumeSlider.Value + step, 0, 100);
        VolumeSlider.Value = next;
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

    // ==================== DIRECT TOGGLE HANDLERS (ON / OFF) ====================

    private async void OnToggleWifi(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        WifiStatusText.Text = "Switching...";
        var next = await SystemControlsService.ToggleWifiAsync();
        UpdateToggleVisuals(
            next,
            WifiTileBadge, WifiTileIcon, WifiStatusText,
            "On", "Off",
            WifiSubBadge, WifiSubIcon, WifiSubStatusText,
            WifiPill, WifiKnob);

        if (SubPageView.Visibility == Visibility.Visible && WifiSubPage.Visibility == Visibility.Visible)
        {
            RefreshWifiProfiles();
        }
    }

    private async void OnToggleBluetooth(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BluetoothStatusText.Text = "Switching...";
        var next = await SystemControlsService.ToggleBluetoothAsync();
        UpdateToggleVisuals(
            next,
            BluetoothTileBadge, BluetoothTileIcon, BluetoothStatusText,
            "On", "Off",
            BluetoothSubBadge, BluetoothSubIcon, BluetoothSubStatusText,
            BluetoothPill, BluetoothKnob);

        if (SubPageView.Visibility == Visibility.Visible && BluetoothSubPage.Visibility == Visibility.Visible)
        {
            RefreshBluetoothDevices();
        }
    }

    private void OnToggleAirplane(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var next = SystemControlsService.ToggleAirplaneMode();
        UpdateToggleVisuals(
            next,
            AirplaneTileBadge, AirplaneTileIcon, AirplaneStatusText,
            "On", "Off",
            AirplaneSubBadge, AirplaneSubIcon, AirplaneSubStatusText,
            AirplanePill, AirplaneKnob);
    }

    private void OnToggleNightLight(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var next = SystemControlsService.ToggleNightLight();
        UpdateToggleVisuals(
            next,
            NightLightTileBadge, NightLightTileIcon, NightLightStatusText,
            "Warm", "Off",
            NightLightSubBadge, NightLightSubIcon, NightLightSubStatusText,
            NightLightPill, NightLightKnob);
    }

    private void OnToggleBatterySaver(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var next = SystemControlsService.ToggleBatterySaver();
        UpdateToggleVisuals(
            next,
            BatteryTileBadge, BatteryTileIcon, BatteryStatusText,
            "Saver On", "Normal",
            BatterySubBadge, BatterySubIcon, BatterySubStatusText,
            BatteryPill, BatteryKnob);
    }

    private void OnToggleDnd(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var next = SystemControlsService.ToggleDnd();
        UpdateToggleVisuals(
            next,
            DndTileBadge, DndTileIcon, DndStatusText,
            "Silent", "Off",
            DndSubBadge, DndSubIcon, DndSubStatusText,
            DndPill, DndKnob);
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
        e.Handled = true;
        SwitchToSubPage("Wi-Fi Networks", WifiSubPage, "ms-settings:network-wifi");
        RefreshWifiProfiles();
    }

    private void RefreshWifiProfiles()
    {
        try
        {
            var connectedSsid = NativeWifiService.GetConnectedSsid();
            if (!string.IsNullOrEmpty(connectedSsid))
            {
                WifiConnectedCard.Visibility = Visibility.Visible;
                WifiConnectedSsidText.Text = connectedSsid;
            }
            else
            {
                WifiConnectedCard.Visibility = Visibility.Collapsed;
            }

            var profiles = NativeWifiService.GetSavedProfiles();
            WifiProfilesHeader.Text = $"SAVED NETWORKS ({profiles.Count})";

            var items = new List<WifiProfileItem>();
            foreach (var prof in profiles)
            {
                var isConn = !string.IsNullOrEmpty(connectedSsid) && string.Equals(prof, connectedSsid, StringComparison.OrdinalIgnoreCase);
                items.Add(new WifiProfileItem
                {
                    Ssid = prof,
                    IsConnected = isConn
                });
            }

            // Put connected network at top if present
            items.Sort((a, b) => b.IsConnected.CompareTo(a.IsConnected));
            WifiProfilesList.ItemsSource = items;
        }
        catch { }
    }

    private async void OnConnectProfileClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is WifiProfileItem item)
        {
            NativeWifiService.Connect(item.Ssid);
            await Task.Delay(1200);
            RefreshWifiProfiles();
            RefreshState();
        }
    }

    private async void OnDisconnectActiveWifi(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NativeWifiService.Disconnect();
        await Task.Delay(1000);
        RefreshWifiProfiles();
        RefreshState();
    }

    private void OnForgetProfileClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is WifiProfileItem item)
        {
            NativeWifiService.ForgetProfile(item.Ssid);
            RefreshWifiProfiles();
            RefreshState();
        }
    }

    private void OnOpenBluetoothSubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToSubPage("Bluetooth Devices", BluetoothSubPage, "ms-settings:bluetooth");
        RefreshBluetoothDevices();
    }

    private void RefreshBluetoothDevices()
    {
        try
        {
            var devices = BluetoothWatcher.Instance.Devices;
            BluetoothDevicesHeader.Text = $"PAIRED DEVICES ({devices.Count})";

            var list = new List<BluetoothDeviceItem>();
            if (devices == null || devices.Count == 0)
            {
                list.Add(new BluetoothDeviceItem
                {
                    Name = "No paired devices found",
                    StatusText = "Add device",
                    StatusBrush = new SolidColorBrush(Color.FromArgb(160, 160, 160, 160)),
                    IsConnected = false
                });
            }
            else
            {
                foreach (var d in devices)
                {
                    list.Add(new BluetoothDeviceItem
                    {
                        Id = d.Id,
                        Name = string.IsNullOrWhiteSpace(d.Name) ? "Bluetooth Device" : d.Name,
                        IsConnected = d.Connected,
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

    private void OnAddBluetoothDeviceClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth-adddevice") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
        }
    }

    private async void OnToggleBluetoothDeviceClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BluetoothDeviceItem item && !string.IsNullOrEmpty(item.Id))
        {
            if (item.IsConnected)
            {
                // To disconnect, launch settings or unpair
                try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
            }
            else
            {
                await BluetoothWatcher.ConnectDeviceAsync(item.Id);
                await Task.Delay(1000);
                RefreshBluetoothDevices();
            }
        }
    }

    private async void OnRemoveBluetoothDeviceClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BluetoothDeviceItem item && !string.IsNullOrEmpty(item.Id))
        {
            await BluetoothWatcher.UnpairDeviceAsync(item.Id);
            await Task.Delay(500);
            RefreshBluetoothDevices();
        }
    }

    private void OnOpenSoundSubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToSubPage("Sound Output", SoundSubPage, "ms-settings:sound");
    }

    private void OnOpenNightLightSubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToSubPage("Display & Brightness", DisplaySubPage, "ms-settings:nightlight");
    }

    private void OnOpenBatterySubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToSubPage("Battery & Power", BatterySubPage, "ms-settings:powersleep");
    }

    private void OnOpenAirplaneSubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToSubPage("Airplane Mode", AirplaneSubPage, "ms-settings:network-airplanemode");
    }

    private void OnOpenDndSubPage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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

public class WifiProfileItem
{
    public string Ssid { get; set; } = "";
    public bool IsConnected { get; set; }
    public string StatusText => IsConnected ? "Connected" : "";
    public Brush StatusBrush => IsConnected ? new SolidColorBrush(Color.FromRgb(74, 222, 128)) : new SolidColorBrush(Color.FromArgb(160, 160, 160, 160));
    public Visibility StatusVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DisconnectVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
}

public class BluetoothDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsConnected { get; set; }
    public string StatusText { get; set; } = "";
    public Brush StatusBrush { get; set; } = Brushes.Gray;
    public string ActionButtonText => IsConnected ? "Disconnect" : "Connect";
}
