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
    private readonly LinkLabel _buyLink = new();
    private readonly System.Windows.Forms.Timer _flash = new();
    private bool _flashOn;
    private int _flashTicks;   // remaining on/off transitions in the current burst
    private bool _locked;

    private static readonly Size UnlockedSize = new(420, 260);
    private static readonly Size LockedSize   = new(760, 480);

    private const string LockedText = "⚠  KEYBOARD LOCKED  ⚠\n\nClick below to unlock";

    /// <summary>The lock/unlock button was clicked; TrayAppContext decides what to do.</summary>
    public event Action? ToggleRequested;

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

        Controls.Add(_status);      // added first so DockStyle.Fill gets the remaining space
        Controls.Add(_buyLink);
        Controls.Add(_toggle);

        // --- Flash timer: a short 2-blink reaction to a blocked key ---
        _flash.Interval = 120;
        _flash.Tick += (_, _) => FlashTick();

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

            Text = "🔒 KEYBOARD LOCKED";
            TopMost = true;

            _status.Font = new Font("Segoe UI", 30f, FontStyle.Bold);
            _status.Text = LockedText;
            _toggle.Text = "Unlock Keyboard";
            _buyLink.Visible = false;
            ResumeLayout();

            ApplyLockedStatic();
        }
        else
        {
            _flash.Stop();
            _flashTicks = 0;

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

    public void FlashBlockedKey()
    {
        if (!_locked || !Visible) return;

        // Don't stack bursts: only start a fresh 2-blink once the last finished.
        if (!_flash.Enabled)
        {
            // 2 flashes = ON, OFF, ON, OFF  (ends back on the static locked look)
            _flashTicks = 4;
            _flashOn = false;   // first tick flips this to ON
            _flash.Start();
        }
    }

    private void FlashTick()
    {
        _flashOn = !_flashOn;
        if (_flashOn)
        {
            BackColor = Color.FromArgb(180, 0, 0);
            _status.ForeColor = Color.White;
        }
        else
        {
            ApplyLockedStatic();
        }

        if (--_flashTicks <= 0)
        {
            _flash.Stop();
            ApplyLockedStatic();   // settle on the static locked look
        }
    }

    private void ApplyLockedStatic()
    {
        BackColor = Color.FromArgb(255, 235, 235);
        _status.ForeColor = Color.FromArgb(180, 0, 0);
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
