using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>How the keyboard gets locked: the hotkey and the idle auto-lock.
/// (Chord mode was pulled 2026-08-02 — see the legacy note in Settings.)</summary>
internal sealed class LockingPage : SettingsPage
{
    private readonly TextBox _txtHotkey = new();
    private readonly CheckBox _chkAutoLock;
    private readonly NumericUpDown _numAutoLock = new();

    private Keys _hotkey;

    private Form? _hookedForm;

    public override string Title => "Locking";

    // Releasing on deactivate and re-arming on activate keeps the "stop
    // listening for the hotkey" hold tied to the box actually being usable,
    // rather than to focus events that don't fire across an app switch.
    private void HookFormActivation()
    {
        if (_hookedForm is not null) return;
        _hookedForm = FindForm();
        if (_hookedForm is null) return;
        _hookedForm.Deactivate += OnOwnerDeactivate;
        _hookedForm.Activated += OnOwnerActivated;
    }

    private void OnOwnerDeactivate(object? sender, EventArgs e) => Session.SetHotkeyCapture(false);

    private void OnOwnerActivated(object? sender, EventArgs e)
    {
        if (_txtHotkey.Focused) Session.SetHotkeyCapture(true);
    }

    public LockingPage(SettingsSession session) : base(session)
    {
        _hotkey = Settings.Hotkey;

        // --- Hotkey ---
        AddSection("Hotkey");
        AddCheck("Lock and unlock the keyboard with a hotkey",
            Settings.HotkeyEnabled,
            v => Session.Apply(s => s.HotkeyEnabled = v));

        _txtHotkey.ReadOnly = true;
        _txtHotkey.Font = BodyFont;
        _txtHotkey.Width = 210;
        // The click target for rebinding a hotkey; worth being comfortably
        // larger than a default single-line box.
        _txtHotkey.TextAlign = HorizontalAlignment.Center;
        _txtHotkey.Padding = new Padding(0, 6, 0, 6);
        _txtHotkey.MinimumSize = new Size(0, 30);
        _txtHotkey.KeyDown += OnHotkeyKeyDown;
        // While the box has focus the app stops listening for the current
        // hotkey, so the combo being rebound lands here instead of toggling.
        _txtHotkey.Enter += (_, _) => Session.SetHotkeyCapture(true);
        _txtHotkey.Leave += (_, _) => Session.SetHotkeyCapture(false);
        // Leave does not fire when the whole window loses activation, so
        // Alt+Tabbing away with the cursor still in this box would strand the
        // hold — the hotkey would stay unregistered, and the watchdog can't
        // repair it because it goes through the same guard.
        HandleCreated += (_, _) => HookFormActivation();

        var hint = new Label
        {
            Text = "Click the box, then press keys.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(10, 6, 0, 0),
        };
        AddRow(Row(_txtHotkey, hint), indent: 20);

        UpdateHotkeyDisplay();

        // --- Auto-lock ---
        AddSection("Auto-lock");
        _chkAutoLock = new CheckBox
        {
            Text = "Auto-lock after",
            AutoSize = true,
            Font = BodyFont,
            Checked = Settings.AutoLockEnabled,
            Margin = new Padding(0, 4, 6, 0),
        };
        _numAutoLock.Minimum = 1;
        _numAutoLock.Maximum = 120;
        _numAutoLock.Font = BodyFont;
        _numAutoLock.Width = 70;
        // Taller than the default: the up/down arrows are the smallest hit
        // targets on any settings page.
        _numAutoLock.MinimumSize = new Size(0, 28);
        _numAutoLock.Value = Math.Clamp(Settings.AutoLockMinutes, 1, 120);
        _numAutoLock.Enabled = _chkAutoLock.Checked;
        _numAutoLock.Margin = new Padding(0, 3, 8, 0);
        _numAutoLock.ValueChanged += (_, _) =>
            Session.Apply(s => s.AutoLockMinutes = (int)_numAutoLock.Value);
        _chkAutoLock.CheckedChanged += (_, _) =>
        {
            _numAutoLock.Enabled = _chkAutoLock.Checked;
            Session.Apply(s => s.AutoLockEnabled = _chkAutoLock.Checked);
        };

        var lblMinutes = new Label
        {
            Text = "minutes of no keyboard or mouse activity",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        };
        AddRow(Row(_chkAutoLock, _numAutoLock, lblMinutes));
        AddHint("Using the mouse resets the timer, so this only fires once you've stepped away.");
    }

    // ---------------------------------------------------------------
    // Hotkey capture
    // ---------------------------------------------------------------
    private static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None;

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        Keys key = e.KeyCode;
        if (IsModifierKey(key))
            return;   // a modifier alone isn't a hotkey yet
        if (e.Modifiers == Keys.None)
        {
            _txtHotkey.Text = "Add Ctrl, Alt or Shift…";
            return;
        }

        _hotkey = e.Modifiers | key;
        _txtHotkey.Text = HotkeyText.Format(_hotkey);
        Session.Apply(s => s.Hotkey = _hotkey);
    }

    private void UpdateHotkeyDisplay() => _txtHotkey.Text = HotkeyText.Format(_hotkey);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _hookedForm is not null)
        {
            // The form outlives this page during teardown, so leaving these
            // attached would keep a disposed page reachable.
            _hookedForm.Deactivate -= OnOwnerDeactivate;
            _hookedForm.Activated -= OnOwnerActivated;
            _hookedForm = null;
        }
        base.Dispose(disposing);
    }
}
