using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CatFoil;

/// <summary>
/// How CatFoil starts at logon. Two mutually-exclusive mechanisms:
///   - the HKCU\...\Run value — starts CatFoil non-elevated ("Start with Windows");
///   - a Task Scheduler task with highest privileges — starts it elevated with no
///     UAC prompt ("Start elevated at logon").
/// The elevated task is the standard way to auto-elevate your own app silently.
/// Creating or deleting it requires the current process to already be elevated;
/// the Run-key path works unelevated. When the task is enabled it owns startup,
/// so the Run key is suppressed to avoid a racing double-launch at logon.
/// </summary>
internal static class Startup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CatFoil";
    private const string TaskName = "CatFoil Startup (elevated)";

    // ---------------------------------------------------------------
    // Non-elevated Run key
    // ---------------------------------------------------------------
    public static void SetRunKey(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access denied — autostart just won't change; not fatal.
        }
    }

    /// <summary>
    /// Reconciles the Run key with the settings. Call on startup and after a save.
    /// The elevated task, when on, is the startup path, so the Run key is cleared.
    /// </summary>
    public static void Apply(Settings settings) =>
        SetRunKey(settings.StartWithWindows && !settings.StartElevatedOnBoot);

    // ---------------------------------------------------------------
    // Elevated scheduled task (requires elevation to create/delete)
    // ---------------------------------------------------------------
    public static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Creates/updates the logon task. Returns true on success.</summary>
    public static bool EnableTask()
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), "catfoil-startup-task.xml");
        try
        {
            // schtasks reads the XML as Unicode; the declaration says so too.
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F") == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Removes the logon task. Returns true if it's gone afterward.</summary>
    public static bool DisableTask()
    {
        int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0 || !TaskExists();   // non-zero can just mean "not found"
    }

    // ---------------------------------------------------------------
    // Uninstall cleanup (run by the installer's [UninstallRun] entry)
    // ---------------------------------------------------------------

    /// <summary>Switch the uninstaller passes to remove this EXE's logon
    /// registrations before the EXE itself is deleted. Also usable by hand
    /// from a portable copy to deregister it.</summary>
    public const string UninstallCleanupFlag = "--uninstall-cleanup";

    /// <summary>
    /// Deletes the Run value and the elevated task, but only when they point
    /// at THIS executable — a registration made by a different copy (e.g. the
    /// portable EXE alongside an install) is not ours to remove. Best-effort:
    /// the uninstall must proceed no matter what fails here.
    /// </summary>
    public static void UninstallCleanup()
    {
        string exe = Application.ExecutablePath;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is string value &&
                string.Equals(value.Trim('"'), exe, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access denied — leave it; not worth failing the uninstall.
        }

        // The exported task XML escapes '&' etc., so accept the raw or the
        // escaped spelling of the path. Any mismatch (or query failure) errs
        // toward leaving the task in place.
        if (RunSchtasksCapture($"/Query /TN \"{TaskName}\" /XML", out string xml) == 0 &&
            (xml.Contains(exe, StringComparison.OrdinalIgnoreCase) ||
             xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase)))
            DisableTask();
    }

    private static int RunSchtasksCapture(string args, out string stdout)
    {
        stdout = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return -1;
            // Unlike RunSchtasks we do redirect here — but drain the pipe to
            // end-of-stream BEFORE waiting, which is what makes it deadlock-free.
            stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) return -1;
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            // Don't redirect the streams: we discard the output anyway, and an
            // undrained pipe deadlocks the child once it fills (~4 KB), which
            // would hang us for the whole timeout. CreateNoWindow alone keeps the
            // console hidden.
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return -1;
            if (!p.WaitForExit(10000)) return -1;
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    // A logon-triggered task that runs elevated in the user's interactive session.
    private static string BuildTaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);   // DOMAIN\User
        string exe = SecurityElement.Escape(Application.ExecutablePath);
        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Starts CatFoil elevated at logon so it can block elevated windows.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
    </Exec>
  </Actions>
</Task>";
    }
}
