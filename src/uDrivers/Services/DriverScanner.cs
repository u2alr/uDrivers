using System.Management;
using uDrivers.Models;

namespace uDrivers.Services;

/// <summary>Reads the drivers installed on this PC via WMI.</summary>
public static class DriverScanner
{
    private const string Query =
        "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, IsSigned " +
        "FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL";

    public static Task<IReadOnlyList<InstalledDriver>> ScanAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<InstalledDriver>>(() =>
        {
            var list = new List<InstalledDriver>();
            using var searcher = new ManagementObjectSearcher(Query);
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    list.Add(new InstalledDriver(
                        DeviceName: Str(mo, "DeviceName"),
                        DeviceClass: Str(mo, "DeviceClass"),
                        Provider: Str(mo, "DriverProviderName"),
                        Version: Str(mo, "DriverVersion"),
                        Date: ParseCimDate(mo["DriverDate"] as string),
                        InfName: Str(mo, "InfName"),
                        HardwareId: Str(mo, "HardWareID"),
                        IsSigned: mo["IsSigned"] as bool? ?? false));
                }
            }

            Log.Info($"Scanned {list.Count} installed drivers.");
            return list
                .OrderBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    private static string Str(ManagementBaseObject mo, string prop) =>
        (mo[prop] as string)?.Trim() ?? "";

    private static DateTime? ParseCimDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch { return null; }
    }
}
