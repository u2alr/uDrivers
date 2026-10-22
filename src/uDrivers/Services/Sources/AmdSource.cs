using System.Text.RegularExpressions;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Latest AMD Software: Adrenalin Edition driver straight from AMD. AMD has no public update API, so this reads
/// AMD's official release-notes pages (RN-RAD-WIN-YY-M-N), which link the WHQL installer and list the
/// Windows Driver Store versions inside it.
/// </summary>
public sealed partial class AmdSource : IDriverSource
{
    private const string NotesBase = "https://www.amd.com/en/resources/support-articles/release-notes/";
    private const string Referer = "https://www.amd.com/en/support/download/drivers.html";

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/[A-Za-z0-9._\-]+\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLink();

    [GeneratedRegex(@"\b32\.0\.\d{5}\.\d{3,5}\b")]
    private static partial Regex StoreVersion();

    public UpdateSource Kind => UpdateSource.Amd;

    public bool AppliesTo(SystemProfile p) => p.Gpus.Any(IsAmd);

    private static bool IsAmd(GpuInfo g) => g.PnpDeviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string Url);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        var gpu = p.Gpus.First(IsAmd);
        var installed = Matching.ParseVersion(gpu.DriverVersion);
        if (installed is null || installed.Major != 32)
        {
            // Pre-RDNA GPUs (Vega, Polaris) are on AMD's legacy 31.x branch, which no longer gets monthly releases.
            Log.Info($"AMD: {gpu.Name} driver {gpu.DriverVersion} isn't on the current Adrenalin branch; skipping.");
            return [];
        }

        var (notesUrl, page, packageVersion) = await FindLatestNotesAsync(ct);
        if (page is null)
            throw new InvalidOperationException("Couldn't find AMD's latest driver release notes.");

        var links = DownloadLink().Matches(page).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var os = p.IsWindows11 ? "win11" : "win10";
        var url = links.FirstOrDefault(l => l.Contains(os, StringComparison.OrdinalIgnoreCase) && !l.Contains("minimal", StringComparison.OrdinalIgnoreCase))
                  ?? links.FirstOrDefault(l => !l.Contains("minimal", StringComparison.OrdinalIgnoreCase));
        if (url is null) return [];

        // The package carries one driver per GPU generation; compare with the one on the same branch as ours
        // (third field 32xxx vs 21xxx, etc.).
        var storeVersions = StoreVersion().Matches(page).Select(m => m.Value).Distinct().ToList();
        if (storeVersions.Count == 0) return [];
        var branch = installed.Build.ToString()[..1];
        var target = storeVersions.FirstOrDefault(v => v.Split('.')[2].StartsWith(branch, StringComparison.Ordinal))
                     ?? storeVersions.OrderByDescending(v => Matching.ParseVersion(v)).First();

        if (Matching.IsNewer(target, gpu.DriverVersion) != true)
        {
            Log.Info($"AMD: installed {gpu.DriverVersion} is current (latest {target}).");
            return [];
        }

        var device = p.Devices.FirstOrDefault(d => d.DeviceId.Equals(gpu.PnpDeviceId, StringComparison.OrdinalIgnoreCase));
        return
        [
            new DriverUpdate
            {
                UpdateId = $"amd:{packageVersion}",
                Title = $"AMD Software: Adrenalin Edition {packageVersion}",
                Source = UpdateSource.Amd,
                Kind = UpdateKind.Driver,
                Manufacturer = "AMD",
                Model = $"{gpu.Name} — Adrenalin {packageVersion}",
                DriverClass = "Display",
                NewVersion = target,
                InfoUrl = notesUrl,
                CurrentVersion = gpu.DriverVersion,
                CurrentDate = device?.Date,
                DeviceInstanceId = device?.DeviceId ?? gpu.PnpDeviceId,
                Payload = new Package(url),
            }
        ];
    }

    /// <summary>Probes AMD's release-notes URLs from this month backwards; AMD versions are YY.M.N.</summary>
    private static async Task<(string Url, string? Page, string Version)> FindLatestNotesAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        for (int back = 0; back <= 5; back++)
        {
            var month = now.AddMonths(-back);
            int yy = month.Year % 100, m = month.Month;

            var probes = Enumerable.Range(1, 5).Reverse().Select(async n =>
            {
                var url = $"{NotesBase}RN-RAD-WIN-{yy}-{m}-{n}.html";
                var page = await Web.TryGetStringAsync(url, ct);
                bool real = page is not null && page.Contains("drivers.amd.com/drivers/", StringComparison.OrdinalIgnoreCase);
                return (n, url, page: real ? page : null);
            }).ToList();

            var hit = (await Task.WhenAll(probes)).Where(x => x.page is not null).OrderByDescending(x => x.n).FirstOrDefault();
            if (hit.page is not null)
            {
                Log.Info($"AMD: latest release notes {hit.url}");
                return (hit.url, hit.page, $"{yy}.{m}.{hit.n}");
            }
        }
        return ("", null, "");
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("amd");
        var file = Path.Combine(dir, Web.SafeFileName(pkg.Url));

        status("Downloading…");
        // AMD's download server requires a Referer from amd.com.
        await Web.DownloadAsync(pkg.Url, file, ct, pct => status($"Downloading… {pct}%"), Referer);
        SignatureVerifier.Require(file, hashVerified: false, "Advanced Micro Devices", "AMD");

        status("Installing… your screen may flicker");
        var code = await PackageRunner.RunAsync(file, "-install", dir, TimeSpan.FromMinutes(45), ct);
        return ExitCodes.Interpret(code);
    }
}
