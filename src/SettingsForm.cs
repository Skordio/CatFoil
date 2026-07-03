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
    private readonly TextBox _txtHotkey = new();
    private readonly TextBox _txtLicenseKey = new();
    private readonly Button _btnActivate = new();
    private readonly Label _lblLicenseStatus = new();
    private readonly LinkLabel _lnkBuy = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnCancel = new();

    private Keys _hotkey;

    public event Action? SettingsSaved;

    public SettingsForm(Settings settings, ILicenseProvider license)
    {
        _settings = settings;
        _license = license;
        _hotkey = settings.Hotkey;

        Text = "CatFoil Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 472);
        Font = new Font("Segoe UI", 9.5f);

        // --- General ---
        var grpGeneral = new GroupBox { Text = "General", Bounds = new Rectangle(12, 12, 416, 150) };
        AddCheck(grpGeneral, _chkTrayOnClose, "Hide to the tray when the window is closed", 26, settings.MinimizeToTrayOnClose);
        AddCheck(grpGeneral, _chkStartMinimized, "Start hidden in the system tray", 56, settings.StartMinimized);
        AddCheck(grpGeneral, _chkStartWithWindows, "Start CatFoil when Windows starts", 86, settings.StartWithWindows);
        AddCheck(grpGeneral, _chkOverlay, "Show the cat overlay while the keyboard is locked", 116, settings.ShowOverlay);

        // --- Hotkey ---
        var grpHotkey = new GroupBox { Text = "Hotkey", Bounds = new Rectangle(12, 172, 416, 98) };
        AddCheck(grpHotkey, _chkHotkeyEnabled, "Lock/unlock the keyboard with a hotkey:", 26, settings.HotkeyEnabled);
        _txtHotkey.ReadOnly = true;
        _txtHotkey.Bounds = new Rectangle(16, 56, 170, 27);
        _txtHotkey.Text = FormatHotkey(_hotkey);
        _txtHotkey.KeyDown += OnHotkeyKeyDown;
        var hint = new Label
        {
            Text = "Click the box, then press a combination.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(196, 60),
        };
        grpHotkey.Controls.Add(_txtHotkey);
        grpHotkey.Controls.Add(hint);

        // --- License ---
        var grpLicense = new GroupBox { Text = "License", Bounds = new Rectangle(12, 280, 416, 138) };
        _lblLicenseStatus.AutoSize = true;
        _lblLicenseStatus.MaximumSize = new Size(384, 0);
        _lblLicenseStatus.Location = new Point(16, 24);
        _txtLicenseKey.Bounds = new Rectangle(16, 58, 268, 27);
        _txtLicenseKey.PlaceholderText = "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX";
        _txtLicenseKey.Text = settings.LicenseKey ?? "";
        _btnActivate.Text = "Activate";
        _btnActivate.Bounds = new Rectangle(296, 57, 104, 29);
        _btnActivate.Click += OnActivateClicked;
        _lnkBuy.AutoSize = true;
        _lnkBuy.Location = new Point(16, 100);
        _lnkBuy.Text = "Buy a license — removes the 30-minute session limit";
        _lnkBuy.LinkClicked += (_, _) => MainForm.OpenBuyPage();
        grpLicense.Controls.AddRange(new Control[] { _lblLicenseStatus, _txtLicenseKey, _btnActivate, _lnkBuy });

        // --- Buttons ---
        _btnSave.Text = "Save";
        _btnSave.Bounds = new Rectangle(252, 430, 85, 30);
        _btnSave.Click += OnSaveClicked;
        _btnCancel.Text = "Cancel";
        _btnCancel.Bounds = new Rectangle(343, 430, 85, 30);
        _btnCancel.Click += (_, _) => Close();
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { grpGeneral, grpHotkey, grpLicense, _btnSave, _btnCancel });
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

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None)
            return;   // a modifier alone isn't a hotkey yet
        if (e.Modifiers == Keys.None)
        {
            _txtHotkey.Text = "Add Ctrl, Alt or Shift…";
            return;
        }

        _hotkey = e.Modifiers | key;
        _txtHotkey.Text = FormatHotkey(_hotkey);
    }

    internal static string FormatHotkey(Keys combo)
    {
        var parts = new List<string>();
        if (combo.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (combo.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (combo.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add((combo & Keys.KeyCode).ToString());
        return string.Join(" + ", parts);
    }

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

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        _settings.MinimizeToTrayOnClose = _chkTrayOnClose.Checked;
        _settings.StartMinimized = _chkStartMinimized.Checked;
        _settings.StartWithWindows = _chkStartWithWindows.Checked;
        _settings.ShowOverlay = _chkOverlay.Checked;
        _settings.HotkeyEnabled = _chkHotkeyEnabled.Checked;
        _settings.Hotkey = _hotkey;
        _settings.Save();

        SettingsSaved?.Invoke();
        Close();
    }
}
