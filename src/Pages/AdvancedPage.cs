using System;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Elevation: running as administrator so the hook can also block elevated
/// windows, and the scheduled task that does it at logon without a prompt.
/// Kept together on their own page because the second is a sub-option of the
/// first and both are heavier decisions than the everyday settings.
/// </summary>
internal sealed class AdvancedPage : SettingsPage
{
    private readonly CheckBox _chkRunAsAdmin;
    private readonly CheckBox _chkStartElevatedBoot;

    // Set while this page is ticking the box itself, so the change handler can
    // tell that apart from the user clicking it.
    private bool _syncingElevatedBoot;

    public override string Title => "Advanced";

    public AdvancedPage(SettingsSession session) : base(session)
    {
        bool elevated = Elevation.IsElevated();

        AddSection("Administrator rights");
        _chkRunAsAdmin = AddCheck("Run as administrator (also block elevated windows)",
            elevated,
            _ => OnRunAsAdminChanged(),
            elevated
                ? "CatFoil is already running as administrator."
                : "Restarts CatFoil with administrator rights (Windows shows a UAC prompt) so it can\n" +
                  "also block keystrokes to elevated windows. Ctrl+Alt+Del and Win+L still can't be\n" +
                  "blocked. Autostart launches normally, so re-enable this after a restart.");
        _chkRunAsAdmin.Enabled = !elevated;   // already elevated → nothing more to do

        // Sub-option of run-as-admin, and creating the task needs elevation, so
        // it's enabled only while elevated.
        _chkStartElevatedBoot = AddCheck("Start automatically at logon, elevated (no prompt)",
            false,
            _ => OnStartElevatedBootChanged(),
            elevated
                ? "Creates a Windows scheduled task so CatFoil starts with administrator rights at\n" +
                  "logon, with no UAC prompt. Replaces the normal 'Start with Windows' startup."
                : "Turn on 'Run as administrator' first — creating the elevated startup task needs\n" +
                  "administrator rights.",
            indent: 20);
        _chkStartElevatedBoot.Enabled = elevated;
        // Whether the task actually exists is answered by spawning schtasks.exe,
        // which is too slow to block on here; OnLoad fills it in off-thread.

        AddHint("Ctrl+Alt+Del and Win+L are reserved by Windows and can never be blocked, " +
                "even as administrator.", indent: 20);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Reflect the current elevated-startup task state without blocking the
        // message pump: schtasks.exe can take tens of ms (longer under load), so
        // query it on the thread pool and marshal the result back to the checkbox.
        System.Threading.Tasks.Task.Run(() => Startup.TaskExists())
            .ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(() => SetElevatedBootChecked(t.Result)); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private void OnRunAsAdminChanged()
    {
        // Only act on the user turning it ON. Reverting it below sets it back to
        // false, which re-enters here and returns immediately.
        if (!_chkRunAsAdmin.Checked || Elevation.IsElevated()) return;

        var confirm = MessageBox.Show(this,
            "CatFoil will restart with administrator rights so it can block elevated windows too.\n\n" +
            "Windows will show a UAC prompt. Continue?",
            "Run as administrator", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            _chkRunAsAdmin.Checked = false;
            return;
        }

        // Make sure any debounced edit is on disk before handing off, so the
        // elevated instance reads the settings the user just made.
        Session.Flush();
        // Tell the elevated instance which windows are up, so it re-opens them
        // instead of following StartMinimized: the settings window on this page
        // (it hosts this checkbox, so it's open by definition), and the main
        // window if the user had it showing too.
        bool mainVisible = false;
        foreach (Form form in Application.OpenForms)
            if (form is MainForm { Visible: true }) mainVisible = true;
        string restore = RestoreUi.Encode(mainVisible, (FindForm() as SettingsForm)?.CurrentPageTitle ?? Title);
        if (Elevation.TryRelaunchElevated(restore))
        {
            Session.RequestRestartElevated();
        }
        else
        {
            _chkRunAsAdmin.Checked = false;
            MessageBox.Show(this,
                "CatFoil was not restarted with administrator rights.",
                "Run as administrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OnStartElevatedBootChanged()
    {
        if (_syncingElevatedBoot) return;
        if (!Elevation.IsElevated()) return;   // the control is disabled unless elevated

        if (_chkStartElevatedBoot.Checked)
        {
            if (Startup.EnableTask())
            {
                Startup.SetRunKey(false);   // the scheduled task owns startup now
                Session.Apply(s => s.StartElevatedOnBoot = true);
            }
            else
            {
                SetElevatedBootChecked(false);
                MessageBox.Show(this, "Could not create the elevated startup task.",
                    "Start elevated at logon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            Startup.DisableTask();
            Startup.SetRunKey(Settings.StartWithWindows);   // restore normal autostart if wanted
            Session.Apply(s => s.StartElevatedOnBoot = false);
        }
    }

    // Change the checkbox without re-entering its CheckedChanged handler.
    private void SetElevatedBootChecked(bool value)
    {
        _syncingElevatedBoot = true;
        _chkStartElevatedBoot.Checked = value;
        _syncingElevatedBoot = false;
    }
}
