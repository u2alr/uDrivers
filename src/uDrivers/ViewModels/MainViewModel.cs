using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using uDrivers.Models;
using uDrivers.Services;
using uDrivers.Services.Sources;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace uDrivers.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly AppUpdateService _appUpdates = new();
    private readonly AppSettings _settings = SettingsService.Load();
    private readonly Dictionary<UpdateSource, IDriverSource> _sources;
    private IReadOnlyList<InstalledDriver> _allDrivers = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<DriverUpdate> Updates { get; } = [];
    public ObservableCollection<InstalledDriver> Drivers { get; } = [];

    public bool IsElevated { get; } = Elevation.IsElevated;
    public string VersionText => $"Version {AppInfo.Version}";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _hasChecked;
    [ObservableProperty] private string _summary = "Check for official driver updates.";
    [ObservableProperty] private string _systemSummary = "Windows Update, your PC maker and your graphics vendor";
    [ObservableProperty] private DateTime? _lastChecked;

    [ObservableProperty] private bool _messageOpen;
    [ObservableProperty] private string _messageTitle = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    [ObservableProperty] private string _driverFilter = "";
    [ObservableProperty] private string _driverCountText = "";

    [ObservableProperty] private bool _appUpdateAvailable;
    [ObservableProperty] private string _appUpdateText = "";
    [ObservableProperty] private int _appUpdateProgress;

    public MainViewModel()
    {
        IDriverSource[] all =
        [
            new WindowsUpdateSource(() => UseManagedServer),
            new DellSource(),
            new HpSource(),
            new LenovoSource(),
            new NvidiaSource(),
            new AmdSource(),
            new IntelGraphicsSource(),
        ];
        _sources = all.ToDictionary(s => s.Kind);

        if (!IsElevated)
            ShowMessage("Read-only mode",
                "uDrivers isn't running as administrator, so it can check for updates but not install them. Restart it and accept the prompt to install drivers.",
                InfoBarSeverity.Warning);
    }

    // ---- Settings (two-way bound, saved on change) ----

    private bool Set(bool current, bool value, Action<AppSettings, bool> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(current, value, _settings, apply, name)) return false;
        SettingsService.Save(_settings);
        return true;
    }

    public bool CreateRestorePoint
    {
        get => _settings.CreateRestorePoint;
        set => Set(_settings.CreateRestorePoint, value, (s, v) => s.CreateRestorePoint = v);
    }

    public bool CheckAppUpdatesOnStart
    {
        get => _settings.CheckAppUpdatesOnStart;
        set => Set(_settings.CheckAppUpdatesOnStart, value, (s, v) => s.CheckAppUpdatesOnStart = v);
    }

    public bool UseManagedServer
    {
        get => _settings.UseManagedServer;
        set => Set(_settings.UseManagedServer, value, (s, v) => s.UseManagedServer = v);
    }

    public bool SourceWindowsUpdate
    {
        get => _settings.SourceWindowsUpdate;
        set => Set(_settings.SourceWindowsUpdate, value, (s, v) => s.SourceWindowsUpdate = v);
    }

    public bool SourceManufacturer
    {
        get => _settings.SourceManufacturer;
        set => Set(_settings.SourceManufacturer, value, (s, v) => s.SourceManufacturer = v);
    }

    public bool SourceGraphics
    {
        get => _settings.SourceGraphics;
        set => Set(_settings.SourceGraphics, value, (s, v) => s.SourceGraphics = v);
    }

    private bool IsEnabled(UpdateSource s) => s switch
    {
        UpdateSource.WindowsUpdate => SourceWindowsUpdate,
        UpdateSource.Nvidia or UpdateSource.Amd or UpdateSource.Intel => SourceGraphics,
        _ => SourceManufacturer,
    };

    private static string SourceName(UpdateSource s) => s switch
    {
        UpdateSource.WindowsUpdate => "Windows Update",
        UpdateSource.Nvidia => "NVIDIA",
        UpdateSource.Amd => "AMD",
        _ => s.ToString(),
    };

    // ---- Startup ----

    public async Task InitializeAsync()
    {
        await LoadDriversAsync();
        if (CheckAppUpdatesOnStart)
            await CheckAppUpdateAsync();
    }

    // ---- Installed drivers ----

    [RelayCommand]
    private async Task LoadDriversAsync()
    {
        try
        {
            _allDrivers = await DriverScanner.ScanAsync();
            ApplyDriverFilter();
        }
        catch (Exception ex)
        {
            Log.Error("Driver scan failed", ex);
            ShowMessage("Couldn't read installed drivers", ex.Message, InfoBarSeverity.Error);
        }
    }

    partial void OnDriverFilterChanged(string value) => ApplyDriverFilter();

    private void ApplyDriverFilter()
    {
        var f = DriverFilter.Trim();
        var filtered = f.Length == 0
            ? _allDrivers
            : _allDrivers.Where(d =>
                d.DeviceName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                d.Provider.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                d.DeviceClass.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();

        Drivers.Clear();
        foreach (var d in filtered) Drivers.Add(d);
        DriverCountText = f.Length == 0
            ? $"{_allDrivers.Count} drivers installed"
            : $"{filtered.Count} of {_allDrivers.Count} drivers";
    }

    // ---- Driver updates ----

    private bool CanRun => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        MessageOpen = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            BusyText = "Reading your hardware…";
            _allDrivers = await DriverScanner.ScanAsync(ct);
            ApplyDriverFilter();
            var profile = await SystemInfoService.LoadAsync(_allDrivers);
            SystemSummary = profile.Summary;

            var active = _sources.Values.Where(s => IsEnabled(s.Kind) && s.AppliesTo(profile)).ToList();
            BusyText = $"Checking {string.Join(", ", active.Select(s => SourceName(s.Kind)))}… this can take a minute.";

            var errors = new List<string>();
            var tasks = active.Select(async s =>
            {
                try { return await s.FindAsync(profile, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"{SourceName(s.Kind)} check failed", ex);
                    lock (errors) errors.Add($"{SourceName(s.Kind)}: {ex.Message}");
                    return (IReadOnlyList<DriverUpdate>)Array.Empty<DriverUpdate>();
                }
            }).ToList();

            var found = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
            ResolveOverlaps(found);

            var ordered = found
                .OrderByDescending(u => u.CanInstall)
                .ThenBy(u => u.Kind)
                .ThenBy(u => u.Source)
                .ThenBy(u => u.DeviceLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var old in Updates) old.PropertyChanged -= OnUpdateChanged;
            Updates.Clear();
            foreach (var u in ordered)
            {
                u.PropertyChanged += OnUpdateChanged;
                Updates.Add(u);
            }

            HasChecked = true;
            LastChecked = DateTime.Now;
            Summary = ordered.Count switch
            {
                0 => "Your drivers are up to date.",
                1 => "1 update is available.",
                _ => $"{ordered.Count} updates are available."
            };
            if (ordered.Count > 0)
                Summary += "  " + string.Join(" · ", ordered.GroupBy(u => u.Source).Select(g => $"{SourceName(g.Key)} {g.Count()}"));

            if (errors.Count > 0)
                ShowMessage("Some sources couldn't be checked", string.Join("\n", errors), InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            Summary = "Check cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            ShowMessage("Couldn't check for updates", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            InstallSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>When two sources offer a driver for the same device, keep only the newest one selected.</summary>
    private static void ResolveOverlaps(List<DriverUpdate> found)
    {
        foreach (var group in found.Where(u => u.Kind == UpdateKind.Driver && u.CanInstall && u.DeviceInstanceId is not null)
                                   .GroupBy(u => u.DeviceInstanceId!, StringComparer.OrdinalIgnoreCase)
                                   .Where(g => g.Count() > 1))
        {
            var best = group
                .OrderByDescending(u => u.NewDate ?? DateTime.MinValue)
                .ThenBy(u => u.Source == UpdateSource.WindowsUpdate) // vendor wins ties
                .First();
            foreach (var other in group.Where(u => u != best))
            {
                other.IsSelected = false;
                other.StatusText = $"{best.SourceText} has a newer driver for this device";
            }
        }
    }

    private bool CanInstall => !IsBusy && IsElevated &&
        Updates.Any(u => u.IsSelected && u.CanInstall && u.Status != UpdateStatus.Installed);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedAsync()
    {
        // Windows Update first, then vendor packages (which are usually newer and should win).
        var selected = Updates
            .Where(u => u.IsSelected && u.CanInstall && u.Status != UpdateStatus.Installed)
            .OrderBy(u => u.Source == UpdateSource.WindowsUpdate ? 0 : 1)
            .ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        MessageOpen = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int ok = 0, failed = 0;
        bool reboot = false;
        try
        {
            if (CreateRestorePoint)
            {
                BusyText = "Creating a restore point…";
                var problem = await RestorePointService.CreateAsync($"{AppInfo.Name}: before driver updates");
                if (problem is not null)
                    ShowMessage("No restore point was created", problem + " Installing anyway.", InfoBarSeverity.Warning);
            }

            for (int i = 0; i < selected.Count && !ct.IsCancellationRequested; i++)
            {
                var update = selected[i];
                var prefix = $"{i + 1} of {selected.Count}: {update.DeviceLabel}";
                BusyText = prefix;
                update.Status = UpdateStatus.Downloading;

                void Report(string text) => _ui.TryEnqueue(() =>
                {
                    update.StatusText = text;
                    if (text.StartsWith("Install", StringComparison.Ordinal)) update.Status = UpdateStatus.Installing;
                    BusyText = $"{prefix} — {text}";
                });

                try
                {
                    var result = await _sources[update.Source].InstallAsync(update, Report, ct);
                    update.Status = result.Success ? UpdateStatus.Installed : UpdateStatus.Failed;
                    update.StatusText = result.Message;
                    if (result.Success) { ok++; reboot |= result.RebootRequired; } else failed++;
                    Log.Info($"{(result.Success ? "Installed" : "Failed")}: {update.Title} — {result.Message}");
                }
                catch (OperationCanceledException)
                {
                    update.Status = UpdateStatus.Available;
                    update.StatusText = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    update.Status = UpdateStatus.Failed;
                    update.StatusText = ex.Message;
                    Log.Error($"Failed: {update.Title}", ex);
                }
            }

            var severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            var text = $"{ok} installed" + (failed > 0 ? $", {failed} failed (see each item for details)." : ".");
            if (reboot) text += " Restart your PC to finish.";
            ShowMessage("Driver updates finished", text, severity);

            await LoadDriversAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            ShowMessage("Install failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            InstallSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll() => SetSelection(true);

    [RelayCommand]
    private void SelectNone() => SetSelection(false);

    private void SetSelection(bool select)
    {
        foreach (var u in Updates.Where(u => u.CanInstall && u.Status != UpdateStatus.Installed)) u.IsSelected = select;
    }

    private void OnUpdateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriverUpdate.IsSelected) or nameof(DriverUpdate.Status))
            InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Couldn't open link", ex); }
    }

    // ---- App self-update ----

    [RelayCommand]
    private async Task CheckAppUpdateAsync()
    {
        if (!_appUpdates.IsInstalled)
        {
            AppUpdateText = "Updates are available once uDrivers is installed from a release.";
            return;
        }

        var version = await _appUpdates.CheckAsync();
        AppUpdateAvailable = version is not null;
        AppUpdateText = version is null ? "uDrivers is up to date." : $"Version {version} is available.";
    }

    [RelayCommand]
    private async Task InstallAppUpdateAsync()
    {
        try
        {
            AppUpdateText = "Downloading update…";
            await _appUpdates.DownloadAndRestartAsync(p => _ui.TryEnqueue(() => AppUpdateProgress = p));
        }
        catch (Exception ex)
        {
            Log.Error("App update failed", ex);
            AppUpdateText = "Update failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(Log.Folder);
        OpenUrl(Log.Folder);
    }

    private void ShowMessage(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        MessageOpen = true;
    }
}
