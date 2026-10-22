using System.Xml.Linq;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Dell's official update catalog — the same one Dell Command | Update uses.
/// CatalogIndexPC.cab lists every model by system SKU; each model has its own catalog of driver/firmware/BIOS packages.
/// </summary>
public sealed class DellSource : IDriverSource
{
    private const string IndexUrl = "https://downloads.dell.com/catalog/CatalogIndexPC.cab";
    private const string DefaultBase = "https://downloads.dell.com/";

    public UpdateSource Kind => UpdateSource.Dell;

    public bool AppliesTo(SystemProfile p) =>
        p.Manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase) ||
        p.Manufacturer.Contains("Alienware", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string Url, string? Hash);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.SystemSku))
            throw new InvalidOperationException("Couldn't read this Dell's system SKU.");

        var work = PackageRunner.FreshFolder("dell-catalog");

        // 1. Index of all models
        var indexCab = Path.Combine(work, "CatalogIndexPC.cab");
        await Web.DownloadAsync(IndexUrl, indexCab, ct);
        var index = Xml.Load(Xml.FirstXml(await PackageRunner.ExpandCabAsync(indexCab, ct)));

        var group = index.All("GroupManifest").FirstOrDefault(g =>
            g.All("Model").Any(m => m.Attr("systemID").Equals(p.SystemSku, StringComparison.OrdinalIgnoreCase)));
        if (group is null)
        {
            Log.Info($"Dell: SKU {p.SystemSku} isn't in the catalog index.");
            return [];
        }

        var modelPath = group.All("ManifestInformation").Select(m => m.Attr("path")).FirstOrDefault(s => s.Length > 0)
                        ?? throw new InvalidOperationException("Dell's catalog index entry has no path.");

        // 2. This model's catalog
        var modelCab = Path.Combine(work, "model.cab");
        await Web.DownloadAsync(DefaultBase + modelPath.TrimStart('/'), modelCab, ct);
        var catalog = Xml.Load(Xml.FirstXml(await PackageRunner.ExpandCabAsync(modelCab, ct)));

        var baseLocation = catalog.Root.Attr("baseLocation");
        var baseUrl = baseLocation.Length > 0
            ? (baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseLocation : "https://" + baseLocation).TrimEnd('/') + "/"
            : DefaultBase;

        var results = new List<DriverUpdate>();
        foreach (var c in catalog.All("SoftwareComponent"))
        {
            ct.ThrowIfCancellationRequested();
            var update = Evaluate(c, p, baseUrl);
            if (update is not null) results.Add(update);
        }

        // Dell often lists several releases of the same package; keep the newest per device.
        var deduped = results
            .GroupBy(u => (u.Kind, Key: u.DeviceInstanceId ?? u.Title))
            .Select(g => g.OrderByDescending(u => Matching.ParseVersion(u.NewVersion) ?? new Version(0, 0))
                          .ThenByDescending(u => u.NewDate).First())
            .ToList();

        Log.Info($"Dell: {deduped.Count} update(s) for SKU {p.SystemSku}.");
        return deduped;
    }

    private static DriverUpdate? Evaluate(XElement c, SystemProfile p, string baseUrl)
    {
        var type = c.Kid("ComponentType").Attr("value").ToUpperInvariant();
        var kind = type switch
        {
            "DRVR" => UpdateKind.Driver,
            "FRMW" => UpdateKind.Firmware,
            "BIOS" => UpdateKind.Bios,
            _ => (UpdateKind?)null,
        };
        if (kind is null) return null;

        // 64-bit Windows packages only (when the catalog says)
        var oses = c.All("OperatingSystem").ToList();
        if (oses.Count > 0 && !oses.Any(o => o.Attr("osArch").Equals("x64", StringComparison.OrdinalIgnoreCase) || o.Attr("osArch").Length == 0))
            return null;

        var path = c.Attr("path");
        if (path.Length == 0) return null;

        var name = c.Kid("Name").Display();
        var version = c.Attr("vendorVersion");
        var date = Matching.ParseDate(c.Attr("dateTime")) ?? Matching.ParseDate(c.Attr("releaseDate"));
        var hash = c.All("Hash").FirstOrDefault(h => h.Attr("algorithm").Contains("SHA256", StringComparison.OrdinalIgnoreCase))?.Value.Trim()
                   ?? c.Attr("hashMD5");
        long.TryParse(c.Attr("size"), out var size);
        var category = c.Kid("Category").Display();
        var info = c.Kid("ImportantInfo").Attr("URL");

        string? currentVersion;
        DateTime? currentDate = null;
        string? deviceId = null;

        if (kind == UpdateKind.Bios)
        {
            if (Matching.IsNewer(version, p.BiosVersion) != true) return null;
            currentVersion = p.BiosVersion;
        }
        else
        {
            var ids = new List<string>();
            foreach (var dev in c.All("Device"))
            {
                foreach (var pci in dev.Kids("PCIInfo"))
                {
                    var ven = pci.Attr("vendorID");
                    var did = pci.Attr("deviceID");
                    if (ven.Length > 0 && did.Length > 0)
                        ids.Add(Matching.PciId(ven, did, pci.Attr("subVendorID"), pci.Attr("subDeviceID")));
                }
                // Non-PCI devices (USB, ACPI, HDAUDIO…) are given as PnP strings on other child elements.
                foreach (var a in dev.Descendants().SelectMany(e => e.Attributes()))
                    if (a.Value.Contains('\\')) ids.Add(a.Value);
            }
            if (ids.Count == 0) return null; // can't tell which device it's for, so don't guess

            var matched = Matching.FindDevices(p.Devices, ids);
            var current = Matching.Newest(matched);
            if (current is null) return null; // hardware isn't in this PC

            var newer = Matching.IsNewer(version, current.Version);
            if (newer == false) return null;
            if (newer is null && (date is null || current.Date is null || date <= current.Date)) return null;

            currentVersion = current.Version;
            currentDate = current.Date;
            deviceId = current.DeviceId;
        }

        bool installable = kind == UpdateKind.Driver;
        return new DriverUpdate
        {
            UpdateId = "dell:" + (c.Attr("releaseID").Length > 0 ? c.Attr("releaseID") : path),
            Title = name.Length > 0 ? name : Path.GetFileName(path),
            Source = UpdateSource.Dell,
            Kind = kind.Value,
            Manufacturer = "Dell",
            Model = name,
            DriverClass = category,
            NewVersion = version,
            NewDate = date,
            SizeBytes = size,
            InfoUrl = info.Length > 0 ? info : null,
            CanInstall = installable,
            IsSelected = installable,
            Note = installable ? null : "Install with Dell Command | Update",
            CurrentVersion = currentVersion,
            CurrentDate = currentDate,
            DeviceInstanceId = deviceId,
            Payload = new Package(baseUrl + path.TrimStart('/'), hash),
        };
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("dell", update.UpdateId.Replace(':', '_').Replace('/', '_'));
        var file = Path.Combine(dir, Web.SafeFileName(pkg.Url));

        status("Downloading…");
        await Web.DownloadAsync(pkg.Url, file, ct, pct => status($"Downloading… {pct}%"));

        var hashOk = Web.VerifyHash(file, pkg.Hash);
        if (hashOk == false) return new(false, false, "Download was corrupted (hash mismatch)");
        SignatureVerifier.Require(file, hashVerified: false, "Dell");

        status("Installing…");
        var log = Path.Combine(dir, "install.log");
        var code = await PackageRunner.RunAsync(file, $"/s /l=\"{log}\"", dir, TimeSpan.FromMinutes(30), ct);

        // Dell Update Package exit codes: 0 OK, 2 OK + reboot, 3 not applicable / already current
        return code switch
        {
            0 => new(true, false, "Installed"),
            2 => new(true, true, "Installed — restart needed"),
            3 => new(true, false, "Already up to date"),
            _ => ExitCodes.Interpret(code),
        };
    }
}
