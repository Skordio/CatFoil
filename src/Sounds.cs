using System.Media;

namespace CatFoil;

/// <summary>
/// Short audio cues for lock state changes, using the user's own Windows system
/// sounds — no bundled audio, non-blocking, and silent if the user has system
/// sounds turned off.
/// </summary>
internal static class Sounds
{
    public static void Lock() => SystemSounds.Exclamation.Play();
    public static void Unlock() => SystemSounds.Asterisk.Play();
    public static void Blocked() => SystemSounds.Hand.Play();
}
