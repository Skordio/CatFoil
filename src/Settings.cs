using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// User settings, persisted as JSON in %APPDATA%\CatFoil\settings.json so the
/// portable EXE can be moved around (and an MSIX build works the same way).
/// </summary>
public sealed class Settings
{
    public static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatFoil");

    private static readonly string FilePath = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Keys Hotkey { get; set; } = Keys.Alt | Keys.G;
    public bool HotkeyEnabled { get; set; } = true;

    // Chord mode: modifiers + several keys held together (e.g. Alt+C+F),
    // detected by our own hook because RegisterHotKey can't express it.
    public bool UseChordHotkey { get; set; }
    public Keys ChordModifiers { get; set; } = Keys.Alt;
    public Keys[] ChordKeys { get; set; } = new[] { Keys.C, Keys.F };
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; }
    // Auto-start elevated at logon via a scheduled task (no UAC prompt). When on,
    // it replaces the non-elevated Run-key autostart.
    public bool StartElevatedOnBoot { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowOverlay { get; set; } = true;

    // Optional audio cues (uses the user's Windows system sounds).
    public bool SoundOnLockUnlock { get; set; }
    public bool SoundOnBlockedKey { get; set; }

    // Auto-lock the keyboard after a stretch of no keyboard/mouse input, so
    // walking away leaves it protected without remembering to lock.
    public bool AutoLockEnabled { get; set; }
    public int AutoLockMinutes { get; set; } = 5;   // clamped 1..120
    public bool WelcomeShown { get; set; }

    /// <summary>
    /// Every badge to put on screen while locked. Guaranteed non-empty once
    /// <see cref="EnsureOverlays"/> has run, which <see cref="Load"/> always does.
    /// </summary>
    public List<OverlayItem> Overlays { get; set; } = new();

    // --- Legacy single-overlay settings (0.3 and earlier) -------------------
    // Still read, so upgrading never loses a custom icon or the badge's
    // position, but folded into Overlays by EnsureOverlays and then nulled so
    // they are never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point? OverlayPosition { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayStateSettings? OverlayNormal { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayStateSettings? OverlayFullscreen { get; set; }

    // Lifetime usage statistics.
    public int StatLockSessions { get; set; }
    public long StatLockedSeconds { get; set; }
    public long StatBlockedKeys { get; set; }

    public static Settings Load()
    {
        Settings loaded;
        try
        {
            loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings()
                : new Settings();
        }
        catch
        {
            // Corrupted settings file — fall back to defaults.
            loaded = new Settings();
        }
        loaded.EnsureOverlays();
        return loaded;
    }

    /// <summary>
    /// Returns <see cref="Overlays"/>, guaranteeing at least one item. On the
    /// first run after upgrading from 0.3 the list is empty and the legacy
    /// per-state properties hold the user's real overlay, so it is folded into
    /// a single item rather than replaced by a default one; on a fresh install
    /// all of them are absent and the same code produces the default badge.
    /// Idempotent, so any caller that needs a non-empty list can just call it.
    /// </summary>
    public List<OverlayItem> EnsureOverlays()
    {
        // settings.json is plain text the user can edit or truncate, so none of
        // these can be trusted to match their non-nullable annotations.
        Overlays ??= new List<OverlayItem>();
        Overlays.RemoveAll(item => item is null);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayItem item in Overlays)
        {
            item.Normal ??= new OverlayStateSettings();
            item.Fullscreen ??= new OverlayStateSettings { Visible = false };
            // Ids name image files and match badges to items, so a missing or
            // repeated one would quietly break the second overlay.
            if (string.IsNullOrWhiteSpace(item.Id) || !seenIds.Add(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(item.Id);
            }
        }

        if (Overlays.Count == 0)
        {
            Overlays.Add(new OverlayItem
            {
                Position = OverlayPosition,
                Normal = OverlayNormal ?? new OverlayStateSettings(),
                Fullscreen = OverlayFullscreen ?? new OverlayStateSettings { Visible = false },
            });
        }

        // Absorbed above — drop them so Save() stops writing them.
        OverlayPosition = null;
        OverlayNormal = null;
        OverlayFullscreen = null;
        return Overlays;
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Write-to-temp + rename: the rename is atomic on NTFS, so an
        // interrupted save leaves either the old or the new complete file —
        // never a truncated one.
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>
/// One on-screen badge: its identity, whether it is shown at all, where it
/// sits, and how it looks in each system state. A list of these replaces the
/// single overlay 0.3 and earlier had, so several badges can be on screen at
/// once. <see cref="Id"/> is stable for the item's lifetime and names its
/// custom image files on disk, so it must never be reused after a delete.
/// </summary>
public sealed class OverlayItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Overlay";
    public bool Enabled { get; set; } = true;

    /// <summary>Where the user dragged it, or null to place it automatically.</summary>
    public Point? Position { get; set; }

    // Defaults reproduce the original behavior: the badge shows normally and
    // hides over fullscreen apps.
    public OverlayStateSettings Normal { get; set; } = new();
    public OverlayStateSettings Fullscreen { get; set; } = new() { Visible = false };
}

/// <summary>
/// How the locked overlay looks in one system state (normal vs. a fullscreen
/// app being foreground). A custom icon is a file kept inside
/// <see cref="Settings.Directory"/> so it survives the original being moved;
/// <see cref="CustomIconFile"/> is the path relative to that folder.
/// </summary>
public sealed class OverlayStateSettings
{
    public const int MinSize = 32;
    public const int MaxSize = 256;

    public bool Visible { get; set; } = true;
    public bool UseCustomIcon { get; set; }
    public string? CustomIconFile { get; set; }
    public int Size { get; set; } = 64;
    public bool ShowBackground { get; set; } = true;

    public int ClampedSize() => Math.Clamp(Size, MinSize, MaxSize);

    /// <summary>
    /// A field-for-field copy. Deliberately <see cref="object.MemberwiseClone"/>
    /// rather than an initializer listing every property: a hand-written list
    /// silently drops any field added later, and the failure is invisible —
    /// the settings page shows the new value while the badge keeps rendering
    /// the old one, with no error. Every field here is a value type or an
    /// immutable string, and the class is sealed, so a shallow copy is a
    /// complete one.
    /// </summary>
    public OverlayStateSettings Clone() => (OverlayStateSettings)MemberwiseClone();
}
