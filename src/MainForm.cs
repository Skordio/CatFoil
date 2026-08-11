using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CatFoil;

public sealed class MainForm : Form
{
    private readonly Settings _settings;

    // The window's two views: the main lock screen, and settings. One of them
    // fills the window at a time; each remembers its own size (persisted via
    // Settings.MainWindowSize / SettingsWindowSize).
    private readonly Panel _mainView = new();
    private Panel? _settingsView;
    private SettingsShell? _shell;
    private SettingsShell.BackButton? _returnButton;
    private bool _inSettings;

    private readonly Label _status = new();
    private readonly Button _toggle = new();
    private readonly Button _settingsButton = new();
    private readonly HotkeyBadge _hotkeyBadge = new();
    private readonly ToolTip _tip = new();
    private bool _locked;

    // One size for the main view whether locked or not: locking used to grow
    // the window, but a resize the user didn't ask for reads as the window
    // jumping around — the state change is the text, not the geometry.
    private static readonly Size MainViewSize = new(700, 440);
    private static readonly Size MainViewMinimum = new(420, 260);

    // The settings pages were designed for SettingsShell.DesignSize; the strip
    // with the return button sits above the shell, so the window adds its height.
    private const int ReturnStripHeight = 44;
    private static readonly Size SettingsViewSize =
        new(SettingsShell.DesignSize.Width, SettingsShell.DesignSize.Height + ReturnStripHeight);
    private static readonly Size SettingsViewMinimum =
        new(SettingsShell.MinimumWindowSize.Width, SettingsShell.MinimumWindowSize.Height + ReturnStripHeight);

    // Cached so lock/unlock toggling reuses them instead of allocating (and
    // leaking, since WinForms doesn't dispose a Font you overwrite) each time.
    private static readonly Font ActiveFont = new("Segoe UI", 16f, FontStyle.Bold);
    private static readonly Font LockedFont = new("Segoe UI", 18f, FontStyle.Regular);
    // Same reasoning for the fixed control fonts: WinForms never disposes a Font
    // assigned to a control's .Font, so shared statics avoid a per-form leak.
    private static readonly Font ToggleFont = new("Segoe UI", 14f, FontStyle.Bold);
    private static readonly Font ButtonFont = new("Segoe UI", 10f);
    private static readonly Font ReturnFont = new("Segoe UI", 10.5f, FontStyle.Bold);

    private const string LockedText =
        "The keyboard is currently locked.";

    /// <summary>The lock/unlock button was clicked; TrayAppContext decides what to do.</summary>
    public event Action? ToggleRequested;

    /// <summary>The Settings button was clicked; TrayAppContext opens the settings view.</summary>
    public event Action? SettingsRequested;

    /// <summary>The user closed the window with "Hide to tray on close" off,
    /// which means quitting CatFoil; TrayAppContext runs the real shutdown.</summary>
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
        MinimumSize = SizeFromClientSize(MainViewMinimum);
        ClientSize = ClampToScreen(_settings.MainWindowSize ?? MainViewSize, MainViewMinimum);
        BackColor = Color.FromArgb(245, 245, 245);

        // --- Status label (fills the main view) ---
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = ActiveFont;
        _status.ForeColor = Color.FromArgb(0, 130, 0);
        _status.Text = "Keyboard is unlocked.";

        // --- Toggle button (docked to the bottom, big enough to mouse-click) ---
        _toggle.Dock = DockStyle.Bottom;
        _toggle.Height = 64;
        _toggle.Font = ToggleFont;
        _toggle.Text = "Lock Keyboard";
        _toggle.Click += (_, _) => ToggleRequested?.Invoke();
        // Stop the button from grabbing keyboard focus / space-bar activation.
        _toggle.TabStop = false;

        // --- Settings button (top-left) ---
        // Exit lives in the tray menu only: an always-visible red Exit next to
        // Settings read as an equal, everyday action, which quitting is not.
        _settingsButton.Text = "Settings";
        _settingsButton.Size = new Size(104, 40);
        _settingsButton.Location = new Point(12, 10);
        _settingsButton.Font = ButtonFont;
        _settingsButton.TabStop = false;
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        // --- Hotkey badge (bottom-left, right above the lock button) ---
        _hotkeyBadge.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _tip.SetToolTip(_hotkeyBadge, "Global hotkey — locks and unlocks the keyboard");
        RefreshHotkey();

