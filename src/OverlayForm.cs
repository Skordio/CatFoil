using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Small draggable topmost cat badge shown while the keyboard is locked.
/// Never steals focus (WS_EX_NOACTIVATE); hides itself while a fullscreen
/// app is in the foreground and reappears afterwards.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WS_EX_TOPMOST    = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private readonly Bitmap _iconBitmap;
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 120 };
    private int _flashTicks;
    private bool _active;
    private string? _remainingText;

    private bool _dragging;
    private bool _moved;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    public event Action? OpenRequested;
    public event Action<Point>? PositionChanged;

    public OverlayForm(Icon appIcon)
    {
        _iconBitmap = new Icon(appIcon, 48, 48).ToBitmap();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(64, 64);
        BackColor = Color.FromArgb(45, 45, 48);
        Opacity = 0.92;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Region = new Region(RoundedRect(new Rectangle(0, 0, 64, 64), 16));

        // The tooltip is shown manually: SetToolTip's automatic hover tracking
        // dismisses tips the moment it notices the owner window isn't active,
        // and this window never activates by design (WS_EX_NOACTIVATE).
        MouseHover += (_, _) => ShowTip();
        MouseLeave += (_, _) => _tip.Hide(this);

        _poll.Tick += (_, _) => UpdateVisibility();
        _flashTimer.Tick += (_, _) => FlashTick();

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    private const string TipText =
        "CatFoil is locking your keyboard.\nClick to open CatFoil.\n(Ctrl+Alt+Del always works.)";

    private void ShowTip()
    {
        if (_dragging) return;

        // Place the tip just outside the badge — a tip that spawns under the
        // cursor triggers MouseLeave, which would hide it again instantly.
        var screen = Screen.FromControl(this).WorkingArea;
        int y = Bottom + 90 > screen.Bottom ? -90 : Height + 6;
        _tip.Show(TipText, this, new Point(0, y), 4000);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ApplySavedPosition(Point? saved)
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Point p = saved ?? new Point(workArea.Right - Width - 16, workArea.Top + 16);

        // Clamp so a position saved on a since-removed monitor stays reachable.
        var virt = SystemInformation.VirtualScreen;
        p.X = Math.Max(virt.Left, Math.Min(p.X, virt.Right - Width));
        p.Y = Math.Max(virt.Top, Math.Min(p.Y, virt.Bottom - Height));
        Location = p;
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            UpdateVisibility();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
            _flashTimer.Stop();
            _remainingText = null;
            _tip.Hide(this);
            Hide();
        }
    }

    public void SetRemaining(TimeSpan? remaining)
    {
        _remainingText = remaining?.ToString(@"m\:ss");
        if (Visible) Invalidate();
    }

    public void FlashBlockedKey()
    {
        if (!Visible || _flashTimer.Enabled) return;
        _flashTicks = 4;
        _flashTimer.Start();
        Invalidate();
    }

    private void FlashTick()
    {
        if (--_flashTicks <= 0) _flashTimer.Stop();
        Invalidate();
    }

    private void UpdateVisibility()
    {
        if (!_active) return;
        bool hide = ForegroundIsFullscreen();
        if (hide && Visible) Hide();
        else if (!hide && !Visible) Show();
    }

    private static bool ForegroundIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        // The desktop/shell reports monitor-sized bounds but isn't "fullscreen".
        var cls = new StringBuilder(256);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(fg, out RECT r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (_remainingText is null)
        {
            g.DrawImage(_iconBitmap, new Rectangle(10, 10, 44, 44));
        }
        else
        {
            g.DrawImage(_iconBitmap, new Rectangle(13, 4, 38, 38));
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
            TextRenderer.DrawText(g, _remainingText, font, new Rectangle(0, 42, Width, 20),
                Color.FromArgb(255, 180, 70), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (_flashTimer.Enabled && _flashTicks % 2 == 1)
        {
            using var pen = new Pen(Color.FromArgb(220, 60, 60), 3f);
            using var path = RoundedRect(new Rectangle(2, 2, Width - 5, Height - 5), 14);
            g.DrawPath(pen, path);
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

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _tip.Hide(this);
        _dragging = true;
        _moved = false;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = Cursor.Position;
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;
        if (!_moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _moved = true;
        Location = new Point(_dragStartLocation.X + dx, _dragStartLocation.Y + dy);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        if (_moved) PositionChanged?.Invoke(Location);
        else OpenRequested?.Invoke();
    }
}
