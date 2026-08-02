using System.Collections.Generic;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Renders hotkeys and chords as display strings. Lives on its own rather than
/// on the settings window because the main window, the welcome tour and the
/// tray balloons all need it without caring where hotkeys are edited.
/// </summary>
internal static class HotkeyText
{
    public static string[] ChordParts(Keys modifiers, Keys[] keys)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        foreach (Keys key in keys) parts.Add(key.ToString());
        return parts.ToArray();
    }

    public static string[] HotkeyParts(Keys combo) =>
        ChordParts(combo, new[] { combo & Keys.KeyCode });

    /// <summary>The parts of the active hotkey. Chord mode is no longer offered
    /// (its stored settings are legacy — see Settings), so this is always the
    /// classic combo.</summary>
    public static string[] ActiveParts(Settings s) => HotkeyParts(s.Hotkey);

    public static string Format(Keys combo) => string.Join(" + ", HotkeyParts(combo));
}
