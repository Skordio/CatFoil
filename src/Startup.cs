using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CatFoil;

/// <summary>
/// How CatFoil starts at logon. Two mutually-exclusive mechanisms:
///   - the HKCU\...\Run value — starts CatFoil non-elevated ("Start with Windows");
///   - a Task Scheduler task with highest privileges — starts it elevated with no
///     UAC prompt ("Start elevated at logon").
/// The elevated task is the standard way to auto-elevate your own app silently.
/// Creating it requires the current process to already be elevated; the Run-key
/// path works unelevated. When the task is enabled it owns startup, so the Run
/// key is suppressed to avoid a racing double-launch at logon.
///
/// Deleting it would need elevation too, except that the task is registered with
/// an explicit security descriptor granting the user's own SID delete rights —
/// see <see cref="BuildTaskSddl"/>. Without that, the unelevated uninstaller
/// can't remove it and leaves an orphan behind.
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
        if (!CreateTaskViaSchtasks(BuildTaskXml())) return false;
        // Best-effort second step: the feature works without it, it's only the
        // uninstaller that can't clean up. Deliberately not folded into creation
        // — RegisterTask refuses a descriptor that would cost the caller write
        // access, and refuses it *after* creating the task, so a bad one leaves
        // a task nobody can update. Stamping afterwards has no such trap.
        ApplyTaskSecurity();
        return true;
    }

    private static bool CreateTaskViaSchtasks(string xml)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), "catfoil-startup-task.xml");
        try
        {
            // schtasks reads the XML as Unicode; the declaration says so too.
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
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
    // Task security descriptor
    //
    // Why any of this exists: UAC hands an administrator two tokens, and the
    // everyday one carries BUILTIN\Administrators as *deny-only*. Task Scheduler
    // stamps a descriptor at registration reflecting the registering context, so
    // a RunLevel=HighestAvailable task — which can only be registered elevated —
    // ends up deletable only by Administrators and SYSTEM. A per-user install is
    // uninstalled unelevated, so UninstallCleanup's delete was denied and left an
    // orphan task pointing at a deleted exe (found by the 2026-07-27 E2E).
    // Registering with our own descriptor, which also grants this user's SID
    // delete rights, makes that same unelevated delete succeed.
    // ---------------------------------------------------------------

    // SECURITY_INFORMATION bits: owner | group | DACL.
    private const int TaskSecurityInfo = 0x1 | 0x2 | 0x4;

    private const uint RightDelete = 0x00010000;
    private const uint RightGenericAll = 0x10000000;

    /// <summary>
    /// The DACL the logon task carries: full control for SYSTEM (the service has
    /// to run it) and Administrators, and read + DELETE for this user, so an
    /// unelevated uninstall can remove it.
    ///
    /// The user is deliberately granted nothing writable. Write access on a
    /// HighestAvailable task is an elevation primitive: anything running as the
    /// user could rewrite the action and have arbitrary code launched elevated at
    /// logon with no prompt. Delete only lets them throw their own startup entry
    /// away.
    ///
    /// DACL only, no owner clause. The task is created elevated, so its owner is
    /// already BUILTIN\Administrators — setting it again buys nothing and is the
    /// one part the service can reject (ERROR_INVALID_OWNER) when we're not.
    ///
    /// Null if the user's SID can't be read, so the caller leaves the descriptor
    /// alone rather than writing a malformed one.
    /// </summary>
    internal static string? BuildTaskSddl()
    {
        string? sid = CurrentUserSid();
        return sid is null ? null : $"D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRSD;;;{sid})";
    }

    /// <summary>Widens the existing task's DACL so this user can delete it.
    /// Best-effort throughout: nothing here is worth failing startup over.</summary>
    private static void ApplyTaskSecurity()
    {
        string? sddl = BuildTaskSddl();
        if (sddl is not null) TrySetTaskSddl(TaskName, sddl);
    }

    /// <summary>True if <paramref name="sddl"/> has an allow ACE giving
    /// <paramref name="sid"/> the right to delete the object. Used to tell a
    /// repaired task from one still carrying the old descriptor.</summary>
    internal static bool SddlGrantsDeleteTo(string? sddl, string? sid)
    {
        if (string.IsNullOrEmpty(sddl) || string.IsNullOrEmpty(sid)) return false;

        // ACEs are "(type;flags;rights;objectGuid;inheritGuid;sid[;attr])". Any
        // audit ACEs from an S: section fall out on the allow-type check.
        foreach (Match m in Regex.Matches(sddl, @"\(([^()]*)\)"))
        {
            string[] f = m.Groups[1].Value.Split(';');
            if (f.Length < 6) continue;
            if (f[0].Trim() is not ("A" or "OA")) continue;
            if (!f[5].Trim().Equals(sid, StringComparison.OrdinalIgnoreCase)) continue;
            if (RightsIncludeDelete(f[2].Trim())) return true;
        }
        return false;
    }

    private static bool RightsIncludeDelete(string rights)
    {
        // Windows re-renders a descriptor when it reads one back, and a mask it
        // has no token for comes back as hex — so both spellings have to parse.
        if (rights.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(rights.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint mask))
            return (mask & (RightDelete | RightGenericAll)) != 0;

        // Rights tokens are two chars each and none ends in S, G, F or K, so a
        // plain Contains can't match across a token boundary.
        return rights.Contains("SD", StringComparison.Ordinal)
            || rights.Contains("GA", StringComparison.Ordinal)
            || rights.Contains("FA", StringComparison.Ordinal)
            || rights.Contains("KA", StringComparison.Ordinal);
    }

    private static string? CurrentUserSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-stamps an existing task with the current descriptor, so installs made
    /// before this shipped stop being undeletable. Needs elevation (which the
    /// task itself provides at every logon), only touches a task pointing at
    /// this exe, and does nothing when the grant is already there.
    /// </summary>
    public static void RepairTaskSecurity()
    {
        if (!Elevation.IsElevated()) return;

        string? current = TryGetTaskSddl(TaskName);   // null when there's no task
        if (current is null) return;
        if (SddlGrantsDeleteTo(current, CurrentUserSid())) return;
        if (!TaskPointsAtThisExe()) return;

        ApplyTaskSecurity();
    }

    // ---------------------------------------------------------------
    // Task Scheduler COM, late-bound
    //
    // Only the descriptor goes through COM; the task itself is still created by
    // schtasks.exe, which has no flag for one. Late-bound on purpose: the interop
    // assembly is a NuGet package and CatFoil takes no dependencies. Reflection
    // over IDispatch rather than `dynamic`, which would drag in the C# runtime
    // binder — an odd thing to need in a single-file self-contained publish just
    // to call three methods.
    // ---------------------------------------------------------------

    /// <summary>The task's descriptor as SDDL, or null if it doesn't exist or
    /// can't be read.</summary>
    internal static string? TryGetTaskSddl(string taskName)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = ConnectScheduler();
            if (service is null) return null;
            folder = Invoke(service, "GetFolder", "\\");
            task = Invoke(folder, "GetTask", taskName);
            return Invoke(task, "GetSecurityDescriptor", TaskSecurityInfo) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(task); Release(folder); Release(service);
        }
    }

    /// <summary>Replaces the task's explicit ACEs with <paramref name="sddl"/>.
    /// Inherited ACEs stay; only what was set explicitly is overwritten.</summary>
    internal static bool TrySetTaskSddl(string taskName, string sddl)
    {
        // A protected DACL ("D:P", inheritance blocked) is refused with
        // E_ACCESSDENIED — but only after the inherited ACEs are already gone,
        // which leaves a task that can no longer be updated or deleted without
        // elevation. Exactly the state this whole fix exists to avoid, so it is
        // rejected here rather than trusted not to be passed.
        if (Regex.IsMatch(sddl, @"D:[A-Z]*P")) return false;

        object? service = null, folder = null, task = null;
        try
        {
            service = ConnectScheduler();
            if (service is null) return false;
            folder = Invoke(service, "GetFolder", "\\");
            task = Invoke(folder, "GetTask", taskName);
            if (task is null) return false;
            Invoke(task, "SetSecurityDescriptor", sddl, 0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(task); Release(folder); Release(service);
        }
    }

    private static object? ConnectScheduler()
    {
        Type? t = Type.GetTypeFromProgID("Schedule.Service");
        if (t is null) return null;
        object? service = Activator.CreateInstance(t);
        if (service is null) return null;
        Invoke(service, "Connect");   // trailing optional args: local machine, current user
        return service;
    }

    private static object? Invoke(object? target, string member, params object?[] args) =>
        target?.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, args);

    private static void Release(object? o)
    {
        try
        {
            if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
        catch
        {
            // Already released, or never a real RCW — nothing to do.
        }
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

        if (TaskPointsAtThisExe()) DisableTask();
    }

    /// <summary>
    /// True when the logon task exists and runs this exe. The exported XML
    /// escapes '&amp;' etc., so both spellings of the path count. A mismatch —
    /// or a query that fails — errs toward leaving the task alone: a
    /// registration made by another copy isn't ours to touch.
    /// </summary>
    private static bool TaskPointsAtThisExe()
    {
        string exe = Application.ExecutablePath;
        return RunSchtasksCapture($"/Query /TN \"{TaskName}\" /XML", out string xml) == 0 &&
               (xml.Contains(exe, StringComparison.OrdinalIgnoreCase) ||
                xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase));
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
