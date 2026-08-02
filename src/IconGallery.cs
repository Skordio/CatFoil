using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace CatFoil;

/// <summary>
/// The built-in badge icons, drawn from a Windows symbol font rather than
/// shipped as artwork. They are single-colour outlines, so the user's chosen
/// icon colour tints them for free, and nothing has to be bundled or downloaded.
/// </summary>
internal static class IconGallery
{
    public readonly record struct Entry(string Id, string Label, int Codepoint);

    /// <summary>
    /// Every codepoint here is present in <c>Segoe MDL2 Assets</c>, the Windows 10
    /// font, so it also exists in Windows 11's Fluent set. That restriction is
    /// deliberate: GDI+ has no glyph-exists test and falling back family-by-family
    /// doesn't help when the family is present but the glyph isn't — a Fluent-only
    /// codepoint would render as an empty box on Windows 10 with nothing to catch it.
    /// </summary>
    public static readonly Entry[] Icons =
    {
        new("lock",     "Padlock",   0xE72E),
        new("keyboard", "Keyboard",  0xE765),
        new("warning",  "Warning",   0xE7BA),
        new("alert",    "Alert",     0xE783),
        new("stop",     "Stop",      0xE711),
        new("eye",      "Watching",  0xE890),
        new("heart",    "Heart",     0xEB52),
        new("star",     "Star",      0xE735),
        new("key",      "Key",       0xE8D7),
        new("bulb",     "Bulb",      0xEA80),
    };

    public const string DefaultIconColor = "#FFFFFF";

    private static readonly string[] FontCandidates = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

    private static FontFamily? _family;
    private static bool _probed;

    // The render is the expensive part, so it is cached and copies are handed
    // out. Keyed on all three inputs — a single-slot cache would thrash, since
    // the picker draws every icon and the preview draws another at another size.
    private static readonly Dictionary<(string Id, int Argb, int Size), Bitmap> Cache = new();

    public static bool IsAvailable => Family() is not null;

    public static Entry? Find(string? id) =>
        id is null ? null : Icons.Cast<Entry?>().FirstOrDefault(e => e!.Value.Id == id);

    /// <summary>The id to use when none is recorded yet.</summary>
    public static string DefaultId => Icons[0].Id;

    private static FontFamily? Family()
    {
        if (_probed) return _family;
        _probed = true;

        foreach (string name in FontCandidates)
        {
            try
            {
                _family = new FontFamily(name);
                return _family;
            }
            catch (ArgumentException)
            {
                // Not installed. Note this is why the probe uses FontFamily and
                // not `new Font(name, …)`: Font silently substitutes a default
                // and would render tofu rather than telling us anything.
            }
        }
        return null;
    }

    /// <summary>
    /// Renders a gallery icon, or null if the id is unknown or no symbol font is
    /// installed — callers fall back to the bundled cat. The returned bitmap
    /// belongs to the caller.
    /// </summary>
    public static Bitmap? Render(string? id, Color tint, int size = 256)
    {
        Entry? entry = Find(id);
        FontFamily? family = Family();
        if (entry is null || family is null || size <= 0) return null;

        var key = (entry.Value.Id, tint.ToArgb(), size);
        if (Cache.TryGetValue(key, out Bitmap? hit)) return new Bitmap(hit);

        Bitmap? rendered = RenderUncached(entry.Value, family, tint, size);
        if (rendered is null) return null;

        // Bounded: a handful of icons across a handful of colours and sizes is
        // fine to keep, but nothing should grow without limit for the life of
        // the process.
        if (Cache.Count >= 48)
        {
            foreach (Bitmap stale in Cache.Values) stale.Dispose();
            Cache.Clear();
        }
        Cache[key] = rendered;
        return new Bitmap(rendered);
    }

    private static Bitmap? RenderUncached(Entry entry, FontFamily family, Color tint, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = new GraphicsPath();
            // AddString + FillPath rather than DrawString: it gives the glyph's
            // real ink rectangle to centre on, and the fill colour *is* the tint,
            // so there is no recolouring step to get wrong. It also obeys
            // SmoothingMode, sidestepping text-rendering hints entirely.
            path.AddString(char.ConvertFromUtf32(entry.Codepoint), family, (int)FontStyle.Regular,
                           size * 0.62f, new PointF(0, 0), StringFormat.GenericTypographic);

            RectangleF ink = path.GetBounds();
            if (ink.Width < 0.5f || ink.Height < 0.5f)
            {
                bmp.Dispose();
                return null;   // nothing drawn — treat as missing
            }

            // Fit and centre on the INK, not the line box. Symbol fonts carry
            // asymmetric ascent/descent, so line-box centring sits every glyph
            // high — and by a different amount each, which a gallery shows up
            // immediately as icons that don't line up with one another.
            float target = size * 0.76f;
            float scale = Math.Min(target / ink.Width, target / ink.Height);
            using var m = new Matrix();
            m.Translate((size - ink.Width * scale) / 2f - ink.X * scale,
                        (size - ink.Height * scale) / 2f - ink.Y * scale);
            m.Scale(scale, scale);
            path.Transform(m);

            using var brush = new SolidBrush(tint);
            g.FillPath(brush, path);
            return bmp;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            bmp.Dispose();
            return null;
        }
    }
}
