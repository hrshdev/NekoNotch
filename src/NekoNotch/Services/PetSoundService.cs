using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;

namespace NekoNotch.Services;

/// <summary>
/// Dedicated high-fidelity procedural audio player for Coucou Mochi pet sounds
/// (Wake chirp, Done chime, and cozy Petting purr).
/// </summary>
public static class PetSoundService
{
    private static SoundPlayer? _wakePlayer;
    private static SoundPlayer? _donePlayer;
    private static SoundPlayer? _purrPlayer;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Task.Run(() =>
        {
            try
            {
                _wakePlayer = LoadPlayer("neko_wake.wav");
                _donePlayer = LoadPlayer("neko_done.wav");
                _purrPlayer = LoadPlayer("neko_purr.wav");
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        });
    }

    private static SoundPlayer? LoadPlayer(string soundFileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = $"NekoNotch.Resources.Sounds.{soundFileName}";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            var player = new SoundPlayer(ms);
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    public static void PlayWake()
    {
        if (!App.Settings.VoiceChime) return;
        try
        {
            if (_wakePlayer != null)
            {
                _wakePlayer.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch { }
    }

    public static void PlayDone()
    {
        if (!App.Settings.VoiceChime) return;
        try
        {
            if (_donePlayer != null)
            {
                _donePlayer.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch { }
    }

    public static void PlayPurr()
    {
        if (!App.Settings.VoiceChime) return;
        try
        {
            _purrPlayer?.Play();
        }
        catch { }
    }
}
