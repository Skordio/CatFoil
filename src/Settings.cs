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

    public Keys Hotkey { get; set; } = Keys.Control | Keys.Alt | Keys.L;
    public bool HotkeyEnabled { get; set; } = true;

    // Chord mode: modifiers + several keys held together (e.g. Alt+C+F),
    // detected by our own hook because RegisterHotKey can't express it.
    public bool UseChordHotkey { get; set; }
    public Keys ChordModifiers { get; set; } = Keys.Alt;
    public Keys[] ChordKeys { get; set; } = new[] { Keys.C, Keys.F };
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowOverlay { get; set; } = true;
    public bool WelcomeShown { get; set; }
    public Point? OverlayPosition { get; set; }
    public string? LicenseKey { get; set; }
    public string? LicenseInstanceId { get; set; }
    public string? LicenseSignature { get; set; }

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
