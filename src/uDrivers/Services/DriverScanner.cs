using System.Management;
using uDrivers.Models;

namespace uDrivers.Services;

/// <summary>Reads the drivers installed on this PC via WMI.</summary>
public static class DriverScanner
{
    private const string DriverQuery =
        "SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, IsSigned " +
        "FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL";

    private const string EntityQuery = "SELECT PNPDeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity";

    public static Task<IReadOnlyList<InstalledDriver>> ScanAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<InstalledDriver>>(() =>
        {
            var idsByDevice = ReadHardwareIds(ct);
            var list = new List<InstalledDriver>();

            using var searcher = new ManagementObjectSearcher(DriverQuery);
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    var deviceId = Str(mo, "DeviceID");
                    var primary = Str(mo, "HardWareID");
                    var ids = idsByDevice.TryGetValue(deviceId, out var found) ? found : [];
                    if (primary.Length > 0 && !ids.Contains(primary.ToUpperInvariant()))
                        ids = [primary.ToUpperInvariant(), .. ids];

                    list.Add(new InstalledDriver(
                        DeviceName: Str(mo, "DeviceName"),
                        DeviceClass: Str(mo, "DeviceClass"),
                        Provider: Str(mo, "DriverProviderName"),
                        Version: Str(mo, "DriverVersion"),
                        Date: ParseCimDate(mo["DriverDate"] as string),
                        InfName: Str(mo, "InfName"),
                        HardwareId: primary,
                        IsSigned: mo["IsSigned"] as bool? ?? false)
                    {
                        DeviceId = deviceId,
                        HardwareIds = ids,
                    });
                }
            }

            Log.Info($"Scanned {list.Count} installed drivers.");
            return list
                .OrderBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    private static Dictionary<string, string[]> ReadHardwareIds(CancellationToken ct)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(EntityQuery);
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    var id = Str(mo, "PNPDeviceID");
                    if (id.Length == 0) continue;
                    var hw = (mo["HardwareID"] as string[]) ?? [];
                    var compat = (mo["CompatibleID"] as string[]) ?? [];
                    map[id] = hw.Concat(compat)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim().ToUpperInvariant())
                        .Distinct()
                        .ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading hardware IDs failed", ex);
        }
        return map;
    }

    private static string Str(ManagementBaseObject mo, string prop) =>
        (mo[prop] as string)?.Trim() ?? "";

    private static DateTime? ParseCimDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch { return null; }
    }
}
