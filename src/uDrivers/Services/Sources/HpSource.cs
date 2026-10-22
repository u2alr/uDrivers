using System.Xml.Linq;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// HP's official SoftPaq catalog — the reference files HP Image Assistant uses, published per platform ID
/// (baseboard product) and Windows version. Each SoftPaq's .cva metadata lists the hardware IDs it supports.
/// </summary>
public sealed class HpSource : IDriverSource
{
    private const string RefBase = "https://hpia.hpcloud.hp.com/ref/";

    public UpdateSource Kind => UpdateSource.HP;

    public bool AppliesTo(SystemProfile p) =>
        p.Manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase) ||
        p.Manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) ||
        p.Manufacturer.StartsWith("HP ", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string Url, string? Hash, string SilentInstall);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        var platform = p.BaseBoardProduct.Trim();
        if (platform.Length == 0) throw new InvalidOperationException("Couldn't read this HP's platform ID.");

        var work = PackageRunner.FreshFolder("hp-catalog");
        var xmlPath = await DownloadReferenceAsync(platform, p, work, ct);
        if (xmlPath is null)
        {
            Log.Info($"HP: no reference file for platform {platform}.");
            return [];
        }

        var doc = Xml.Load(xmlPath);
        var softpaqs = doc.All("UpdateInfo")
            .GroupBy(u => u.Text("Id"), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var results = new List<DriverUpdate>();
        using var gate = new SemaphoreSlim(6);
        var tasks = softpaqs.Select(async sp =>
        {
            await gate.WaitAsync(ct);
            try { return await EvaluateAsync(sp, p, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Info($"HP: skipped {sp.Text("Id")}: {ex.Message}"); return null; }
            finally { gate.Release(); }
        }).ToList();

        foreach (var u in await Task.WhenAll(tasks))
            if (u is not null) results.Add(u);

        Log.Info($"HP: {results.Count} update(s) for platform {platform}.");
        return results;
    }

    private static async Task<string?> DownloadReferenceAsync(string platform, SystemProfile p, string work, CancellationToken ct)
    {
        var os = p.IsWindows11 ? "11.0" : "10.0";
        var versions = new List<string>();
        if (p.DisplayVersion.Length > 0) versions.Add(p.DisplayVersion);
        versions.AddRange(p.IsWindows11
            ? ["25H2", "24H2", "23H2", "22H2", "21H2"]
            : ["22H2", "21H2", "2009", "2004"]);

        foreach (var v in versions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = $"{platform}_64_{os}.{v}";
            var cab = Path.Combine(work, name + ".cab");
            if (!await Web.TryDownloadAsync($"{RefBase}{platform}/{name}.cab", cab, ct)) continue;

            var folder = await PackageRunner.ExpandCabAsync(cab, ct);
            Log.Info($"HP: using reference file {name}.");
            return Xml.FirstXml(folder);
        }
        return null;
    }

    private static async Task<DriverUpdate?> EvaluateAsync(XElement sp, SystemProfile p, CancellationToken ct)
    {
        var id = sp.Text("Id");
        var category = sp.Text("Category");
        var kind = category switch
        {
            _ when category.StartsWith("Driver", StringComparison.OrdinalIgnoreCase) => UpdateKind.Driver,
            _ when category.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase) => UpdateKind.Bios,
            _ when category.StartsWith("Firmware", StringComparison.OrdinalIgnoreCase) => UpdateKind.Firmware,
            _ => (UpdateKind?)null,
        };
        if (kind is null || id.Length == 0) return null;

        var name = sp.Text("Name");
        var version = sp.Text("Version");
        var url = Https(sp.Text("Url"));
        if (url.Length == 0) return null;
        var date = Matching.ParseDate(sp.Text("DateReleased", "ReleaseDate"));
        long.TryParse(sp.Text("Size"), out var size);
        var hash = sp.Text("SHA256", "MD5");
        var ssm = sp.Text("SSM").Equals("true", StringComparison.OrdinalIgnoreCase);
        var notes = Https(sp.Text("ReleaseNotesUrl"));

        string? currentVersion;
        DateTime? currentDate = null;
        string? deviceId = null;
        string silent = sp.Text("SilentInstall");

        if (kind == UpdateKind.Bios)
        {
            if (Matching.IsNewer(version, p.BiosVersion) != true) return null;
            currentVersion = p.BiosVersion;
        }
        else
        {
            var cvaUrl = Https(sp.Text("CvaFileUrl", "CvaUrl"));
            if (cvaUrl.Length == 0) cvaUrl = Path.ChangeExtension(url, ".cva");
            var cvaText = await Web.TryGetStringAsync(cvaUrl, ct);
            if (cvaText is null) return null;

            var cva = Cva.Parse(cvaText);
            if (silent.Length == 0) silent = cva.Get("Install Execution", "SilentInstall");

            var ids = cva.Keys("Devices").ToList();
            if (ids.Count == 0) return null;

            var matched = Matching.FindDevices(p.Devices, ids);
            var current = Matching.Newest(matched);
            if (current is null) return null;

            var newer = Matching.IsNewer(version, current.Version);
            if (newer == false) return null;
            if (newer is null && (date is null || current.Date is null || date <= current.Date)) return null;

            currentVersion = current.Version;
            currentDate = current.Date;
            deviceId = current.DeviceId;
        }

        bool installable = kind == UpdateKind.Driver && ssm && silent.Length > 0;
        return new DriverUpdate
        {
            UpdateId = "hp:" + id,
            Title = name,
            Source = UpdateSource.HP,
            Kind = kind.Value,
            Manufacturer = "HP",
            Model = name,
            DriverClass = category,
            NewVersion = version,
            NewDate = date,
            SizeBytes = size,
            InfoUrl = notes.Length > 0 ? notes : null,
            CanInstall = installable,
            IsSelected = installable,
            Note = installable ? null : "Install with HP Image Assistant",
            CurrentVersion = currentVersion,
            CurrentDate = currentDate,
            DeviceInstanceId = deviceId,
            Payload = new Package(url, hash.Length > 0 ? hash : null, silent),
        };
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("hp", update.UpdateId.Replace(':', '_'));
        var file = Path.Combine(dir, Web.SafeFileName(pkg.Url));

        status("Downloading…");
        await Web.DownloadAsync(pkg.Url, file, ct, pct => status($"Downloading… {pct}%"));

        var hashOk = Web.VerifyHash(file, pkg.Hash);
        if (hashOk == false) return new(false, false, "Download was corrupted (hash mismatch)");
        SignatureVerifier.Require(file, hashVerified: false, "HP Inc", "Hewlett");

        // SoftPaqs are self-extractors: unpack silently, then run the SoftPaq's own silent install command.
        status("Unpacking…");
        var extractDir = Path.Combine(dir, "x");
        var ex = await PackageRunner.RunAsync(file, $"/s /e /f \"{extractDir}\"", dir, TimeSpan.FromMinutes(10), ct);
        if (ex != 0 || !Directory.Exists(extractDir))
            return new(false, false, $"Couldn't unpack the SoftPaq (exit code {ex})");

        status("Installing…");
        var code = await PackageRunner.RunCommandLineAsync(pkg.SilentInstall, extractDir, TimeSpan.FromMinutes(30), ct);
        return ExitCodes.Interpret(code);
    }

    private static string Https(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "";
        if (url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) url = url[6..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = url[7..];
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }
}

/// <summary>Minimal reader for HP .cva files (INI format).</summary>
internal sealed class Cva
{
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static Cva Parse(string text)
    {
        var cva = new Cva();
        List<KeyValuePair<string, string>>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!cva._sections.TryGetValue(name, out current))
                    cva._sections[name] = current = [];
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return cva;
    }

    public string Get(string section, string key) =>
        _sections.TryGetValue(section, out var kv)
            ? kv.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? ""
            : "";

    public IEnumerable<string> Keys(string section) =>
        _sections.TryGetValue(section, out var kv) ? kv.Select(p => p.Key.Trim('"')) : [];
}
