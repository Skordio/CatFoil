using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CatFoil;

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
    /// to store. Throws if the copy fails: the caller can tell the user, which
    /// beats silently leaving the previous icon in place after they picked one.
    /// </summary>
    public static string Import(string sourcePath, string overlayId, string stateName)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) ext = ".png";

        Directory.CreateDirectory(Folder);
        string relative = Path.Combine(FolderName, $"{overlayId}-{stateName}{ext}");
        File.Copy(sourcePath, FullPath(relative), overwrite: true);
        return relative;
    }

    /// <summary>
    /// Deletes stored images nothing refers to any more. They are left behind
    /// when an overlay is removed, switched back to the default icon, or given a
    /// replacement image with a different file extension (which lands under a
    /// different name than the one it replaces).
    /// </summary>
    public static void CollectGarbage(Settings settings)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayItem item in settings.Overlays)
        {
            foreach (OverlayStateSettings state in new[] { item.Normal, item.Fullscreen })
            {
                if (string.IsNullOrWhiteSpace(state.CustomIconFile)) continue;
                // A hand-edited settings.json can hold anything; an unusable
                // path just means that file protects nothing.
                try { referenced.Add(Path.GetFullPath(FullPath(state.CustomIconFile))); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
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
