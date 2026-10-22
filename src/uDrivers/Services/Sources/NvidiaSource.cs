using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Latest Game Ready (DCH, WHQL) driver straight from NVIDIA, found with NVIDIA's own driver lookup service.
/// NVIDIA's newest drivers usually reach their site weeks before Windows Update.
/// </summary>
public sealed class NvidiaSource : IDriverSource
{
    private const string LookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string DriverService =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    public UpdateSource Kind => UpdateSource.Nvidia;

    public bool AppliesTo(SystemProfile p) => p.Gpus.Any(IsNvidia);

    private static bool IsNvidia(GpuInfo g) => g.PnpDeviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);

    private sealed record Package(string Url);

    public async Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile p, CancellationToken ct)
    {
        var gpu = p.Gpus.First(IsNvidia);
        var name = Normalize(gpu.Name);

        // 1. NVIDIA's product list → product family ID (pfid) and series ID (psid) for this GPU.
        var lookup = await Web.TryGetStringAsync(LookupUrl, ct)
                     ?? throw new InvalidOperationException("Couldn't reach NVIDIA's product list.");
        var product = Xml.Parse(lookup).All("LookupValue")
            .Select(v => (Name: Normalize(v.Text("Name")), Pfid: v.Text("Value"), Psid: v.Attr("ParentID")))
            .FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (product.Pfid is null or "")
        {
            Log.Info($"NVIDIA: \"{gpu.Name}\" isn't in NVIDIA's product list.");
            return [];
        }

        // 2. Latest WHQL DCH Game Ready driver for this GPU and OS.
        var osId = p.IsWindows11 ? "135" : "57";
        var url = $"{DriverService}?func=DriverManualLookup&psid={product.Psid}&pfid={product.Pfid}&osID={osId}" +
                  "&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        var json = await Web.TryGetStringAsync(url, ct)
                   ?? throw new InvalidOperationException("Couldn't reach NVIDIA's driver service.");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
            return [];
        var info = ids[0].GetProperty("downloadInfo");

        var version = Str(info, "Version");
        var download = Str(info, "DownloadURL").Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
        if (version.Length == 0 || download.Length == 0) return [];

        var installed = ToNvidiaVersion(gpu.DriverVersion);
        if (Matching.IsNewer(version, installed) != true)
        {
            Log.Info($"NVIDIA: installed {installed} is current (latest {version}).");
            return [];
        }

        var device = p.Devices.FirstOrDefault(d => d.DeviceId.Equals(gpu.PnpDeviceId, StringComparison.OrdinalIgnoreCase));

        return
        [
            new DriverUpdate
            {
                UpdateId = $"nvidia:{product.Pfid}:{version}",
                Title = $"NVIDIA Game Ready Driver {version}",
                Source = UpdateSource.Nvidia,
                Kind = UpdateKind.Driver,
                Manufacturer = "NVIDIA",
                Model = $"{gpu.Name} — Game Ready Driver",
                DriverClass = "Display",
                NewVersion = version,
                NewDate = Matching.ParseDate(Str(info, "ReleaseDateTime")),
                InfoUrl = Str(info, "DetailsURL") is { Length: > 0 } d ? d : null,
                CurrentVersion = installed,
                CurrentDate = device?.Date,
                DeviceInstanceId = device?.DeviceId ?? gpu.PnpDeviceId,
                Payload = new Package(download),
            }
        ];
    }

    public async Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct)
    {
        var pkg = (Package)update.Payload!;
        var dir = PackageRunner.FreshFolder("nvidia");
        var file = Path.Combine(dir, Web.SafeFileName(pkg.Url));

        status("Downloading…");
        await Web.DownloadAsync(pkg.Url, file, ct, pct => status($"Downloading… {pct}%"));
        SignatureVerifier.Require(file, hashVerified: false, "NVIDIA");

        // The driver .exe is a 7-Zip self-extractor. Unpack it, then run setup.exe silently.
        status("Unpacking…");
        var extractDir = Path.Combine(dir, "x");
        string setup;
        try
        {
            await Task.Run(() => ExtractSfx(file, extractDir), ct);
            setup = Path.Combine(extractDir, "setup.exe");
            if (!File.Exists(setup)) throw new FileNotFoundException("setup.exe not found in the driver package.");
        }
        catch (Exception ex)
        {
            Log.Error("NVIDIA: couldn't unpack; running the package directly", ex);
            setup = file;
        }

        status("Installing… your screen may flicker");
        var code = await PackageRunner.RunAsync(setup, "-s -noreboot -noeula", Path.GetDirectoryName(setup), TimeSpan.FromMinutes(30), ct);
        return ExitCodes.Interpret(code);
    }

    private static void ExtractSfx(string exePath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var fs = File.OpenRead(exePath);
        var offset = FindSevenZipSignature(fs);
        if (offset < 0) throw new InvalidDataException("No 7-Zip archive found in the driver package.");

        using var archiveStream = new OffsetStream(fs, offset);
        using var archive = SevenZipArchive.Open(archiveStream);
        using var reader = archive.ExtractAllEntries();
        reader.WriteAllToDirectory(outDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
    }

    private static long FindSevenZipSignature(Stream s)
    {
        byte[] sig = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        var buffer = new byte[1 << 20];
        long basePos = 0;
        const long maxScan = 32L << 20;
        s.Position = 0;
        while (basePos < maxScan)
        {
            int n = s.Read(buffer, 0, buffer.Length);
            if (n < sig.Length) break;
            for (int i = 0; i <= n - sig.Length; i++)
            {
                if (buffer.AsSpan(i, sig.Length).SequenceEqual(sig))
                    return basePos + i;
            }
            basePos += n - (sig.Length - 1);
            s.Position = basePos;
        }
        return -1;
    }

    /// <summary>"32.0.15.6094" (Windows) → "560.94" (NVIDIA).</summary>
    public static string ToNvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return windowsVersion;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return windowsVersion;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    private static string Normalize(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            name.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "").Replace("(TM)", ""),
            @"\s+", " ").Trim();

    private static string Str(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return "";
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')).Trim(); }
        catch { return s.Trim(); }
    }
}

/// <summary>Read-only view of a stream starting at an offset (to open the 7-Zip archive inside an .exe).</summary>
internal sealed class OffsetStream(Stream inner, long offset) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length - offset;

    public override long Position
    {
        get => inner.Position - offset;
        set => inner.Position = value + offset;
    }

    public override int Read(byte[] buffer, int index, int count) => inner.Read(buffer, index, count);

    public override long Seek(long position, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => inner.Seek(position + offset, SeekOrigin.Begin) - offset,
        SeekOrigin.Current => inner.Seek(position, SeekOrigin.Current) - offset,
        _ => inner.Seek(position, SeekOrigin.End) - offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int index, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) { /* the caller owns the inner stream */ }
}
