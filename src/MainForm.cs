using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CatFoil;

public sealed class MainForm : Form
{
    private readonly Settings _settings;

    private readonly Label _status = new();
    private readonly Button _toggle = new();
    private readonly Button _settingsButton = new();
    private readonly Button _exitButton = new();
    private readonly HotkeyBadge _hotkeyBadge = new();
    private readonly ToolTip _tip = new();
    private readonly LinkLabel _buyLink = new();
    private bool _locked;

    private static readonly Size UnlockedSize = new(420, 260);
    private static readonly Size LockedSize   = new(760, 480);

    // Cached so lock/unlock toggling reuses them instead of allocating (and
    // leaking, since WinForms doesn't dispose a Font you overwrite) each time.
    private static readonly Font ActiveFont = new("Segoe UI", 16f, FontStyle.Bold);
    private static readonly Font LockedFont = new("Segoe UI", 18f, FontStyle.Regular);

    private const string LockedText =
        "The keyboard is currently locked.";

    /// <summary>The lock/unlock button was clicked; TrayAppContext decides what to do.</summary>
    public event Action? ToggleRequested;

    /// <summary>The Settings button was clicked; TrayAppContext opens the settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>The Exit button was clicked; TrayAppContext shuts the app down.</summary>
    public event Action? ExitRequested;

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
        _status.Font = ActiveFont;
        _status.ForeColor = Color.FromArgb(0, 130, 0);
        _status.Text = "Keyboard is unlocked.";

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

        // --- Exit + Settings buttons (top-left) ---
        _exitButton.Text = "Exit";
        _exitButton.Size = new Size(104, 40);
        _exitButton.Location = new Point(12, 10);
        _exitButton.Font = new Font("Segoe UI", 10f);
        _exitButton.BackColor = Color.FromArgb(250, 228, 226);   // soft red tint
        _exitButton.ForeColor = Color.FromArgb(140, 35, 35);
        _exitButton.TabStop = false;
        _exitButton.Click += (_, _) => ExitRequested?.Invoke();

        _settingsButton.Text = "Settings";
        _settingsButton.Size = new Size(104, 40);
        _settingsButton.Location = new Point(12 + 104 + 8, 10);
        _settingsButton.Font = new Font("Segoe UI", 10f);
        _settingsButton.TabStop = false;
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        // --- Hotkey badge (bottom-left, right above the lock button) ---
        _hotkeyBadge.Location = new Point(12, UnlockedSize.Height - _toggle.Height - _hotkeyBadge.Height - 10);
        _hotkeyBadge.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _tip.SetToolTip(_hotkeyBadge, "Global hotkey — locks and unlocks the keyboard");
        RefreshHotkey();

        Controls.Add(_hotkeyBadge);      // low indexes = topmost, above the docked label
        Controls.Add(_exitButton);
        Controls.Add(_settingsButton);
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

            _status.Font = LockedFont;
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

            _status.Font = ActiveFont;
            _status.ForeColor = Color.FromArgb(0, 130, 0);
            _status.Text = "Keyboard is unlocked.";
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

    /// <summary>Countdown for a user-chosen timed lock (no buy link).</summary>
    public void ShowLockCountdown(TimeSpan remaining)
    {
        if (!_locked) return;
        // A timed lock is its own single countdown; never carry over the trial's buy link.
        _buyLink.Visible = false;
        _status.Text = LockedText + $"\n\nAuto-unlock in {remaining:m\\:ss}";
    }

    public void ShowTrialExpired()
    {
        _status.ForeColor = Color.FromArgb(180, 0, 0);
        _status.Text = "Free session limit reached — the keyboard has been unlocked.\n\nBuy a license for unlimited lock time.";
        _buyLink.Visible = true;
    }

    /// <summary>Re-reads the hotkey from settings (call after settings change).</summary>
    public void RefreshHotkey()
    {
        _hotkeyBadge.Visible = _settings.HotkeyEnabled;
        _hotkeyBadge.SetParts(SettingsForm.ActiveHotkeyParts(_settings));
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

    /// <summary>
    /// Draws a key combo as keycaps — rounded boxes with a 3D bottom lip,
    /// joined by "+" — like the keys look on a physical keyboard.
    /// </summary>
    private sealed class HotkeyBadge : Control
    {
        private const int PadX = 9;    // horizontal padding inside a keycap
        private const int Gap  = 5;    // space on each side of a "+"
        private const int Lip  = 3;    // height of the keycap's bottom edge

        private static readonly Font KeyFont = new("Segoe UI", 9.5f, FontStyle.Bold);

        private string[] _parts = Array.Empty<string>();

        public HotkeyBadge()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 30;
        }

        public void SetParts(string[] parts)
        {
            _parts = parts;

            int width = 0;
            foreach (string part in _parts)
                width += TextRenderer.MeasureText(part, KeyFont).Width + PadX * 2;
            width += (_parts.Length - 1) * (TextRenderer.MeasureText("+", KeyFont).Width + Gap * 2);
            Width = width;

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var sideBrush   = new SolidBrush(Color.FromArgb(180, 180, 185));
            using var faceBrush   = new SolidBrush(Color.White);
            using var borderPen   = new Pen(Color.FromArgb(160, 160, 165));
            int plusWidth = TextRenderer.MeasureText("+", KeyFont).Width;
            int capHeight = Height - Lip;

            int x = 0;
            for (int i = 0; i < _parts.Length; i++)
            {
                int capWidth = TextRenderer.MeasureText(_parts[i], KeyFont).Width + PadX * 2;

                // The "side" sticks out below the face, giving the 3D lip.
                using (var side = RoundedRect(new Rectangle(x, Lip, capWidth - 1, capHeight - 1), 5))
                    g.FillPath(sideBrush, side);
                using (var face = RoundedRect(new Rectangle(x, 0, capWidth - 1, capHeight - 1), 5))
                {
                    g.FillPath(faceBrush, face);
                    g.DrawPath(borderPen, face);
                }

                TextRenderer.DrawText(g, _parts[i], KeyFont,
                    new Rectangle(x, 0, capWidth, capHeight), Color.FromArgb(70, 70, 70),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                x += capWidth;
                if (i < _parts.Length - 1)
                {
                    TextRenderer.DrawText(g, "+", KeyFont,
                        new Rectangle(x + Gap, 0, plusWidth, capHeight), Color.FromArgb(140, 140, 140),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    x += plusWidth + Gap * 2;
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
