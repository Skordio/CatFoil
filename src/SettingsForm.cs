using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

public sealed class SettingsForm : Form
{
    private static readonly Font DialogFont = new("Segoe UI", 9.5f);

    private readonly Settings _settings;

    private readonly CheckBox _chkTrayOnClose = new();
    private readonly CheckBox _chkStartMinimized = new();
    private readonly CheckBox _chkStartWithWindows = new();
    private readonly CheckBox _chkOverlay = new();
    private readonly CheckBox _chkSoundLock = new();
    private readonly CheckBox _chkSoundBlocked = new();
    private readonly CheckBox _chkAutoLock = new();
    private readonly NumericUpDown _numAutoLock = new();
    private readonly CheckBox _chkRunAsAdmin = new();
    private readonly CheckBox _chkStartElevatedBoot = new();
    private readonly CheckBox _chkHotkeyEnabled = new();
    private readonly CheckBox _chkChord = new();
    private readonly TextBox _txtHotkey = new();
    private readonly ToolTip _tip = new() { AutoPopDelay = 20000 };
    private readonly Button _btnSave = new();
    private readonly Button _btnApply = new();
    private readonly Button _btnCancel = new();
    private readonly Button _btnWelcome = new();

    private Keys _hotkey;

    // Chord being edited, plus live capture state for the hotkey box.
    private Keys _chordModifiers;
    private Keys[] _chordKeys;
    private Keys _chordCaptureMods;
    private readonly List<Keys> _chordCapture = new();
    private readonly HashSet<Keys> _chordHeld = new();

    public event Action? SettingsSaved;

    /// <summary>Raised after an elevated instance has been launched; the app
    /// should quit so that instance can take over the single-instance slot.</summary>
    public event Action? RestartElevatedRequested;

