using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

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

    /// <summary>
    /// Converts a percentage to the alpha byte to multiply by. 92 lands exactly
    /// on 235, which is what the overlay window applied before opacity moved in
    /// here — so the default is bit-for-bit the old appearance.
    /// </summary>
    public static byte OpacityAlpha(int percent) =>
        (byte)Math.Round(255.0 * Math.Clamp(percent, 0, 100) / 100.0);

    /// <param name="flashOn">The blink phase of the blocked-key ring.</param>
    /// <param name="blocked">True for the whole blocked-key window, not just the
    /// blink phase — this is what the separate blocked opacity follows, so the
    /// badge holds its reaction steady while the ring pulses.</param>
    public static void Draw(Graphics g, Rectangle bounds, OverlayStateSettings state,
        Bitmap icon, string? remainingText, bool flashOn, bool blocked = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Draw mutates Graphics state (smoothing, interpolation) and the caller
        // may well care — the preview paints a checkerboard around this.
        GraphicsState entry = g.Save();
        try
        {
            int radius = CornerRadius(bounds.Width);

            // Render the badge opaque into its own layer, then fade the whole
            // layer once. Fading each element as it is painted would leave the
            // places they overlap more opaque than the edges — two layers at 20%
            // read as 36%, three as 49% — so a translucent badge would come out
            // as a solid icon inside a ghostly frame.
            using (var layer = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // Not the default (SystemDefault, i.e. ClearType): subpixel
                    // antialiasing writes colour fringes whose RGB doesn't match
                    // the alpha channel, which on a transparent surface shows up
                    // as coloured haloes around the countdown digits.
                    lg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    lg.Clear(Color.Transparent);
                    DrawBadge(lg, new Rectangle(0, 0, bounds.Width, bounds.Height),
                              state, icon, remainingText, radius);
                }

                DrawFaded(g, layer, bounds, OpacityAlpha(state.EffectiveOpacity(blocked)));
            }

            // Outside the fade, and with its own opacity: the ring is the
            // blocked-key signal, so dimming the badge to react to a keypress
            // must not dim the very thing announcing it.
            if (flashOn) DrawFlashRing(g, bounds, state, radius);
        }
        finally
        {
            g.Restore(entry);
        }
    }

    // Blits the finished badge through a single alpha multiply. The ColorMatrix
    // scales the existing per-pixel alpha rather than replacing it, so the
    // icon's own transparency survives.
    private static void DrawFaded(Graphics g, Bitmap layer, Rectangle bounds, byte alpha)
    {
        var matrix = new ColorMatrix { Matrix33 = alpha / 255f };
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        // Without this GDI+ samples beyond the source rectangle and leaves a
        // faint halo along all four edges — which reads as "opacity broke my icon".
        attrs.SetWrapMode(WrapMode.TileFlipXY);

        // A 1:1 blit, so nothing should be resampled on the way through.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(layer, bounds, 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, attrs);
    }

    private static void DrawFlashRing(Graphics g, Rectangle bounds, OverlayStateSettings state, int radius)
    {
        byte alpha = OpacityAlpha(state.ClampedRingOpacity());
        if (alpha == 0) return;

        float width = Math.Max(2f, bounds.Width / 21f);
        // The pen straddles the path, so half the stroke falls outside it. A
        // path flush to the bounds therefore loses its outer half off the edge
        // of the bitmap: with the old fixed 2 px inset the ring was clipped on
        // the top and left above ~84 px, and on all four sides above ~126 px.
        // At the default 64 px badge this works out to the same 2 px as before.
        int inset = Math.Max(2, (int)Math.Ceiling(width / 2f));
        var ring = new Rectangle(bounds.Left + inset, bounds.Top + inset,
                                 bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
        if (ring.Width <= 0 || ring.Height <= 0) return;

        using var pen = new Pen(Color.FromArgb(alpha, FlashColor), width);
        // Follows the badge's own shape, so a round badge doesn't get a
        // rectangular ring. With no background there is no shape to follow, so
        // it keeps the rounded box it has always drawn.
        OverlayShape shape = state.Shape == OverlayShape.None ? OverlayShape.RoundedSquare : state.Shape;
        using var path = ShapePath(ring, shape, Math.Max(3, radius - inset));
        g.DrawPath(pen, path);
    }

    /// <summary>The outline of a badge background, or null for
    /// <see cref="OverlayShape.None"/>.</summary>
    public static GraphicsPath ShapePath(Rectangle bounds, OverlayShape shape, int radius)
    {
        var path = new GraphicsPath();
        switch (shape)
        {
            case OverlayShape.Square:
                path.AddRectangle(bounds);
                break;
            case OverlayShape.Circle:
                // Inscribed in the square bounds, so a circle badge occupies the
                // same footprint as any other and dragging feels the same.
                path.AddEllipse(bounds);
                break;
            default:
                path.Dispose();
                return RoundedRect(bounds, radius);
        }
        path.CloseFigure();
        return path;
    }

    // The badge itself, always opaque: opacity is applied to the finished layer.
    //
    // The blocked-key ring is painted in here, inside the fade, which is what
    // the single global alpha used to do. It moves outside the fade when it
    // gains its own opacity — compositing it over an already-faded badge is a
    // different result, not just a brighter one, so that change belongs with
    // the setting that motivates it rather than in a step meant to be invisible.
    private static void DrawBadge(Graphics g, Rectangle bounds, OverlayStateSettings state,
        Bitmap icon, string? remainingText, int radius)
    {
        if (state.Shape != OverlayShape.None)
        {
            using var back = new SolidBrush(HexColor.Parse(state.BackgroundColor, BackColor));
            using var path = ShapePath(bounds, state.Shape, radius);
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
