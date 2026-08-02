using System;
using System.Drawing;
using System.IO;

namespace CatFoil;

/// <summary>
/// Resolves the image a badge should show. Shared by the live overlay, the
/// settings preview and the overlay list, which otherwise each grew their own
/// copy of "custom file, or fall back to the default".
/// </summary>
internal static class OverlayIcon
{
    /// <summary>
    /// Returns the bitmap for <paramref name="state"/>, or
    /// <paramref name="fallback"/> when there is no usable custom image.
    ///
    /// The result is either <paramref name="fallback"/> itself — which the
    /// caller does not own — or a fresh bitmap the caller must dispose. Compare
    /// against <paramref name="fallback"/> before disposing.
    /// </summary>
    public static Bitmap Load(OverlayAppearance state, Bitmap fallback)
    {
        if (state.IconSource == OverlayIconSource.Gallery)
        {
            // Null when the symbol font is missing or the id is unknown, in
            // which case the bundled cat stands in.
            return IconGallery.Render(state.GalleryIconId ?? IconGallery.DefaultId,
                                      HexColor.Parse(state.IconColor, Color.White))
                   ?? fallback;
        }

        if (state.IconSource != OverlayIconSource.Custom || string.IsNullOrWhiteSpace(state.CustomIconFile))
            return fallback;

        try
        {
            string path = IconStore.FullPath(state.CustomIconFile);
            if (!File.Exists(path)) return fallback;
            return Decode(path) ?? fallback;
        }
        catch
        {
            return fallback;   // unreadable/corrupt file
        }
    }

    /// <summary>
    /// Reads an image file into a bitmap the badge can draw, or null when
    /// nothing in it is readable. The import check calls this too, so "the
    /// editor accepted your picture" and "the badge can draw your picture"
    /// cannot drift apart. The caller owns the result.
    /// </summary>
    public static Bitmap? Decode(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
            return DecodeIcon(path);

        // Copy into memory so the file on disk isn't locked open.
        using Bitmap? fromFile = TryBitmap(path);
        return fromFile is null ? null : new Bitmap(fromFile);
    }

    // An .ico holds several sizes and the two decoders disagree about which one
    // to hand back. GDI+ ignores the size you want; Icon(path, w, h) matches on
    // size but cannot read the PNG-compressed 256 px frame modern icons carry,
    // so it stops at the largest bitmap frame — 48 px for assets\cat.ico, where
    // GDI+ returns the full 256. Either can be the smaller one, so take
    // whichever has more pixels: the badge goes up to 256 px, and upscaling a
    // 16 or 48 px frame into it is the visible failure.
    private static Bitmap? DecodeIcon(string path)
    {
        Bitmap? viaGdi = TryBitmap(path);
        Bitmap? viaIcon = TryIcon(path);

        if (viaGdi is null) return viaIcon;
        if (viaIcon is null) return viaGdi;

        if (viaIcon.Width > viaGdi.Width)
        {
            viaGdi.Dispose();
            return viaIcon;
        }
        viaIcon.Dispose();
        return viaGdi;
    }

    // GDI+ reports every decode failure as one of these two whatever the real
    // cause — OutOfMemory included, which here means "bad image data".
    private static Bitmap? TryBitmap(string path)
    {
        try { return new Bitmap(path); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { return null; }
    }

    private static Bitmap? TryIcon(string path)
    {
        try
        {
            using var ico = new Icon(path, OverlayAppearance.MaxSize, OverlayAppearance.MaxSize);
            return ico.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { return null; }
    }
}
