using CommunityToolkit.Mvvm.ComponentModel;

namespace uDrivers.Models;

public enum UpdateStatus { Available, Downloading, Installing, Installed, Failed }

/// <summary>A driver update offered by Windows Update for a device on this PC.</summary>
public sealed partial class DriverUpdate : ObservableObject
{
    public required string UpdateId { get; init; }
    public required string Title { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string DriverClass { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public DateTime? NewDate { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Currently installed version for the same device, if we could match it.</summary>
    public string? CurrentVersion { get; set; }
    public DateTime? CurrentDate { get; set; }

    /// <summary>The underlying WUA IUpdate COM object, needed to download/install.</summary>
    internal object? ComUpdate { get; init; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private UpdateStatus _status = UpdateStatus.Available;
    [ObservableProperty] private string _statusText = "";

    public string DeviceLabel => string.IsNullOrWhiteSpace(Model) ? Title : Model;
    public string SizeText => SizeBytes <= 0 ? "" : SizeBytes switch
    {
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):0.0} MB"
    };
    public string NewDateText => NewDate?.ToString("yyyy-MM-dd") ?? "—";
    public string CurrentText => CurrentDate is null && CurrentVersion is null
        ? "Not matched"
        : $"{CurrentVersion ?? "?"} ({CurrentDate?.ToString("yyyy-MM-dd") ?? "?"})";
}
