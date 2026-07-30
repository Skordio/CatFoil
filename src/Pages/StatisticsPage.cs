using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Lifetime usage read-out: how many times the keyboard was locked, the total
/// time it spent locked, and how many keystrokes were blocked (i.e. how many
/// keys the cat pressed that never reached anything). Resettable. Replaced the
/// separate StatsForm dialog when settings became a shell of pages.
/// </summary>
internal sealed class StatisticsPage : SettingsPage
{
    private static readonly Font ValueFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Color CaptionColor = Color.FromArgb(80, 80, 80);

    private readonly Func<long>? _inProgressSeconds;
    private readonly Action? _onReset;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private readonly Label _sessions = new();
    private readonly Label _time = new();
    private readonly Label _blocked = new();

    public override string Title => "Statistics";

    /// <param name="inProgressSeconds">Elapsed seconds of a lock session in
    /// progress (0 when unlocked) — added to the displayed total so the
    /// read-out ticks live while the keyboard is locked.</param>
    /// <param name="onReset">Called after a reset zeroes the counters, before
    /// they're saved — lets the owner restart an in-progress session's clock.</param>
    public StatisticsPage(SettingsSession session, Func<long>? inProgressSeconds, Action? onReset)
        : base(session)
    {
        _inProgressSeconds = inProgressSeconds;
        _onReset = onReset;

        AddSection("Since you started using CatFoil");
        AddValueRow("Times locked", _sessions);
        AddValueRow("Total time locked", _time);
        AddValueRow("Keys the cat didn't type", _blocked);

        var reset = new Button
        {
            Text = "Reset…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 8, 16, 8),
            TabStop = false,
        };
        reset.Click += OnResetClick;
        AddRow(reset, topGap: 26);
        AddHint("Sets all three counters back to zero.");

        RefreshValues();
        _refresh.Tick += (_, _) => RefreshValues();
        // Tick only while on screen: the page lives for the whole settings
        // session, and a hidden page repainting every second is pure waste.
        // Constructed hidden on purpose — a control starts with its visible
        // state already true, so without this the shell's first Visible = true
        // would be a no-op and VisibleChanged would never start the timer.
        Visible = false;
        VisibleChanged += (_, _) =>
        {
            if (Visible) { RefreshValues(); _refresh.Start(); }
            else _refresh.Stop();
        };
    }

    private void AddValueRow(string caption, Label value)
    {
        var label = new Label
        {
            Text = caption + ":",
            AutoSize = false,
            Size = new Size(210, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CaptionColor,
            Margin = Padding.Empty,
        };
        value.AutoSize = true;
        value.Font = ValueFont;
        value.Margin = new Padding(0, 2, 0, 0);
        AddRow(Row(label, value), indent: 4, topGap: 2);
    }

    private void RefreshValues()
    {
        _sessions.Text = Settings.StatLockSessions.ToString("N0");
        _time.Text = FormatDuration(Settings.StatLockedSeconds + (_inProgressSeconds?.Invoke() ?? 0));
        _blocked.Text = Settings.StatBlockedKeys.ToString("N0");
    }

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    // Zero + notify + repaint, without the confirm or the save — the click
    // handler owns those, and keeping the disk write out of here lets probes
    // exercise the reset without touching the live settings.json.
    private void ResetCounters()
    {
        Settings.StatLockSessions = 0;
        Settings.StatLockedSeconds = 0;
        Settings.StatBlockedKeys = 0;
        _onReset?.Invoke();
        RefreshValues();
    }

    private void OnResetClick(object? sender, EventArgs e)
    {
        if (MessageBox.Show(FindForm(), "Reset all statistics to zero?", "Reset statistics",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        ResetCounters();
        Settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        // The timer isn't parented to the control tree, so Dispose won't reach
        // it on its own.
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
