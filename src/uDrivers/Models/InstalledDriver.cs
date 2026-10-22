namespace uDrivers.Models;

/// <summary>A driver currently installed on this PC (Win32_PnPSignedDriver joined with Win32_PnPEntity).</summary>
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
    /// <summary>Device instance path, e.g. PCI\VEN_8086&amp;DEV_15F3&amp;...\3&amp;11583659&amp;0&amp;FE.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>All hardware and compatible IDs the device reports, upper-cased, most specific first.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "—";
}
