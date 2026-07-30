using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// The settings window: a navigation list on the left and one page at a time
/// on the right. Each page owns its own controls (see <see cref="SettingsPage"/>),
/// so a new setting is a row on a page rather than a re-layout of one long
/// fixed-pixel dialog.
///
/// There is no Save/Cancel — edits apply immediately and are persisted by
/// <see cref="SettingsSession"/>.
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly Font DialogFont = new("Segoe UI", 9.5f);
    private static readonly Font HeaderFont = new("Segoe UI", 15f);

    private static readonly Color NavBack = Color.FromArgb(246, 246, 246);
    private static readonly Color NavSelected = Color.FromArgb(226, 234, 246);
    private static readonly Color NavAccent = Color.FromArgb(58, 110, 190);
    private static readonly Color NavText = Color.FromArgb(30, 30, 30);

    private const int NavWidth = 172;

    private readonly SettingsSession _session;
    private readonly SettingsPage[] _pages;
    private readonly ListBox _nav = new();
    private readonly Panel _host = new();
    private readonly Label _header = new();
    private readonly BackButton _back = new();
    private readonly TableLayoutPanel _headerRow;
    private SettingsPage? _current;

    // A page reached from within another page (the per-overlay editor) rather
    // than from the nav list. One deep is enough for everything planned, and a
    // stack would only add ways to get lost.
    private SettingsPage? _subPage;
    private SettingsPage? _subPageParent;

    /// <summary>Raised after any edit, so the app can apply it live.</summary>
    public event Action? SettingsSaved;

    /// <summary>Raised after an elevated instance has been launched; the app
    /// should quit so that instance can take over the single-instance slot.</summary>
    public event Action? RestartElevatedRequested;

    /// <summary>True while the user is binding a new hotkey — the app should
    /// stop listening for the current one until it goes false again.</summary>
    public event Action<bool>? HotkeyCaptureChanged;

    public SettingsForm(Settings settings) : this(settings, null, null) { }

    /// <param name="inProgressLockSeconds">Elapsed seconds of a lock session in
    /// progress (0 when unlocked) — the Statistics page adds it to the displayed
    /// total so the read-out ticks live while the keyboard is locked.</param>
    /// <param name="onStatsReset">Called when the Statistics page zeroes the
    /// counters, so the owner can restart an in-progress session's clock.</param>
    public SettingsForm(Settings settings, Func<long>? inProgressLockSeconds, Action? onStatsReset)
    {
        _session = new SettingsSession(settings);
        _session.Changed += () => SettingsSaved?.Invoke();
        _session.RestartElevatedRequested += () => RestartElevatedRequested?.Invoke();
        _session.HotkeyCaptureChanged += capturing => HotkeyCaptureChanged?.Invoke(capturing);

        _pages = new SettingsPage[]
        {
            new GeneralPage(_session),
            new LockingPage(_session),
            new OverlaysPage(_session),
            new SoundsPage(_session),
            new StatisticsPage(_session, inProgressLockSeconds, onStatsReset),
            new AdvancedPage(_session),
            new AboutPage(_session),
        };

        Text = "CatFoil Settings";
        // Its own taskbar button, grouped under the main window's icon (same
        // process, so same AppUserModelID) — hovering the tray-adjacent taskbar
        // icon shows both windows. Minimize has to come with it: a taskbar
        // button that can't minimize its window is a button that does nothing.
        ShowInTaskbar = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = DialogFont;
        BackColor = Color.White;
        // Wide enough for the overlay editor, which puts a real-size preview
        // beside its controls; the minimum still fits that page without clipping.
        // Tall enough that the editor's controls aren't bisected by the bottom of
        // the scroll viewport, which reads as a half-drawn row rather than as
        // "there is more below".
        ClientSize = new Size(900, 720);
        MinimumSize = new Size(840, 560);

        _nav.Dock = DockStyle.Fill;
        _nav.BorderStyle = BorderStyle.None;
        _nav.BackColor = NavBack;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.ItemHeight = 34;
        _nav.IntegralHeight = false;   // otherwise the list snaps its height to whole rows
        _nav.Font = DialogFont;
        _nav.DrawItem += OnDrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        // Clicking the row that is already selected raises no SelectedIndexChanged,
        // so without this a sub-page would strand the user: the nav highlights
        // Overlays, clicking Overlays does nothing, and only Escape gets back.
        _nav.MouseDown += (_, e) =>
        {
            if (_subPage is not null && _nav.IndexFromPoint(e.Location) == _nav.SelectedIndex)
                PopSubPage();
        };
        foreach (SettingsPage page in _pages)
            _nav.Items.Add(page.Title);

        _back.Size = new Size(34, 34);
        _back.Anchor = AnchorStyles.Left;
        _back.Margin = new Padding(16, 0, 0, 0);
        _back.Visible = false;
        _back.Click += (_, _) => PopSubPage();

        _header.Dock = DockStyle.Fill;
        _header.Font = HeaderFont;
        _header.TextAlign = ContentAlignment.MiddleLeft;
        _header.Padding = new Padding(22, 0, 0, 0);
        _header.BackColor = Color.White;

        // The back button occupies its own column so the header text doesn't
        // shift sideways as it appears and disappears.
        var headerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        headerRow.Controls.Add(_back, 0, 0);
        headerRow.Controls.Add(_header, 1, 0);
        _headerRow = headerRow;

        _host.Dock = DockStyle.Fill;
        _host.BackColor = Color.White;

        // Nested TableLayoutPanels rather than raw docking: with Dock alone the
        // result depends on the order controls were added, which is a trap for
        // whoever adds the next panel here.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        right.Controls.Add(headerRow, 0, 0);
        right.Controls.Add(_host, 0, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.Controls.Add(_nav, 0, 0);
        root.Controls.Add(right, 1, 0);

        Controls.Add(root);

        _nav.SelectedIndex = 0;
    }

    private void OnDrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _pages.Length) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? NavSelected : NavBack))
            e.Graphics.FillRectangle(back, e.Bounds);

        if (selected)
        {
            using var accent = new SolidBrush(NavAccent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 7, 3, e.Bounds.Height - 14);
        }

        var text = new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - 22, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, _pages[e.Index].Title, DialogFont, text, NavText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Navigates to a page by type — how the tray's "Statistics…"
    /// entry lands on that page rather than wherever the window last was.</summary>
    internal void SelectPage<T>() where T : SettingsPage
    {
        int index = Array.FindIndex(_pages, p => p is T);
        if (index >= 0) _nav.SelectedIndex = index;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length) return;

        SettingsPage page = _pages[index];
        if (_subPage is null && ReferenceEquals(page, _current)) return;

        // Navigating by the nav list abandons any sub-page — without this the
        // sub-page would stay on top of the newly selected page.
        DiscardSubPage();
        SwapTo(page);
    }

    /// <summary>
    /// Shows a page reached from within the current one (the per-overlay
    /// editor), with a back arrow and a breadcrumb. The page is owned from here
    /// on and is disposed when it goes away.
    /// </summary>
    internal void ShowSubPage(SettingsPage page)
    {
        if (_current is null) { page.Dispose(); return; }

        SettingsPage parent = _current;
        DiscardSubPage();          // only ever one deep
        _subPageParent = parent;
        _subPage = page;

        SwapTo(page);
        _header.Text = parent.Title + " ›  " + page.Title;
        SetBackVisible(true);
    }

    /// <summary>
    /// Rebuilds the breadcrumb. A sub-page's title can change while it is open —
    /// renaming an overlay does exactly that — and the header is built from it.
    /// </summary>
    internal void RefreshSubPageTitle()
    {
        if (_subPage is null || _subPageParent is null) return;
        _header.Text = _subPageParent.Title + " ›  " + _subPage.Title;
    }

    /// <summary>Returns from a sub-page to the page it was opened from.</summary>
    internal void PopSubPage()
    {
        if (_subPage is null || _subPageParent is null) return;

        SettingsPage parent = _subPageParent;
        DiscardSubPage();
        SwapTo(parent);
    }

    // Tears down the sub-page without showing anything in its place; every
    // caller decides for itself what should be visible next.
    private void DiscardSubPage()
    {
        if (_subPage is null) return;

        SettingsPage gone = _subPage;
        _subPage = null;
        _subPageParent = null;
        SetBackVisible(false);

        // SwapTo hides whatever _current points at; it must not still be this.
        if (ReferenceEquals(_current, gone)) _current = null;
        _host.Controls.Remove(gone);
        gone.Dispose();
    }

    private void SwapTo(SettingsPage page)
    {
        _host.SuspendLayout();
        if (_current is not null && !_current.IsDisposed) _current.Visible = false;
        // Added on first visit, so a page's OnLoad work (Advanced spawns
        // schtasks.exe) only happens if the user actually goes there.
        if (!_host.Controls.Contains(page)) _host.Controls.Add(page);
        page.Visible = true;
        page.BringToFront();
        _host.ResumeLayout();

        _current = page;
        _header.Text = page.Title;
    }

    private void SetBackVisible(bool visible)
    {
        _back.Visible = visible;
        _headerRow.ColumnStyles[0].Width = visible ? 62f : 0f;
    }

    /// <summary>
    /// The "go back" affordance: a dark rounded square with a white triangle.
    /// It replaced a bare "‹" glyph, which at header size was both easy to miss
    /// and clipped by its own bounds — a text glyph gives no control over how
    /// much of the em box it actually fills.
    /// </summary>
    private sealed class BackButton : Control
    {
        private static readonly Color Idle = Color.FromArgb(64, 64, 68);
        private static readonly Color Hover = Color.FromArgb(90, 90, 96);
        private static readonly Color Down = Color.FromArgb(40, 40, 44);

        private bool _hover;
        private bool _down;

        public BackButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var fill = new SolidBrush(_down ? Down : _hover ? Hover : Idle))
            using (var path = OverlayRenderer.RoundedRect(box, 7))
                g.FillPath(fill, path);

            // Triangle sized from the box so it stays centred at any scale.
            float cx = Width / 2f, cy = Height / 2f;
            float w = Width * 0.20f, h = Height * 0.26f;
            var arrow = new[]
            {
                new PointF(cx - w, cy),
                new PointF(cx + w * 0.8f, cy - h),
                new PointF(cx + w * 0.8f, cy + h),
            };
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, arrow);
        }
    }

    // The old dialog closed on Escape via its Cancel button; immediate-apply
    // removed the buttons but not the habit. Bare Escape only — a modifier is
    // required to bind a hotkey, so Ctrl+Esc and friends still reach the
    // capture box untouched.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            // On a sub-page Escape means "back", not "close" — closing the whole
            // window from the overlay editor would be a surprising amount of exit.
            if (_subPage is not null) PopSubPage();
            else Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Leave never fires if the window is closed while the hotkey box has
        // focus, so release the capture hold explicitly, then write out any
        // edit still sitting in the debounce.
        _session.SetHotkeyCapture(false);
        _session.Flush();

        // Sweep overlay images nothing refers to any more. Deferred to here
        // rather than done when an overlay is removed, so removing one and
        // changing your mind inside the same visit doesn't cost you the image.
        // Release the MCI devices first: they hold their audio files open, so a
        // cue auditioned with Test would otherwise pin the file it replaced and
        // the sweep would silently skip it for the life of the process.
        AudioPlayer.CloseAll();
        IconStore.CollectGarbage(_session.Settings);
        SoundStore.CollectGarbage(_session.Settings);

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Dispose();
            // Pages are only parented on first visit, so the control tree won't
            // reach the ones the user never opened.
            foreach (SettingsPage page in _pages)
                page.Dispose();
            // A sub-page is never in _pages; it is owned by whoever pushed it.
            _subPage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
