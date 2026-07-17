using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Shared painting for the locked overlay badge, used by both the live
/// <see cref="OverlayForm"/> and the live preview in the overlay settings menu
/// so the two always match. All drawing is relative to <paramref name="bounds"/>
/// so it works for any badge size.
/// </summary>
public static class OverlayRenderer
{
    private static readonly Color BackColor = Color.FromArgb(45, 45, 48);
    private static readonly Color CountdownColor = Color.FromArgb(255, 180, 70);
    private static readonly Color FlashColor = Color.FromArgb(220, 60, 60);

    private static readonly StringFormat CenterFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

    // The countdown font depends only on the badge width, which rarely changes,
    // but Draw runs every second while the timer is visible. Cache it by size so
    // we don't build and throw away a Font each tick. Callers are all UI-thread.
    private static Font? _countdownFont;
    private static float _countdownFontSize = -1f;

    private static Font CountdownFont(float emSize)
    {
        if (_countdownFont is null || _countdownFontSize != emSize)
        {
            _countdownFont?.Dispose();
            _countdownFont = new Font("Segoe UI", emSize, FontStyle.Bold);
            _countdownFontSize = emSize;
        }
        return _countdownFont;
    }

    /// <summary>Corner radius as a fraction of the badge size (matches the old 16/64).</summary>
    public static int CornerRadius(int size) => Math.Max(4, size / 4);

    public static void Draw(Graphics g, Rectangle bounds, OverlayStateSettings state,
        Bitmap icon, string? remainingText, bool flashOn)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int radius = CornerRadius(bounds.Width);

        if (state.ShowBackground)
        {
            using var back = new SolidBrush(BackColor);
            using var path = RoundedRect(bounds, radius);
            g.FillPath(back, path);
        }

        // Icon fills the badge minus a small inset; when a countdown is showing
        // it shrinks and rises to leave room for the timer text beneath it.
        int inset = Math.Max(4, bounds.Width / 8);
        var iconRect = remainingText is null
            ? new Rectangle(bounds.Left + inset, bounds.Top + inset,
                            bounds.Width - inset * 2, bounds.Height - inset * 2)
            : new Rectangle(bounds.Left + inset, bounds.Top + inset / 2,
                            bounds.Width - inset * 2, (int)((bounds.Height - inset) * 0.62));
        g.DrawImage(icon, iconRect);

        if (remainingText is not null)
        {
            // GDI+ DrawString (not GDI TextRenderer) so the glyphs carry an
            // alpha channel — required to show up on the layered overlay window.
            Font font = CountdownFont(Math.Max(7f, bounds.Width / 6.4f));
            using var brush = new SolidBrush(CountdownColor);
            var textRect = new RectangleF(bounds.Left, iconRect.Bottom, bounds.Width, bounds.Bottom - iconRect.Bottom);
            g.DrawString(remainingText, font, brush, textRect, CenterFormat);
        }

        if (flashOn)
        {
            using var pen = new Pen(FlashColor, Math.Max(2f, bounds.Width / 21f));
            using var path = RoundedRect(
                new Rectangle(bounds.Left + 2, bounds.Top + 2, bounds.Width - 5, bounds.Height - 5),
                Math.Max(3, radius - 2));
            g.DrawPath(pen, path);
        }
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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
