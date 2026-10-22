using uDrivers.Models;

namespace uDrivers.Services.Sources;

public sealed record InstallResult(bool Success, bool RebootRequired, string Message);

/// <summary>An official place driver updates come from (Windows Update, a PC maker, a GPU maker).</summary>
public interface IDriverSource
{
    UpdateSource Kind { get; }

    /// <summary>Whether this source is relevant for this PC (e.g. Dell source only on Dell PCs).</summary>
    bool AppliesTo(SystemProfile profile);

    Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile profile, CancellationToken ct);

    Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct);
}

public static class ExitCodes
{
    /// <summary>Common success codes: 0 = OK, 3010 = OK but restart needed, 1641 = restart started.</summary>
    public static InstallResult Interpret(int code, params int[] extraSuccess)
    {
        return code switch
        {
            0 => new(true, false, "Installed"),
            3010 or 1641 => new(true, true, "Installed — restart needed"),
            _ when extraSuccess.Contains(code) => new(true, false, "Installed"),
            _ => new(false, false, $"Installer failed (exit code {code})"),
        };
    }
}
