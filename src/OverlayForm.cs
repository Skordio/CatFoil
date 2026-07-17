using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Small draggable topmost badge shown while the keyboard is locked. Never
/// steals focus (WS_EX_NOACTIVATE). Its appearance is per-state: one look when
/// no fullscreen app is foreground, another (or hidden) when one is. Rendered as
/// a layered window so custom icons keep their own transparency with no halo.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WS_EX_TOPMOST    = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED    = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // Layered-window compositing.
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;
    private const byte OverlayAlpha = 235;   // ~0.92 overall opacity, as before

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; public SIZE(int cx, int cy) { Cx = cx; Cy = cy; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private readonly Bitmap _defaultIcon;
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 120 };
    private int _flashTicks;
    private bool _active;
    private string? _remainingText;

    // Per-state appearance and the resolved bitmap for each (default or custom).
    private OverlayStateSettings _normal = new();
    private OverlayStateSettings _fullscreen = new() { Visible = false };
    private Bitmap? _normalIcon;
    private Bitmap? _fullscreenIcon;
    private OverlayStateSettings _currentState = new();
    private Bitmap? _currentIcon;

    // What the last RenderLayered() actually painted. The 1s poll has to run to
    // notice a fullscreen app appearing, but re-compositing a layered window is
    // a bitmap allocation plus a DWM update — far too expensive to repeat every
    // second for a badge that almost never changes. Render only on a real change.
    private OverlayStateSettings? _paintedState;
    private Bitmap? _paintedIcon;
    private Size _paintedSize;
    private string? _paintedText;
    private bool _paintedFlash;

    private bool _dragging;
    private bool _moved;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    public event Action? OpenRequested;
    public event Action<Point>? PositionChanged;

    public OverlayForm(Icon appIcon)
    {
        using (var sized = new Icon(appIcon, 256, 256))
            _defaultIcon = sized.ToBitmap();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(_normal.ClampedSize(), _normal.ClampedSize());

        _normalIcon = _defaultIcon;
        _fullscreenIcon = _defaultIcon;
        _currentState = _normal;
        _currentIcon = _defaultIcon;

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
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Applies the two per-state appearances and refreshes if active.</summary>
    public void ApplyAppearance(OverlayStateSettings normal, OverlayStateSettings fullscreen)
    {
        _normal = normal.Clone();
        _fullscreen = fullscreen.Clone();

        ReplaceIcon(ref _normalIcon, LoadIcon(_normal));
        ReplaceIcon(ref _fullscreenIcon, LoadIcon(_fullscreen));

        if (_active) UpdateVisibility();
    }

    // Swap a state's bitmap, disposing the old one unless it's the shared default.
    private void ReplaceIcon(ref Bitmap? slot, Bitmap next)
    {
        if (slot is not null && slot != _defaultIcon && slot != next) slot.Dispose();
        slot = next;
    }

    private Bitmap LoadIcon(OverlayStateSettings state)
    {
        if (!state.UseCustomIcon || string.IsNullOrWhiteSpace(state.CustomIconFile))
            return _defaultIcon;
        try
        {
            string path = Path.Combine(Settings.Directory, state.CustomIconFile);
            if (!File.Exists(path)) return _defaultIcon;
            // Copy into memory so the file on disk isn't locked open.
            using var fromFile = new Bitmap(path);
            return new Bitmap(fromFile);
        }
        catch
        {
            return _defaultIcon;   // unreadable/corrupt file — fall back
        }
    }

    public void ApplySavedPosition(Point? saved)
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Point p = saved ?? new Point(workArea.Right - Width - 16, workArea.Top + 16);
        Location = ClampToScreen(p);
    }

    private Point ClampToScreen(Point p)
    {
        var virt = SystemInformation.VirtualScreen;
        p.X = Math.Max(virt.Left, Math.Min(p.X, virt.Right - Width));
        p.Y = Math.Max(virt.Top, Math.Min(p.Y, virt.Bottom - Height));
        return p;
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
        if (Visible) RenderIfChanged();
    }

    public void FlashBlockedKey()
    {
        if (!Visible || _flashTimer.Enabled) return;
        _flashTicks = 4;
        _flashTimer.Start();
        RenderIfChanged();
    }

    private void FlashTick()
    {
        if (--_flashTicks <= 0) _flashTimer.Stop();
        RenderIfChanged();
    }

    private void UpdateVisibility()
    {
        if (!_active) return;

        var state = ForegroundIsFullscreen() ? _fullscreen : _normal;
        Bitmap icon = state == _fullscreen ? (_fullscreenIcon ?? _defaultIcon) : (_normalIcon ?? _defaultIcon);

        if (!state.Visible)
        {
            if (Visible)
            {
                Hide();
                _paintedState = null;   // force a repaint if this state comes back
            }
            return;
        }

        _currentState = state;
        _currentIcon = icon;

        int size = state.ClampedSize();
        if (ClientSize.Width != size)
        {
            ClientSize = new Size(size, size);
            Location = ClampToScreen(Location);
        }

        if (!Visible) Show();
        RenderIfChanged();
    }

    // The poll runs every second; this keeps it nearly free unless something the
    // badge actually shows has changed since the last paint.
    private void RenderIfChanged()
    {
        bool flashOn = _flashTimer.Enabled && _flashTicks % 2 == 1;
        if (ReferenceEquals(_paintedState, _currentState)
            && ReferenceEquals(_paintedIcon, _currentIcon)
            && _paintedSize == ClientSize
            && _paintedText == _remainingText
            && _paintedFlash == flashOn)
        {
            return;
        }
        RenderLayered();
    }

    // True when the foreground window belongs to CatFoil itself (Settings,
    // Welcome, the main window, etc.) — used to avoid auto-locking while the
    // user is reading/configuring one of our own windows.
    internal static bool ForegroundIsOwnProcess()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    internal static bool ForegroundIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        var cls = new StringBuilder(256);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(fg, out RECT r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // Paint the badge into a 32bpp ARGB bitmap and push it to the layered window.
    private void RenderLayered()
    {
        if (!IsHandleCreated || _currentIcon is null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        bool flashOn = _flashTimer.Enabled && _flashTicks % 2 == 1;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            OverlayRenderer.Draw(g, new Rectangle(0, 0, w, h), _currentState, _currentIcon, _remainingText, flashOn);
        }

        // Remember exactly what this paint represents, so RenderIfChanged can skip
        // the next poll tick when nothing moved.
        _paintedState = _currentState;
        _paintedIcon = _currentIcon;
        _paintedSize = ClientSize;
        _paintedText = _remainingText;
        _paintedFlash = flashOn;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));   // premultiplied ARGB DIB
        IntPtr oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(w, h);
            var src = new POINT(0, 0);
            var dst = new POINT(Left, Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = OverlayAlpha,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReplaceIcon(ref _normalIcon, _defaultIcon);
            ReplaceIcon(ref _fullscreenIcon, _defaultIcon);
            _defaultIcon.Dispose();
            _tip.Dispose();
            _poll.Dispose();
            _flashTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
