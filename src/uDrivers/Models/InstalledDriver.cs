namespace uDrivers.Models;

/// <summary>A driver currently installed on this PC (from Win32_PnPSignedDriver).</summary>
public sealed record InstalledDriver(
    string DeviceName,
    string DeviceClass,
    string Provider,
    string Version,
    DateTime? Date,
    string InfName,
    string HardwareId,
    bool IsSigned)
{
    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "—";
}