    public SettingsForm(Settings settings)
    {
        _settings = settings;
        _hotkey = settings.Hotkey;
        _chordModifiers = settings.ChordModifiers;
        _chordKeys = settings.ChordKeys;

        Text = "CatFoil Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(512, 636);
        Font = DialogFont;

        // --- General ---
        var grpGeneral = new GroupBox { Text = "General", Bounds = new Rectangle(12, 12, 488, 244) };
        AddCheck(grpGeneral, _chkTrayOnClose, "Hide to the tray when the window is closed", 26, settings.MinimizeToTrayOnClose);
        AddCheck(grpGeneral, _chkStartMinimized, "Start hidden in the system tray", 56, settings.StartMinimized);
        AddCheck(grpGeneral, _chkStartWithWindows, "Start CatFoil when Windows starts", 86, settings.StartWithWindows);
        AddCheck(grpGeneral, _chkOverlay, "Show the cat overlay while the keyboard is locked", 116, settings.ShowOverlay);
        var btnOverlay = new Button
        {
            Text = "Customize overlay…",
            Bounds = new Rectangle(34, 148, 200, 28),
            TabStop = false,
        };
        btnOverlay.Click += OnCustomizeOverlay;
        grpGeneral.Controls.Add(btnOverlay);

        // Run-as-admin: elevating lets the keyboard hook also block elevated windows.
        _chkRunAsAdmin.Text = "Run as administrator (also block elevated windows)";
        _chkRunAsAdmin.AutoSize = true;
        _chkRunAsAdmin.Location = new Point(16, 184);
        bool elevated = Elevation.IsElevated();
        _chkRunAsAdmin.Checked = elevated;
        _chkRunAsAdmin.Enabled = !elevated;   // already elevated → nothing more to do
        _tip.SetToolTip(_chkRunAsAdmin, elevated
            ? "CatFoil is already running as administrator."
            : "Restarts CatFoil with administrator rights (Windows shows a UAC prompt) so it can\n" +
              "also block keystrokes to elevated windows. Ctrl+Alt+Del and Win+L still can't be\n" +
              "blocked. Autostart launches normally, so re-enable this after a restart.");
        _chkRunAsAdmin.CheckedChanged += OnRunAsAdminChanged;   // wired AFTER setting initial state
        grpGeneral.Controls.Add(_chkRunAsAdmin);

        // Silent elevated autostart (a scheduled task) — sub-option of run-as-admin,
        // and it needs elevation to create, so it's enabled only while elevated.
        _chkStartElevatedBoot.Text = "Start automatically at logon, elevated (no prompt)";
        _chkStartElevatedBoot.AutoSize = true;
        _chkStartElevatedBoot.Location = new Point(36, 210);   // indented under Run-as-admin
        // Whether the task exists is answered by spawning schtasks.exe — too slow to
        // block the window's construction on, so it's queried off-thread in OnLoad.
        _chkStartElevatedBoot.Enabled = elevated;
        _tip.SetToolTip(_chkStartElevatedBoot, elevated
            ? "Creates a Windows scheduled task so CatFoil starts with administrator rights at\n" +
              "logon, with no UAC prompt. Replaces the normal 'Start with Windows' startup."
            : "Turn on 'Run as administrator' first — creating the elevated startup task needs\n" +
              "administrator rights.");
        _chkStartElevatedBoot.CheckedChanged += OnStartElevatedBootChanged;   // after initial state
        grpGeneral.Controls.Add(_chkStartElevatedBoot);

        // --- Hotkey ---
        var grpHotkey = new GroupBox { Text = "Hotkey", Bounds = new Rectangle(12, 266, 488, 132) };
        AddCheck(grpHotkey, _chkHotkeyEnabled, "Lock/unlock the keyboard with a hotkey:", 26, settings.HotkeyEnabled);
        _txtHotkey.ReadOnly = true;
        _txtHotkey.Bounds = new Rectangle(16, 56, 170, 27);
        _txtHotkey.KeyDown += OnHotkeyKeyDown;
        _txtHotkey.KeyUp += OnHotkeyKeyUp;
        var hint = new Label
        {
            Text = "Click the box, then press keys.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(196, 60),
        };
        grpHotkey.Controls.Add(_txtHotkey);
        grpHotkey.Controls.Add(hint);
        AddCheck(grpHotkey, _chkChord, "Multi-key chord (e.g. Alt + C + F)", 96, settings.UseChordHotkey);
        _tip.SetToolTip(_chkChord,
            "Lets the hotkey be modifiers plus two or three keys held together,\n" +
            "detected by CatFoil itself instead of Windows.\n\n" +
            "Example: with the chord Alt + C + F, hold down Alt, keep holding it\n" +
            "while you press C, and then press F. The moment all three are down\n" +
            "together, the keyboard locks — the same chord unlocks it again.\n\n" +
            "Trade-off: while the keyboard is unlocked, the first keys of the\n" +
            "chord still reach the app you're in — so the Alt + C part may\n" +
            "briefly open a menu in some programs before the F lands.");
        _chkChord.CheckedChanged += (_, _) => UpdateHotkeyDisplay();
        UpdateHotkeyDisplay();

        // --- Auto-lock ---
        // Two rows so nothing overlaps: "Auto-lock after [n] minutes" on the first
        // line, the "of no keyboard or mouse activity" qualifier beneath it. The
        // selector and the "minutes" label are placed from the checkbox's *measured*
        // width (not a hardcoded x), so the AutoSize "Auto-lock after" text can never
        // sit on top of the selector.
        var grpAutoLock = new GroupBox { Text = "Auto-lock", Bounds = new Rectangle(12, 408, 488, 88) };
        _chkAutoLock.Text = "Auto-lock after";
        _chkAutoLock.AutoSize = true;
        _chkAutoLock.Font = Font;                 // pin the form font so PreferredSize measures correctly
        _chkAutoLock.Location = new Point(16, 26);
        _chkAutoLock.Checked = settings.AutoLockEnabled;
        _numAutoLock.Minimum = 1;
        _numAutoLock.Maximum = 120;
        _numAutoLock.Value = Math.Clamp(settings.AutoLockMinutes, 1, 120);
        int autoLockNumX = _chkAutoLock.Location.X + _chkAutoLock.PreferredSize.Width + 6;
        _numAutoLock.Bounds = new Rectangle(autoLockNumX, 23, 56, 27);
        var lblMinutes = new Label { Text = "minutes", AutoSize = true, Location = new Point(autoLockNumX + 56 + 6, 26) };
        var lblAutoLockDetail = new Label
        {
            Text = "of no keyboard or mouse activity",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(16, 58),
        };
        _chkAutoLock.CheckedChanged += (_, _) => _numAutoLock.Enabled = _chkAutoLock.Checked;
        _numAutoLock.Enabled = _chkAutoLock.Checked;
        grpAutoLock.Controls.AddRange(new Control[] { _chkAutoLock, _numAutoLock, lblMinutes, lblAutoLockDetail });

        // --- Sounds ---
        var grpSounds = new GroupBox { Text = "Sounds", Bounds = new Rectangle(12, 506, 488, 78) };
        AddCheck(grpSounds, _chkSoundLock, "Play a sound when locking and unlocking", 24, settings.SoundOnLockUnlock);
        AddCheck(grpSounds, _chkSoundBlocked, "Play a sound when a key is blocked while locked", 50, settings.SoundOnBlockedKey);

        // --- Buttons ---
        _btnWelcome.Text = "Welcome tour…";
        _btnWelcome.Bounds = new Rectangle(12, 596, 120, 30);
        _btnWelcome.TabStop = false;
        _btnWelcome.Click += (_, _) =>
        {
            using var welcome = new WelcomeForm(_settings);
            welcome.ShowDialog(this);
        };
        _btnApply.Text = "Apply";
        _btnApply.Bounds = new Rectangle(233, 596, 85, 30);
        _btnApply.Click += (_, _) => PersistSettings();
        _btnSave.Text = "Save";
        _btnSave.Bounds = new Rectangle(324, 596, 85, 30);
        _btnSave.Click += OnSaveClicked;
        _btnCancel.Text = "Cancel";
        _btnCancel.Bounds = new Rectangle(415, 596, 85, 30);
        _btnCancel.Click += (_, _) => Close();
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { grpGeneral, grpHotkey, grpAutoLock, grpSounds, _btnWelcome, _btnApply, _btnSave, _btnCancel });
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

