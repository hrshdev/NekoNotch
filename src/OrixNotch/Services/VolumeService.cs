using System.Runtime.InteropServices;
using System.Windows;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>
/// Master volume watcher and keyboard hook.
/// Suppresses the intrusive default Windows OSD flyout and routes volume adjustments
/// directly to Core Audio (WASAPI), triggering the sleek NekoNotch Dynamic Island volume HUD.
/// </summary>
public sealed class VolumeService
{
    public static VolumeService Instance { get; } = new();

    public event Action<float, bool>? Changed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Held so COM objects and hooks stay alive
    private IAudioEndpointVolume? _endpoint;
    private Callback? _callback;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook = IntPtr.Zero;

    private void EnsureEndpoint()
    {
        if (_endpoint is not null) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice? device = null;
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev1) == 0)
            {
                device = dev1;
            }
            else if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var dev0) == 0)
            {
                device = dev0;
            }

            if (device is not null)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0)
                {
                    _endpoint = obj as IAudioEndpointVolume;
                    if (_endpoint is not null)
                    {
                        _callback = new Callback(this);
                        _endpoint.RegisterControlChangeNotify(_callback);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _endpoint = null;
        }
    }

    public void Start()
    {
        EnsureEndpoint();

        if (_hook == IntPtr.Zero)
        {
            try
            {
                _hookProc = HookCallback;
                var hMod = GetModuleHandle(System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName);
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
                if (_hook == IntPtr.Zero)
                {
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public float GetLevel()
    {
        EnsureEndpoint();
        if (_endpoint is not null && _endpoint.GetMasterVolumeLevelScalar(out var level) == 0)
        {
            return Math.Clamp(level, 0f, 1f);
        }
        return 0.5f;
    }

    public bool IsMuted()
    {
        EnsureEndpoint();
        if (_endpoint is not null && _endpoint.GetMute(out var muted) == 0)
        {
            return muted;
        }
        return false;
    }

    public void SetLevel(float level)
    {
        EnsureEndpoint();
        if (_endpoint is not null)
        {
            var ctx = Guid.NewGuid();
            _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
        }
    }

    public void ToggleMute()
    {
        EnsureEndpoint();
        if (_endpoint is not null && _endpoint.GetMute(out var muted) == 0)
        {
            var ctx = Guid.NewGuid();
            _endpoint.SetMute(!muted, ref ctx);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && App.Settings.VolumeHud)
        {
            EnsureEndpoint();
            var vkCode = Marshal.ReadInt32(lParam);
            if (vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var ctx = Guid.NewGuid();
                    try
                    {
                        if (vkCode == VK_VOLUME_UP)
                        {
                            if (_endpoint is null || _endpoint.VolumeStepUp(ref ctx) != 0)
                            {
                                _endpoint = null;
                                EnsureEndpoint();
                                _endpoint?.VolumeStepUp(ref ctx);
                            }
                        }
                        else if (vkCode == VK_VOLUME_DOWN)
                        {
                            if (_endpoint is null || _endpoint.VolumeStepDown(ref ctx) != 0)
                            {
                                _endpoint = null;
                                EnsureEndpoint();
                                _endpoint?.VolumeStepDown(ref ctx);
                            }
                        }
                        else if (vkCode == VK_VOLUME_MUTE)
                        {
                            if (_endpoint is null || _endpoint.GetMute(out var muted) != 0)
                            {
                                _endpoint = null;
                                EnsureEndpoint();
                            }
                            if (_endpoint is not null && _endpoint.GetMute(out var m) == 0)
                            {
                                _endpoint.SetMute(!m, ref ctx);
                            }
                        }
                    }
                    catch
                    {
                        _endpoint = null;
                    }
                }
                // Return non-zero to swallow the key press: suppresses the intrusive native Windows volume OSD banner!
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Raise(float level, bool muted) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(level, muted));

    // Public: Core Audio calls back through a COM wrapper, which needs visible types.
    public sealed class Callback(VolumeService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16 bytes), BOOL bMuted, float fMasterVolume, ...
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner.Raise(level, muted);
            return 0;
        }
    }
}
