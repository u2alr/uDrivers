namespace uDrivers.Models;

public sealed record GpuInfo(string Name, string PnpDeviceId, string DriverVersion);

/// <summary>Who made this PC, which model it is, and what hardware is in it.</summary>
public sealed class SystemProfile
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string SystemSku { get; init; } = "";
    public string BaseBoardProduct { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public bool IsWindows11 { get; init; }
    public string DisplayVersion { get; init; } = "";   // e.g. 24H2
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<InstalledDriver> Devices { get; init; } = [];

    public string Summary
    {
        get
        {
            var os = IsWindows11 ? "Windows 11" : "Windows 10";
            if (DisplayVersion.Length > 0) os += " " + DisplayVersion;
            var pc = $"{Manufacturer} {Model}".Trim();
            return pc.Length > 0 ? $"{pc}  ·  {os}" : os;
        }
    }
}
