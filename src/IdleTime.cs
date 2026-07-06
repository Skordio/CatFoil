using System;
using System.Runtime.InteropServices;

namespace CatFoil;

/// <summary>System-wide time since the last keyboard or mouse input.</summary>
internal static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Milliseconds since the last user input anywhere on the system.</summary>
    public static uint Milliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        // Both are GetTickCount-based 32-bit counters; unchecked subtraction wraps safely.
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }
}