    private static void AddCheck(GroupBox parent, CheckBox check, string text, int y, bool value)
    {
        check.Text = text;
        check.AutoSize = true;
        check.Location = new Point(16, y);
        check.Checked = value;
        parent.Controls.Add(check);
    }

    private static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None;

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        if (_chkChord.Checked)
        {
            ChordKeyDown(e);
            return;
        }

        Keys key = e.KeyCode;
        if (IsModifierKey(key))
            return;   // a modifier alone isn't a hotkey yet
        if (e.Modifiers == Keys.None)
        {
            _txtHotkey.Text = "Add Ctrl, Alt or Shift…";
            return;
        }

        _hotkey = e.Modifiers | key;
        _txtHotkey.Text = FormatHotkey(_hotkey);
    }

    private void ChordKeyDown(KeyEventArgs e)
    {
        Keys key = e.KeyCode;
        if (IsModifierKey(key))
        {
            if (_chordHeld.Count == 0)
                _txtHotkey.Text = "Now hold 2–3 more keys…";
            return;
        }

        // A fresh chord starts when nothing was held.
        if (_chordHeld.Count == 0)
        {
            _chordCapture.Clear();
            _chordCaptureMods = Keys.None;
        }

        if (_chordHeld.Add(key) && _chordCapture.Count < 3 && !_chordCapture.Contains(key))
            _chordCapture.Add(key);
        _chordCaptureMods |= e.Modifiers;

        _txtHotkey.Text = string.Join(" + ", ChordParts(_chordCaptureMods, _chordCapture.ToArray()));
    }

    private void OnHotkeyKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_chkChord.Checked) return;

        _chordHeld.Remove(e.KeyCode);
        if (_chordHeld.Count > 0 || _chordCapture.Count == 0) return;

        // Everything released — keep the chord if valid, otherwise explain.
        if (_chordCaptureMods != Keys.None && _chordCapture.Count >= 2)
        {
            _chordModifiers = _chordCaptureMods;
            _chordKeys = _chordCapture.ToArray();
            UpdateHotkeyDisplay();
        }
        else
        {
            _txtHotkey.Text = "Hold a modifier + 2–3 keys together…";
        }
        _chordCapture.Clear();
    }

    private void UpdateHotkeyDisplay() =>
        _txtHotkey.Text = _chkChord.Checked
            ? string.Join(" + ", ChordParts(_chordModifiers, _chordKeys))
            : FormatHotkey(_hotkey);

    internal static string[] ChordParts(Keys modifiers, Keys[] keys)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        foreach (Keys key in keys) parts.Add(key.ToString());
        return parts.ToArray();
    }

    internal static string[] HotkeyParts(Keys combo) =>
        ChordParts(combo, new[] { combo & Keys.KeyCode });

    /// <summary>The parts of whichever hotkey style is currently active.</summary>
    internal static string[] ActiveHotkeyParts(Settings s) =>
        s.UseChordHotkey && s.ChordKeys.Length >= 2
            ? ChordParts(s.ChordModifiers, s.ChordKeys)
            : HotkeyParts(s.Hotkey);

    internal static string FormatHotkey(Keys combo) => string.Join(" + ", HotkeyParts(combo));

    private void OnRunAsAdminChanged(object? sender, EventArgs e)
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

        // Save any pending changes so the elevated instance picks them up, then
        // hand off: launch elevated and, only on success, ask the app to quit.
        PersistSettings();
        if (Elevation.TryRelaunchElevated())
        {
            RestartElevatedRequested?.Invoke();
        }
        else
        {
            _chkRunAsAdmin.Checked = false;
            MessageBox.Show(this,
                "CatFoil was not restarted with administrator rights.",
                "Run as administrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OnStartElevatedBootChanged(object? sender, EventArgs e)
    {
        if (!Elevation.IsElevated()) return;   // the control is disabled unless elevated

        if (_chkStartElevatedBoot.Checked)
        {
            if (Startup.EnableTask())
            {
                _settings.StartElevatedOnBoot = true;
                Startup.SetRunKey(false);   // the scheduled task owns startup now
                _settings.Save();
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
            _settings.StartElevatedOnBoot = false;
            Startup.SetRunKey(_chkStartWithWindows.Checked);   // restore normal autostart if wanted
            _settings.Save();
        }
    }

    // Change the checkbox without re-entering its CheckedChanged handler.
    private void SetElevatedBootChecked(bool value)
    {
        _chkStartElevatedBoot.CheckedChanged -= OnStartElevatedBootChanged;
        _chkStartElevatedBoot.Checked = value;
        _chkStartElevatedBoot.CheckedChanged += OnStartElevatedBootChanged;
    }

    private void OnCustomizeOverlay(object? sender, EventArgs e)
    {
        using var overlay = new OverlaySettingsForm(_settings, Icon ?? SystemIcons.Application);
        // Re-raise so the tray applies the new overlay look to the live badge.
        overlay.SettingsSaved += () => SettingsSaved?.Invoke();
        overlay.ShowDialog(this);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        PersistSettings();
        Close();
    }

    // Write the controls into settings and apply them live. Used by both Save
    // (which then closes) and Apply (which leaves the window open).
    private void PersistSettings()
    {
        _settings.MinimizeToTrayOnClose = _chkTrayOnClose.Checked;
        _settings.StartMinimized = _chkStartMinimized.Checked;
        _settings.StartWithWindows = _chkStartWithWindows.Checked;
        _settings.StartElevatedOnBoot = _chkStartElevatedBoot.Checked;
        _settings.ShowOverlay = _chkOverlay.Checked;
        _settings.SoundOnLockUnlock = _chkSoundLock.Checked;
        _settings.SoundOnBlockedKey = _chkSoundBlocked.Checked;
        _settings.AutoLockEnabled = _chkAutoLock.Checked;
        _settings.AutoLockMinutes = (int)_numAutoLock.Value;
        _settings.HotkeyEnabled = _chkHotkeyEnabled.Checked;
        _settings.Hotkey = _hotkey;
        _settings.UseChordHotkey = _chkChord.Checked;
        _settings.ChordModifiers = _chordModifiers;
        _settings.ChordKeys = _chordKeys;
        _settings.Save();

        SettingsSaved?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        // ToolTip is a handle-backed component and isn't parented to the form, so
        // Form.Dispose won't reach it — release it explicitly. (DialogFont is a
        // shared static and is intentionally not disposed.)
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
