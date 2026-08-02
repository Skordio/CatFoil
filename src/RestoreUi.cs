using System;

namespace CatFoil;

/// <summary>
/// Carries the open-windows state across the elevation relaunch. "Run as
/// administrator" starts a fresh elevated process, and without this it came up
/// with whatever StartMinimized said — a start-closed preference made the app
/// vanish out from under the user mid-settings-visit. The relaunching instance
/// encodes what's on screen into the command line; the elevated one re-opens
/// exactly that, overriding StartMinimized for that single launch.
/// </summary>
internal static class RestoreUi
{
    public const string MainFlag = "--restore-main";
    /// <summary>Followed by the settings page title to land on.</summary>
    public const string SettingsFlag = "--restore-settings";

    /// <summary>The command-line fragment describing the current UI, or ""
    /// when nothing is open. The page title is quoted for the argument
    /// string; Windows strips the quotes again before argv.</summary>
    public static string Encode(bool mainVisible, string? settingsPage)
    {
        string result = mainVisible ? MainFlag : "";
        if (settingsPage is not null)
            result += (result.Length > 0 ? " " : "") + $"{SettingsFlag} \"{settingsPage}\"";
        return result;
    }

    /// <summary>True when this launch carries any restore instruction — the
    /// caller should then follow the flags instead of StartMinimized.</summary>
    public static bool Requested(string[] args) =>
        MainWindow(args) || Array.IndexOf(args, SettingsFlag) >= 0;

    // Not "Main" — the compiler warns about a second entry-point candidate.
    public static bool MainWindow(string[] args) => Array.IndexOf(args, MainFlag) >= 0;

    /// <summary>The settings page title to restore, or null for none.</summary>
    public static string? SettingsPage(string[] args)
    {
        int i = Array.IndexOf(args, SettingsFlag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
