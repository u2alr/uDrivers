using System.Management;
using Microsoft.Win32;
using uDrivers.Models;

namespace uDrivers.Services;

public static class SystemInfoService
{
    public static Task<SystemProfile> LoadAsync(IReadOnlyList<InstalledDriver> devices) => Task.Run(() =>
    {
        var cs = First("SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem");
        var board = First("SELECT Product FROM Win32_BaseBoard");
        var bios = First("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
        var cpu = First("SELECT Name FROM Win32_Processor");

        string displayVersion = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = (key?.GetValue("DisplayVersion") as string)
                             ?? (key?.GetValue("ReleaseId") as string) ?? "";
        }
        catch (Exception ex) { Log.Error("Reading Windows version failed", ex); }

        var gpus = new List<GpuInfo>();
        foreach (var vc in All("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController"))
            gpus.Add(new GpuInfo(Get(vc, "Name"), Get(vc, "PNPDeviceID"), Get(vc, "DriverVersion")));

        var profile = new SystemProfile
        {
            Manufacturer = Get(cs, "Manufacturer"),
            Model = Get(cs, "Model"),
            SystemSku = Get(cs, "SystemSKUNumber"),
            BaseBoardProduct = Get(board, "Product"),
            BiosVersion = Get(bios, "SMBIOSBIOSVersion"),
            CpuName = Get(cpu, "Name"),
            IsWindows11 = Environment.OSVersion.Version.Build >= 22000,
            DisplayVersion = displayVersion,
            Gpus = gpus,
            Devices = devices,
        };
        Log.Info($"System: {profile.Manufacturer} | {profile.Model} | SKU {profile.SystemSku} | board {profile.BaseBoardProduct} | BIOS {profile.BiosVersion} | {profile.Summary}");
        return profile;
    });

    private static ManagementBaseObject? First(string query) => All(query).FirstOrDefault();

    private static List<ManagementBaseObject> All(string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"WMI query failed: {query}", ex);
            return [];
        }
    }

    private static string Get(ManagementBaseObject? mo, string prop)
    {
        try { return (mo?[prop]?.ToString() ?? "").Trim(); }
        catch { return ""; }
    }
}
