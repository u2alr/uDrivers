using System.Xml.Linq;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Lenovo's official update catalog — the one Lenovo System Update / Vantage uses.
/// download.lenovo.com/catalog/{MachineType}_{Win10|Win11}.xml lists package descriptors; each package XML
/// carries the installer file, silent command line, and detection rules (hardware IDs + minimum version).
/// </summary>
public sealed class LenovoSource : IDriverSource
{
    private const string CatalogBase = "https://download.lenovo.com/catalog/";
    private const string PackageTypeDriver = "2";

    public UpdateSource Kind => UpdateSource.Lenovo;

    public bool AppliesTo(SystemProfile p) => p.Manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string FileUrl, string? Hash, string ExtractCommand, string InstallCommand, int[] SuccessCodes, bool Reboot);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        // Lenovo stores the machine type-model in Model, e.g. "20XW0026US" → machine type "20XW".
        var mt = new string(p.Model.Trim().Take(4).ToArray()).ToUpperInvariant();
        if (mt.Length < 4) throw new InvalidOperationException("Couldn't read this Lenovo's machine type.");

        string? catalog = null;
        string[] osNames = p.IsWindows11 ? ["Win11", "Win10"] : ["Win10"];
        foreach (var os in osNames)
        {
            catalog = await Web.TryGetStringAsync($"{CatalogBase}{mt}_{os}.xml", ct);
            if (catalog is not null) break;
        }
        if (catalog is null)
        {
            Log.Info($"Lenovo: no catalog for machine type {mt}.");
            return [];
        }

        var locations = Xml.Parse(catalog).All("package")
            .Select(e => e.Text("location"))
            .Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var gate = new SemaphoreSlim(8);
        var tasks = locations.Select(async loc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var text = await Web.TryGetStringAsync(loc, ct);
                return text is null ? null : Evaluate(Xml.Parse(text), loc, p);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Info($"Lenovo: skipped {loc}: {ex.Message}"); return null; }
            finally { gate.Release(); }
        }).ToList();

        var results = (await Task.WhenAll(tasks)).Where(u => u is not null).Cast<DriverUpdate>().ToList();
        Log.Info($"Lenovo: {results.Count} update(s) for machine type {mt} ({locations.Count} packages checked).");
        return results;
    }

    private static DriverUpdate? Evaluate(XDocument doc, string location, SystemProfile p)
    {
        var pkg = doc.Root;
        if (pkg is null) return null;
        if (pkg.Kid("PackageType").Attr("type") != PackageTypeDriver) return null;

        // Dependencies: the hardware must be present.
        var required = pkg.Kid("Dependencies")?.All("_PnPID").Select(e => e.Value.Trim()).ToList() ?? [];
        if (required.Count > 0 && Matching.FindDevices(p.Devices, required).Count == 0) return null;

        // DetectInstall: "already installed" if a matching device has at least the given driver version/date.
        var rules = pkg.Kid("DetectInstall")?.All("_Driver").ToList() ?? [];
        if (rules.Count == 0) return null; // no way to tell if it's needed

        InstalledDriver? current = null;
        foreach (var rule in rules)
        {
            var ids = rule.All("HardwareID").Select(e => e.Value.Trim()).ToList();
            var matched = Matching.FindDevices(p.Devices, ids);
            if (matched.Count == 0) continue;

            var device = Matching.Newest(matched)!;
            var minVersion = rule.Text("Version").Trim('^');
            var minDate = Matching.ParseDate(rule.Text("Date"));

            bool installed =
                (minVersion.Length > 0 && Matching.IsNewer(minVersion, device.Version) == false) ||
                (minDate is not null && device.Date is not null && device.Date >= minDate);
            if (installed) return null;
            current ??= device;
        }
        if (current is null) return null; // hardware not present

        var installer = pkg.Kid("Files")?.Kid("Installer")?.Kid("File");
        var fileName = installer.Text("Name");
        if (fileName.Length == 0) return null;
        var baseUrl = location[..(location.LastIndexOf('/') + 1)];
        var crc = installer.Text("CRC");
        long.TryParse(installer.Text("Size"), out var size);

        var install = pkg.Kid("Install");
        var cmd = install.Text("Cmdline");
        if (cmd.Length == 0) return null;
        var codes = install.Attr("rc").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : (int?)null).Where(n => n is not null).Select(n => n!.Value).ToArray();
        if (codes.Length == 0) codes = [0, 3010];

        var title = pkg.Kid("Title").Display();
        if (title.Length == 0) title = pkg.Attr("name");
        var version = pkg.Attr("version");
        var readme = pkg.Kid("Files")?.Kid("Readme")?.Kid("File").Text("Name") ?? "";

        return new DriverUpdate
        {
            UpdateId = "lenovo:" + pkg.Attr("id"),
            Title = title,
            Source = UpdateSource.Lenovo,
            Kind = UpdateKind.Driver,
            Manufacturer = "Lenovo",
            Model = title,
            DriverClass = current.DeviceClass,
            NewVersion = version,
            NewDate = Matching.ParseDate(pkg.Text("ReleaseDate")),
            SizeBytes = size,
            InfoUrl = readme.Length > 0 ? baseUrl + readme : null,
            CurrentVersion = current.Version,
            CurrentDate = current.Date,
            DeviceInstanceId = current.DeviceId,
            Payload = new Package(
                baseUrl + fileName,
                crc.Length == 64 ? crc : null,
                pkg.Text("ExtractCommand"),
                cmd,
                codes,
                pkg.Kid("Reboot").Attr("type") is "1" or "3" or "4"),
        };
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("lenovo", update.UpdateId.Replace(':', '_'));
        var file = Path.Combine(dir, Web.SafeFileName(pkg.FileUrl));

        status("Downloading…");
        await Web.DownloadAsync(pkg.FileUrl, file, ct, pct => status($"Downloading… {pct}%"));

        var hashOk = Web.VerifyHash(file, pkg.Hash);
        if (hashOk == false) return new(false, false, "Download was corrupted (hash mismatch)");
        SignatureVerifier.Require(file, hashVerified: hashOk == true, "Lenovo");

        string Expand(string s) => s.Replace("%PACKAGEPATH%", dir, StringComparison.OrdinalIgnoreCase);

        if (pkg.ExtractCommand.Length > 0)
        {
            status("Unpacking…");
            var x = await PackageRunner.RunCommandLineAsync(Expand(pkg.ExtractCommand), dir, TimeSpan.FromMinutes(10), ct);
            if (x != 0) return new(false, false, $"Couldn't unpack the package (exit code {x})");
        }

        status("Installing…");
        var code = await PackageRunner.RunCommandLineAsync(Expand(pkg.InstallCommand), dir, TimeSpan.FromMinutes(30), ct);
        if (pkg.SuccessCodes.Contains(code))
            return new(true, pkg.Reboot || code == 3010, pkg.Reboot || code == 3010 ? "Installed — restart needed" : "Installed");
        return ExitCodes.Interpret(code);
    }
}
