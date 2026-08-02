using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CatFoil;

/// <summary>
/// Where custom cue audio lives. Mirrors <see cref="IconStore"/> exactly: a
/// chosen file is copied into %APPDATA%\CatFoil\sounds so the cue keeps working
/// after the original is moved or deleted, and settings store only the path
/// relative to <see cref="Settings.Directory"/>.
/// </summary>
internal static class SoundStore
{
    private const string FolderName = "sounds";

    /// <summary>
    /// What the file picker offers. MCI decides at open time whether it can
    /// actually decode a file, so this is a filter rather than a guarantee —
    /// an unplayable file falls back to the Windows scheme sound.
    /// </summary>
    public static readonly string[] AllowedExtensions = { ".mp3", ".wav", ".wma", ".m4a", ".aac" };

    private static string Folder => Path.Combine(Settings.Directory, FolderName);

    public static string FullPath(string relative) => Path.Combine(Settings.Directory, relative);

    /// <summary>
    /// Copies a chosen file into the sounds folder and returns the relative path
    /// to store. Throws if the copy fails so the caller can say so.
    /// </summary>
    public static string Import(string sourcePath, string eventName)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) ext = ".mp3";

        Directory.CreateDirectory(Folder);
        // Unique per import, for the same reason icons are: reusing one fixed
        // name per event would leave the stored path unchanged when the file
        // behind it changed, and the player caches by path.
        string relative = Path.Combine(FolderName, $"{eventName}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
        File.Copy(sourcePath, FullPath(relative), overwrite: true);
        return relative;
    }

    /// <summary>Deletes stored audio no cue refers to any more.</summary>
    public static void CollectGarbage(Settings settings)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SoundSetting cue in new[] { settings.LockSound, settings.UnlockSound, settings.BlockedSound })
        {
            if (cue is null || string.IsNullOrWhiteSpace(cue.File)) continue;
            try { referenced.Add(Path.GetFullPath(FullPath(cue.File))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        string[] stored;
        try { stored = Directory.Exists(Folder) ? Directory.GetFiles(Folder) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (string path in stored)
        {
            if (referenced.Contains(path)) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in the player, most likely. It'll be swept next time.
            }
        }
    }
}
