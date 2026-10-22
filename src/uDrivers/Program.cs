using uDrivers.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace uDrivers;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and exits early when needed.
        VelopackApp.Build().Run();

        // Driver installs need admin. Relaunch elevated once; if the user declines UAC,
        // keep running in read-only mode (scan + check still work, install is disabled).
        if (!Elevation.IsElevated && Elevation.TryRelaunchElevated(args))
            return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
