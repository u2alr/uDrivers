using uDrivers.Models;

namespace uDrivers.Services;

public sealed record InstallOutcome(int Succeeded, int Failed, bool RebootRequired);

/// <summary>
/// Finds and installs official driver updates through the Windows Update Agent (WUA) COM API.
/// These are the same signed drivers Windows Update offers under "Optional updates".
/// </summary>
public sealed class WindowsUpdateDriverService
{
    // WUA ServerSelection: 0 = default (WSUS if managed), 2 = public Windows Update.
    private const int ServerSelectionDefault = 0;
    private const int ServerSelectionWindowsUpdate = 2;

    // OperationResultCode values
    private const int OrcSucceeded = 2;
    private const int OrcSucceededWithErrors = 3;

    private dynamic? _session;

    private dynamic Session => _session ??= CreateSession();

    private static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Windows Update Agent is not available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = AppInfo.Name;
        return session;
    }

    /// <summary>Asks Windows Update which driver updates are available for this PC.</summary>
    /// <param name="useManagedServer">Use the organisation's WSUS server instead of public Windows Update.</param>
    public Task<IReadOnlyList<DriverUpdate>> SearchAsync(
        IReadOnlyList<InstalledDriver> installed, bool useManagedServer, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DriverUpdate>>(() =>
        {
            dynamic searcher = Session.CreateUpdateSearcher();
            searcher.ServerSelection = useManagedServer ? ServerSelectionDefault : ServerSelectionWindowsUpdate;
            searcher.Online = true;

            Log.Info("Searching Windows Update for driver updates…");
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            dynamic updates = result.Updates;
            int count = updates.Count;

            var byHardwareId = installed
                .Where(d => d.HardwareId.Length > 0)
                .GroupBy(d => d.HardwareId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var list = new List<DriverUpdate>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                dynamic u = updates.Item(i);

                var update = new DriverUpdate
                {
                    UpdateId = (string)u.Identity.UpdateID,
                    Title = (string)u.Title,
                    Manufacturer = TryGet<string>(() => u.DriverManufacturer) ?? "",
                    Model = TryGet<string>(() => u.DriverModel) ?? "",
                    DriverClass = TryGet<string>(() => u.DriverClass) ?? "",
                    HardwareId = TryGet<string>(() => u.DriverHardwareID) ?? "",
                    NewDate = TryGet<DateTime?>(() => (DateTime)u.DriverVerDate),
                    SizeBytes = TryGet<long?>(() => (long)(decimal)u.MaxDownloadSize) ?? 0,
                    ComUpdate = u,
                };

                if (byHardwareId.TryGetValue(update.HardwareId, out var current))
                {
                    update.CurrentVersion = current.Version;
                    update.CurrentDate = current.Date;
                }

                list.Add(update);
            }

            Log.Info($"Windows Update offered {list.Count} driver update(s).");
            return list;
        }, ct);

    /// <summary>
    /// Downloads and installs the given updates one at a time so the UI can show per-item progress.
    /// Requires the process to be elevated.
    /// </summary>
    public Task<InstallOutcome> InstallAsync(
        IReadOnlyList<DriverUpdate> updates,
        Action<DriverUpdate, UpdateStatus, string> onStatus,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            bool reboot = false;

            foreach (var update in updates)
            {
                if (ct.IsCancellationRequested) break;
                if (update.ComUpdate is null) continue;

                try
                {
                    dynamic u = update.ComUpdate;
                    if (!(bool)u.EulaAccepted) u.AcceptEula();

                    dynamic coll = NewUpdateCollection();
                    coll.Add(u);

                    onStatus(update, UpdateStatus.Downloading, "Downloading…");
                    dynamic downloader = Session.CreateUpdateDownloader();
                    downloader.Updates = coll;
                    dynamic dl = downloader.Download();
                    if ((int)dl.ResultCode is not (OrcSucceeded or OrcSucceededWithErrors))
                        throw new InvalidOperationException($"Download failed (0x{(int)dl.HResult:X8}).");

                    onStatus(update, UpdateStatus.Installing, "Installing…");
                    dynamic installer = Session.CreateUpdateInstaller();
                    installer.Updates = coll;
                    dynamic res = installer.Install();

                    int code = (int)res.ResultCode;
                    if (code is OrcSucceeded or OrcSucceededWithErrors)
                    {
                        ok++;
                        bool needsReboot = (bool)res.RebootRequired;
                        reboot |= needsReboot;
                        onStatus(update, UpdateStatus.Installed, needsReboot ? "Installed — restart needed" : "Installed");
                        Log.Info($"Installed: {update.Title}");
                    }
                    else
                    {
                        throw new InvalidOperationException($"Install failed (0x{(int)res.HResult:X8}).");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    onStatus(update, UpdateStatus.Failed, ex.Message);
                    Log.Error($"Failed: {update.Title}", ex);
                }
            }

            return new InstallOutcome(ok, failed, reboot);
        }, ct);

    private static dynamic NewUpdateCollection()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
        return Activator.CreateInstance(type)!;
    }

    private static T? TryGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
