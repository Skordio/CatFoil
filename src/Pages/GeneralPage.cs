namespace CatFoil;

/// <summary>Window behavior and ordinary (non-elevated) startup.</summary>
internal sealed class GeneralPage : SettingsPage
{
    public override string Title => "General";

    public GeneralPage(SettingsSession session) : base(session)
    {
        AddSection("Window");
        AddCheck("Hide to the tray when the window is closed",
            Settings.MinimizeToTrayOnClose,
            v => Session.Apply(s => s.MinimizeToTrayOnClose = v));

        AddSection("Startup");
        AddCheck("Start CatFoil when Windows starts",
            Settings.StartWithWindows,
            v => Session.Apply(s => s.StartWithWindows = v));
        AddCheck("Start hidden in the system tray",
            Settings.StartMinimized,
            v => Session.Apply(s => s.StartMinimized = v));
        AddHint("Starting elevated at logon is on the Advanced page.");
    }
}
