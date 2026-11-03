using System.Runtime.InteropServices;
using OrixNotch.Interop;

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
}
