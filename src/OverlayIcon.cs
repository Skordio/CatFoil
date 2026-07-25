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
    public static Bitmap Load(OverlayStateSettings state, Bitmap fallback)
    {
        if (!state.UseCustomIcon || string.IsNullOrWhiteSpace(state.CustomIconFile))
            return fallback;

        try
        {
            string path = IconStore.FullPath(state.CustomIconFile);
            if (!File.Exists(path)) return fallback;
            // Copy into memory so the file on disk isn't locked open.
            using var fromFile = new Bitmap(path);
            return new Bitmap(fromFile);
        }
        catch
        {
            return fallback;   // unreadable/corrupt file
        }
    }
}
