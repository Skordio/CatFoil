using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>The on-screen badge shown while the keyboard is locked.</summary>
internal sealed class OverlaysPage : SettingsPage
{
    public override string Title => "Overlays";

    public OverlaysPage(SettingsSession session) : base(session)
    {
        AddSection("Locked-keyboard badge");
        AddCheck("Show the cat overlay while the keyboard is locked",
            Settings.ShowOverlay,
            v => Session.Apply(s => s.ShowOverlay = v));
        AddHint("Drag the badge anywhere on screen; CatFoil remembers where you put it.");

        var customize = new Button
        {
            Text = "Customize overlay…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            TabStop = false,
        };
        customize.Click += OnCustomize;
        AddRow(customize, topGap: 8);
    }

    private void OnCustomize(object? sender, EventArgs e)
    {
        Form? owner = FindForm();
        // Settings can hold several overlays, but until the Overlays page grows
        // a list this dialog edits the first (and so far only) one.
        OverlayItem item = Settings.EnsureOverlays()[0];
        using var overlay = new OverlaySettingsForm(item, Settings, owner?.Icon ?? SystemIcons.Application);
        // That dialog saves its own changes; just re-announce so the tray
        // applies the new look to the live badge.
        overlay.SettingsSaved += Session.NotifyChanged;
        if (owner is not null) overlay.ShowDialog(owner);
        else overlay.ShowDialog();
    }
}
