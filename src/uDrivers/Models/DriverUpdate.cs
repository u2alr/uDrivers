using CommunityToolkit.Mvvm.ComponentModel;

namespace uDrivers.Models;

public enum UpdateStatus { Available, Downloading, Installing, Installed, Failed }

public enum UpdateSource { WindowsUpdate, Dell, HP, Lenovo, Nvidia, Amd, Intel }

public enum UpdateKind { Driver, Firmware, Bios }

/// <summary>An official driver (or firmware/BIOS) update found for a device on this PC.</summary>
public sealed partial class DriverUpdate : ObservableObject
{
    public required string UpdateId { get; init; }
    public required string Title { get; init; }
    public UpdateSource Source { get; init; }
    public UpdateKind Kind { get; init; } = UpdateKind.Driver;

    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string DriverClass { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string? NewVersion { get; init; }
    public DateTime? NewDate { get; init; }
    public long SizeBytes { get; init; }
    public string? InfoUrl { get; init; }

    /// <summary>False for updates uDrivers lists but leaves to the vendor's own tool (BIOS, some firmware).</summary>
    public bool CanInstall { get; init; } = true;
    public string? Note { get; init; }

    /// <summary>Currently installed version for the matched device.</summary>
    public string? CurrentVersion { get; set; }
    public DateTime? CurrentDate { get; set; }

    /// <summary>Instance path of the matched device; used to avoid offering two drivers for one device.</summary>
    public string? DeviceInstanceId { get; set; }

    /// <summary>Source-specific data needed to install (WUA IUpdate COM object, package URLs, etc.).</summary>
    internal object? Payload { get; init; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private UpdateStatus _status = UpdateStatus.Available;
    [ObservableProperty] private string _statusText = "";

    public string DeviceLabel => string.IsNullOrWhiteSpace(Model) ? Title : Model;

    public string SourceText => Source switch
    {
        UpdateSource.WindowsUpdate => "Windows Update",
        UpdateSource.Nvidia => "NVIDIA",
        UpdateSource.Amd => "AMD",
        _ => Source.ToString(),
    };

    public string KindText => Kind switch
    {
        UpdateKind.Bios => "BIOS",
        UpdateKind.Firmware => "Firmware",
        _ => "",
    };

    public string SizeText => SizeBytes <= 0 ? "" : SizeBytes switch
    {
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):0.0} MB"
    };

    public string NewText
    {
        get
        {
            var date = NewDate?.ToString("yyyy-MM-dd");
            return (NewVersion, date) switch
            {
                (not null, not null) => $"{NewVersion} ({date})",
                (not null, null) => NewVersion,
                (null, not null) => date,
                _ => "—",
            };
        }
    }

    public string CurrentText => CurrentDate is null && CurrentVersion is null
        ? "—"
        : $"{CurrentVersion ?? "?"}" + (CurrentDate is null ? "" : $" ({CurrentDate:yyyy-MM-dd})");
}
