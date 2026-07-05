using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace CatFoil;

internal static class Program
{
    private const string MutexName = @"Local\CatFoil-SingleInstance";
    private const string ShowEventName = @"Local\CatFoil-ShowMainWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        // If this is an elevation relaunch, wait for the old (non-elevated)
        // instance to fully exit and release the single-instance slot first.
        WaitForPredecessor(args);

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var mutex = new Mutex(initiallyOwned: false, MutexName);

        if (!TryAcquire(mutex, args))
        {
            // Another CatFoil is already running — surface it instead.
            showEvent.Set();
            return;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var context = new TrayAppContext(showEvent);
            Application.Run(context);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* never owned it — fine */ }
        }
    }

    // Block until the predecessor process named on the command line has exited,
    // so an elevation handoff doesn't collide with it on the mutex. Bounded so a
    // stale/unreachable pid can't hang startup.
    private static void WaitForPredecessor(string[] args)
    {
        int pid = AwaitExitPid(args);
        if (pid <= 0) return;
        try
        {
            using var predecessor = Process.GetProcessById(pid);
            predecessor.WaitForExit(5000);
        }
        catch
        {
            // Already gone, or we can't open it — nothing to wait for.
        }
    }

    private static bool TryAcquire(Mutex mutex, string[] args)
    {
        // A normal second launch shouldn't wait at all; an elevation handoff gets
        // a few seconds' grace for the old instance to release the mutex.
        TimeSpan timeout = AwaitExitPid(args) > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing — we now own it.
            return true;
        }
    }

    private static int AwaitExitPid(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == Elevation.AwaitExitFlag && int.TryParse(args[i + 1], out int pid))
                return pid;
        return 0;
    }
}
