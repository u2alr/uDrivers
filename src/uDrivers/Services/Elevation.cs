using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace uDrivers.Services;

public static class Elevation
{
    private const int ErrorCancelled = 1223; // user clicked "No" on the UAC prompt

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts an elevated copy of this exe. Returns false if the user declined UAC.</summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(a => $"\"{a.Replace("\"", "\\\"")}\"")),
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("UAC declined; running in read-only mode.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Could not relaunch elevated", ex);
            return false;
        }
    }
}
