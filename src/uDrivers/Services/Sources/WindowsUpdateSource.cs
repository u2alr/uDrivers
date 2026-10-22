using uDrivers.Models;

namespace uDrivers.Services.Sources;

/// <summary>
/// Official signed drivers from Microsoft's Windows Update catalog via the Windows Update Agent COM API —
/// the same list Windows shows under "Optional updates".
/// </summary>
public sealed class WindowsUpdateSource(Func<bool> useManagedServer) : IDriverSource
{
    private const int ServerSelectionDefault = 0;        // WSUS if the PC is managed
    private const int ServerSelectionWindowsUpdate = 2;  // public Windows Update
    private const int OrcSucceeded = 2;
    private const int OrcSucceededWithErrors = 3;

    private dynamic? _session;

    public UpdateSource Kind => UpdateSource.WindowsUpdate;

    public bool AppliesTo(SystemProfile profile) => true;

    private dynamic Session => _session ??= CreateSession();

    private static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Windows Update Agent is not available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = AppInfo.Name;
        return session;
    }

    public Task<IReadOnlyList<DriverUpdate>> FindAsync(SystemProfile profile, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DriverUpdate>>(() =>
        {
            dynamic searcher = Session.CreateUpdateSearcher();
            searcher.ServerSelection = useManagedServer() ? ServerSelectionDefault : ServerSelectionWindowsUpdate;
            searcher.Online = true;

            Log.Info("Searching Windows Update for driver updates…");
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            dynamic updates = result.Updates;
            int count = updates.Count;

            var list = new List<DriverUpdate>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                dynamic u = updates.Item(i);

                string driverClass = TryGet<string>(() => u.DriverClass) ?? "";
                var update = new DriverUpdate
                {
                    UpdateId = "wu:" + (string)u.Identity.UpdateID,
                    Title = (string)u.Title,
                    Source = UpdateSource.WindowsUpdate,
                    Kind = driverClass.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ? UpdateKind.Firmware : UpdateKind.Driver,
                    Manufacturer = TryGet<string>(() => u.DriverManufacturer) ?? "",
                    Model = TryGet<string>(() => u.DriverModel) ?? "",
                    DriverClass = driverClass,
                    HardwareId = TryGet<string>(() => u.DriverHardwareID) ?? "",
                    NewDate = TryGet<DateTime?>(() => (DateTime)u.DriverVerDate),
                    SizeBytes = TryGet<long?>(() => (long)(decimal)u.MaxDownloadSize) ?? 0,
                    Payload = u,
                };

                var matched = Matching.FindDevices(profile.Devices, [update.HardwareId]);
                var current = Matching.Newest(matched);
                if (current is not null)
                {
                    update.CurrentVersion = current.Version;
                    update.CurrentDate = current.Date;
                    update.DeviceInstanceId = current.DeviceId;
                }

                list.Add(update);
            }

            Log.Info($"Windows Update offered {list.Count} driver update(s).");
            return list;
        }, ct);

    public Task<InstallResult> InstallAsync(DriverUpdate update, Action<string> status, CancellationToken ct) =>
        Task.Run(() =>
        {
            dynamic u = update.Payload ?? throw new InvalidOperationException("Missing Windows Update data.");
            if (!(bool)u.EulaAccepted) u.AcceptEula();

            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            dynamic coll = Activator.CreateInstance(collType)!;
            coll.Add(u);

            status("Downloading…");
            dynamic downloader = Session.CreateUpdateDownloader();
            downloader.Updates = coll;
            dynamic dl = downloader.Download();
            if ((int)dl.ResultCode is not (OrcSucceeded or OrcSucceededWithErrors))
                return new InstallResult(false, false, $"Download failed (0x{(int)dl.HResult:X8})");

            ct.ThrowIfCancellationRequested();
            status("Installing…");
            dynamic installer = Session.CreateUpdateInstaller();
            installer.Updates = coll;
            dynamic res = installer.Install();

            if ((int)res.ResultCode is OrcSucceeded or OrcSucceededWithErrors)
            {
                bool reboot = (bool)res.RebootRequired;
                return new InstallResult(true, reboot, reboot ? "Installed — restart needed" : "Installed");
            }
            return new InstallResult(false, false, $"Install failed (0x{(int)res.HResult:X8})");
        }, ct);

    private static T? TryGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
