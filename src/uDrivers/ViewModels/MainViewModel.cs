using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using uDrivers.Models;
using uDrivers.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace uDrivers.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly WindowsUpdateDriverService _windowsUpdate = new();
    private readonly AppUpdateService _appUpdates = new();
    private readonly AppSettings _settings = SettingsService.Load();
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
    [ObservableProperty] private string _summary = "Check Windows Update for official driver updates.";
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
        if (!IsElevated)
            ShowMessage("Read-only mode",
                "uDrivers isn't running as administrator, so it can check for updates but not install them. Restart it and accept the prompt to install drivers.",
                InfoBarSeverity.Warning);
    }

    // ---- Settings (two-way bound, saved on change) ----

    public bool CreateRestorePoint
    {
        get => _settings.CreateRestorePoint;
        set { if (SetProperty(_settings.CreateRestorePoint, value, _settings, (s, v) => s.CreateRestorePoint = v)) SettingsService.Save(_settings); }
    }

    public bool CheckAppUpdatesOnStart
    {
        get => _settings.CheckAppUpdatesOnStart;
        set { if (SetProperty(_settings.CheckAppUpdatesOnStart, value, _settings, (s, v) => s.CheckAppUpdatesOnStart = v)) SettingsService.Save(_settings); }
    }

    public bool UseManagedServer
    {
        get => _settings.UseManagedServer;
        set { if (SetProperty(_settings.UseManagedServer, value, _settings, (s, v) => s.UseManagedServer = v)) SettingsService.Save(_settings); }
    }

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
        try
        {
            BusyText = "Reading installed drivers…";
            _allDrivers = await DriverScanner.ScanAsync(_cts.Token);
            ApplyDriverFilter();

            BusyText = "Asking Windows Update for driver updates… this can take a minute.";
            var found = await _windowsUpdate.SearchAsync(_allDrivers, UseManagedServer, _cts.Token);

            foreach (var old in Updates) old.PropertyChanged -= OnUpdateChanged;
            Updates.Clear();
            foreach (var u in found)
            {
                u.PropertyChanged += OnUpdateChanged;
                Updates.Add(u);
            }

            HasChecked = true;
            LastChecked = DateTime.Now;
            Summary = found.Count switch
            {
                0 => "Your drivers are up to date.",
                1 => "1 driver update is available.",
                _ => $"{found.Count} driver updates are available."
            };
        }
        catch (OperationCanceledException)
        {
            Summary = "Check cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            ShowMessage("Couldn't reach Windows Update", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            InstallSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstall => !IsBusy && IsElevated && Updates.Any(u => u.IsSelected && u.Status != UpdateStatus.Installed);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedAsync()
    {
        var selected = Updates.Where(u => u.IsSelected && u.Status != UpdateStatus.Installed).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        MessageOpen = false;
        _cts = new CancellationTokenSource();
        try
        {
            if (CreateRestorePoint)
            {
                BusyText = "Creating a restore point…";
                var problem = await RestorePointService.CreateAsync($"{AppInfo.Name}: before driver updates");
                if (problem is not null)
                    ShowMessage("No restore point was created", problem + " Installing anyway.", InfoBarSeverity.Warning);
            }

            int done = 0;
            var outcome = await _windowsUpdate.InstallAsync(selected, (update, status, text) =>
            {
                _ui.TryEnqueue(() =>
                {
                    update.Status = status;
                    update.StatusText = text;
                    if (status is UpdateStatus.Installed or UpdateStatus.Failed) done++;
                    BusyText = $"Installing {Math.Min(done + 1, selected.Count)} of {selected.Count}: {update.DeviceLabel}";
                });
            }, _cts.Token);

            var severity = outcome.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            var text = $"{outcome.Succeeded} installed" + (outcome.Failed > 0 ? $", {outcome.Failed} failed (see each item for details)." : ".");
            if (outcome.RebootRequired) text += " Restart your PC to finish.";
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
        foreach (var u in Updates.Where(u => u.Status != UpdateStatus.Installed)) u.IsSelected = select;
    }

    private void OnUpdateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriverUpdate.IsSelected) or nameof(DriverUpdate.Status))
            InstallSelectedCommand.NotifyCanExecuteChanged();
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
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Folder) { UseShellExecute = true });
    }

    private void ShowMessage(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        MessageOpen = true;
    }
}
