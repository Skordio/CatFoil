namespace CatFoil;

/// <summary>Audio cues for lock state changes and blocked keys.</summary>
internal sealed class SoundsPage : SettingsPage
{
    // These play the user's Windows scheme sounds, so if the mapped events are
    // set to "(None)" nothing plays — point people at the real knob.
    private const string SoundTip =
        "Uses your Windows system sounds (Exclamation, Asterisk and Critical Stop).\n" +
        "If you hear nothing, check those events in Windows Settings > Sound > More sound settings.";

    public override string Title => "Sounds";

    public SoundsPage(SettingsSession session) : base(session)
    {
        AddSection("Audio cues");
        AddCheck("Play a sound when locking and unlocking",
            Settings.SoundOnLockUnlock,
            v => Session.Apply(s => s.SoundOnLockUnlock = v),
            SoundTip);
        AddCheck("Play a sound when a key is blocked while locked",
            Settings.SoundOnBlockedKey,
            v => Session.Apply(s => s.SoundOnBlockedKey = v),
            SoundTip);
        AddHint("The blocked-key sound is throttled so a held key can't machine-gun it.");
    }
}
