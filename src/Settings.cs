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

        // Per-state migration has to run here, after the fold above, not in the
        // loop before it: on a 0.3 file the states exist only as OverlayNormal /
        // OverlayFullscreen until that fold, so migrating earlier would skip
        // precisely the users the legacy properties exist to protect.
        foreach (OverlayItem item in Overlays)
        {
            MigrateState(item.Normal);
            MigrateState(item.Fullscreen);
        }

        return Overlays;
    }

    private static void MigrateState(OverlayStateSettings state)
    {
        // Keyed strictly on the legacy property still being present — never on
        // the new one "looking like a default". EnsureOverlays is idempotent and
        // called from several places, so deriving Shape from its current value
        // would turn a deliberately chosen None back into a box on the next call.
        if (state.ShowBackground.HasValue)
        {
            state.Shape = state.ShowBackground.Value ? OverlayShape.RoundedSquare : OverlayShape.None;
            state.ShowBackground = null;
        }
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

    public const int MinOpacity = 10;

    public bool Visible { get; set; } = true;
    public bool UseCustomIcon { get; set; }
    public string? CustomIconFile { get; set; }
    public int Size { get; set; } = 64;

    /// <summary>Shape of the box behind the icon. <see cref="OverlayShape.None"/>
    /// leaves the icon floating on its own transparency.</summary>
    [JsonConverter(typeof(LenientEnumConverter<OverlayShape>))]
    public OverlayShape Shape { get; set; } = OverlayShape.RoundedSquare;

    /// <summary>Background colour as <c>#RRGGBB</c>. Transparency is
    /// <see cref="Opacity"/>'s job, so the alpha here is ignored.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>How solid the whole badge is, as a percentage. 92 reproduces
    /// the fixed translucency the overlay window used to apply.</summary>
    public int Opacity { get; set; } = 92;

    /// <summary>Use a different opacity while a keypress is being blocked, so
    /// the badge visibly reacts rather than only flashing a ring.</summary>
    public bool BlockedOpacityEnabled { get; set; }
    public int BlockedOpacity { get; set; } = 100;

    /// <summary>Opacity of the ring flashed on a blocked keypress. 0 hides it.</summary>
    public int RingOpacity { get; set; } = 100;

    // --- Legacy (0.3 and earlier) -------------------------------------------
    // Superseded by Shape. Read once, folded in by Settings.EnsureOverlays, then
    // nulled so it is never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowBackground { get; set; }

    // settings.json is text a user can edit, so nothing here can be trusted to
    // be in range.
    public int ClampedSize() => Math.Clamp(Size, MinSize, MaxSize);
    public int ClampedOpacity() => Math.Clamp(Opacity, MinOpacity, 100);
    public int ClampedRingOpacity() => Math.Clamp(RingOpacity, 0, 100);

    /// <summary>The opacity to draw at right now: the blocked one only while a
    /// keypress is actually being blocked and the option is on.</summary>
    public int EffectiveOpacity(bool blocked) => blocked && BlockedOpacityEnabled
        ? Math.Clamp(BlockedOpacity, MinOpacity, 100)
        : ClampedOpacity();

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

/// <summary>The box painted behind the badge icon.</summary>
public enum OverlayShape
{
    // RoundedSquare is deliberately the zero value: it is what an overlay with
    // no Shape recorded should be, so a value the converter can't read falls
    // back to the sensible default rather than to something arbitrary.
    RoundedSquare,
    Square,
    Circle,
    None,
}

/// <summary>
/// Reads an enum by name, falling back to the default rather than throwing.
///
/// The stock converter throws on a value it doesn't recognise, and
/// <see cref="Settings.Load"/> catches everything and starts from defaults — so
/// one mistyped word in a hand-edited file, or a settings.json written by a
/// newer CatFoil and opened by an older one, would silently discard the hotkey,
/// the autostart choice and the lifetime statistics. The statistics cannot be
/// recovered. An unreadable appearance value costs nothing by comparison.
/// </summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return Enum.TryParse(reader.GetString(), ignoreCase: true, out T byName) ? byName : default;

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
            return Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : default;

        // Something structural where a name was expected; step over it so the
        // reader stays in sync with the rest of the document.
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Colours travel through settings.json as <c>#RRGGBB</c> text —
/// <see cref="Color"/> itself has no settable properties and cannot round-trip
/// through System.Text.Json.</summary>
internal static class HexColor
{
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            // FromHtml also accepts colour names, and returns transparent black
            // for one it doesn't know instead of throwing — which would be an
            // invisible badge with no error. Treat that as unreadable.
            Color parsed = ColorTranslator.FromHtml(hex.Trim());
            return parsed.A == 0 ? fallback : Color.FromArgb(255, parsed);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    // Not ColorTranslator.ToHtml: that emits names ("Red") for known colours,
    // which a strict #-only reader would then reject.
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
