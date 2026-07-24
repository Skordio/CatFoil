using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>How the keyboard gets locked: the hotkey (single combo or chord)
/// and the idle auto-lock.</summary>
internal sealed class LockingPage : SettingsPage
{
    private readonly CheckBox _chkChord;
    private readonly TextBox _txtHotkey = new();
    private readonly CheckBox _chkAutoLock;
    private readonly NumericUpDown _numAutoLock = new();

    private Keys _hotkey;

    // Chord being edited, plus live capture state for the hotkey box.
    private Keys _chordModifiers;
    private Keys[] _chordKeys;
    private Keys _chordCaptureMods;
    private readonly List<Keys> _chordCapture = new();
    private readonly HashSet<Keys> _chordHeld = new();

    public override string Title => "Locking";

    public LockingPage(SettingsSession session) : base(session)
    {
        _hotkey = Settings.Hotkey;
        _chordModifiers = Settings.ChordModifiers;
        _chordKeys = Settings.ChordKeys;

        // --- Hotkey ---
        AddSection("Hotkey");
        AddCheck("Lock and unlock the keyboard with a hotkey",
            Settings.HotkeyEnabled,
            v => Session.Apply(s => s.HotkeyEnabled = v));

        _txtHotkey.ReadOnly = true;
        _txtHotkey.Font = BodyFont;
        _txtHotkey.Width = 190;
        _txtHotkey.KeyDown += OnHotkeyKeyDown;
        _txtHotkey.KeyUp += OnHotkeyKeyUp;
        // While the box has focus the app stops listening for the current
        // hotkey, so the combo being rebound lands here instead of toggling.
        _txtHotkey.Enter += (_, _) => Session.SetHotkeyCapture(true);
        _txtHotkey.Leave += (_, _) => Session.SetHotkeyCapture(false);

        var hint = new Label
        {
            Text = "Click the box, then press keys.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(10, 6, 0, 0),
        };
        AddRow(Row(_txtHotkey, hint), indent: 20);

        _chkChord = AddCheck("Multi-key chord (e.g. Alt + C + F)",
            Settings.UseChordHotkey,
            v => { Session.Apply(s => s.UseChordHotkey = v); UpdateHotkeyDisplay(); },
            "Lets the hotkey be modifiers plus two or three keys held together,\n" +
            "detected by CatFoil itself instead of Windows.\n\n" +
            "Example: with the chord Alt + C + F, hold down Alt, keep holding it\n" +
            "while you press C, and then press F. The moment all three are down\n" +
            "together, the keyboard locks — the same chord unlocks it again.\n\n" +
            "Trade-off: while the keyboard is unlocked, the first keys of the\n" +
            "chord still reach the app you're in — so the Alt + C part may\n" +
            "briefly open a menu in some programs before the F lands.",
            indent: 20);

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
        _numAutoLock.Width = 58;
        _numAutoLock.Value = Math.Clamp(Settings.AutoLockMinutes, 1, 120);
        _numAutoLock.Enabled = _chkAutoLock.Checked;
        _numAutoLock.Margin = new Padding(0, 1, 6, 0);
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
        _txtHotkey.Text = HotkeyText.Format(_hotkey);
        Session.Apply(s => s.Hotkey = _hotkey);
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

        _txtHotkey.Text = string.Join(" + ", HotkeyText.ChordParts(_chordCaptureMods, _chordCapture.ToArray()));
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
            Session.Apply(s =>
            {
                s.ChordModifiers = _chordModifiers;
                s.ChordKeys = _chordKeys;
            });
        }
        else
        {
            _txtHotkey.Text = "Hold a modifier + 2–3 keys together…";
        }
        _chordCapture.Clear();
    }

    private void UpdateHotkeyDisplay() =>
        _txtHotkey.Text = _chkChord.Checked
            ? string.Join(" + ", HotkeyText.ChordParts(_chordModifiers, _chordKeys))
            : HotkeyText.Format(_hotkey);
}
