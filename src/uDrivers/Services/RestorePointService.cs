using System.Management;

namespace uDrivers.Services;

/// <summary>Creates a System Restore point before drivers are changed, so the user can roll back.</summary>
public static class RestorePointService
{
    private const int DeviceDriverInstall = 10;  // RestorePointType
    private const int BeginSystemChange = 100;   // EventType

    /// <returns>null on success, otherwise a short reason it could not be created.</returns>
    public static Task<string?> CreateAsync(string description) => Task.Run<string?>(() =>
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = DeviceDriverInstall;
            input["EventType"] = BeginSystemChange;

            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);
            if (code == 0)
            {
                Log.Info("Restore point created.");
                return null;
            }

            Log.Info($"Restore point returned code {code}.");
            return code == 1058
                ? "System Restore is turned off on this PC."
                : $"System Restore returned code {code}.";
        }
        catch (Exception ex)
        {
            Log.Error("Restore point failed", ex);
            return "System Restore is unavailable on this PC.";
        }
    });
}
