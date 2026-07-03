using System;
using System.Threading;
using System.Windows.Forms;

namespace CatFoil;

internal static class Program
{
    private const string MutexName = @"Local\CatFoil-SingleInstance";
    private const string ShowEventName = @"Local\CatFoil-ShowMainWindow";

    [STAThread]
    private static void Main()
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Another CatFoil is already running — surface it instead.
            showEvent.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayAppContext(showEvent);
        Application.Run(context);

        GC.KeepAlive(mutex);
    }
}
