using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>Version, and the way back into the first-run tour.</summary>
internal sealed class AboutPage : SettingsPage
{
    public override string Title => "About";

    public AboutPage(SettingsSession session) : base(session)
    {
        string version = typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "";

        AddSection("CatFoil");
        AddHint($"Version {version}");
        AddHint("Locks the keyboard so a cat on the desk can't type, and leaves the mouse alone.");

        AddSection("Getting started");
        var tour = new Button
        {
            Text = "Welcome tour…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 8, 16, 8),
            TabStop = false,
        };
        tour.Click += OnWelcomeTour;
        AddRow(tour, topGap: 4);
    }

    private void OnWelcomeTour(object? sender, EventArgs e)
    {
        Form? owner = FindForm();
        using var welcome = new WelcomeForm(Settings);
        if (owner is not null) welcome.ShowDialog(owner);
        else welcome.ShowDialog();
    }
}
