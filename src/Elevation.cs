using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Helpers for running CatFoil with administrator rights. A process can't
/// elevate itself in place, so "run as admin" means relaunching a fresh
/// elevated process (which raises the UAC prompt) and letting it take over the
/// single-instance slot once this one exits. Elevation matters because a
/// medium-integrity keyboard hook can't block keystrokes to elevated windows.
/// </summary>
internal static class Elevation
{
    /// <summary>Command-line flag telling a relaunched instance to wait for its
    /// predecessor (by pid) to exit before claiming the single-instance slot.</summary>
    public const string AwaitExitFlag = "--await-exit";

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches CatFoil elevated via a UAC prompt. Returns true if a new
    /// elevated process was started — the caller should then quit so it can take
    /// over — or false if the user declined the prompt (or it otherwise failed),
    /// in which case nothing has changed and this instance keeps running.
    /// </summary>
    public static bool TryRelaunchElevated()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            UseShellExecute = true,   // required for the "runas" verb / UAC
            Verb = "runas",
            Arguments = $"{AwaitExitFlag} {Environment.ProcessId}",
        };
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            // 1223 = user cancelled the UAC prompt; anything else = launch failed.
            return false;
        }
    }
}
