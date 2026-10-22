using System.Globalization;
using System.Text.RegularExpressions;
using uDrivers.Models;

namespace uDrivers.Services;

/// <summary>Version parsing and hardware-ID matching shared by all vendor sources.</summary>
public static partial class Matching
{
    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex DottedVersion();

    /// <summary>Pulls the first dotted number out of strings like "31.0.101.4502 A" or "T76 Ver. 01.17.00".</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DottedVersion().Match(text);
        if (!m.Success) return null;
        var parts = m.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return parts.Length switch
        {
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3]),
        };
    }

    /// <summary>True if <paramref name="candidate"/> is newer than <paramref name="installed"/>; null if they can't be compared.</summary>
    public static bool? IsNewer(string? candidate, string? installed)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(installed);
        if (a is null || b is null) return null;
        return Normalize(a) > Normalize(b);
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Trim('^');
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "MMMM dd, yyyy", "MMMM d, yyyy", "MM/dd/yyyy", "M/d/yyyy", "ddd MMM dd, yyyy"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
            return d.Date;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d))
            return d.Date;
        return null;
    }

    /// <summary>
    /// Devices whose hardware IDs start with any of the given ID prefixes
    /// (e.g. "PCI\VEN_8086&amp;DEV_15F3" matches "PCI\VEN_8086&amp;DEV_15F3&amp;SUBSYS_0A3B1028&amp;REV_20").
    /// </summary>
    public static List<InstalledDriver> FindDevices(IEnumerable<InstalledDriver> devices, IEnumerable<string> idPrefixes)
    {
        var prefixes = idPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"').ToUpperInvariant())
            .Distinct()
            .ToList();
        if (prefixes.Count == 0) return [];

        return devices
            .Where(d => d.HardwareIds.Any(id => prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))))
            .ToList();
    }

    /// <summary>Builds a PCI ID prefix from Dell-style PCIInfo values.</summary>
    public static string PciId(string vendor, string device, string? subVendor = null, string? subDevice = null)
    {
        var id = $@"PCI\VEN_{vendor}&DEV_{device}".ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(subVendor) && !string.IsNullOrWhiteSpace(subDevice))
            id += $"&SUBSYS_{subDevice}{subVendor}".ToUpperInvariant();
        return id;
    }

    /// <summary>The installed driver with the highest version among matched devices.</summary>
    public static InstalledDriver? Newest(IEnumerable<InstalledDriver> matched) =>
        matched.OrderByDescending(d => ParseVersion(d.Version) ?? new Version(0, 0)).FirstOrDefault();
}
