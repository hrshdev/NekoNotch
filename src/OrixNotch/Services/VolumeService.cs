using System.Runtime.InteropServices;
using System.Windows;

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

    // Held so COM objects and hooks stay alive
    private IAudioEndpointVolume? _endpoint;
    private Callback? _callback;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook = IntPtr.Zero;

    public void Start()
    {
        if (_endpoint is null)
        {
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

        if (_hook == IntPtr.Zero)
        {
            try
            {
                _hookProc = HookCallback;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && App.Settings.VolumeHud && _endpoint is not null)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            if (vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var ctx = Guid.NewGuid();
                    if (vkCode == VK_VOLUME_UP)
                    {
                        _endpoint.VolumeStepUp(ref ctx);
                    }
                    else if (vkCode == VK_VOLUME_DOWN)
                    {
                        _endpoint.VolumeStepDown(ref ctx);
                    }
                    else if (vkCode == VK_VOLUME_MUTE)
                    {
                        if (_endpoint.GetMute(out var muted) == 0)
                        {
                            _endpoint.SetMute(!muted, ref ctx);
                        }
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

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out int pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
        [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }
}
