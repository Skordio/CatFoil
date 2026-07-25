using System.IO;
using System.Media;

namespace CatFoil;

/// <summary>
/// The audio cues for lock state changes and blocked keys. Each is either one of
/// the user's own Windows scheme sounds — no bundled audio, and silent if they
/// have those events turned off — or a file they chose.
/// </summary>
internal static class Sounds
{
    // Stable per cue so a retrigger restarts that cue rather than layering, and
    // so the three can overlap each other.
    private const string LockAlias = "catfoil_lock";
    private const string UnlockAlias = "catfoil_unlock";
    private const string BlockedAlias = "catfoil_blocked";

    public static void Lock(Settings settings) =>
        Play(settings.LockSound, LockAlias, SystemSounds.Exclamation);

    public static void Unlock(Settings settings) =>
        Play(settings.UnlockSound, UnlockAlias, SystemSounds.Asterisk);

    public static void Blocked(Settings settings) =>
        Play(settings.BlockedSound, BlockedAlias, SystemSounds.Hand);

    /// <summary>Plays a cue regardless of its Enabled flag — for the Test button
    /// on the settings page, where the user has explicitly asked to hear it.</summary>
    public static void Preview(SoundSetting cue, string alias, SystemSound fallback) =>
        Play(cue, alias, fallback, ignoreEnabled: true);

    public static void PreviewLock(Settings s) => Preview(s.LockSound, LockAlias, SystemSounds.Exclamation);
    public static void PreviewUnlock(Settings s) => Preview(s.UnlockSound, UnlockAlias, SystemSounds.Asterisk);
    public static void PreviewBlocked(Settings s) => Preview(s.BlockedSound, BlockedAlias, SystemSounds.Hand);

    private static void Play(SoundSetting cue, string alias, SystemSound fallback, bool ignoreEnabled = false)
    {
        if (cue is null) return;
        if (!ignoreEnabled && !cue.Enabled) return;

        if (cue.Source == SoundSource.Custom && !string.IsNullOrWhiteSpace(cue.File))
        {
            string path = SoundStore.FullPath(cue.File);
            // A missing or undecodable file falls through to the system sound:
            // silence would look like the cue simply not working.
            if (File.Exists(path) && AudioPlayer.TryPlay(path, cue.ClampedVolume(), alias)) return;
        }

        fallback.Play();
    }
}
