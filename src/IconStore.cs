using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace CatFoil;

/// <summary>
/// Thrown when a chosen file is not a picture CatFoil can draw. The message is
/// written for the user and is shown verbatim, so it explains what to do rather
/// than what failed.
/// </summary>
internal sealed class UnusableImageException : Exception
{
    public UnusableImageException(string message) : base(message) { }
}

/// <summary>
/// Where custom overlay images live. A chosen image is copied into
/// %APPDATA%\CatFoil\icons so the badge keeps working after the original is
/// moved or deleted, and settings store only the path relative to
/// <see cref="Settings.Directory"/> — which keeps the portable EXE portable.
/// </summary>
internal static class IconStore
{
    private const string FolderName = "icons";

    private static readonly string[] AllowedExtensions = { ".png", ".ico", ".jpg", ".jpeg", ".bmp" };

    // 0.3 and earlier copied images to two fixed names in the CatFoil folder
    // itself. Those are stored relative the same way, so they still resolve and
    // nothing has to be moved on upgrade — but once nothing refers to them they
    // deserve sweeping up like any other orphan.
    private static readonly string[] LegacyBaseNames = { "overlay-normal", "overlay-fullscreen" };

    private static string Folder => Path.Combine(Settings.Directory, FolderName);

    /// <summary>Resolves a settings-relative image path to a full path.</summary>
    public static string FullPath(string relative) => Path.Combine(Settings.Directory, relative);

    /// <summary>
    /// Copies a chosen image into the icons folder and returns the relative path
    /// to store. Throws if the copy fails or if what arrived cannot be decoded:
    /// the caller can tell the user, which beats silently leaving the previous
    /// icon in place after they picked one.
    ///
    /// Returning normally means the stored file really does draw.
    /// </summary>
    public static string Import(string sourcePath, string overlayId)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) ext = ".png";

        Directory.CreateDirectory(Folder);
        string relative = Path.Combine(FolderName, $"{overlayId}-{Token()}{ext}");
        string full = FullPath(relative);
        File.Copy(sourcePath, full, overwrite: true);

        try
        {
            Verify(full, sourcePath);
        }
        catch
        {
            // Never keep a file the badge can't draw. Settings would go on
            // naming it, which pins it against CollectGarbage for the life of
            // the install, and the overlay would quietly render the default cat.
            try { File.Delete(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return relative;
    }

    // The one failure the caller cannot otherwise see. OverlayIcon.Load falls
    // back to the default icon when a decode fails, so without this the editor
    // shows the chosen filename and the radio sits on "Custom image" while the
    // badge keeps drawing the cat — indistinguishable from the picker not working.
    private static void Verify(string storedPath, string sourcePath)
    {
        string name = Path.GetFileName(sourcePath);

        if (new FileInfo(storedPath).Length == 0)
        {
            // Nearly always a cloud placeholder whose contents were never
            // downloaded: File.Copy reports success and copies nothing at all.
            throw new UnusableImageException(
                $"\"{name}\" is empty — 0 bytes — so there is no picture in it to show.\n\n" +
                "If it lives in OneDrive or another synced folder, the file may not be " +
                "downloaded to this PC yet. Right-click it in File Explorer, choose " +
                "\"Always keep on this device\", wait for it to finish, then pick it again.");
        }

        // Decoded exactly the way the badge will decode it, rather than by a
        // second reading of the same file — a check that accepted more than the
        // renderer draws would put us back to a silent fallback to the cat.
        // Content is what decides: GDI+ sniffs it, so the extension proves nothing.
        using Bitmap? probe = OverlayIcon.Decode(storedPath);
        if (probe is null)
        {
            throw new UnusableImageException(
                $"\"{name}\" isn't a picture CatFoil can read.\n\n" +
                "PNG, JPEG, BMP and ICO files work. SVG and WebP don't, and neither " +
                "does a file that has been damaged.");
        }
    }

    // Every import lands under a name of its own. Reusing one fixed name per
    // overlay would mean picking a different picture leaves the stored path
    // unchanged, so nothing downstream can tell the image needs re-reading —
    // the badge would keep showing the old one. The superseded file stops being
    // referenced and is swept when the settings window closes.
    private static string Token() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// Copies an existing stored image to a new overlay's name, so a duplicated
    /// overlay owns its picture instead of sharing the original's file. Returns
    /// null — meaning "no custom image" — when there is nothing to copy or the
    /// copy fails; a duplicate quietly falling back to the default cat is a far
    /// better outcome than failing the duplicate outright.
    /// </summary>
    public static string? Duplicate(string? sourceRelative, string overlayId)
    {
        if (string.IsNullOrWhiteSpace(sourceRelative)) return null;

        try
        {
            string source = FullPath(sourceRelative);
            if (!File.Exists(source)) return null;

            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) ext = ".png";

            Directory.CreateDirectory(Folder);
            string relative = Path.Combine(FolderName, $"{overlayId}-{Token()}{ext}");
            File.Copy(source, FullPath(relative), overwrite: true);
            return relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes stored images nothing refers to any more. They are left behind
    /// when an overlay is removed, switched back to the default icon, given a
    /// replacement image (every import lands under a fresh name), or — for a
    /// file written before 0.4.1 — when its second per-state image was dropped
    /// by the collapse to a single appearance.
    /// </summary>
    public static void CollectGarbage(Settings settings)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayItem item in settings.Overlays)
        {
            string? file = item.Appearance.CustomIconFile;
            if (string.IsNullOrWhiteSpace(file)) continue;
            // A hand-edited settings.json can hold anything; an unusable
            // path just means that file protects nothing.
            try { referenced.Add(Path.GetFullPath(FullPath(file))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        foreach (string path in Orphanable())
        {
            if (referenced.Contains(path)) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover image costs a few KB; never fail a save over one.
            }
        }
    }

    // Only these are ever deletion candidates: files we put in the icons folder,
    // plus the two names the old single-overlay build used. Anything else in
    // %APPDATA%\CatFoil is not ours to remove.
    private static IEnumerable<string> Orphanable()
    {
        string[] stored;
        try { stored = Directory.Exists(Folder) ? Directory.GetFiles(Folder) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stored = Array.Empty<string>(); }
        foreach (string path in stored) yield return path;

        foreach (string baseName in LegacyBaseNames)
            foreach (string ext in AllowedExtensions)
            {
                string path = Path.Combine(Settings.Directory, baseName + ext);
                if (File.Exists(path)) yield return path;
            }
    }
}
