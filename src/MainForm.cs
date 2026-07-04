using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

public sealed class MainForm : Form
{
    private readonly Settings _settings;

    private readonly Label _status = new();
    private readonly Button _toggle = new();
    private readonly Button _settingsButton = new();
    private readonly ToolTip _tip = new();
    private readonly LinkLabel _buyLink = new();
    private bool _locked;

    private static readonly Size UnlockedSize = new(420, 260);
    private static readonly Size LockedSize   = new(760, 480);

    private const string LockedText =
        "The keyboard is currently locked and will not accept input\nexcept Ctrl + Alt + Delete";

    /// <summary>The lock/unlock button was clicked; TrayAppContext decides what to do.</summary>
    public event Action? ToggleRequested;

    /// <summary>The settings cog was clicked; TrayAppContext opens the settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Set on real exit so closing stops hiding to the tray.</summary>
    public bool AllowClose { get; set; }

    public MainForm(Settings settings)
    {
        _settings = settings;

        // --- Form ---
        Text = "CatFoil";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        ClientSize = UnlockedSize;
        BackColor = Color.FromArgb(245, 245, 245);

        // --- Status label (fills the window) ---
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(0, 130, 0);
        _status.Text = "Keyboard is ACTIVE";

        // --- Buy-a-license link (only shown for trial countdown / expiry) ---
        _buyLink.Dock = DockStyle.Top;
        _buyLink.Height = 34;
        _buyLink.TextAlign = ContentAlignment.MiddleCenter;
        _buyLink.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _buyLink.Text = "Buy a CatFoil license — removes the 30-minute session limit";
        _buyLink.Visible = false;
        _buyLink.LinkClicked += (_, _) => OpenBuyPage();

        // --- Toggle button (docked to the bottom, big enough to mouse-click) ---
        _toggle.Dock = DockStyle.Bottom;
        _toggle.Height = 64;
        _toggle.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        _toggle.Text = "Lock Keyboard";
        _toggle.Click += (_, _) => ToggleRequested?.Invoke();
        // Stop the button from grabbing keyboard focus / space-bar activation.
        _toggle.TabStop = false;

        // --- Settings cog (floats in the top-right corner, above the status label) ---
        _settingsButton.Text = "⚙";
        _settingsButton.Font = new Font("Segoe UI", 14f);
        _settingsButton.Size = new Size(36, 36);
        _settingsButton.Location = new Point(UnlockedSize.Width - 44, 8);
        _settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 225, 225);
        _settingsButton.BackColor = Color.FromArgb(245, 245, 245);
        _settingsButton.Cursor = Cursors.Hand;
        _settingsButton.TabStop = false;
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        _tip.SetToolTip(_settingsButton, "Settings");

        Controls.Add(_settingsButton);   // index 0 = topmost, so it sits above the docked label
        Controls.Add(_status);           // Fill gets the space left over by the docked controls
        Controls.Add(_buyLink);
        Controls.Add(_toggle);

        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void SetLockedUi(bool locked)
    {
        _locked = locked;
        if (locked)
        {
            SuspendLayout();
            ClientSize = LockedSize;
            CenterToScreen();

            Text = "CatFoil — keyboard locked";
            TopMost = true;

            _status.Font = new Font("Segoe UI", 18f, FontStyle.Regular);
            _status.ForeColor = Color.FromArgb(60, 60, 60);
            _status.Text = LockedText;
            _toggle.Text = "Unlock Keyboard";
            _buyLink.Visible = false;
            ResumeLayout();
        }
        else
        {
            SuspendLayout();
            BackColor = Color.FromArgb(245, 245, 245);
            ClientSize = UnlockedSize;
            CenterToScreen();

            Text = "CatFoil";
            TopMost = false;

            _status.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            _status.ForeColor = Color.FromArgb(0, 130, 0);
            _status.Text = "Keyboard is ACTIVE";
            _toggle.Text = "Lock Keyboard";
            _buyLink.Visible = false;
            ResumeLayout();
        }
    }

    public void ShowTrialCountdown(TimeSpan remaining)
    {
        if (!_locked) return;
        _buyLink.Visible = true;
        _status.Text = LockedText + $"\n\nFree session ends in {remaining:m\\:ss}";
    }

    public void ShowTrialExpired()
    {
        _status.ForeColor = Color.FromArgb(180, 0, 0);
        _status.Text = "Free session limit reached — the keyboard has been unlocked.\n\nBuy a license for unlimited lock time.";
        _buyLink.Visible = true;
    }

    public static void OpenBuyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Licensing.LemonSqueezyProvider.BuyUrl) { UseShellExecute = true });
        }
        catch
        {
            // No browser available — nothing sensible to do.
        }
    }
}
