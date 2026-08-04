using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// The settings window: a thin host around <see cref="SettingsShell"/>, which
/// is the whole settings UI. The form's own job is window chrome and the two
/// window-level meanings the shell delegates: "leave settings" is Close, and
/// "the window closed" ends the visit.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly SettingsShell _shell;

    /// <summary>Raised after any edit, so the app can apply it live.</summary>
    public event Action? SettingsSaved;

    /// <summary>Raised after an elevated instance has been launched; the app
    /// should quit so that instance can take over the single-instance slot.</summary>
    public event Action? RestartElevatedRequested;

    /// <summary>True while the user is binding a new hotkey — the app should
    /// stop listening for the current one until it goes false again.</summary>
    public event Action<bool>? HotkeyCaptureChanged;

    public SettingsForm(Settings settings) : this(settings, null, null) { }

    /// <param name="inProgressLockSeconds">Elapsed seconds of a lock session in
    /// progress (0 when unlocked) — the Statistics page adds it to the displayed
    /// total so the read-out ticks live while the keyboard is locked.</param>
    /// <param name="onStatsReset">Called when the Statistics page zeroes the
    /// counters, so the owner can restart an in-progress session's clock.</param>
    public SettingsForm(Settings settings, Func<long>? inProgressLockSeconds, Action? onStatsReset)
    {
        _shell = new SettingsShell(settings, inProgressLockSeconds, onStatsReset)
        {
            Dock = DockStyle.Fill,
        };
        _shell.SettingsSaved += () => SettingsSaved?.Invoke();
        _shell.RestartElevatedRequested += () => RestartElevatedRequested?.Invoke();
        _shell.HotkeyCaptureChanged += capturing => HotkeyCaptureChanged?.Invoke(capturing);
        _shell.LeaveRequested += Close;

        Text = "CatFoil Settings";
        // Its own taskbar button, grouped under the main window's icon (same
        // process, so same AppUserModelID) — hovering the tray-adjacent taskbar
        // icon shows both windows. Minimize has to come with it: a taskbar
        // button that can't minimize its window is a button that does nothing.
        ShowInTaskbar = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ClientSize = SettingsShell.DesignSize;
        MinimumSize = SettingsShell.MinimumWindowSize;

        Controls.Add(_shell);
    }

    /// <summary>See <see cref="SettingsShell.SelectPage{T}"/>.</summary>
    internal void SelectPage<T>() where T : SettingsPage => _shell.SelectPage<T>();

    /// <summary>See <see cref="SettingsShell.SelectPage(string)"/>.</summary>
    internal void SelectPage(string title) => _shell.SelectPage(title);

    /// <summary>The title of the page currently shown.</summary>
    internal string CurrentPageTitle => _shell.CurrentPageTitle;

    // The shell's own ProcessCmdKey catches Escape whenever focus is inside it,
    // which is almost always. This covers the remainder: focus resting on the
    // form itself.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) return _shell.HandleEscape();
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _shell.EndVisit();
        base.OnFormClosed(e);
    }
}
