using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// A small read-out of lifetime usage: how many times the keyboard was locked,
/// the total time it spent locked, and how many keystrokes were blocked (i.e.
/// how many keys the cat pressed that never reached anything). Resettable.
/// </summary>
public sealed class StatsForm : Form
{
    private readonly Settings _settings;
    private readonly Label _sessions = new();
    private readonly Label _time = new();
    private readonly Label _blocked = new();

    public StatsForm(Settings settings, Icon appIcon)
    {
        _settings = settings;
        Icon = appIcon;

        Text = "CatFoil Statistics";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 244);
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "Since you started using CatFoil",
            AutoSize = true,
            Location = new Point(20, 18),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
        };
        Controls.Add(title);

        AddRow("Times locked", _sessions, 62);
        AddRow("Total time locked", _time, 98);
        AddRow("Keys the cat didn't type", _blocked, 134);

        var btnReset = new Button { Text = "Reset…", Bounds = new Rectangle(20, 198, 90, 30), TabStop = false };
        btnReset.Click += OnReset;
        var btnClose = new Button { Text = "Close", Bounds = new Rectangle(275, 198, 85, 30) };
        btnClose.Click += (_, _) => Close();
        AcceptButton = btnClose;
        CancelButton = btnClose;

        Controls.Add(btnReset);
        Controls.Add(btnClose);

        RefreshValues();
    }

    private void AddRow(string caption, Label value, int y)
    {
        Controls.Add(new Label
        {
            Text = caption + ":",
            AutoSize = true,
            Location = new Point(24, y),
            ForeColor = Color.FromArgb(80, 80, 80),
        });
        value.AutoSize = true;
        value.Location = new Point(232, y);
        value.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        Controls.Add(value);
    }

    private void RefreshValues()
    {
        _sessions.Text = _settings.StatLockSessions.ToString("N0");
        _time.Text = FormatDuration(_settings.StatLockedSeconds);
        _blocked.Text = _settings.StatBlockedKeys.ToString("N0");
    }

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private void OnReset(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Reset all statistics to zero?", "Reset statistics",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _settings.StatLockSessions = 0;
        _settings.StatLockedSeconds = 0;
        _settings.StatBlockedKeys = 0;
        _settings.Save();
        RefreshValues();
    }
}