        _mainView.Dock = DockStyle.Fill;
        _mainView.Controls.Add(_hotkeyBadge);      // low indexes = topmost, above the docked label
        _mainView.Controls.Add(_settingsButton);
        _mainView.Controls.Add(_status);           // Fill gets the space left over by the docked controls
        _mainView.Controls.Add(_toggle);
        Controls.Add(_mainView);

        // The view fills the client area, so its height is known now; the
        // anchor keeps the badge riding the bottom edge from here on.
        _hotkeyBadge.Location =
            new Point(12, ClientSize.Height - _toggle.Height - _hotkeyBadge.Height - 10);

        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            // Leaving here — not remembering the view — is what makes "the next
            // open always shows the main window" true by construction. It also
            // ends the settings visit exactly where the old window-close did.
            LeaveSettings();
            if (_settings.MinimizeToTrayOnClose)
            {
                Hide();
            }
            else
            {
                // With hide-to-tray off, closing the only window means quitting.
                // Letting the close proceed instead would dispose this form while
                // the tray lived on — every later tray action (and the hook's
                // BeginInvoke on a blocked key) then threw ObjectDisposedException.
                // Deferred so the closing event fully unwinds before shutdown.
                BeginInvoke(() => ExitRequested?.Invoke());
            }
        }
        else
        {
            // A real close still has to end the visit, or a debounced edit made
            // just before exiting would never reach disk.
            LeaveSettings();
        }
    }

    /// <summary>
    /// Switches the window to the settings view, adopting the shell on first
    /// call. The shell arrives constructed-hidden and stays alive for the app's
    /// lifetime; per-visit semantics live in <see cref="SettingsShell.EndVisit"/>.
    /// </summary>
    internal void EnterSettings(SettingsShell shell)
    {
        AdoptShell(shell);
        if (_inSettings) return;
        _inSettings = true;

        SuspendLayout();
        // Minimum first: applying a ClientSize below the old minimum would be
        // silently corrected against the wrong floor.
        MinimumSize = SizeFromClientSize(SettingsViewMinimum);
        ClientSize = ClampToScreen(_settings.SettingsWindowSize ?? SettingsViewSize, SettingsViewMinimum);
        _mainView.Visible = false;
        _settingsView!.Visible = true;
        _shell!.Visible = true;
        ResumeLayout();
    }

    /// <summary>
    /// Back to the main view. Ends the settings visit (flush, sweeps). Safe to
    /// call when already there — the close path calls it unconditionally.
    /// </summary>
    internal void LeaveSettings()
    {
        if (!_inSettings) return;
        _inSettings = false;

        _shell?.EndVisit();

        SuspendLayout();
        _settingsView!.Visible = false;
        _mainView.Visible = true;
        MinimumSize = SizeFromClientSize(MainViewMinimum);
        ClientSize = ClampToScreen(_settings.MainWindowSize ?? MainViewSize, MainViewMinimum);
        ResumeLayout();
    }

    private void AdoptShell(SettingsShell shell)
    {
        if (ReferenceEquals(_shell, shell)) return;

        if (_settingsView is null) BuildSettingsView();
        if (_shell is not null)
        {
            _shell.LeaveRequested -= LeaveSettings;
            _settingsView!.Controls.Remove(_shell);
        }

        _shell = shell;
        shell.Visible = false;   // shown by EnterSettings — a real transition,
                                 // which the Statistics page's timer relies on
        shell.Dock = DockStyle.Fill;
        shell.LeaveRequested += LeaveSettings;
        _settingsView!.Controls.Add(shell);
        // Fill must sit in front of the Top-docked strip in the z-order, or the
        // dock layout hands the strip's row to the shell too.
        shell.BringToFront();
    }

    // The strip above the shell: the always-present way back to the main view,
    // visible on every settings page and sub-page (the shell's own back button
    // only walks sub-page → page).
    private void BuildSettingsView()
    {
        _settingsView = new Panel { Dock = DockStyle.Fill, Visible = false };

        var strip = new Panel
        {
            Dock = DockStyle.Top,
            Height = ReturnStripHeight,
            BackColor = Color.White,
        };
        // A hairline under the strip, so it reads as chrome rather than as the
        // first row of the page.
        strip.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(225, 225, 225));
            e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
        };

        _returnButton = new SettingsShell.BackButton
        {
            Size = new Size(34, 34),
            Location = new Point(12, (ReturnStripHeight - 34) / 2),
        };
        _returnButton.Click += (_, _) => LeaveSettings();

        var label = new Label
        {
            Text = "CatFoil",
            AutoSize = true,
            Font = ReturnFont,
            ForeColor = Color.FromArgb(40, 40, 44),
            Location = new Point(54, (ReturnStripHeight - 20) / 2),
            Cursor = Cursors.Hand,
        };
        label.Click += (_, _) => LeaveSettings();

        strip.Controls.Add(_returnButton);
        strip.Controls.Add(label);
        _settingsView.Controls.Add(strip);
        Controls.Add(_settingsView);
        _settingsView.BringToFront();
    }

    /// <summary>
    /// Stores the current size into the active view's slot. Called with a
    /// Save() from the drag-end handler; probes call it alone, because Save()
    /// writes the user's real settings.json.
    /// </summary>
    private void RecordViewSize()
    {
        if (WindowState != FormWindowState.Normal) return;
        if (_inSettings) _settings.SettingsWindowSize = ClientSize;
        else _settings.MainWindowSize = ClientSize;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        // Fires after moves too (same WM_EXITSIZEMOVE), so skip the disk write
        // when the size the active view remembers is already this one.
        Size? recorded = _inSettings ? _settings.SettingsWindowSize : _settings.MainWindowSize;
        if (recorded == ClientSize) return;
        RecordViewSize();
        _settings.Save();
    }

    // settings.json is hand-editable, so a stored size can be absurd: floor it
    // at the view's minimum and cap it to the screen it will appear on.
    private Size ClampToScreen(Size wanted, Size minimum)
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;
        return new Size(
            Math.Clamp(wanted.Width, minimum.Width, Math.Max(minimum.Width, area.Width)),
            Math.Clamp(wanted.Height, minimum.Height, Math.Max(minimum.Height, area.Height)));
    }

    // Escape is a settings gesture: the shell's own ProcessCmdKey catches it
    // while focus is inside the shell; this covers focus resting on the form.
    // In the main view Escape means nothing.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_inSettings && keyData == Keys.Escape && _shell is not null)
            return _shell.HandleEscape();
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SetLockedUi(bool locked)
    {
        // The lock screen IS the main view — snapping back also keeps the two
        // views' sizes from fighting, since this no longer resizes anything.
        if (locked) LeaveSettings();

        _locked = locked;
        if (locked)
        {
            Text = "CatFoil — keyboard locked";

            _status.Font = LockedFont;
            _status.ForeColor = Color.FromArgb(60, 60, 60);
            _status.Text = LockedText;
            _toggle.Text = "Unlock Keyboard";
        }
        else
        {
            Text = "CatFoil";

            _status.Font = ActiveFont;
            _status.ForeColor = Color.FromArgb(0, 130, 0);
            _status.Text = "Keyboard is unlocked.";
            _toggle.Text = "Lock Keyboard";
        }
    }

    /// <summary>Countdown for a user-chosen timed lock.</summary>
    public void ShowLockCountdown(TimeSpan remaining)
    {
        if (!_locked) return;
        _status.Text = LockedText + $"\n\nAuto-unlock in {remaining:m\\:ss}";
    }

    /// <summary>Re-reads the hotkey from settings (call after settings change).</summary>
    public void RefreshHotkey()
    {
        _hotkeyBadge.Visible = _settings.HotkeyEnabled;
        _hotkeyBadge.SetParts(HotkeyText.ActiveParts(_settings));
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
