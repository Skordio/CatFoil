using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CatFoil.Licensing;

namespace CatFoil;

public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly ILicenseProvider _license;

    private readonly CheckBox _chkTrayOnClose = new();
    private readonly CheckBox _chkStartMinimized = new();
    private readonly CheckBox _chkStartWithWindows = new();
    private readonly CheckBox _chkOverlay = new();
    private readonly CheckBox _chkHotkeyEnabled = new();
    private readonly CheckBox _chkChord = new();
    private readonly TextBox _txtHotkey = new();
    private readonly ToolTip _tip = new() { AutoPopDelay = 20000 };
    private readonly TextBox _txtLicenseKey = new();
    private readonly Button _btnActivate = new();
    private readonly Label _lblLicenseStatus = new();
    private readonly LinkLabel _lnkBuy = new();
    private readonly Button _btnSave = new();
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

    public SettingsForm(Settings settings, ILicenseProvider license)
    {
        _settings = settings;
        _license = license;
        _hotkey = settings.Hotkey;
        _chordModifiers = settings.ChordModifiers;
        _chordKeys = settings.ChordKeys;

        Text = "CatFoil Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(512, 542);
        Font = new Font("Segoe UI", 9.5f);

        // --- General ---
        var grpGeneral = new GroupBox { Text = "General", Bounds = new Rectangle(12, 12, 488, 186) };
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

        // --- Hotkey ---
        var grpHotkey = new GroupBox { Text = "Hotkey", Bounds = new Rectangle(12, 208, 488, 132) };
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

        // --- License ---
        // The status label wraps to 2 lines for longer messages, so it goes
        // LAST (at the bottom) where it can only grow into empty space — never
        // over the key box the way it did when it sat on top.
        var grpLicense = new GroupBox { Text = "License", Bounds = new Rectangle(12, 350, 488, 138) };
        _txtLicenseKey.Bounds = new Rectangle(16, 28, 268, 27);
        _txtLicenseKey.PlaceholderText = "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX";
        _txtLicenseKey.Text = settings.LicenseKey ?? "";
        _btnActivate.Text = "Activate";
        _btnActivate.Bounds = new Rectangle(296, 27, 104, 29);
        _btnActivate.Click += OnActivateClicked;
        _lnkBuy.AutoSize = true;
        _lnkBuy.Location = new Point(16, 64);
        _lnkBuy.Text = "Buy a license — removes the 30-minute session limit";
        _lnkBuy.LinkClicked += (_, _) => MainForm.OpenBuyPage();
        _lblLicenseStatus.AutoSize = true;
        _lblLicenseStatus.MaximumSize = new Size(456, 0);
        _lblLicenseStatus.Location = new Point(16, 94);
        grpLicense.Controls.AddRange(new Control[] { _txtLicenseKey, _btnActivate, _lnkBuy, _lblLicenseStatus });

        // --- Buttons ---
        _btnWelcome.Text = "Welcome tour…";
        _btnWelcome.Bounds = new Rectangle(12, 500, 120, 30);
        _btnWelcome.TabStop = false;
        _btnWelcome.Click += (_, _) =>
        {
            using var welcome = new WelcomeForm(_settings);
            welcome.ShowDialog(this);
        };
        _btnSave.Text = "Save";
        _btnSave.Bounds = new Rectangle(324, 500, 85, 30);
        _btnSave.Click += OnSaveClicked;
        _btnCancel.Text = "Cancel";
        _btnCancel.Bounds = new Rectangle(415, 500, 85, 30);
        _btnCancel.Click += (_, _) => Close();
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { grpGeneral, grpHotkey, grpLicense, _btnWelcome, _btnSave, _btnCancel });
        RefreshLicenseStatus(null);
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

    private async void OnActivateClicked(object? sender, EventArgs e)
    {
        _btnActivate.Enabled = false;
        _lblLicenseStatus.ForeColor = SystemColors.ControlText;
        _lblLicenseStatus.Text = "Activating…";

        var result = await _license.ActivateAsync(_txtLicenseKey.Text);

        RefreshLicenseStatus(result);
        _btnActivate.Enabled = true;
    }

    private void RefreshLicenseStatus(LicenseActivationResult? result)
    {
        if (result is { Success: false })
        {
            _lblLicenseStatus.ForeColor = Color.FromArgb(180, 0, 0);
            _lblLicenseStatus.Text = result.Message;
            return;
        }

        if (_license.IsLicensed)
        {
            _lblLicenseStatus.ForeColor = Color.FromArgb(0, 130, 0);
            _lblLicenseStatus.Text = "Licensed ✓ — unlimited lock time on this machine.";
        }
        else
        {
            _lblLicenseStatus.ForeColor = SystemColors.ControlText;
            _lblLicenseStatus.Text = "Free version — lock sessions end after 30 minutes.";
        }
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
        _settings.MinimizeToTrayOnClose = _chkTrayOnClose.Checked;
        _settings.StartMinimized = _chkStartMinimized.Checked;
        _settings.StartWithWindows = _chkStartWithWindows.Checked;
        _settings.ShowOverlay = _chkOverlay.Checked;
        _settings.HotkeyEnabled = _chkHotkeyEnabled.Checked;
        _settings.Hotkey = _hotkey;
        _settings.UseChordHotkey = _chkChord.Checked;
        _settings.ChordModifiers = _chordModifiers;
        _settings.ChordKeys = _chordKeys;
        _settings.Save();

        SettingsSaved?.Invoke();
        Close();
    }
}
