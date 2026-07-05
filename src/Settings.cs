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
    public bool WelcomeShown { get; set; }
    public Point? OverlayPosition { get; set; }

    // Per-state overlay appearance. Defaults reproduce the original behavior:
    // the cat badge shows normally and hides over fullscreen apps.
    public OverlayStateSettings OverlayNormal { get; set; } = new();
    public OverlayStateSettings OverlayFullscreen { get; set; } = new() { Visible = false };

    public string? LicenseKey { get; set; }
    public string? LicenseInstanceId { get; set; }
    public string? LicenseSignature { get; set; }

    // Lifetime usage statistics.
    public int StatLockSessions { get; set; }
    public long StatLockedSeconds { get; set; }
    public long StatBlockedKeys { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Corrupted settings file — fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>
/// How the locked overlay looks in one system state (normal vs. a fullscreen
/// app being foreground). A custom icon is a file kept inside
/// <see cref="Settings.Directory"/> so it survives the original being moved.
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

    public OverlayStateSettings Clone() => new()
    {
        Visible = Visible,
        UseCustomIcon = UseCustomIcon,
        CustomIconFile = CustomIconFile,
        Size = Size,
        ShowBackground = ShowBackground,
    };
}
