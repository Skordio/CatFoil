using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CatFoil;

/// <summary>
/// Plays a short audio file through MCI (winmm).
///
/// Chosen over <see cref="System.Media.SoundPlayer"/> because that one is
/// WAV-only and has no volume control, and over any decoder library because
/// CatFoil takes no NuGet dependencies. The cost is a little P/Invoke and MCI's
/// string command interface.
/// </summary>
internal static class AudioPlayer
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue,
                                            int returnLength, IntPtr callback);

    // One alias per cue. Re-opening the same alias replaces what was there, so
    // a cue retriggering itself restarts rather than layering.
    private static readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase);

    private static bool Send(string command) => mciSendString(command, null, 0, IntPtr.Zero) == 0;

    /// <summary>
    /// Plays <paramref name="fullPath"/> from the start under
    /// <paramref name="alias"/>. Returns false if MCI can't open it — the caller
    /// should fall back to a system sound rather than leaving the user with
    /// silence and no explanation.
    /// </summary>
    public static bool TryPlay(string fullPath, int volumePercent, string alias)
    {
        try
        {
            Close(alias);

            // Quoted because paths contain spaces. No explicit type: MCI picks a
            // device from the extension, which covers wav and mp3 on every
            // supported Windows without hard-coding a mapping.
            if (!Send($"open \"{fullPath}\" alias {alias}"))
            {
                // Some MP3s only open when the device is named outright.
                if (!Send($"open \"{fullPath}\" type mpegvideo alias {alias}")) return false;
            }
            Open.Add(alias);

            // MCI volume is 0–1000. Not supported by every device, so a failure
            // here just means the cue plays at its natural level.
            Send($"setaudio {alias} volume to {Math.Clamp(volumePercent, 0, 100) * 10}");

            // Fire and forget: these are sub-second cues, and waiting would block
            // the UI thread that raised them.
            return Send($"play {alias} from 0");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;   // no winmm: fall back to the system sound
        }
    }

    private static void Close(string alias)
    {
        if (Open.Remove(alias)) Send($"close {alias}");
    }

    /// <summary>Releases every open device. Call on shutdown — MCI handles are
    /// process-wide and hold the file open.</summary>
    public static void CloseAll()
    {
        foreach (string alias in Open) Send($"close {alias}");
        Open.Clear();
    }
}
