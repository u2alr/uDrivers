using System.Text.RegularExpressions;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Latest Intel graphics driver straight from Intel's Download Center. Intel publishes one download page per
/// GPU family (Arc / Core Ultra, 11th–14th gen, 7th–10th gen, 6th gen); this picks the right page for the CPU/GPU,
/// reads the current installer link and version from it, and installs silently.
/// </summary>
public sealed partial class IntelGraphicsSource : IDriverSource
{
    private const string ArcPage = "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html";
    private const string Gen11To14Page = "https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html";
    private const string Gen7To10Page = "https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html";
    private const string Gen6Page = "https://www.intel.com/content/www/us/en/download/762755/intel-6th-gen-processor-graphics-windows.html";

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/gfx_win_[\d.]+\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerLink();

    [GeneratedRegex(@"\b3[12]\.0\.10[01]\.\d{4,5}\b")]
    private static partial Regex DriverVersion();

    [GeneratedRegex(@"i[3579]-(\d{4,5})", RegexOptions.IgnoreCase)]
    private static partial Regex CoreModel();

    [GeneratedRegex(@"Core\(TM\)\s+[3579]\s+\d{3}[A-Z]*", RegexOptions.IgnoreCase)]
    private static partial Regex CoreSeries1();

    public UpdateSource Kind => UpdateSource.Intel;

    public bool AppliesTo(SystemProfile p) => p.Gpus.Any(IsIntel);

    private static bool IsIntel(GpuInfo g) => g.PnpDeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string Url);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        var gpu = p.Gpus.First(IsIntel);
        var pageUrl = PickPage(gpu.Name, p.CpuName);
        if (pageUrl is null)
        {
            Log.Info($"Intel: no current driver family for \"{gpu.Name}\" on \"{p.CpuName}\".");
            return [];
        }

        var page = await Web.TryGetStringAsync(pageUrl, ct)
                   ?? throw new InvalidOperationException("Couldn't reach Intel's download page.");

        var link = InstallerLink().Matches(page).Select(m => m.Value).FirstOrDefault();
        var version = DriverVersion().Matches(page).Select(m => m.Value)
            .OrderByDescending(v => Matching.ParseVersion(v)).FirstOrDefault();
        if (link is null || version is null)
        {
            Log.Info($"Intel: couldn't read the installer or version from {pageUrl}.");
            return [];
        }

        if (Matching.IsNewer(version, gpu.DriverVersion) != true)
        {
            Log.Info($"Intel: installed {gpu.DriverVersion} is current (latest {version}).");
            return [];
        }

        var device = p.Devices.FirstOrDefault(d => d.DeviceId.Equals(gpu.PnpDeviceId, StringComparison.OrdinalIgnoreCase));
        return
        [
            new DriverUpdate
            {
                UpdateId = $"intel:gfx:{version}",
                Title = $"Intel Graphics Driver {version}",
                Source = UpdateSource.Intel,
                Kind = UpdateKind.Driver,
                Manufacturer = "Intel",
                Model = $"{gpu.Name} — Intel Graphics Driver",
                DriverClass = "Display",
                NewVersion = version,
                InfoUrl = pageUrl,
                CurrentVersion = gpu.DriverVersion,
                CurrentDate = device?.Date,
                DeviceInstanceId = device?.DeviceId ?? gpu.PnpDeviceId,
                Payload = new Package(link),
            }
        ];
    }

    private static string? PickPage(string gpuName, string cpuName)
    {
        if (gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
            return ArcPage;

        // "Core 5 120U"-style names (Core 100 series) use the 11th–14th gen driver.
        if (CoreSeries1().IsMatch(cpuName)) return Gen11To14Page;

        var m = CoreModel().Match(cpuName);
        if (!m.Success) return null;
        var digits = m.Groups[1].Value;
        // i7-12700K / i7-10750H (5 digits) and i7-1165G7 / i5-1035G1 (4 digits starting with 1) → first two digits;
        // i7-8550U / i5-6500 → first digit.
        int gen = digits.Length == 5 || digits[0] == '1' ? int.Parse(digits[..2]) : int.Parse(digits[..1]);

        return gen switch
        {
            >= 11 and <= 14 => Gen11To14Page,
            >= 7 and <= 10 => Gen7To10Page,
            6 => Gen6Page,
            _ => null,
        };
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("intel-gfx");
        var file = Path.Combine(dir, Web.SafeFileName(pkg.Url));

        status("Downloading…");
        await Web.DownloadAsync(pkg.Url, file, ct, pct => status($"Downloading… {pct}%"));
        SignatureVerifier.Require(file, hashVerified: false, "Intel");

        status("Installing… your screen may flicker");
        var code = await PackageRunner.RunAsync(file, "-s", dir, TimeSpan.FromMinutes(45), ct);
        return ExitCodes.Interpret(code);
    }
}
