using System.Runtime.InteropServices;

namespace OrixNotch.Services;

/// <summary>
/// Reads real-time audio output peak meter from Windows Core Audio (WASAPI).
/// Returns 0.0 when audio is muted or silent.
/// </summary>
public sealed class AudioMeterService
{
    public static AudioMeterService Instance { get; } = new();

    private IAudioMeterInformation? _meter;

    public void Start()
    {
        if (_meter != null) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // Try Multimedia role (1) first, fallback to Console role (0)
            IMMDevice? device = null;
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev1) == 0)
            {
                device = dev1;
            }
            else if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var dev0) == 0)
            {
                device = dev0;
            }

            if (device != null)
            {
                var iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0)
                {
                    _meter = (IAudioMeterInformation)obj;
                }
            }
        }
        catch
        {
            _meter = null;
        }
    }

    /// <summary>
    /// Returns the current peak audio level (0.0 to 1.0) on the default audio output.
    /// Returns 0.0 when muted, silent, or between words/beats.
    /// </summary>
    public float GetPeak()
    {
        if (_meter == null)
        {
            Start();
            if (_meter == null) return 0f;
        }

        try
        {
            if (_meter.GetPeakValue(out var peak) == 0)
            {
                return Math.Clamp(peak, 0f, 1f);
            }
        }
        catch
        {
            _meter = null;
        }
        return 0f;
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

    // Official Windows SDK IAudioMeterInformation GUID from endpointvolume.h
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out int pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(int u32ChannelCount, [Out] float[] afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out int pdwHardwareSupportMask);
    }
}
